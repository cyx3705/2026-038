using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using static SolidEdgeMcp.ComInterop;

namespace SolidEdgeMcp;

/// <summary>Solid Edge 2020 COM adapter via late binding. Instances live entirely on ComWorker's STA.</summary>
public sealed class SolidEdgeBackend(string outputRoot)
{
    private const string ProgId = "SolidEdge.Application";

    public OutputPaths Paths { get; } = new(outputRoot);
    internal dynamic? App { get; set; }
    internal Dictionary<string, object> Documents { get; } = new();
    internal Dictionary<string, (string DocumentId, object Profile)> Profiles { get; } = new();

    private static string NewId() => Guid.NewGuid().ToString("N");

    public JsonObject Connect(bool startIfNeeded = true, bool visible = true)
    {
        if (App is not null)
        {
            try
            {
                _ = App.Version;
            }
            catch (COMException)
            {
                Disconnect();
            }
        }
        var started = false;
        if (App is null)
        {
            try
            {
                App = GetActiveObject(ProgId);
            }
            // Only MK_E_UNAVAILABLE means no running instance. Do not hide access errors.
            catch (COMException exc) when (exc.HResult == MkEUnavailable)
            {
                if (!startIfNeeded)
                    throw new EdgeError("Solid Edge is not running. Use start_if_needed=true.");
                App = Activator.CreateInstance(Type.GetTypeFromProgID(ProgId, throwOnError: true)!);
                started = true;
            }
        }
        App!.Visible = visible;
        var result = Status();
        result["started"] = started;
        return result;
    }

    public JsonObject Disconnect()
    {
        Profiles.Clear();
        Documents.Clear();
        App = null;
        return new JsonObject { ["connected"] = false, ["application_closed"] = false };
    }

    private dynamic RequireApp() => App ?? throw new EdgeError("Not connected. Call se_connect first.");

    public JsonObject Status()
    {
        var result = new JsonObject { ["connected"] = App is not null, ["output_root"] = Paths.Root };
        if (App is not null)
        {
            result["version"] = Str(App.Version);
            result["document_count"] = Int(App.Documents.Count);
        }
        return result;
    }

    private string Register(object doc)
    {
        foreach (var (key, stored) in Documents)
        {
            if (Same(stored, doc))
                return key;
        }
        var id = NewId();
        Documents[id] = doc;
        return id;
    }

    private void Forget(string documentId)
    {
        Documents.Remove(documentId);
        foreach (var key in Profiles.Where(p => p.Value.DocumentId == documentId).Select(p => p.Key).ToList())
            Profiles.Remove(key);
    }

    internal dynamic Doc(string documentId, bool part = false)
    {
        var app = RequireApp();
        if (!Documents.TryGetValue(documentId, out var doc))
            throw new EdgeError("Unknown document_id. Call se_list_documents to obtain current IDs.");
        // Never silently redirect a stale ID to whichever document happens to be active.
        var alive = false;
        int count = Int(app.Documents.Count);
        for (var i = 1; i <= count && !alive; i++)
            alive = Same((object)app.Documents.Item(i), doc);
        if (!alive)
        {
            Forget(documentId);
            throw new EdgeError("Document was closed. Its document_id and profile IDs are invalid.");
        }
        dynamic d = doc;
        if (part && Int(d.Type) != 1)
            throw new EdgeError("This modeling operation requires a .par part document.");
        return d;
    }

    private static JsonObject Info(dynamic doc, string documentId) => new()
    {
        ["document_id"] = documentId,
        ["name"] = Str(doc.Name),
        ["full_name"] = Str(doc.FullName),
        ["type"] = Int(doc.Type),
        ["dirty"] = Convert.ToBoolean(doc.Dirty),
    };

    public JsonObject ListDocuments()
    {
        var app = RequireApp();
        var result = new JsonArray();
        var alive = new HashSet<string>();
        int count = Int(app.Documents.Count);
        for (var i = 1; i <= count; i++)
        {
            object doc = app.Documents.Item(i);
            var key = Register(doc);
            alive.Add(key);
            result.Add(Info(doc, key));
        }
        foreach (var key in Documents.Keys.Except(alive).ToList())
            Forget(key);
        return new JsonObject { ["documents"] = result };
    }

    public JsonObject GetDocument(string documentId) => Info(Doc(documentId), documentId);

    public JsonObject NewPart()
    {
        object doc = RequireApp().Documents.Add("SolidEdge.PartDocument");
        var documentId = Register(doc);
        try
        {
            ((dynamic)doc).ModelingMode = 2; // seModelingModeOrdered; needed for editable extrusion features.
        }
        catch (Exception exc)
        {
            throw new EdgeError($"Part {documentId} was created, but ordered mode failed: {exc.Message}");
        }
        return Info(doc, documentId);
    }

    public JsonObject OpenDocument(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)
            || !Path.GetExtension(path).Equals(".par", StringComparison.OrdinalIgnoreCase))
            throw new EdgeError("Supply an existing absolute path to a .par file.");
        object doc = RequireApp().Documents.Open(Path.GetFullPath(path));
        return Info(doc, Register(doc));
    }

    public JsonObject ReferencePlanes(string documentId)
    {
        var doc = Doc(documentId, part: true);
        var planes = new JsonArray();
        int count = Int(doc.RefPlanes.Count);
        for (var i = 1; i <= count; i++)
            planes.Add(new JsonObject { ["index"] = i, ["name"] = Str(doc.RefPlanes.Item(i).Name) });
        return new JsonObject
        {
            ["planes"] = planes,
            ["coordinates"] = "Sketch coordinates are local to the chosen plane.",
        };
    }

    private dynamic NewProfile(string documentId, int planeIndex)
    {
        var doc = Doc(documentId, part: true);
        if (Int(doc.ModelingMode) != 2)
            throw new EdgeError("Ordered modeling mode is required. Use se_new_part for a new ordered part.");
        int count = Int(doc.RefPlanes.Count);
        if (planeIndex < 1 || planeIndex > count)
            throw new EdgeError($"plane_index must be between 1 and {count}.");
        doc.Activate();
        return doc.ProfileSets.Add().Profiles.Add(doc.RefPlanes.Item(planeIndex));
    }

    private JsonObject FinishProfile(string documentId, dynamic profile)
    {
        int code = Int(profile.End(1)); // igProfileClosed
        if (code != 0)
            throw new EdgeError($"Profile was created but is not closed (validation code {code}).");
        var key = NewId();
        Profiles[key] = (documentId, (object)profile);
        return new JsonObject
        {
            ["profile_id"] = key,
            ["document_id"] = documentId,
            ["validation_code"] = code,
            ["validation_passed"] = code == 0,
            ["note"] = "Extrusion verifies that the constructed loop produces the expected solid.",
        };
    }

    public JsonObject Rectangle(string documentId, double widthMm, double heightMm, double xMm = 0, double yMm = 0, int planeIndex = 1)
    {
        var profile = NewProfile(documentId, planeIndex);
        double x = xMm / 1000, y = yMm / 1000, w = widthMm / 1000, h = heightMm / 1000;
        (double X, double Y)[] points = [(x, y), (x + w, y), (x + w, y + h), (x, y + h)];
        var lines = new object[4];
        for (var i = 0; i < 4; i++)
        {
            var (a, b) = (points[i], points[(i + 1) % 4]);
            lines[i] = profile.Lines2d.AddBy2Points(a.X, a.Y, b.X, b.Y);
        }
        // Coincident endpoints must be constrained; identical coordinates alone can fail End().
        for (var i = 0; i < 4; i++)
        {
            // fwksupp.tlb KeypointIndexConstants: igLineEnd=1, igLineStart=0.
            // KeyPointType's 1/2 flags are a different enum; using them deforms the rectangle.
            profile.Relations2d.AddKeypoint(lines[i], 1, lines[(i + 1) % 4], 0);
        }
        return FinishProfile(documentId, profile);
    }

    public JsonObject Circle(string documentId, double radiusMm, double xMm = 0, double yMm = 0, int planeIndex = 1)
    {
        var profile = NewProfile(documentId, planeIndex);
        profile.Circles2d.AddByCenterRadius(xMm / 1000, yMm / 1000, radiusMm / 1000);
        return FinishProfile(documentId, profile);
    }

    public JsonObject Geometry(string documentId)
    {
        var doc = Doc(documentId, part: true);
        var models = new JsonArray();
        var total = 0.0;
        int count = Int(doc.Models.Count);
        for (var i = 1; i <= count; i++)
        {
            var model = doc.Models.Item(i);
            var body = model.Body;
            // GetRange fills two SAFEARRAY(double) [in,out] arguments.
            Array lo = new double[3], hi = new double[3];
            body.GetRange(ref lo, ref hi);
            double[] minimum = lo.Cast<object>().Select(v => Double(v) * 1000).ToArray();
            double[] maximum = hi.Cast<object>().Select(v => Double(v) * 1000).ToArray();
            double volume = Double(body.Volume) * 1e9;
            total += volume;
            models.Add(new JsonObject
            {
                ["model_index"] = i,
                ["name"] = Str(model.Name),
                ["volume_mm3"] = volume,
                ["face_count"] = Int(body.Faces(1).Count),
                ["min_mm"] = Numbers(minimum),
                ["max_mm"] = Numbers(maximum),
                ["size_mm"] = Numbers(minimum.Zip(maximum, (a, b) => b - a)),
            });
        }
        return new JsonObject { ["models"] = models, ["total_volume_mm3"] = total };
    }

    private static JsonArray Numbers(IEnumerable<double> values) => new(values.Select(v => (JsonNode)v).ToArray());

    public JsonObject Extrude(string documentId, string profileId, double distanceMm, string operation = "add", string direction = "normal")
    {
        var doc = Doc(documentId, part: true);
        if (!Profiles.TryGetValue(profileId, out var entry) || entry.DocumentId != documentId)
            throw new EdgeError("Profile is unknown, consumed, or belongs to a different document.");
        if (Int(doc.ModelingMode) != 2)
            throw new EdgeError("Extrusion requires ordered modeling mode.");
        int models = Int(doc.Models.Count);
        if (models > 1)
            throw new EdgeError("This version supports extrusion in single-body parts only.");
        if (operation == "cut" && models == 0)
            throw new EdgeError("Create a base solid before cutting.");
        dynamic profile = entry.Profile;
        doc.Activate();
        var before = (double)Geometry(documentId)["total_volume_mm3"]!;
        var side = direction == "normal" ? 2 : 1;
        // Part.tlb declares ProfileArray as SAFEARRAY(IDispatch*)*; DispatchWrapper[] marshals as exactly that.
        Array profiles = new DispatchWrapper[] { new((object)profile) };
        if (models == 0)
        {
            doc.Models.AddFiniteExtrudedProtrusion(1, ref profiles, side, distanceMm / 1000);
        }
        else
        {
            var model = doc.Models.Item(1);
            var collection = operation == "add" ? model.ExtrudedProtrusions : model.ExtrudedCutouts;
            collection.AddFiniteMulti(1, ref profiles, side, distanceMm / 1000);
        }
        // A returned COM call can still have created an invalid/empty feature.
        Profiles.Remove(profileId);
        var after = Geometry(documentId);
        var delta = (double)after["total_volume_mm3"]! - before;
        var expectedSign = operation == "add" ? 1 : -1;
        if (expectedSign * delta <= Math.Max(1e-8, Math.Abs(before) * 1e-10))
            throw new EdgeError(
                "Extrusion call completed but the expected volume change was not verified. "
                + "Inspect the part and feature tree; the call may have left an invalid feature.");
        profile.Visible = false;
        return new JsonObject { ["document_id"] = documentId, ["operation"] = operation, ["geometry"] = after };
    }

    public JsonObject Box(string documentId, double widthMm, double depthMm, double heightMm, int planeIndex = 1)
    {
        var doc = Doc(documentId, part: true);
        if (Int(doc.Models.Count) != 0)
            throw new EdgeError("se_create_box requires an empty part. Use a profile and se_extrude to add.");
        var sketch = Rectangle(documentId, widthMm, depthMm, planeIndex: planeIndex);
        return Extrude(documentId, (string)sketch["profile_id"]!, heightMm);
    }

    public JsonObject Cylinder(string documentId, double radiusMm, double heightMm, int planeIndex = 1)
    {
        var doc = Doc(documentId, part: true);
        if (Int(doc.Models.Count) != 0)
            throw new EdgeError("se_create_cylinder requires an empty part.");
        var sketch = Circle(documentId, radiusMm, planeIndex: planeIndex);
        return Extrude(documentId, (string)sketch["profile_id"]!, heightMm);
    }

    public JsonObject Features(string documentId)
    {
        var doc = Doc(documentId, part: true);
        var result = new JsonArray();
        int models = Int(doc.Models.Count);
        for (var i = 1; i <= models; i++)
        {
            var features = doc.Models.Item(i).Features;
            int count = Int(features.Count);
            for (var j = 1; j <= count; j++)
            {
                var feature = features.Item(j);
                result.Add(new JsonObject
                {
                    ["model_index"] = i,
                    ["index"] = j,
                    ["name"] = Str(feature.Name),
                    ["status"] = Int(feature.Status),
                });
            }
        }
        return new JsonObject { ["features"] = result };
    }

    public JsonObject SetExtrusionDepth(string documentId, string featureName, double depthMm)
    {
        var doc = Doc(documentId, part: true);
        var matches = new List<object>();
        int models = Int(doc.Models.Count);
        for (var i = 1; i <= models; i++)
        {
            var model = doc.Models.Item(i);
            foreach (var collection in new[] { model.ExtrudedProtrusions, model.ExtrudedCutouts })
            {
                int count = Int(collection.Count);
                for (var j = 1; j <= count; j++)
                {
                    var feature = collection.Item(j);
                    if (Str(feature.Name) == featureName)
                        matches.Add(feature);
                }
            }
        }
        if (matches.Count != 1)
            throw new EdgeError("Supply a unique extrusion/cutout name from se_features.");
        dynamic target = matches[0];
        int extentType = 0, side = 0;
        double previous = 0;
        target.GetDirection1Extent(out extentType, out side, out previous);
        if (extentType != 13)
            throw new EdgeError("Only an existing finite extrusion depth can be edited.");
        // KeyPointFlags is declared KeyPointExtentConstants* even though it is an input; the dynamic
        // binder cannot convert this signature, so call through IDispatch with a null IDispatch key point.
        Invoke(matches[0], "ApplyDirection1Extent", [13, side, depthMm / 1000, new DispatchWrapper(null), 0], 4);
        int ignoredType = 0, ignoredSide = 0;
        double actual = 0;
        target.GetDirection1Extent(out ignoredType, out ignoredSide, out actual);
        if (!IsClose(actual * 1000, depthMm))
            throw new EdgeError("Feature depth did not read back as requested. Inspect the part.");
        return new JsonObject
        {
            ["feature"] = featureName,
            ["before_mm"] = previous * 1000,
            ["after_mm"] = actual * 1000,
            ["geometry"] = Geometry(documentId),
            ["features"] = Features(documentId),
        };
    }

    private static bool IsClose(double a, double b) =>
        Math.Abs(a - b) <= Math.Max(1e-8 * Math.Max(Math.Abs(a), Math.Abs(b)), 1e-8);

    private static JsonObject VariableInfo(dynamic variables, dynamic variable)
    {
        string name = Str(variables.GetName(variable));
        double raw = Double(variable.Value);
        int kind = Int(variable.UnitsType);
        var unit = kind switch { 1 => "mm", 2 => "deg", _ => "raw" };
        var value = kind switch { 1 => raw * 1000, 2 => raw * 180 / Math.PI, _ => raw };
        return new JsonObject
        {
            ["name"] = name,
            ["value"] = value,
            ["unit"] = unit,
            ["units_type"] = kind,
            ["raw_value"] = raw,
            ["formula"] = Str(variable.Formula),
        };
    }

    public JsonObject ListVariables(string documentId)
    {
        var variables = Doc(documentId, part: true).Variables;
        var found = new Dictionary<string, JsonObject>();
        var order = new List<string>();
        // Query both dimensions and numeric user variables; their enum values are not 0/1.
        foreach (var kind in new[] { 1661573600, 1560616706 })
        {
            var collection = variables.Query("*", 2, kind, true);
            int count = Int(collection.Count);
            for (var i = 1; i <= count; i++)
            {
                JsonObject entry = VariableInfo(variables, collection.Item(i));
                var name = (string)entry["name"]!;
                if (!found.ContainsKey(name))
                    order.Add(name);
                found[name] = entry;
            }
        }
        return new JsonObject { ["variables"] = new JsonArray(order.Select(n => (JsonNode)found[n]).ToArray()) };
    }

    public JsonObject SetVariable(string documentId, string name, double value, string unit)
    {
        var doc = Doc(documentId, part: true);
        var variables = doc.Variables;
        var names = ListVariables(documentId)["variables"]!.AsArray().Select(v => (string)v!["name"]!).ToHashSet();
        if (!names.Contains(name))
            throw new EdgeError("Variable not found. Use an exact name from se_list_variables.");
        var variable = variables.Item(name);
        JsonObject before = VariableInfo(variables, variable);
        var expected = (string)before["unit"]!;
        if (unit != expected)
            throw new EdgeError($"Variable requires unit='{expected}'; received '{unit}'.");
        variable.Value = unit switch { "mm" => value / 1000, "deg" => value * Math.PI / 180, _ => value };
        JsonObject after = VariableInfo(variables, variable);
        if (!IsClose((double)after["value"]!, value))
            throw new EdgeError($"Value did not read back as requested: {after.ToJsonString()}. Inspect the model.");
        return new JsonObject { ["before"] = before, ["after"] = after, ["features"] = Features(documentId) };
    }

    internal void WithoutAlerts(Action action)
    {
        var app = RequireApp();
        var previous = app.DisplayAlerts;
        app.DisplayAlerts = false;
        try
        {
            action();
        }
        finally
        {
            app.DisplayAlerts = previous;
        }
    }

    private static JsonObject Merge(JsonObject first, JsonObject second)
    {
        foreach (var (key, value) in second.ToList())
        {
            second.Remove(key);
            first[key] = value;
        }
        return first;
    }

    public JsonObject SaveDocument(string documentId, string? path = null, bool overwrite = false)
    {
        var doc = Doc(documentId, part: true);
        string current = Str(doc.FullName);
        string target;
        if (path is null)
        {
            if (!Path.IsPathFullyQualified(current) || !File.Exists(current))
                throw new EdgeError("Unsaved part: supply a .par output path.");
            // Saving in place is a write too; retain the same output boundary and overwrite rule.
            target = Paths.Target(current, [".par"], overwrite);
        }
        else
        {
            target = Paths.Target(path, [".par"], overwrite);
        }
        WithoutAlerts(() =>
        {
            if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
                doc.Save();
            else
                doc.SaveAs(target);
        });
        return Merge(Info(doc, documentId), OutputPaths.Verify(target));
    }

    public JsonObject ExportStep(string documentId, string path, bool overwrite = false)
    {
        var doc = Doc(documentId, part: true);
        if ((double)Geometry(documentId)["total_volume_mm3"]! <= 0)
            throw new EdgeError("STEP export requires a nonempty solid.");
        var target = Paths.Target(path, [".stp", ".step"], overwrite);
        var staged = Staged(target, Path.GetExtension(target));
        string originalName = Str(doc.FullName);
        try
        {
            WithoutAlerts(() => doc.SaveCopyAs(staged));
            OutputPaths.Verify(staged);
            if (Str(doc.FullName) != originalName)
                throw new EdgeError("Export unexpectedly changed the native document path. Inspect it.");
            // Without overwrite, Move refuses a file that appeared during translation.
            File.Move(staged, target, overwrite);
        }
        finally
        {
            File.Delete(staged);
        }
        return Merge(new JsonObject { ["document_id"] = documentId, ["format"] = "STEP" }, OutputPaths.Verify(target));
    }

    private static string Staged(string target, string extension) =>
        Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileNameWithoutExtension(target)}-{NewId()}{extension}");

    public JsonObject SetView(string documentId, string orientation = "Iso")
    {
        var doc = Doc(documentId, part: true);
        doc.Activate();
        var view = RequireApp().ActiveWindow.View;
        view.ApplyNamedView(orientation);
        view.Fit();
        return new JsonObject { ["document_id"] = documentId, ["orientation"] = orientation };
    }

    public JsonObject CaptureViewport(string documentId, string path, int width = 1280, int height = 960, bool overwrite = false)
    {
        var doc = Doc(documentId, part: true);
        var target = Paths.Target(path, [".png"], overwrite);
        var staged = Staged(target, ".bmp");
        var converted = Path.ChangeExtension(staged, ".png");
        doc.Activate();
        try
        {
            // Solid Edge 2020 silently appends .tif to a requested .png. BMP is supported natively.
            RequireApp().ActiveWindow.View.SaveAsImage(staged, width, height);
            OutputPaths.Verify(staged);
            using (var image = new Bitmap(staged))
                image.Save(converted, ImageFormat.Png);
            File.Move(converted, target, overwrite);
        }
        finally
        {
            File.Delete(staged);
            File.Delete(converted);
        }
        return Merge(new JsonObject { ["document_id"] = documentId }, OutputPaths.Verify(target));
    }

    public JsonObject CloseDocument(string documentId, bool discardChanges = false)
    {
        var doc = Doc(documentId);
        if (Convert.ToBoolean(doc.Dirty) && !discardChanges)
            throw new EdgeError("Document has unsaved changes. Save it or set discard_changes=true.");
        WithoutAlerts(() => doc.Close(false));
        Forget(documentId);
        return new JsonObject { ["document_id"] = documentId, ["closed"] = true };
    }
}
