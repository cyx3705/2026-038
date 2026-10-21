namespace SolidEdgeMcp.Commands;

public sealed class ReferencePlanes() : SeCommand("reference_planes", CommandKind.Read,
    "List actual reference plane names and 1-based indices in this part.",
    Text("document_id"))
{
    public override CommandOutput Execute(CommandContext c)
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
    public override CommandOutput Execute(CommandContext c)
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
        return (JsonObject)c.Edge.FinishProfile(documentId, profile);
    }
}

public sealed class CreateCircle() : SeCommand("create_circle", CommandKind.Create,
    "Create a closed circle profile. x_mm/y_mm specify its center on the local plane.",
    Text("document_id"), Length("radius_mm"), Coordinate("x_mm"), Coordinate("y_mm"), Index("plane_index"))
{
    public override CommandOutput Execute(CommandContext c)
    {
        var a = c.Args;
        var documentId = a.Text("document_id");
        var profile = c.Edge.NewProfile(documentId, a.Integer("plane_index"));
        profile.Circles2d.AddByCenterRadius(a.Number("x_mm") / 1000, a.Number("y_mm") / 1000, a.Number("radius_mm") / 1000);
        return (JsonObject)c.Edge.FinishProfile(documentId, profile);
    }
}
