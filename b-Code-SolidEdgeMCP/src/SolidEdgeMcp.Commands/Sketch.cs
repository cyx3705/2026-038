using System.Text.Json;
using SolidEdgeMcp.Commands.Sketching;

namespace SolidEdgeMcp.Commands;

public sealed class CreateRectangle() : SeCommand("create_rectangle", CommandKind.Create,
    "Create a rectangle from its lower-left corner, using local plane coordinates.",
    Text("document_id"), Length("width_mm"), Length("height_mm"), Coordinate("x_mm"), Coordinate("y_mm"), Index("plane_index"))
{
    public override CommandOutput Execute(CommandCall c)
    {
        var a = c.Args;
        var documentId = a.Text("document_id");
        var profile = c.Edge.NewProfile(documentId, a.Integer("plane_index"));
        double x = a.Number("x_mm") / 1000, y = a.Number("y_mm") / 1000;
        double w = a.Number("width_mm") / 1000, h = a.Number("height_mm") / 1000;
        (double X, double Y)[] points = [(x, y), (x + w, y), (x + w, y + h), (x, y + h)];
        var lines = new object[4];
        for (var i = 0; i < 4; i++)
        {
            var (p, q) = (points[i], points[(i + 1) % 4]);
            lines[i] = profile.Lines2d.AddBy2Points(p.X, p.Y, q.X, q.Y);
        }
        // Coincident endpoints must be constrained; identical coordinates alone can fail End().
        for (var i = 0; i < 4; i++)
        {
            // fwksupp.tlb KeypointIndexConstants: igLineEnd=1, igLineStart=0.
            // KeyPointType's 1/2 flags are a different enum; using them deforms the rectangle.
            profile.Relations2d.AddKeypoint(lines[i], 1, lines[(i + 1) % 4], 0);
        }
        return (JsonObject)EdgeOps.FinishProfile(c.Edge, documentId, profile);
    }
}

public sealed class CreateCircle() : SeCommand("create_circle", CommandKind.Create,
    "Create a closed circle profile. x_mm/y_mm specify its center on the local plane.",
    Text("document_id"), Length("radius_mm"), Coordinate("x_mm"), Coordinate("y_mm"), Index("plane_index"))
{
    public override CommandOutput Execute(CommandCall c)
    {
        var a = c.Args;
        var documentId = a.Text("document_id");
        var profile = c.Edge.NewProfile(documentId, a.Integer("plane_index"));
        profile.Circles2d.AddByCenterRadius(a.Number("x_mm") / 1000, a.Number("y_mm") / 1000, a.Number("radius_mm") / 1000);
        return (JsonObject)EdgeOps.FinishProfile(c.Edge, documentId, profile);
    }
}

public sealed class DrawSketch() : SeCommand("sketch", CommandKind.Create,
    "Draw a sketch on a reference plane from a description: entities (lines, arcs, circles, ellipses, splines, points, "
    + "rectangles, polygons, polylines, slots), corners (fillet/chamfer), relations, driving dimensions (named ones become "
    + "variables), model references and a revolve axis. Closed outer loops make the profile, inner loops holes. The description "
    + "is checked first (open ends, branches, crossings, relations that do not hold, dimension values) and nothing is drawn "
    + "if it has defects. Returns the geometry read back from Solid Edge, loops with area/centroid/role, relations, dimensions "
    + "with their variables, whether the sketch is fully constrained, and an SVG. dry_run=true only checks and draws the SVG.",
    Text("document_id"), Index("plane_index"),
    Structured("entities", "array", SketchSchema.Entities()),
    OptionalStructured("corners", "array", SketchSchema.Corners()),
    OptionalStructured("relations", "array", SketchSchema.Relations()),
    OptionalStructured("dimensions", "array", SketchSchema.Dimensions()),
    OptionalStructured("references", "array", SketchSchema.References()),
    OptionalText("axis"),
    Flag("dry_run", false))
{
    public override CommandOutput Execute(CommandCall c)
    {
        var description = new JsonObject();
        foreach (var section in new[] { "entities", "corners", "relations", "dimensions", "references" })
        {
            if (c.Args.OptionalElement(section) is { } value)
                description[section] = JsonNode.Parse(value.GetRawText());
        }
        if (c.Args.OptionalText("axis") is { } axis)
            description["axis"] = axis;
        var spec = SketchSpec.Read(JsonSerializer.SerializeToElement(description));
        if (c.Args.Flag("dry_run"))
            return Sketches.DryRun(spec);
        return Sketches.Build(c.Edge, c.Args.Text("document_id"), c.Args.Integer("plane_index"), spec);
    }
}

public sealed class ViewSketch() : SeCommand("view_sketch", CommandKind.Read,
    "Read a sketch back from Solid Edge: by profile_id (until extruded) or 1-based sketch_index. "
    + "Without either, list the part's sketches. Returns structured geometry, relations and dimensions (with their variables) "
    + "for sketches this server drew, whether it is fully constrained, and an SVG; png=true also returns a PNG image.",
    Text("document_id"), Integer("sketch_index", 0, 0, 100000), OptionalText("profile_id"), Flag("png", false))
{
    public override CommandOutput Execute(CommandCall c)
    {
        var documentId = c.Args.Text("document_id");
        var doc = c.Edge.Doc(documentId, part: true);
        var index = c.Args.Integer("sketch_index");
        if (c.Args.OptionalText("profile_id") is { } profileId)
            index = Sketches.IndexOf(doc, SketchLookup.Set(c.Edge, documentId, profileId));
        int count = Int(doc.ProfileSets.Count);
        if (index == 0)
        {
            var list = new JsonArray();
            for (var i = 1; i <= count; i++)
            {
                var set = doc.ProfileSets.Item(i);
                Sketches.Stored? stored = Sketches.Find(c.Edge, documentId, set);
                List<Element> elements = Sketches.ReadSet(set, stored?.Ids, stored?.Spec);
                var analysis = new SketchAnalysis(elements, Sketches.ReadTolerance);
                list.Add(new JsonObject
                {
                    ["sketch_index"] = i,
                    ["profile_id"] = stored is not null && c.Edge.Profiles.ContainsKey(stored.ProfileId) ? stored.ProfileId : null,
                    ["entity_count"] = analysis.Elements.Count,
                    ["closed_loops"] = analysis.Loops.Count(l => l.Closed),
                    ["net_area_mm2"] = Sketch.Round(analysis.NetArea),
                    ["issue_count"] = analysis.Issues.Count,
                });
            }
            return new JsonObject { ["document_id"] = documentId, ["sketches"] = list };
        }
        if (index > count)
            throw new EdgeError($"sketch_index must be between 1 and {count}.");
        var result = SketchLookup.Report(c.Edge, documentId, doc.ProfileSets.Item(index), out SketchAnalysis drawn);
        result["document_id"] = documentId;
        result["sketch_index"] = index;
        return c.Args.Flag("png") ? new CommandOutput(result, SketchReport.Png(drawn)) : result;
    }
}

public sealed class SketchDimensions() : SeCommand("sketch_dimensions", CommandKind.Write,
    "List a sketch's dimensions (variable, value in mm or degrees, driving), or change driving ones with set "
    + "{variable: value}. A change recomputes the part, so a sketch already used by a feature reshapes the solid; the "
    + "sketch is read back and verified, and on any failure the old values are restored.",
    Text("document_id"), Integer("sketch_index", 0, 0, 100000), OptionalText("profile_id"),
    OptionalStructured("set", "object", new JsonObject
    {
        ["description"] = "variable name (or system name) to its new value in mm or degrees",
        ["additionalProperties"] = new JsonObject { ["type"] = "number" },
    }))
{
    public override CommandOutput Execute(CommandCall c)
    {
        var documentId = c.Args.Text("document_id");
        var doc = c.Edge.Doc(documentId, part: true);
        var index = c.Args.Integer("sketch_index");
        object set;
        if (c.Args.OptionalText("profile_id") is { } profileId)
            set = SketchLookup.Set(c.Edge, documentId, profileId);
        else if (index > 0 && index <= Int(doc.ProfileSets.Count))
            set = doc.ProfileSets.Item(index);
        else
            throw new EdgeError("Give profile_id, or a sketch_index from view_sketch.");
        var changes = new List<(string Name, double Value)>();
        if (c.Args.OptionalElement("set") is { } values)
        {
            foreach (var property in values.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Number || !double.IsFinite(property.Value.GetDouble()))
                    throw new EdgeError($"set.{property.Name} must be a number.");
                changes.Add((property.Name, property.Value.GetDouble()));
            }
        }
        if (changes.Count > 0)
            SketchLookup.Change(c.Edge, documentId, doc, set, changes);
        var result = SketchLookup.Report(c.Edge, documentId, set, out _);
        result["document_id"] = documentId;
        result["sketch_index"] = Sketches.IndexOf(doc, set);
        if (Int(doc.Models.Count) > 0)
            result["geometry"] = c.Run("geometry", new JsonObject { ["document_id"] = documentId });
        return result;
    }
}

public sealed class EditSketch() : SeCommand("edit_sketch", CommandKind.Write,
    "Edit a sketch this server drew and that no feature has used yet (profile_id): move, rotate, scale, mirror, offset, "
    + "delete, add, split, trim, extend, corner. The edits are computed exactly on the description, which is then redrawn "
    + "and verified like a new sketch; the profile_id stays the same. Relations and dimensions that no longer apply are "
    + "dropped and listed. dry_run=true returns the edited description's check and SVG without touching Solid Edge.",
    Text("document_id"), Text("profile_id"), Structured("operations", "array", SketchSchema.Operations()), Flag("dry_run", false))
{
    public override CommandOutput Execute(CommandCall c)
    {
        var documentId = c.Args.Text("document_id");
        var profileId = c.Args.Text("profile_id");
        var doc = c.Edge.Doc(documentId, part: true);
        if (!c.Edge.Profiles.TryGetValue(profileId, out var entry) || entry.DocumentId != documentId)
            throw new EdgeError("Profile is unknown, already used by a feature, or in another document; only unused sketches can be edited.");
        var stored = Sketches.Load(c.Edge, profileId)
            ?? throw new EdgeError("This profile was not drawn by the sketch command, so there is no description to edit.");
        var outcome = SketchEdit.Apply(stored.Spec, c.Args.Element("operations"));
        JsonObject result;
        if (c.Args.Flag("dry_run"))
        {
            result = Sketches.DryRun(outcome.Spec);
        }
        else
        {
            // The old sketch stays until the new one is verified, so its variable names must step aside.
            var renamed = SketchCom.RenameDimensions((object)doc, stored.Set, name => $"zzEdit_{name}");
            try
            {
                result = Sketches.Build(c.Edge, documentId, stored.PlaneIndex, outcome.Spec, profileId);
            }
            catch (Exception)
            {
                foreach (var (dimension, name) in renamed)
                    doc.Variables.PutName(dimension, name);
                throw;
            }
            Sketches.TryDelete(stored.Set);
            result["sketch_index"] = Sketches.IndexOf(doc, ((dynamic)c.Edge.Profiles[profileId].Profile).Parent);
        }
        result["applied"] = outcome.Applied;
        result["dropped"] = outcome.Dropped;
        result["description"] = outcome.Spec.ToJson();
        return result;
    }
}

/// <summary>Finding, reporting and changing sketches for the commands above.</summary>
public static class SketchLookup
{
    public static object Set(SolidEdgeBackend edge, string documentId, string profileId)
    {
        if (edge.Profiles.TryGetValue(profileId, out var entry) && entry.DocumentId == documentId)
            return ((dynamic)entry.Profile).Parent;
        if (Sketches.Load(edge, profileId) is { } stored && stored.DocumentId == documentId)
            return stored.Set;
        throw new EdgeError("Profile is unknown or in another document. Use sketch_index from view_sketch.");
    }

    /// <summary>Geometry, relations and dimensions of a set as Solid Edge has it now.</summary>
    public static JsonObject Report(SolidEdgeBackend edge, string documentId, object set, out SketchAnalysis analysis)
    {
        var stored = Sketches.Find(edge, documentId, set);
        List<Element> elements = Sketches.ReadSet(set, stored?.Ids, stored?.Spec);
        if (elements.Count == 0)
            throw new EdgeError("This sketch has no 2D geometry.");
        analysis = new SketchAnalysis(elements, Sketches.ReadTolerance, stored?.Spec, stored?.References);
        var report = SketchReport.Json(analysis);
        var dimensions = SketchCom.ReadDimensions(set);
        var named = new HashSet<string>(StringComparer.Ordinal);
        if (stored is not null && report["dimensions"] is JsonArray entries)
        {
            for (var i = 0; i < entries.Count && i < stored.DimensionNames.Count; i++)
            {
                var match = dimensions.FirstOrDefault(d => d.SystemName == stored.DimensionNames[i]);
                if (match.Com is null)
                    continue;
                named.Add(match.SystemName);
                entries[i]!["variable"] = match.Name;
                entries[i]!["se_value"] = Sketch.Round(match.Value);
                // Solid Edge's value must be what the geometry measures; otherwise the dimension no longer holds.
                if (analysis.DimensionValues[i].Value is { } measured && Math.Abs(measured - match.Value) > Math.Max(1e-5, Math.Abs(measured) * 1e-7))
                {
                    report["issues"]!.AsArray().Add(new JsonObject
                    {
                        ["type"] = "dimension_unsatisfied",
                        ["dimension"] = stored.Spec.Dimensions[i].Json(),
                        ["detail"] = $"Solid Edge holds {Sketch.Format(match.Value)} but the geometry measures {Sketch.Format(measured)}",
                    });
                }
            }
        }
        var others = dimensions.Where(d => !named.Contains(d.SystemName)).ToList();
        if (others.Count > 0)
        {
            report["other_dimensions"] = new JsonArray(others.Select(d => (JsonNode)new JsonObject
            {
                ["variable"] = d.Name,
                ["value"] = Sketch.Round(d.Value),
                ["dimension_type"] = d.Type,
                ["driving"] = d.Driving,
            }).ToArray());
        }
        var result = new JsonObject();
        if (stored is not null)
            result["profile_id"] = edge.Profiles.ContainsKey(stored.ProfileId) ? stored.ProfileId : null;
        try
        {
            result["fully_constrained"] = !Convert.ToBoolean(((dynamic)set).IsUnderDefined);
        }
        catch (Exception)
        {
            // Older sets may not answer; the rest of the report stands.
        }
        return Merge(result, report);
    }

    /// <summary>
    /// Set driving dimensions, recompute, and verify the values and the sketch; restore the old
    /// values if anything fails.
    /// </summary>
    public static void Change(SolidEdgeBackend edge, string documentId, dynamic doc, object set, List<(string Name, double Value)> changes)
    {
        var dimensions = SketchCom.ReadDimensions(set);
        var targets = changes.Select(change =>
        {
            var found = dimensions.Where(d => d.Name == change.Name || d.SystemName == change.Name).ToList();
            if (found.Count != 1)
                throw new EdgeError($"No single dimension named {change.Name}; see the variable names this command lists.");
            if (!found[0].Driving)
                throw new EdgeError($"Dimension {change.Name} is not driving; it only measures.");
            return (Dimension: found[0], change.Value);
        }).ToList();
        void Apply(IEnumerable<(object Com, int Type, double Value)> values)
        {
            foreach (var (com, type, value) in values)
                ((dynamic)com).Value = type is 3 or 7 or 11 ? Sketch.Rad(value) : value / 1000;
            doc.Recompute();
        }
        var previous = targets.Select(t => (t.Dimension.Com, t.Dimension.Type, t.Dimension.Value)).ToList();
        try
        {
            Apply(targets.Select(t => (t.Dimension.Com, t.Dimension.Type, t.Value)));
            foreach (var (dimension, value) in targets)
            {
                var actual = SketchCom.Value(dimension.Com);
                if (Math.Abs(actual - value) > Math.Max(1e-6, Math.Abs(value) * 1e-7))
                    throw new EdgeError($"Dimension {dimension.Name} reads {Sketch.Format(actual)} after the change, not {Sketch.Format(value)}.");
            }
            var stored = Sketches.Find(edge, documentId, set);
            var report = Report(edge, documentId, set, out var analysis);
            if (report["issues"] is JsonArray { Count: > 0 } issues)
                throw new EdgeError($"The new values break the sketch; the old values were restored.\n{issues.ToJsonString(Sketches.Plain)}");
            var elements = analysis.Elements;
            if (stored is not null)
            {
                // The description follows the geometry, so a later edit starts from the new shape.
                var spec = stored.Spec.Clone();
                spec.Elements.Clear();
                spec.Elements.AddRange(elements.Where(e => !e.External).Select(e => e with { SeName = null }));
                Sketches.Update(edge, stored, spec);
            }
        }
        catch (Exception exc)
        {
            try
            {
                Apply(previous);
            }
            catch (Exception)
            {
                // Report the original failure; the part may need a manual check.
            }
            throw exc as EdgeError ?? new EdgeError($"Changing dimensions failed and the old values were restored: {exc.Message}");
        }
    }
}
