namespace SolidEdgeMcp.Commands;

/// <summary>Reference planes and body faces: where sketches can go and what they can refer to.</summary>
public static class Frames
{
    public static double[] Vector(dynamic target, string method)
    {
        Array values = new double[3];
        switch (method)
        {
            case "GetNormal":
                target.GetNormal(ref values);
                break;
            case "GetRootPoint":
                target.GetRootPoint(ref values);
                break;
            case "GetReferenceDirection":
                target.GetReferenceDirection(ref values);
                break;
            case "GetNormalVector":
                target.GetNormalVector(ref values);
                break;
        }
        return values.Cast<object>().Select(Double).ToArray();
    }

    public static JsonArray Json(IEnumerable<double> values, double scale = 1) =>
        new(values.Select(v => (JsonNode)Sketching.Sketch.Round(v * scale)).ToArray());

    public static JsonObject Plane(dynamic plane, int index) => new()
    {
        ["index"] = index,
        ["name"] = Str(plane.Name),
        ["origin_mm"] = Json(Vector(plane, "GetRootPoint"), 1000),
        ["normal"] = Json(Vector(plane, "GetNormal")),
        ["x_axis"] = Json(Vector(plane, "GetReferenceDirection")),
    };

    // geometry.tlb GNTTypePropertyConstants.
    public static string Kind(int type) => type switch
    {
        -1909484335 => "plane",
        -114972029 => "cylinder",
        -114972031 => "cone",
        -114972027 => "sphere",
        -114972025 => "torus",
        1465959633 => "bspline",
        167551109 => "line",
        167551105 => "circle",
        167551107 => "ellipse",
        167551103 => "bspline",
        _ => "other",
    };
}

public sealed class ReferencePlanes() : SeCommand("reference_planes", CommandKind.Read,
    "List reference planes: 1-based index, name, origin (mm), normal and x axis in model coordinates. A sketch's local x/y "
    + "follow the plane's x axis and normal.",
    Text("document_id"))
{
    public override CommandOutput Execute(CommandCall c)
    {
        var doc = c.Edge.Doc(c.Args.Text("document_id"), part: true);
        var planes = new JsonArray();
        int count = Int(doc.RefPlanes.Count);
        for (var i = 1; i <= count; i++)
            planes.Add(Frames.Plane(doc.RefPlanes.Item(i), i));
        return new JsonObject
        {
            ["planes"] = planes,
            ["coordinates"] = "Sketch coordinates are local to the chosen plane; sketch results give the exact plane_frame.",
        };
    }
}

public sealed class CreatePlane() : SeCommand("create_plane", CommandKind.Create,
    "Create a reference plane parallel to a reference plane (plane_index) or to a planar body face (face, from the faces "
    + "command), offset_mm along its normal (flip=true for the other side). Returns the new plane_index for sketch.",
    Text("document_id"), Integer("plane_index", 0, 0, 10000), Integer("face", 0, 0, 100000), Coordinate("offset_mm"), Flag("flip", false))
{
    public override CommandOutput Execute(CommandCall c)
    {
        var doc = c.Edge.Doc(c.Args.Text("document_id"), part: true);
        var (planeIndex, face) = (c.Args.Integer("plane_index"), c.Args.Integer("face"));
        if ((planeIndex > 0) == (face > 0))
            throw new EdgeError("Give either plane_index or face.");
        object parent;
        if (planeIndex > 0)
        {
            int count = Int(doc.RefPlanes.Count);
            if (planeIndex > count)
                throw new EdgeError($"plane_index must be between 1 and {count}.");
            parent = doc.RefPlanes.Item(planeIndex);
        }
        else
        {
            if (Int(doc.Models.Count) == 0)
                throw new EdgeError("The part has no body yet.");
            var faces = doc.Models.Item(1).Body.Faces(1);
            if (face > Int(faces.Count))
                throw new EdgeError($"face must be between 1 and {Int(faces.Count)}.");
            dynamic chosen = faces.Item(face);
            if (Frames.Kind(Int(chosen.Geometry.Type)) != "plane")
                throw new EdgeError($"Face {face} is not planar.");
            parent = chosen;
        }
        // ReferenceElementConstants: igNormalSide=2, igReverseNormalSide=1.
        var side = c.Args.Flag("flip") ? 1 : 2;
        var plane = doc.RefPlanes.AddParallelByDistance(parent, c.Args.Number("offset_mm") / 1000, side);
        int index = Int(doc.RefPlanes.Count);
        if (!Same((object)doc.RefPlanes.Item(index), (object)plane))
            throw new EdgeError("The new plane was not added at the end of the plane list; inspect reference_planes.");
        return new JsonObject { ["plane_index"] = index, ["plane"] = Frames.Plane(plane, index) };
    }
}

public sealed class Faces() : SeCommand("faces", CommandKind.Read,
    "List the body's faces (1-based): kind, area, and for planes a point and the plane normal; with edges=true each face's "
    + "edges (kind, start/end in model mm). Face and edge numbers feed create_plane and sketch references.",
    Text("document_id"), Flag("edges", false))
{
    public override CommandOutput Execute(CommandCall c)
    {
        var doc = c.Edge.Doc(c.Args.Text("document_id"), part: true);
        if (Int(doc.Models.Count) == 0)
            return new JsonObject { ["faces"] = new JsonArray(), ["note"] = "The part has no body yet." };
        var faces = doc.Models.Item(1).Body.Faces(1); // igQueryAll
        var result = new JsonArray();
        int count = Int(faces.Count);
        for (var i = 1; i <= count; i++)
        {
            var face = faces.Item(i);
            var geometry = face.Geometry;
            var kind = Frames.Kind(Int(geometry.Type));
            var json = new JsonObject { ["face"] = i, ["kind"] = kind, ["area_mm2"] = Sketching.Sketch.Round(Double(face.Area) * 1e6) };
            if (kind == "plane")
            {
                json["origin_mm"] = Frames.Json(Frames.Vector(geometry, "GetRootPoint"), 1000);
                json["normal"] = Frames.Json(Frames.Vector(geometry, "GetNormalVector"));
            }
            if (c.Args.Flag("edges"))
            {
                var edges = new JsonArray();
                var faceEdges = face.Edges;
                for (var j = 1; j <= Int(faceEdges.Count); j++)
                {
                    var edge = faceEdges.Item(j);
                    Array start = new double[3], end = new double[3];
                    edge.GetEndPoints(ref start, ref end);
                    edges.Add(new JsonObject
                    {
                        ["edge"] = j,
                        ["kind"] = Frames.Kind(Int(edge.Geometry.Type)),
                        ["start_mm"] = Frames.Json(start.Cast<object>().Select(Double), 1000),
                        ["end_mm"] = Frames.Json(end.Cast<object>().Select(Double), 1000),
                    });
                }
                json["edges"] = edges;
            }
            result.Add(json);
        }
        return new JsonObject { ["faces"] = result };
    }
}
