using System.Text.Json;

namespace SolidEdgeMcp.Commands.Sketching;

/// <summary>A keypoint of an element; an empty key means the element as a whole.</summary>
public sealed record KeyRef(string Id, string Key)
{
    public JsonNode Json() => Key.Length == 0 ? JsonValue.Create(Id)! : new JsonArray(Id, Key);

    public override string ToString() => Key.Length == 0 ? Id : $"{Id}:{Key}";
}

/// <summary>A geometric relation, checked against the geometry before anything is drawn.</summary>
public sealed record Relation(string Type, IReadOnlyList<string> Entities, IReadOnlyList<KeyRef> Points, string? Axis = null, string? Target = null)
{
    public IEnumerable<string> Ids => Entities.Concat(Points.Select(p => p.Id))
        .Concat(Axis is null ? [] : [Axis]).Concat(Target is null ? [] : [Target]);

    public JsonObject Json()
    {
        var json = new JsonObject { ["type"] = Type };
        if (Entities.Count == 1 && Type is "horizontal" or "vertical" or "fix")
            json["entity"] = Entities[0];
        else if (Entities.Count > 0)
            json["entities"] = new JsonArray(Entities.Select(e => (JsonNode)e).ToArray());
        if (Points.Count == 1)
            json["point"] = Points[0].Json();
        else if (Points.Count > 1)
            json["points"] = new JsonArray(Points.Select(p => p.Json()).ToArray());
        if (Axis is not null)
            json["axis"] = Axis;
        if (Target is not null)
            json["target"] = Target;
        return json;
    }
}

/// <summary>A driving (or reference) dimension. Its value is measured from the geometry.</summary>
public sealed record Dimension(string Type, string? Name, IReadOnlyList<string> Entities, KeyRef? From, KeyRef? To, string Axis, double? Value, bool Driving)
{
    public IEnumerable<string> Ids => Entities.Concat(From is null ? [] : [From.Id]).Concat(To is null ? [] : [To.Id]);

    public bool Angular => Type == "angle";

    public JsonObject Json()
    {
        var json = new JsonObject { ["type"] = Type };
        if (Name is not null)
            json["name"] = Name;
        if (Entities.Count == 1)
            json["entity"] = Entities[0];
        else if (Entities.Count > 1)
            json["entities"] = new JsonArray(Entities.Select(e => (JsonNode)e).ToArray());
        if (From is not null)
            json["from"] = From.Json();
        if (To is not null)
            json["to"] = To.Json();
        if (Type == "distance")
            json["axis"] = Axis;
        if (!Driving)
            json["driving"] = false;
        return json;
    }
}

/// <summary>Model geometry brought into the sketch: a reference plane or a body edge.</summary>
public sealed record Reference(string Id, string Type, int PlaneIndex, int Face, int Edge, bool Include)
{
    public JsonObject Json() => Type == "plane"
        ? new JsonObject { ["id"] = Id, ["type"] = "plane", ["plane_index"] = PlaneIndex }
        : new JsonObject { ["id"] = Id, ["type"] = "edge", ["face"] = Face, ["edge"] = Edge, ["mode"] = Include ? "include" : "project" };
}

/// <summary>
/// The full description of one sketch: elements (shorthands and corners already expanded),
/// relations, dimensions, model references and an optional revolve axis.
/// </summary>
public sealed class SketchSpec
{
    public const string XAxis = "@x_axis";
    public const string YAxis = "@y_axis";
    public const string Origin = "@origin";

    public List<Element> Elements { get; } = [];
    public List<Relation> Relations { get; } = [];
    public List<Dimension> Dimensions { get; } = [];
    public List<Reference> References { get; } = [];

    /// <summary>Id of the line that is the revolve axis; it never forms a region.</summary>
    public string? Axis { get; set; }

    public IEnumerable<string> Mentioned => Relations.SelectMany(r => r.Ids).Concat(Dimensions.SelectMany(d => d.Ids));

    /// <summary>
    /// Datums are projected when named, and whenever a distance is measured along x or y (explicitly,
    /// or because one end is a whole line).
    /// </summary>
    public bool UsesDatums => Mentioned.Any(id => id.StartsWith('@'))
        || Dimensions.Any(d => d.Axis is "x" or "y" || (d.Type == "distance" && new[] { d.From, d.To }.Any(WholeLineEnd)));

    private bool WholeLineEnd(KeyRef? key) =>
        key is { Key.Length: 0 } && (Find(key.Id) is LineEl || References.Any(r => r.Id == key.Id && r.Type == "plane"));

    /// <summary>The description needs model geometry (datums, references) before it can be checked exactly.</summary>
    public bool NeedsModel => UsesDatums || References.Count > 0;

    public Element? Find(string id) => Elements.FirstOrDefault(e => e.Id == id);

    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["entities"] = new JsonArray(Elements.Where(e => !e.External)
                .Select(e => (JsonNode)ElementJson(e.Id == Axis ? e with { Construction = false } : e)).ToArray()),
        };
        if (Relations.Count > 0)
            json["relations"] = new JsonArray(Relations.Select(r => (JsonNode)r.Json()).ToArray());
        if (Dimensions.Count > 0)
            json["dimensions"] = new JsonArray(Dimensions.Select(d => (JsonNode)d.Json()).ToArray());
        if (References.Count > 0)
            json["references"] = new JsonArray(References.Select(r => (JsonNode)r.Json()).ToArray());
        if (Axis is not null)
            json["axis"] = Axis;
        return json;
    }

    public SketchSpec Clone() => Read(JsonSerializer.SerializeToElement(ToJson()));

    /// <summary>Primitive form of an element; reading it back gives the same element.</summary>
    public static JsonObject ElementJson(Element element)
    {
        // Nine decimals (a picometre) drop solver noise like 45.00000000000001 and keep every joint exact.
        static JsonArray P(Pt p) => new(Math.Round(p.X, 9) + 0.0, Math.Round(p.Y, 9) + 0.0);
        var json = new JsonObject { ["id"] = element.Id, ["type"] = element.Kind };
        switch (element)
        {
            case LineEl line:
                json["start"] = P(line.Start);
                json["end"] = P(line.End);
                break;
            case ArcEl arc:
                json["center"] = P(arc.Center);
                json["start"] = P(arc.Start);
                json["end"] = P(arc.End);
                if (arc.Sweep < 0)
                    json["clockwise"] = true;
                break;
            case CircleEl circle:
                json["center"] = P(circle.Center);
                json["radius"] = circle.Radius;
                break;
            case EllipseEl ellipse:
                json["center"] = P(ellipse.Center);
                json["major_radius"] = ellipse.MajorRadius;
                json["minor_radius"] = ellipse.MinorRadius;
                json["angle"] = Sketch.Deg(ellipse.Angle);
                break;
            case SplineEl spline:
                json["points"] = new JsonArray(spline.Points.Select(p => (JsonNode)P(p)).ToArray());
                if (spline.Closed)
                    json["closed"] = true;
                break;
            case PointEl point:
                json["at"] = P(point.At);
                break;
        }
        if (element.Construction)
            json["construction"] = true;
        if (element.Group is { } group)
            json["group"] = group;
        return json;
    }

    private static readonly string[] Sections = ["entities", "corners", "relations", "dimensions", "references", "axis"];

    /// <summary>
    /// Parse a description object (entities, corners, relations, dimensions, references, axis),
    /// appending to <paramref name="into"/> when given.
    /// </summary>
    public static SketchSpec Read(JsonElement source, SketchSpec? into = null)
    {
        if (source.ValueKind != JsonValueKind.Object)
            throw new EdgeError("Sketch description must be an object.");
        foreach (var property in source.EnumerateObject())
        {
            if (!Sections.Contains(property.Name))
                throw new EdgeError($"Unknown sketch section {property.Name}; expected {string.Join(", ", Sections)}.");
        }
        var spec = into ?? new SketchSpec();
        var (relationsBefore, dimensionsBefore) = (spec.Relations.Count, spec.Dimensions.Count);
        var corners = new List<CornerRequest>();
        if (Field.Array(source, "entities") is { } entities)
        {
            var index = 0;
            foreach (var item in entities.EnumerateArray())
            {
                var where = $"entities[{index++}]";
                var shape = SketchShapes.Expand(item, where);
                foreach (var element in shape.Elements)
                    spec.Add(element, where);
                spec.Relations.AddRange(shape.Relations);
                corners.AddRange(shape.Corners);
            }
        }
        if (Field.Array(source, "corners") is { } explicitCorners)
        {
            var index = 0;
            foreach (var item in explicitCorners.EnumerateArray())
                corners.Add(SketchShapes.ReadCorner(item, $"corners[{index++}]"));
        }
        foreach (var corner in corners)
            SketchShapes.Apply(spec, corner);
        if (Field.Array(source, "references") is { } references)
        {
            var index = 0;
            foreach (var item in references.EnumerateArray())
                spec.References.Add(ReadReference(item, $"references[{index++}]", spec));
        }
        if (Field.Array(source, "relations") is { } relations)
        {
            var index = 0;
            foreach (var item in relations.EnumerateArray())
                spec.Relations.Add(ReadRelation(item, $"relations[{index++}]"));
        }
        if (Field.Array(source, "dimensions") is { } dimensions)
        {
            var index = 0;
            foreach (var item in dimensions.EnumerateArray())
                spec.Dimensions.Add(ReadDimension(item, $"dimensions[{index++}]", spec));
        }
        if (source.TryGetProperty("axis", out var axis) && axis.ValueKind != JsonValueKind.Null)
        {
            var id = axis.ValueKind == JsonValueKind.String ? axis.GetString()! : throw new EdgeError("axis must be a line id.");
            spec.SetAxis(id);
        }
        if (spec.Elements.Count(e => !e.External) > Sketch.MaxElements)
            throw new EdgeError($"At most {Sketch.MaxElements} sketch elements (after expanding shapes).");
        spec.CheckReferences(relationsBefore, dimensionsBefore);
        return spec;
    }

    public void SetAxis(string id)
    {
        var index = Elements.FindIndex(e => e.Id == id);
        if (index < 0 || Elements[index] is not LineEl)
            throw new EdgeError($"axis {id} must be the id of a line in this sketch.");
        // The axis line is drawn as ordinary geometry, but it never closes or splits a region.
        Elements[index] = Elements[index] with { Construction = true };
        Axis = id;
    }

    public void Add(Element element, string where)
    {
        if (element.Id.Length is 0 or > 64 || element.Id.StartsWith('@') || element.Id.Any(char.IsWhiteSpace))
            throw new EdgeError($"{where}: id must be 1-64 characters, without spaces, and not start with @.");
        if (Elements.Any(e => e.Id == element.Id) || References.Any(r => r.Id == element.Id))
            throw new EdgeError($"{where}: id {element.Id} is used twice.");
        Elements.Add(element);
    }

    /// <summary>
    /// Every id a relation or dimension names must exist (datums and references included); only
    /// the ones added since the given counts are checked, so an edit can add before it reconciles.
    /// </summary>
    public void CheckReferences(int relationsFrom = 0, int dimensionsFrom = 0)
    {
        var known = Elements.Select(e => e.Id).Concat(References.Select(r => r.Id))
            .Concat([XAxis, YAxis, Origin]).ToHashSet(StringComparer.Ordinal);
        var unknown = Relations.Skip(relationsFrom).SelectMany(r => r.Ids)
            .Concat(Dimensions.Skip(dimensionsFrom).SelectMany(d => d.Ids))
            .FirstOrDefault(id => !known.Contains(id));
        if (unknown is not null)
            throw new EdgeError($"Relation or dimension names unknown entity {unknown}.");
        var names = Dimensions.Where(d => d.Name is not null).GroupBy(d => d.Name).FirstOrDefault(g => g.Count() > 1);
        if (names is not null)
            throw new EdgeError($"Dimension name {names.Key} is used twice.");
    }

    // ---- relations, dimensions, references ---------------------------------------------------

    private static readonly string[] RelationTypes =
        ["horizontal", "vertical", "parallel", "perpendicular", "collinear", "equal", "tangent", "concentric",
         "symmetric", "coincident", "point_on", "fix"];

    private static Relation ReadRelation(JsonElement item, string where)
    {
        Field.Only(item, where, "type", "entity", "entities", "point", "points", "axis", "target");
        var type = Field.Text(item, "type", where);
        if (!RelationTypes.Contains(type))
            throw new EdgeError($"{where}: type must be one of {string.Join(", ", RelationTypes)}.");
        var entities = Field.Ids(item, where);
        var points = Field.Points(item, where);
        var axis = Field.OptionalText(item, "axis", where);
        var target = Field.OptionalText(item, "target", where);
        var ok = type switch
        {
            "horizontal" or "vertical" => (entities.Count == 1 && points.Count == 0) || (entities.Count == 0 && points.Count == 2),
            "parallel" or "perpendicular" or "collinear" or "equal" or "tangent" or "concentric" => entities.Count == 2 && points.Count == 0,
            "symmetric" => axis is not null && entities.Count == 2,
            "coincident" => points.Count == 2 && entities.Count == 0,
            "point_on" => points.Count == 1 && target is not null,
            "fix" => entities.Count + points.Count == 1,
            _ => false,
        };
        if (!ok)
            throw new EdgeError($"{where}: {type} takes " + type switch
            {
                "horizontal" or "vertical" => "entity (a line) or points (two keypoints to align).",
                "symmetric" => "axis (a line) and entities (two elements).",
                "coincident" => "points: two keypoints, e.g. [[\"L1\",\"end\"],[\"@origin\"]].",
                "point_on" => "point (a keypoint) and target (an element).",
                "fix" => "entity or point.",
                _ => "entities: two element ids.",
            });
        return new Relation(type, entities, points, axis, target);
    }

    private static readonly string[] DimensionTypes = ["length", "radius", "diameter", "angle", "distance"];

    private static Dimension ReadDimension(JsonElement item, string where, SketchSpec spec)
    {
        Field.Only(item, where, "type", "name", "entity", "entities", "from", "to", "axis", "value", "driving");
        var type = Field.Text(item, "type", where);
        if (!DimensionTypes.Contains(type))
            throw new EdgeError($"{where}: type must be one of {string.Join(", ", DimensionTypes)}.");
        var name = Field.OptionalText(item, "name", where);
        if (name is not null && !System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]{0,31}$"))
            throw new EdgeError($"{where}: name must start with a letter or _ and use letters, digits, _ (max 32).");
        var entities = Field.Ids(item, where);
        var from = Field.Key(item, "from", where);
        var to = Field.Key(item, "to", where);
        var axis = Field.OptionalText(item, "axis", where) ?? "aligned";
        if (axis is not ("x" or "y" or "aligned"))
            throw new EdgeError($"{where}: axis must be x, y or aligned.");
        double? value = item.TryGetProperty("value", out var v) && v.ValueKind != JsonValueKind.Null ? Field.Number(item, "value", where) : null;
        var driving = !item.TryGetProperty("driving", out var d) || d.ValueKind != JsonValueKind.False;
        var ok = type switch
        {
            "length" or "radius" or "diameter" => entities.Count == 1 && from is null && to is null,
            "angle" => entities.Count is 1 or 2 && from is null && to is null,
            "distance" => entities.Count == 0 && from is not null && to is not null,
            _ => false,
        };
        if (!ok)
            throw new EdgeError($"{where}: {type} takes " + type switch
            {
                "angle" => "entities (two lines) or entity (an arc: its sweep).",
                "distance" => "from and to: keypoints like [\"L1\",\"start\"], or a line id for its whole length.",
                _ => "entity.",
            });
        if (axis != "aligned" && type != "distance")
            throw new EdgeError($"{where}: axis applies to distance only.");
        return new Dimension(type, name, entities, from, to, axis, value, driving);
    }

    private static Reference ReadReference(JsonElement item, string where, SketchSpec spec)
    {
        Field.Only(item, where, "id", "type", "plane_index", "face", "edge", "mode");
        var id = Field.Text(item, "id", where);
        if (id.StartsWith('@') || spec.Elements.Any(e => e.Id == id) || spec.References.Any(r => r.Id == id))
            throw new EdgeError($"{where}: id {id} is reserved or used twice.");
        var type = Field.Text(item, "type", where);
        return type switch
        {
            "plane" => new Reference(id, type, Field.Integer(item, "plane_index", where, 1), 0, 0, false),
            "edge" => new Reference(id, type, 0, Field.Integer(item, "face", where, 1), Field.Integer(item, "edge", where, 1),
                (Field.OptionalText(item, "mode", where) ?? "project") switch
                {
                    "project" => false,
                    "include" => true,
                    _ => throw new EdgeError($"{where}: mode must be project or include."),
                }),
            _ => throw new EdgeError($"{where}: type must be plane or edge."),
        };
    }
}

/// <summary>Strict readers for description fields, with the field path in every message.</summary>
public static class Field
{
    public static void Only(JsonElement item, string where, params string[] allowed)
    {
        if (item.ValueKind != JsonValueKind.Object)
            throw new EdgeError($"{where}: expected an object.");
        foreach (var property in item.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw new EdgeError($"{where}: unknown field {property.Name}; allowed: {string.Join(", ", allowed)}.");
        }
    }

    public static JsonElement? Array(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind == JsonValueKind.Array ? value : throw new EdgeError($"{name} must be an array.");
    }

    public static string Text(JsonElement item, string name, string where) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new EdgeError($"{where}: {name} must be a string.");

    public static string? OptionalText(JsonElement item, string name, string where) =>
        !item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ? null : Text(item, name, where);

    public static bool Flag(JsonElement item, string name, bool fallback) =>
        item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    public static double Number(JsonElement item, string name, string where)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
            throw new EdgeError($"{where}: {name} must be a number.");
        var number = value.GetDouble();
        return double.IsFinite(number) && Math.Abs(number) <= Sketch.MaxCoordinate
            ? number
            : throw new EdgeError($"{where}: {name} is out of range.");
    }

    public static double Number(JsonElement item, string name, string where, double fallback) =>
        item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? Number(item, name, where) : fallback;

    public static double Positive(JsonElement item, string name, string where)
    {
        var number = Number(item, name, where);
        return number > Sketch.Tolerance ? number : throw new EdgeError($"{where}: {name} must be positive.");
    }

    public static int Integer(JsonElement item, string name, string where, int minimum)
    {
        if (!item.TryGetProperty(name, out var value) || !value.TryGetInt32(out var number) || number < minimum)
            throw new EdgeError($"{where}: {name} must be an integer of at least {minimum}.");
        return number;
    }

    public static Pt Point(JsonElement value, string where)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2
            || value[0].ValueKind != JsonValueKind.Number || value[1].ValueKind != JsonValueKind.Number)
            throw new EdgeError($"{where} must be [x, y].");
        double x = value[0].GetDouble(), y = value[1].GetDouble();
        if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > Sketch.MaxCoordinate || Math.Abs(y) > Sketch.MaxCoordinate)
            throw new EdgeError($"{where} is out of range.");
        return new Pt(x, y);
    }

    public static Pt Point(JsonElement item, string name, string where) =>
        item.TryGetProperty(name, out var value) ? Point(value, $"{where}: {name}") : throw new EdgeError($"{where}: {name} must be [x, y].");

    public static List<Pt> PointList(JsonElement item, string name, string where, int minimum)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < minimum)
            throw new EdgeError($"{where}: {name} must be an array of at least {minimum} [x, y] points.");
        return value.EnumerateArray().Select((p, i) => Point(p, $"{where}: {name}[{i}]")).ToList();
    }

    /// <summary>"entity": "id" or "entities": ["id", ...].</summary>
    public static List<string> Ids(JsonElement item, string where)
    {
        var result = new List<string>();
        if (item.TryGetProperty("entity", out var one) && one.ValueKind != JsonValueKind.Null)
            result.Add(one.ValueKind == JsonValueKind.String ? one.GetString()! : throw new EdgeError($"{where}: entity must be an id."));
        if (item.TryGetProperty("entities", out var many) && many.ValueKind != JsonValueKind.Null)
        {
            if (many.ValueKind != JsonValueKind.Array || many.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
                throw new EdgeError($"{where}: entities must be an array of ids.");
            result.AddRange(many.EnumerateArray().Select(e => e.GetString()!));
        }
        return result;
    }

    /// <summary>A keypoint written as "id" (point, datum, whole line) or ["id", "start|end|mid|center"].</summary>
    public static KeyRef ParseKey(JsonElement value, string where)
    {
        if (value.ValueKind == JsonValueKind.String)
            return new KeyRef(value.GetString()!, "");
        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() is 1 or 2
            && value.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String))
            return new KeyRef(value[0].GetString()!, value.GetArrayLength() == 2 ? value[1].GetString()! : "");
        throw new EdgeError($"{where} must be an id or [id, keypoint].");
    }

    public static KeyRef? Key(JsonElement item, string name, string where) =>
        item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? ParseKey(value, $"{where}: {name}") : null;

    /// <summary>"point": key or "points": [key, ...].</summary>
    public static List<KeyRef> Points(JsonElement item, string where)
    {
        var result = new List<KeyRef>();
        if (Key(item, "point", where) is { } one)
            result.Add(one);
        if (item.TryGetProperty("points", out var many) && many.ValueKind != JsonValueKind.Null)
        {
            if (many.ValueKind != JsonValueKind.Array)
                throw new EdgeError($"{where}: points must be an array of keypoints.");
            result.AddRange(many.EnumerateArray().Select((p, i) => ParseKey(p, $"{where}: points[{i}]")));
        }
        return result;
    }
}
