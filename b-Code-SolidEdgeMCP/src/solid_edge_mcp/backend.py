"""Solid Edge 2020 COM adapter. Instances live entirely on ComWorker's STA."""

import math
from contextlib import contextmanager
from pathlib import Path
from uuid import uuid4

from .errors import EdgeError
from .paths import OutputPaths


class SolidEdgeBackend:
    def __init__(self, output_root: Path):
        self.paths = OutputPaths(output_root)
        self.app = None
        self.documents = {}
        self.profiles = {}

    def connect(self, start_if_needed=True, visible=True):
        import pythoncom
        import win32com.client

        if self.app is not None:
            try:
                _ = self.app.Version
            except pythoncom.com_error:
                self.disconnect()
        started = False
        if self.app is None:
            try:
                self.app = win32com.client.GetActiveObject("SolidEdge.Application")
            except pythoncom.com_error as exc:
                # Only MK_E_UNAVAILABLE means no running instance. Do not hide access errors.
                if exc.hresult != -2147221021:
                    raise
                if not start_if_needed:
                    raise EdgeError("Solid Edge is not running. Use start_if_needed=true.") from exc
                self.app = win32com.client.Dispatch("SolidEdge.Application")
                started = True
        self.app.Visible = visible
        return {**self.status(), "started": started}

    def disconnect(self):
        self.profiles.clear()
        self.documents.clear()
        self.app = None
        return {"connected": False, "application_closed": False}

    def _app(self):
        if self.app is None:
            raise EdgeError("Not connected. Call se_connect first.")
        return self.app

    def status(self):
        result = {"connected": self.app is not None, "output_root": str(self.paths.root)}
        if self.app is not None:
            result.update(
                version=str(self.app.Version), document_count=int(self.app.Documents.Count)
            )
        return result

    def _register(self, doc):
        for key, stored in self.documents.items():
            if stored._oleobj_ == doc._oleobj_:
                return key
        key = uuid4().hex
        self.documents[key] = doc
        return key

    def _forget(self, document_id):
        self.documents.pop(document_id, None)
        for key in list(self.profiles):
            if self.profiles[key][0] == document_id:
                del self.profiles[key]

    def _doc(self, document_id, *, part=False):
        app = self._app()
        doc = self.documents.get(document_id)
        if doc is None:
            raise EdgeError("Unknown document_id. Call se_list_documents to obtain current IDs.")
        # Never silently redirect a stale ID to whichever document happens to be active.
        if not any(
            app.Documents.Item(i)._oleobj_ == doc._oleobj_
            for i in range(1, app.Documents.Count + 1)
        ):
            self._forget(document_id)
            raise EdgeError("Document was closed. Its document_id and profile IDs are invalid.")
        if part and int(doc.Type) != 1:
            raise EdgeError("This modeling operation requires a .par part document.")
        return doc

    @staticmethod
    def _info(doc, document_id):
        return {
            "document_id": document_id,
            "name": str(doc.Name),
            "full_name": str(doc.FullName),
            "type": int(doc.Type),
            "dirty": bool(doc.Dirty),
        }

    def list_documents(self):
        app = self._app()
        result = []
        alive = set()
        for i in range(1, app.Documents.Count + 1):
            doc = app.Documents.Item(i)
            key = self._register(doc)
            alive.add(key)
            result.append(self._info(doc, key))
        for key in set(self.documents) - alive:
            self._forget(key)
        return {"documents": result}

    def get_document(self, document_id):
        return self._info(self._doc(document_id), document_id)

    def new_part(self):
        doc = self._app().Documents.Add("SolidEdge.PartDocument")
        document_id = self._register(doc)
        try:
            doc.ModelingMode = 2  # seModelingModeOrdered; needed for editable extrusion features.
        except Exception as exc:
            raise EdgeError(
                f"Part {document_id} was created, but ordered mode failed: {exc}"
            ) from exc
        return self._info(doc, document_id)

    def open_document(self, path):
        target = Path(path).expanduser()
        if not target.is_absolute() or not target.is_file() or target.suffix.lower() != ".par":
            raise EdgeError("Supply an existing absolute path to a .par file.")
        doc = self._app().Documents.Open(str(target.resolve()))
        return self._info(doc, self._register(doc))

    def reference_planes(self, document_id):
        doc = self._doc(document_id, part=True)
        planes = []
        for i in range(1, doc.RefPlanes.Count + 1):
            plane = doc.RefPlanes.Item(i)
            planes.append({"index": i, "name": str(plane.Name)})
        return {
            "planes": planes,
            "coordinates": "Sketch coordinates are local to the chosen plane.",
        }

    def _profile(self, document_id, plane_index):
        doc = self._doc(document_id, part=True)
        if int(doc.ModelingMode) != 2:
            raise EdgeError(
                "Ordered modeling mode is required. Use se_new_part for a new ordered part."
            )
        if not 1 <= plane_index <= doc.RefPlanes.Count:
            raise EdgeError(f"plane_index must be between 1 and {doc.RefPlanes.Count}.")
        doc.Activate()
        profile = doc.ProfileSets.Add().Profiles.Add(doc.RefPlanes.Item(plane_index))
        return profile

    def _finish_profile(self, document_id, profile):
        code = int(profile.End(1))  # igProfileClosed
        if code != 0:
            raise EdgeError(f"Profile was created but is not closed (validation code {code}).")
        key = uuid4().hex
        self.profiles[key] = (document_id, profile)
        return {
            "profile_id": key,
            "document_id": document_id,
            "validation_code": code,
            "validation_passed": code == 0,
            "note": "Extrusion verifies that the constructed loop produces the expected solid.",
        }

    def rectangle(self, document_id, width_mm, height_mm, x_mm=0, y_mm=0, plane_index=1):
        profile = self._profile(document_id, plane_index)
        x, y, w, h = (v / 1000 for v in (x_mm, y_mm, width_mm, height_mm))
        points = [(x, y), (x + w, y), (x + w, y + h), (x, y + h)]
        lines = [profile.Lines2d.AddBy2Points(*points[i], *points[(i + 1) % 4]) for i in range(4)]
        # Coincident endpoints must be constrained; identical coordinates alone can fail End().
        for i in range(4):
            # fwksupp.tlb KeypointIndexConstants: igLineEnd=1, igLineStart=0.
            # KeyPointType's 1/2 flags are a different enum; using them deforms the rectangle.
            profile.Relations2d.AddKeypoint(lines[i], 1, lines[(i + 1) % 4], 0)
        return self._finish_profile(document_id, profile)

    def circle(self, document_id, radius_mm, x_mm=0, y_mm=0, plane_index=1):
        profile = self._profile(document_id, plane_index)
        profile.Circles2d.AddByCenterRadius(x_mm / 1000, y_mm / 1000, radius_mm / 1000)
        return self._finish_profile(document_id, profile)

    def geometry(self, document_id):
        doc = self._doc(document_id, part=True)
        result = []
        for i in range(1, doc.Models.Count + 1):
            model = doc.Models.Item(i)
            body = model.Body
            # pywin32 uses the type info to marshal SAFEARRAY(double)* and returns [in,out] values.
            lo, hi = body.GetRange([0.0] * 3, [0.0] * 3)
            minimum = [float(v) * 1000 for v in lo]
            maximum = [float(v) * 1000 for v in hi]
            result.append(
                {
                    "model_index": i,
                    "name": str(model.Name),
                    "volume_mm3": float(body.Volume) * 1e9,
                    "face_count": int(body.Faces(1).Count),
                    "min_mm": minimum,
                    "max_mm": maximum,
                    "size_mm": [b - a for a, b in zip(minimum, maximum, strict=True)],
                }
            )
        return {"models": result, "total_volume_mm3": sum(m["volume_mm3"] for m in result)}

    def extrude(self, document_id, profile_id, distance_mm, operation="add", direction="normal"):
        doc = self._doc(document_id, part=True)
        entry = self.profiles.get(profile_id)
        if entry is None or entry[0] != document_id:
            raise EdgeError("Profile is unknown, consumed, or belongs to a different document.")
        if int(doc.ModelingMode) != 2:
            raise EdgeError("Extrusion requires ordered modeling mode.")
        if doc.Models.Count > 1:
            raise EdgeError("This version supports extrusion in single-body parts only.")
        if operation == "cut" and doc.Models.Count == 0:
            raise EdgeError("Create a base solid before cutting.")
        profile = entry[1]
        doc.Activate()
        before = self.geometry(document_id)["total_volume_mm3"]
        side = {"normal": 2, "reverse": 1}[direction]
        profiles = (profile,)
        if doc.Models.Count == 0:
            doc.Models.AddFiniteExtrudedProtrusion(1, profiles, side, distance_mm / 1000)
        else:
            model = doc.Models.Item(1)
            collection = model.ExtrudedProtrusions if operation == "add" else model.ExtrudedCutouts
            collection.AddFiniteMulti(1, profiles, side, distance_mm / 1000)
        # A returned COM call can still have created an invalid/empty feature.
        del self.profiles[profile_id]
        after = self.geometry(document_id)
        delta = after["total_volume_mm3"] - before
        expected_sign = 1 if operation == "add" else -1
        if expected_sign * delta <= max(1e-8, abs(before) * 1e-10):
            raise EdgeError(
                "Extrusion call completed but the expected volume change was not verified. "
                "Inspect the part and feature tree; the call may have left an invalid feature."
            )
        profile.Visible = False
        return {"document_id": document_id, "operation": operation, "geometry": after}

    def box(self, document_id, width_mm, depth_mm, height_mm, plane_index=1):
        doc = self._doc(document_id, part=True)
        if doc.Models.Count:
            raise EdgeError(
                "se_create_box requires an empty part. Use a profile and se_extrude to add."
            )
        sketch = self.rectangle(document_id, width_mm, depth_mm, plane_index=plane_index)
        return self.extrude(document_id, sketch["profile_id"], height_mm)

    def cylinder(self, document_id, radius_mm, height_mm, plane_index=1):
        doc = self._doc(document_id, part=True)
        if doc.Models.Count:
            raise EdgeError("se_create_cylinder requires an empty part.")
        sketch = self.circle(document_id, radius_mm, plane_index=plane_index)
        return self.extrude(document_id, sketch["profile_id"], height_mm)

    def features(self, document_id):
        doc = self._doc(document_id, part=True)
        result = []
        for i in range(1, doc.Models.Count + 1):
            features = doc.Models.Item(i).Features
            for j in range(1, features.Count + 1):
                feature = features.Item(j)
                result.append(
                    {
                        "model_index": i,
                        "index": j,
                        "name": str(feature.Name),
                        "status": int(feature.Status),
                    }
                )
        return {"features": result}

    def set_extrusion_depth(self, document_id, feature_name, depth_mm):
        doc = self._doc(document_id, part=True)
        matches = []
        for i in range(1, doc.Models.Count + 1):
            model = doc.Models.Item(i)
            for collection in (model.ExtrudedProtrusions, model.ExtrudedCutouts):
                for j in range(1, collection.Count + 1):
                    feature = collection.Item(j)
                    if str(feature.Name) == feature_name:
                        matches.append(feature)
        if len(matches) != 1:
            raise EdgeError("Supply a unique extrusion/cutout name from se_features.")
        feature = matches[0]
        extent_type, side, previous = feature.GetDirection1Extent()
        if extent_type != 13:
            raise EdgeError("Only an existing finite extrusion depth can be edited.")
        feature.ApplyDirection1Extent(13, side, depth_mm / 1000, None, 0)
        _, _, actual = feature.GetDirection1Extent()
        if not math.isclose(actual * 1000, depth_mm, rel_tol=1e-8, abs_tol=1e-8):
            raise EdgeError("Feature depth did not read back as requested. Inspect the part.")
        return {
            "feature": feature_name,
            "before_mm": previous * 1000,
            "after_mm": actual * 1000,
            "geometry": self.geometry(document_id),
            "features": self.features(document_id),
        }

    @staticmethod
    def _variable_info(variables, variable):
        name = str(variables.GetName(variable))
        raw = float(variable.Value)
        kind = int(variable.UnitsType)
        unit = {1: "mm", 2: "deg"}.get(kind, "raw")
        value = raw * 1000 if kind == 1 else math.degrees(raw) if kind == 2 else raw
        return {
            "name": name,
            "value": value,
            "unit": unit,
            "units_type": kind,
            "raw_value": raw,
            "formula": str(variable.Formula),
        }

    def list_variables(self, document_id):
        variables = self._doc(document_id, part=True).Variables
        found = {}
        # Query both dimensions and numeric user variables; their enum values are not 0/1.
        for kind in (1661573600, 1560616706):
            collection = variables.Query("*", 2, kind, True)
            for i in range(1, collection.Count + 1):
                entry = self._variable_info(variables, collection.Item(i))
                found[entry["name"]] = entry
        return {"variables": list(found.values())}

    def set_variable(self, document_id, name, value, unit):
        doc = self._doc(document_id, part=True)
        variables = doc.Variables
        names = {v["name"] for v in self.list_variables(document_id)["variables"]}
        if name not in names:
            raise EdgeError("Variable not found. Use an exact name from se_list_variables.")
        variable = variables.Item(name)
        before = self._variable_info(variables, variable)
        if unit != before["unit"]:
            raise EdgeError(f"Variable requires unit={before['unit']!r}; received {unit!r}.")
        variable.Value = (
            value / 1000 if unit == "mm" else math.radians(value) if unit == "deg" else value
        )
        after = self._variable_info(variables, variable)
        if not math.isclose(after["value"], value, rel_tol=1e-8, abs_tol=1e-8):
            raise EdgeError(f"Value did not read back as requested: {after}. Inspect the model.")
        return {"before": before, "after": after, "features": self.features(document_id)}

    @contextmanager
    def _no_alerts(self):
        app = self._app()
        previous = app.DisplayAlerts
        app.DisplayAlerts = False
        try:
            yield
        finally:
            app.DisplayAlerts = previous

    def save_document(self, document_id, path=None, overwrite=False):
        doc = self._doc(document_id, part=True)
        if path is None:
            current = Path(str(doc.FullName))
            if not current.is_absolute() or not current.is_file():
                raise EdgeError("Unsaved part: supply a .par output path.")
            # Saving in place is a write too; retain the same output boundary and overwrite rule.
            target = self.paths.target(str(current), {".par"}, overwrite)
        else:
            target = self.paths.target(path, {".par"}, overwrite)
        with self._no_alerts():
            if Path(str(doc.FullName)) == target:
                doc.Save()
            else:
                doc.SaveAs(str(target))
        return {**self._info(doc, document_id), **self.paths.verify(target)}

    def export_step(self, document_id, path, overwrite=False):
        doc = self._doc(document_id, part=True)
        if self.geometry(document_id)["total_volume_mm3"] <= 0:
            raise EdgeError("STEP export requires a nonempty solid.")
        target = self.paths.target(path, {".stp", ".step"}, overwrite)
        staged = target.with_name(f".{target.stem}-{uuid4().hex}{target.suffix}")
        original_name = str(doc.FullName)
        try:
            with self._no_alerts():
                doc.SaveCopyAs(str(staged))
            self.paths.verify(staged)
            if str(doc.FullName) != original_name:
                raise EdgeError("Export unexpectedly changed the native document path. Inspect it.")
            if overwrite:
                staged.replace(target)
            else:
                # Windows rename refuses to overwrite a file that appeared during translation.
                staged.rename(target)
        finally:
            staged.unlink(missing_ok=True)
        return {"document_id": document_id, "format": "STEP", **self.paths.verify(target)}

    def set_view(self, document_id, orientation="Iso"):
        doc = self._doc(document_id, part=True)
        doc.Activate()
        view = self._app().ActiveWindow.View
        view.ApplyNamedView(orientation)
        view.Fit()
        return {"document_id": document_id, "orientation": orientation}

    def capture_viewport(self, document_id, path, width=1280, height=960, overwrite=False):
        from PIL import Image

        doc = self._doc(document_id, part=True)
        target = self.paths.target(path, {".png"}, overwrite)
        staged = target.with_name(f".{target.stem}-{uuid4().hex}.bmp")
        converted = staged.with_suffix(".png")
        doc.Activate()
        try:
            # Solid Edge 2020 silently appends .tif to a requested .png. BMP is supported natively.
            self._app().ActiveWindow.View.SaveAsImage(str(staged), width, height)
            self.paths.verify(staged)
            with Image.open(staged) as image:
                image.save(converted, format="PNG")
            if overwrite:
                converted.replace(target)
            else:
                converted.rename(target)
        finally:
            staged.unlink(missing_ok=True)
            converted.unlink(missing_ok=True)
        return {"document_id": document_id, **self.paths.verify(target)}

    def close_document(self, document_id, discard_changes=False):
        doc = self._doc(document_id)
        if bool(doc.Dirty) and not discard_changes:
            raise EdgeError("Document has unsaved changes. Save it or set discard_changes=true.")
        with self._no_alerts():
            doc.Close(False)
        self._forget(document_id)
        return {"document_id": document_id, "closed": True}
