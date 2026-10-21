namespace SolidEdgeMcp.Commands;

public sealed class Extrude() : SeCommand("extrude", CommandKind.Write,
    "Extrude a closed profile and verify volume change. Consumes the profile on completion.",
    Text("document_id"), Text("profile_id"), Length("distance_mm"), Choice("operation", "add", "add", "cut"),
    Choice("direction", "normal", "normal", "reverse"))
{
    public override CommandOutput Execute(CommandCall c)
    {
        var a = c.Args;
        var edge = c.Edge;
        var documentId = a.Text("document_id");
        var profileId = a.Text("profile_id");
        var distanceMm = a.Number("distance_mm");
        var operation = a.Text("operation");
        var doc = edge.Doc(documentId, part: true);
        if (!edge.Profiles.TryGetValue(profileId, out var entry) || entry.DocumentId != documentId)
            throw new EdgeError("Profile is unknown, consumed, or belongs to a different document.");
        if (Int(doc.ModelingMode) != 2)
            throw new EdgeError("Extrusion requires ordered modeling mode.");
        int models = Int(doc.Models.Count);
        if (models > 1)
            throw new EdgeError("This version supports extrusion in single-body parts only.");
        if (operation == "cut" && models == 0)
            throw new EdgeError("Create a base solid before cutting.");
        dynamic profile = entry.Profile;
        // Profile.End() puts nested loops (holes) into sibling profiles of the same set; extrude them together.
        var set = profile.Parent;
        int profileCount = Int(set.Profiles.Count);
        var members = new DispatchWrapper[profileCount];
        for (var i = 0; i < profileCount; i++)
            members[i] = new DispatchWrapper((object)set.Profiles.Item(i + 1));
        doc.Activate();
        var geometry = new JsonObject { ["document_id"] = documentId };
        var before = (double)c.Run("geometry", geometry)["total_volume_mm3"]!;
        var side = a.Text("direction") == "normal" ? 2 : 1;
        // Part.tlb declares ProfileArray as SAFEARRAY(IDispatch*)*; DispatchWrapper[] marshals as exactly that.
        Array profiles = members;
        if (models == 0)
        {
            doc.Models.AddFiniteExtrudedProtrusion(profileCount, ref profiles, side, distanceMm / 1000);
        }
        else
        {
            var model = doc.Models.Item(1);
            var collection = operation == "add" ? model.ExtrudedProtrusions : model.ExtrudedCutouts;
            collection.AddFiniteMulti(profileCount, ref profiles, side, distanceMm / 1000);
        }
        // A returned COM call can still have created an invalid/empty feature.
        edge.Profiles.Remove(profileId);
        var after = c.Run("geometry", geometry);
        var delta = (double)after["total_volume_mm3"]! - before;
        var expectedSign = operation == "add" ? 1 : -1;
        if (expectedSign * delta <= Math.Max(1e-8, Math.Abs(before) * 1e-10))
            throw new EdgeError(
                "Extrusion call completed but the expected volume change was not verified. "
                + "Inspect the part and feature tree; the call may have left an invalid feature.");
        for (var i = 1; i <= profileCount; i++)
            set.Profiles.Item(i).Visible = false;
        return new JsonObject { ["document_id"] = documentId, ["operation"] = operation, ["geometry"] = after };
    }
}

public sealed class CreateBox() : SeCommand("create_box", CommandKind.Create,
    "Create a box in an EMPTY part; width/depth follow plane axes, height its normal.",
    Text("document_id"), Length("width_mm"), Length("depth_mm"), Length("height_mm"), Index("plane_index"))
{
    public override CommandOutput Execute(CommandCall c)
    {
        var a = c.Args;
        var documentId = a.Text("document_id");
        if (Int(c.Edge.Doc(documentId, part: true).Models.Count) != 0)
            throw new EdgeError("create_box requires an empty part. Use a profile and extrude to add.");
        var sketch = c.Run("create_rectangle", new JsonObject
        {
            ["document_id"] = documentId,
            ["width_mm"] = a.Number("width_mm"),
            ["height_mm"] = a.Number("depth_mm"),
            ["plane_index"] = a.Integer("plane_index"),
        });
        return c.Run("extrude", new JsonObject
        {
            ["document_id"] = documentId,
            ["profile_id"] = sketch["profile_id"]!.DeepClone(),
            ["distance_mm"] = a.Number("height_mm"),
        });
    }
}

public sealed class CreateCylinder() : SeCommand("create_cylinder", CommandKind.Create,
    "Create a cylinder in an EMPTY part, centered at the plane origin.",
    Text("document_id"), Length("radius_mm"), Length("height_mm"), Index("plane_index"))
{
    public override CommandOutput Execute(CommandCall c)
    {
        var a = c.Args;
        var documentId = a.Text("document_id");
        if (Int(c.Edge.Doc(documentId, part: true).Models.Count) != 0)
            throw new EdgeError("create_cylinder requires an empty part.");
        var sketch = c.Run("create_circle", new JsonObject
        {
            ["document_id"] = documentId,
            ["radius_mm"] = a.Number("radius_mm"),
            ["plane_index"] = a.Integer("plane_index"),
        });
        return c.Run("extrude", new JsonObject
        {
            ["document_id"] = documentId,
            ["profile_id"] = sketch["profile_id"]!.DeepClone(),
            ["distance_mm"] = a.Number("height_mm"),
        });
    }
}
