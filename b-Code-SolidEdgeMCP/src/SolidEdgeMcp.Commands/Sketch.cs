namespace SolidEdgeMcp.Commands;

public sealed class ReferencePlanes() : SeCommand("reference_planes", CommandKind.Read,
    "List actual reference plane names and 1-based indices in this part.",
    Text("document_id"))
{
    public override CommandOutput Execute(CommandCall c)
    {
        var doc = c.Edge.Doc(c.Args.Text("document_id"), part: true);
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
}

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
    "Draw lines, arcs and circles on a reference plane as one profile: closed outer loops, inner loops become holes. "
    + "The description is checked first (open ends, branches, crossings) and nothing is drawn if it has defects. "
    + "Returns the geometry read back from Solid Edge, loops with area/centroid/role, net area, issues, and an SVG of the result.",
    Text("document_id"), Index("plane_index"), Structured("entities", "array", Sketching.Sketch.EntitiesSchema()))
{
    public override CommandOutput Execute(CommandCall c)
    {
        var documentId = c.Args.Text("document_id");
        var planeIndex = c.Args.Integer("plane_index");
        var elements = Sketching.Sketch.Parse(c.Args.Element("entities"));
        var planned = new Sketching.SketchAnalysis(elements);
        if (!planned.Valid)
            throw Sketching.Sketches.Rejected("Sketch description has defects; nothing was drawn.", planned);

        var profile = c.Edge.NewProfile(documentId, planeIndex);
        dynamic set = profile.Parent;
        Sketching.SketchAnalysis drawn;
        int code;
        double deviation;
        try
        {
            Sketching.SketchCom.Draw(profile, elements);
            code = Int(profile.End(Sketching.SketchCom.Validation));
            // End() splits nested loops into separate profiles of the same set; read them all.
            var actual = Sketching.Sketches.ReadSet(set, elements);
            drawn = new Sketching.SketchAnalysis(actual, Sketching.Sketches.ReadTolerance);
            deviation = Sketching.SketchCom.Deviation(elements, actual);
        }
        catch (Exception exc) when (exc is not EdgeError)
        {
            Sketching.Sketches.TryDelete(set);
            throw new EdgeError($"Drawing failed and the partial sketch was removed: {exc.Message}");
        }
        var areaDrift = Math.Abs(drawn.NetArea - planned.NetArea);
        if (code != 0 || !drawn.Valid || deviation > 1e-3 || areaDrift > Math.Max(1e-6, Math.Abs(planned.NetArea) * 1e-6))
        {
            Sketching.Sketches.TryDelete(set);
            throw Sketching.Sketches.Rejected(
                $"Solid Edge result differs from the description (validation code {code}, deviation {Sketching.Sketch.Format(deviation)} mm, "
                + $"area drift {Sketching.Sketch.Format(areaDrift)} mm2); the sketch was removed.", drawn);
        }

        var profileId = SolidEdgeBackend.NewId();
        c.Edge.Profiles[profileId] = (documentId, (object)profile);
        var result = new JsonObject
        {
            ["document_id"] = documentId,
            ["profile_id"] = profileId,
            ["sketch_index"] = Sketching.Sketches.IndexOf(c.Edge.Doc(documentId, part: true), set),
            ["plane_index"] = planeIndex,
            ["validation_code"] = code,
            ["max_deviation_mm"] = Sketching.Sketch.Round(deviation),
        };
        return Merge(result, Sketching.SketchReport.Json(drawn));
    }
}

public sealed class ViewSketch() : SeCommand("view_sketch", CommandKind.Read,
    "Read a sketch back from Solid Edge: by profile_id (until extruded) or 1-based sketch_index. "
    + "Without either, list the part's sketches. Returns structured geometry and an SVG; png=true also returns a PNG image.",
    Text("document_id"), Integer("sketch_index", 0, 0, 100000), OptionalText("profile_id"), Flag("png", false))
{
    public override CommandOutput Execute(CommandCall c)
    {
        var documentId = c.Args.Text("document_id");
        var doc = c.Edge.Doc(documentId, part: true);
        var index = c.Args.Integer("sketch_index");
        if (c.Args.OptionalText("profile_id") is { } profileId)
        {
            if (!c.Edge.Profiles.TryGetValue(profileId, out var entry) || entry.DocumentId != documentId)
                throw new EdgeError("Profile is unknown, consumed by an extrusion, or in another document. Use sketch_index.");
            index = Sketching.Sketches.IndexOf(doc, ((dynamic)entry.Profile).Parent);
        }
        int count = Int(doc.ProfileSets.Count);
        if (index == 0)
        {
            var list = new JsonArray();
            for (var i = 1; i <= count; i++)
            {
                var analysis = new Sketching.SketchAnalysis(Sketching.Sketches.ReadSet(doc.ProfileSets.Item(i)), Sketching.Sketches.ReadTolerance);
                list.Add(new JsonObject
                {
                    ["sketch_index"] = i,
                    ["entity_count"] = analysis.Elements.Count,
                    ["closed_loops"] = analysis.Loops.Count(l => l.Closed),
                    ["net_area_mm2"] = Sketching.Sketch.Round(analysis.NetArea),
                    ["issue_count"] = analysis.Issues.Count,
                });
            }
            return new JsonObject { ["document_id"] = documentId, ["sketches"] = list };
        }
        if (index > count)
            throw new EdgeError($"sketch_index must be between 1 and {count}.");
        var elements = Sketching.Sketches.ReadSet(doc.ProfileSets.Item(index));
        if (elements.Count == 0)
            throw new EdgeError("This sketch has no lines, arcs or circles.");
        var drawn = new Sketching.SketchAnalysis(elements, Sketching.Sketches.ReadTolerance);
        var result = Merge(new JsonObject { ["document_id"] = documentId, ["sketch_index"] = index }, Sketching.SketchReport.Json(drawn));
        return c.Args.Flag("png") ? new CommandOutput(result, Sketching.SketchReport.Png(drawn)) : result;
    }
}
