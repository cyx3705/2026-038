using SolidEdgeMcp.Commands.Sketching;

namespace SolidEdgeMcp.Commands;

/// <summary>Shared pieces of the profile-consuming features.</summary>
public static class FeatureKit
{
    /// <summary>Every profile of the set (End() puts holes in sibling profiles), as Part.tlb's SAFEARRAY(IDispatch*).</summary>
    public static Array Members(dynamic set)
    {
        int count = Int(set.Profiles.Count);
        var members = new DispatchWrapper[count];
        for (var i = 0; i < count; i++)
            members[i] = new DispatchWrapper((object)set.Profiles.Item(i + 1));
        return members;
    }

    /// <summary>The sketch as Solid Edge has it now, when this server drew it.</summary>
    public static SketchAnalysis? Drawn(SolidEdgeBackend edge, string profileId, object set)
    {
        if (Sketches.Load(edge, profileId) is not { } stored)
            return null;
        List<Element> elements = Sketches.ReadSet(set, stored.Ids, stored.Spec);
        return new SketchAnalysis(elements, Sketches.ReadTolerance, stored.Spec, stored.References);
    }

    /// <summary>A first feature must make exactly the expected volume; later ones change it in the right direction.</summary>
    public static void Verify(double before, double after, double? expected, bool first, int sign, bool approximate)
    {
        var delta = after - before;
        if (sign * delta <= Math.Max(1e-8, Math.Abs(before) * 1e-10))
            throw new EdgeError(
                "Feature call completed but the expected volume change was not verified. "
                + "Inspect the part and feature tree; the call may have left an invalid feature.");
        if (expected is not { } want)
            return;
        var slack = Math.Max(1e-6, want * (approximate ? 1e-2 : 1e-6));
        if (first && Math.Abs(delta - want) > slack)
            throw new EdgeError($"The new solid has {Sketch.Format(delta)} mm3, not the {Sketch.Format(want)} mm3 the sketch implies. Inspect the part.");
        if (!first && Math.Abs(delta) > want + slack)
            throw new EdgeError($"The volume changed by {Sketch.Format(delta)} mm3, more than the {Sketch.Format(want)} mm3 the sketch can sweep.");
    }
}

public sealed class Extrude() : SeCommand("extrude", CommandKind.Write,
    "Extrude a closed profile and verify the volume change (exactly net area x depth for a first solid drawn by sketch). "
    + "Consumes the profile on completion.",
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
        var set = profile.Parent;
        int profileCount = Int(set.Profiles.Count);
        SketchAnalysis? sketch = FeatureKit.Drawn(edge, profileId, set);
        doc.Activate();
        var geometry = new JsonObject { ["document_id"] = documentId };
        var before = (double)c.Run("geometry", geometry)["total_volume_mm3"]!;
        var side = a.Text("direction") == "normal" ? 2 : 1;
        // Part.tlb declares ProfileArray as SAFEARRAY(IDispatch*)*; DispatchWrapper[] marshals as exactly that.
        Array profiles = FeatureKit.Members(set);
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
        double? expected = sketch is null ? null : Math.Abs(sketch.NetArea) * distanceMm;
        FeatureKit.Verify(before, (double)after["total_volume_mm3"]!, expected, models == 0, operation == "add" ? 1 : -1, sketch?.Approximate == true);
        for (var i = 1; i <= profileCount; i++)
            set.Profiles.Item(i).Visible = false;
        return new JsonObject
        {
            ["document_id"] = documentId,
            ["operation"] = operation,
            ["expected_volume_mm3"] = expected,
            ["geometry"] = after,
        };
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

public sealed class Revolve() : SeCommand("revolve", CommandKind.Write,
    "Revolve a sketch drawn with axis=<line id> about that line by angle_deg (default 360) and verify the volume change: "
    + "exactly area x centroid path (Pappus) for a first solid. Consumes the profile on completion.",
    Text("document_id"), Text("profile_id"), new ToolParameter("angle_deg", "number", false, 360, ExclusiveMinimum: 0, Maximum: 360),
    Choice("operation", "add", "add", "cut"), Choice("direction", "normal", "normal", "reverse"))
{
    public override CommandOutput Execute(CommandCall c)
    {
        var a = c.Args;
        var edge = c.Edge;
        var documentId = a.Text("document_id");
        var profileId = a.Text("profile_id");
        var operation = a.Text("operation");
        var angle = Sketch.Rad(a.Number("angle_deg"));
        var doc = edge.Doc(documentId, part: true);
        if (!edge.Profiles.TryGetValue(profileId, out var entry) || entry.DocumentId != documentId)
            throw new EdgeError("Profile is unknown, consumed, or belongs to a different document.");
        if (!edge.State.TryGetValue(Sketches.AxisKey(profileId), out var axis))
            throw new EdgeError("This profile has no revolve axis. Draw it with sketch and axis=<id of a line>.");
        int models = Int(doc.Models.Count);
        if (models > 1)
            throw new EdgeError("This version supports single-body parts only.");
        if (operation == "cut" && models == 0)
            throw new EdgeError("Create a base solid before cutting.");
        dynamic profile = entry.Profile;
        var set = profile.Parent;
        SketchAnalysis sketch = FeatureKit.Drawn(edge, profileId, set)
            ?? throw new EdgeError("The sketch description is not available; draw it again with sketch.");
        var line = sketch.Elements.OfType<LineEl>().Single(e => e.Id == sketch.Spec!.Axis);
        // Pappus: every closed loop sweeps its area along the circle its centroid travels.
        var d = line.Direction;
        double Side(Pt p) => Pt.Cross(d, p - line.Start);
        var sides = sketch.Loops.Where(l => l.Closed).SelectMany(SketchAnalysis.Polygon).Select(Side).ToList();
        if (sides.Min() < -1e-6 && sides.Max() > 1e-6)
            throw new EdgeError($"The profile lies on both sides of the axis {line.Id}; a revolve would intersect itself.");
        var expected = angle * sketch.Loops.Where(l => l.Closed)
            .Sum(l => (l.Depth % 2 == 0 ? 1 : -1) * Math.Abs(l.SignedArea) * Math.Abs(Side(l.Centroid)));
        doc.Activate();
        var geometry = new JsonObject { ["document_id"] = documentId };
        var before = (double)c.Run("geometry", geometry)["total_volume_mm3"]!;
        var side = a.Text("direction") == "normal" ? 2 : 1;
        int profileCount = Int(set.Profiles.Count);
        Array profiles = FeatureKit.Members(set);
        if (models == 0)
        {
            doc.Models.AddFiniteRevolvedProtrusion(profileCount, ref profiles, axis, side, angle);
        }
        else
        {
            var model = doc.Models.Item(1);
            var collection = operation == "add" ? model.RevolvedProtrusions : model.RevolvedCutouts;
            collection.AddFiniteMulti(profileCount, ref profiles, axis, side, angle);
        }
        edge.Profiles.Remove(profileId);
        var after = c.Run("geometry", geometry);
        FeatureKit.Verify(before, (double)after["total_volume_mm3"]!, expected, models == 0, operation == "add" ? 1 : -1, sketch.Approximate);
        for (var i = 1; i <= profileCount; i++)
            set.Profiles.Item(i).Visible = false;
        return new JsonObject
        {
            ["document_id"] = documentId,
            ["operation"] = operation,
            ["angle_deg"] = a.Number("angle_deg"),
            ["expected_volume_mm3"] = Sketch.Round(expected),
            ["geometry"] = after,
        };
    }
}
