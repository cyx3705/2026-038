using System.Text.Json;

namespace SolidEdgeMcp.Commands.Sketching;

/// <summary>A rounded (fillet) or cut (chamfer) corner between two lines, applied after expansion.</summary>
public sealed record CornerRequest(Pt? At, IReadOnlyList<string>? Between, double Fillet, double Setback1, double Setback2, string? Id, string Where);

public sealed record ShapeResult(List<Element> Elements, List<Relation> Relations, List<CornerRequest> Corners);

/// <summary>
/// Entities of the description: primitives (line, arc, circle, ellipse, spline, point) and
/// shorthands (rectangle, polygon, polyline, slot) that expand into lines and arcs carrying their
/// design intent as relations. Corners turn a sharp joint of two lines into a fillet or chamfer.
/// </summary>
public static class SketchShapes
{
    private static readonly string[] Common = ["id", "type", "construction", "group"];

    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        ["line"] = ["start", "end"],
        ["arc"] = ["center", "start", "end", "clockwise", "radius", "start_angle", "end_angle"],
        ["circle"] = ["center", "radius"],
        ["ellipse"] = ["center", "major_radius", "minor_radius", "angle"],
        ["spline"] = ["points", "closed"],
        ["point"] = ["at"],
        ["rectangle"] = ["corner", "center", "size", "angle", "corner_radius", "corner_chamfer"],
        ["polygon"] = ["center", "radius", "sides", "angle", "inscribed", "corner_radius", "corner_chamfer"],
        ["polyline"] = ["points", "closed", "corner_radius", "corner_chamfer"],
        ["slot"] = ["start", "end", "width", "center", "length", "angle"],
    };

    public static IEnumerable<string> Types => Allowed.Keys;

    public static ShapeResult Expand(JsonElement item, string where)
    {
        if (item.ValueKind != JsonValueKind.Object)
            throw new EdgeError($"{where}: expected an object.");
        var id = Field.Text(item, "id", where);
        where = $"{where} ({id})";
        var type = Field.Text(item, "type", where);
        if (!Allowed.TryGetValue(type, out var fields))
            throw new EdgeError($"{where}: type must be one of {string.Join(", ", Allowed.Keys)}.");
        Field.Only(item, where, [.. Common, .. fields]);
        var construction = Field.Flag(item, "construction", false);
        var group = Field.OptionalText(item, "group", where);
        var result = new ShapeResult([], [], []);
        switch (type)
        {
            case "line":
                result.Elements.Add(Line(id, Field.Point(item, "start", where), Field.Point(item, "end", where), where));
                break;
            case "arc":
                result.Elements.Add(Arc(item, id, where));
                break;
            case "circle":
                result.Elements.Add(new CircleEl(id, Field.Point(item, "center", where), Field.Positive(item, "radius", where)));
                break;
            case "ellipse":
                result.Elements.Add(Ellipse(item, id, where));
                break;
            case "spline":
                result.Elements.Add(Spline(item, id, where));
                break;
            case "point":
                result.Elements.Add(new PointEl(id, Field.Point(item, "at", where)));
                break;
            case "rectangle":
                Rectangle(item, id, where, result);
                break;
            case "polygon":
                Polygon(item, id, where, result);
                break;
            case "polyline":
                Polyline(item, id, where, result);
                break;
            case "slot":
                Slot(item, id, where, result);
                break;
        }
        var shorthand = type is "rectangle" or "polygon" or "polyline" or "slot";
        for (var i = 0; i < result.Elements.Count; i++)
            result.Elements[i] = result.Elements[i] with { Construction = construction, Group = shorthand ? id : group };
        return result;
    }

    // ---- primitives ---------------------------------------------------------------------------

    public static LineEl Line(string id, Pt start, Pt end, string where) =>
        start.DistanceTo(end) > Sketch.Tolerance ? new LineEl(id, start, end) : throw new EdgeError($"{where}: start and end coincide.");

    private static ArcEl Arc(JsonElement item, string id, string where)
    {
        var center = Field.Point(item, "center", where);
        if (item.TryGetProperty("start_angle", out _) || item.TryGetProperty("end_angle", out _))
        {
            var radius = Field.Positive(item, "radius", where);
            var from = Sketch.Rad(Field.Number(item, "start_angle", where));
            var sweep = Sketch.Normalize(Sketch.Rad(Field.Number(item, "end_angle", where)) - from);
            if (sweep * radius <= Sketch.Tolerance)
                throw new EdgeError($"{where}: start_angle and end_angle give an empty arc; use a circle for 360 degrees.");
            return ArcEl.FromAngles(id, center, radius, from, sweep);
        }
        var start = Field.Point(item, "start", where);
        var end = Field.Point(item, "end", where);
        var r = start.DistanceTo(center);
        if (r <= Sketch.Tolerance)
            throw new EdgeError($"{where}: start coincides with center.");
        if (Math.Abs(end.DistanceTo(center) - r) > Math.Max(Sketch.Tolerance, r * 1e-9))
            throw new EdgeError($"{where}: start and end are not the same distance from center "
                + $"({Sketch.Format(r)} vs {Sketch.Format(end.DistanceTo(center))}).");
        if (start.DistanceTo(end) <= Sketch.Tolerance)
            throw new EdgeError($"{where}: start and end coincide; use a circle for a full turn.");
        var a0 = start.AngleFrom(center);
        var a1 = end.AngleFrom(center);
        var span = Field.Flag(item, "clockwise", false) ? -Sketch.Normalize(a0 - a1) : Sketch.Normalize(a1 - a0);
        return new ArcEl(id, center, r, a0, span, start, end);
    }

    private static EllipseEl Ellipse(JsonElement item, string id, string where)
    {
        var major = Field.Positive(item, "major_radius", where);
        var minor = Field.Positive(item, "minor_radius", where);
        if (minor > major)
            throw new EdgeError($"{where}: minor_radius must not exceed major_radius; rotate with angle instead.");
        return new EllipseEl(id, Field.Point(item, "center", where), major, minor, Sketch.Rad(Field.Number(item, "angle", where, 0)));
    }

    private static SplineEl Spline(JsonElement item, string id, string where)
    {
        var closed = Field.Flag(item, "closed", false);
        var points = Field.PointList(item, "points", where, closed ? 3 : 2);
        for (var i = 1; i < points.Count; i++)
        {
            if (points[i].DistanceTo(points[i - 1]) <= Sketch.Tolerance)
                throw new EdgeError($"{where}: points[{i}] repeats the previous point.");
        }
        if (closed && points[0].DistanceTo(points[^1]) <= Sketch.Tolerance)
            points.RemoveAt(points.Count - 1); // closed is implied; do not repeat the first point
        return new SplineEl(id, points, closed);
    }

    // ---- shorthands ---------------------------------------------------------------------------

    private static Relation Rel(string type, params string[] entities) => new(type, entities, []);

    /// <summary>corner_radius / corner_chamfer of a shorthand become corner requests at its vertices.</summary>
    private static void VertexCorners(JsonElement item, string id, string where, IEnumerable<Pt> vertices, ShapeResult result)
    {
        var radius = Field.Number(item, "corner_radius", where, 0);
        var chamfer = Field.Number(item, "corner_chamfer", where, 0);
        if (radius < 0 || chamfer < 0 || (radius > 0 && chamfer > 0))
            throw new EdgeError($"{where}: give corner_radius or corner_chamfer, positive.");
        if (radius == 0 && chamfer == 0)
            return;
        var k = 0;
        foreach (var vertex in vertices)
        {
            k++;
            result.Corners.Add(radius > 0
                ? new CornerRequest(vertex, null, radius, 0, 0, $"{id}.f{k}", where)
                : new CornerRequest(vertex, null, 0, chamfer, chamfer, $"{id}.c{k}", where));
        }
    }

    private static void Rectangle(JsonElement item, string id, string where, ShapeResult result)
    {
        var size = Field.Point(item, "size", where);
        if (size.X <= Sketch.Tolerance || size.Y <= Sketch.Tolerance)
            throw new EdgeError($"{where}: size [width, height] must be positive.");
        var angle = Sketch.Rad(Field.Number(item, "angle", where, 0));
        var u = new Pt(Math.Cos(angle), Math.Sin(angle));
        var v = new Pt(-u.Y, u.X);
        var hasCorner = item.TryGetProperty("corner", out _);
        if (hasCorner == item.TryGetProperty("center", out _))
            throw new EdgeError($"{where}: give either corner (lower-left before rotation) or center.");
        var c0 = hasCorner ? Field.Point(item, "corner", where) : Field.Point(item, "center", where) - u * (size.X / 2) - v * (size.Y / 2);
        Pt[] c = [c0, c0 + u * size.X, c0 + u * size.X + v * size.Y, c0 + v * size.Y];
        string[] names = [$"{id}.b", $"{id}.r", $"{id}.t", $"{id}.l"];
        for (var i = 0; i < 4; i++)
            result.Elements.Add(new LineEl(names[i], c[i], c[(i + 1) % 4]));
        if (angle == 0)
        {
            result.Relations.AddRange([Rel("horizontal", names[0]), Rel("horizontal", names[2]),
                Rel("vertical", names[1]), Rel("vertical", names[3])]);
        }
        else
        {
            result.Relations.AddRange([Rel("parallel", names[0], names[2]), Rel("parallel", names[1], names[3]),
                Rel("perpendicular", names[0], names[1])]);
        }
        VertexCorners(item, id, where, c, result);
    }

    private static void Polygon(JsonElement item, string id, string where, ShapeResult result)
    {
        var center = Field.Point(item, "center", where);
        var radius = Field.Positive(item, "radius", where);
        var sides = Field.Integer(item, "sides", where, 3);
        if (sides > 128)
            throw new EdgeError($"{where}: sides must be at most 128.");
        // inscribed (default): radius reaches the vertices; otherwise it reaches the side midpoints.
        var vertexRadius = Field.Flag(item, "inscribed", true) ? radius : radius / Math.Cos(Math.PI / sides);
        var first = Sketch.Rad(Field.Number(item, "angle", where, 90));
        var vertices = Enumerable.Range(0, sides).Select(k => Pt.Polar(center, vertexRadius, first + 2 * Math.PI * k / sides)).ToArray();
        for (var k = 0; k < sides; k++)
            result.Elements.Add(new LineEl($"{id}.{k + 1}", vertices[k], vertices[(k + 1) % sides]));
        for (var k = 2; k <= sides; k++)
            result.Relations.Add(Rel("equal", $"{id}.1", $"{id}.{k}"));
        VertexCorners(item, id, where, vertices, result);
    }

    private static void Polyline(JsonElement item, string id, string where, ShapeResult result)
    {
        var closed = Field.Flag(item, "closed", false);
        var points = Field.PointList(item, "points", where, closed ? 3 : 2);
        if (closed && points[0].DistanceTo(points[^1]) <= Sketch.Tolerance)
            points.RemoveAt(points.Count - 1);
        var count = closed ? points.Count : points.Count - 1;
        for (var k = 0; k < count; k++)
            result.Elements.Add(Line($"{id}.{k + 1}", points[k], points[(k + 1) % points.Count], $"{where}: segment {k + 1}"));
        VertexCorners(item, id, where, closed ? points : points.Skip(1).Take(points.Count - 2), result);
    }

    private static void Slot(JsonElement item, string id, string where, ShapeResult result)
    {
        var width = Field.Positive(item, "width", where);
        Pt a, b;
        if (item.TryGetProperty("center", out _))
        {
            var center = Field.Point(item, "center", where);
            var length = Field.Positive(item, "length", where);
            if (length - width <= Sketch.Tolerance)
                throw new EdgeError($"{where}: length (overall) must exceed width.");
            var angle = Sketch.Rad(Field.Number(item, "angle", where, 0));
            var half = new Pt(Math.Cos(angle), Math.Sin(angle)) * ((length - width) / 2);
            (a, b) = (center - half, center + half);
        }
        else
        {
            (a, b) = (Field.Point(item, "start", where), Field.Point(item, "end", where));
            if (a.DistanceTo(b) <= Sketch.Tolerance)
                throw new EdgeError($"{where}: start and end (the two arc centers) coincide; use a circle.");
        }
        var d = (b - a).Unit;
        var n = new Pt(-d.Y, d.X);
        var r = width / 2;
        result.Elements.Add(new LineEl($"{id}.a", a - n * r, b - n * r));
        result.Elements.Add(ArcEl.FromAngles($"{id}.e", b, r, (n * -1).AngleFrom(new Pt(0, 0)), Math.PI));
        result.Elements.Add(new LineEl($"{id}.b", b + n * r, a + n * r));
        result.Elements.Add(ArcEl.FromAngles($"{id}.s", a, r, n.AngleFrom(new Pt(0, 0)), Math.PI));
        result.Relations.AddRange([Rel("tangent", $"{id}.a", $"{id}.e"), Rel("tangent", $"{id}.e", $"{id}.b"),
            Rel("tangent", $"{id}.b", $"{id}.s"), Rel("tangent", $"{id}.s", $"{id}.a"), Rel("equal", $"{id}.s", $"{id}.e")]);
    }

    // ---- corners ------------------------------------------------------------------------------

    public static CornerRequest ReadCorner(JsonElement item, string where)
    {
        Field.Only(item, where, "id", "at", "between", "fillet", "chamfer");
        var id = Field.OptionalText(item, "id", where);
        Pt? at = item.TryGetProperty("at", out _) ? Field.Point(item, "at", where) : null;
        var between = new List<string>();
        if (item.TryGetProperty("between", out var pair))
        {
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2 || pair.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
                throw new EdgeError($"{where}: between must be [line1, line2].");
            between.AddRange(pair.EnumerateArray().Select(e => e.GetString()!));
        }
        if ((at is null) == (between.Count == 0))
            throw new EdgeError($"{where}: give at: [x, y] (the joint) or between: [line1, line2].");
        if (item.TryGetProperty("fillet", out _) == item.TryGetProperty("chamfer", out _))
            throw new EdgeError($"{where}: give fillet (radius) or chamfer (setback, or [setback1, setback2]).");
        if (item.TryGetProperty("fillet", out _))
            return new CornerRequest(at, between.Count == 2 ? between : null, Field.Positive(item, "fillet", where), 0, 0, id, where);
        var chamfer = item.GetProperty("chamfer");
        var (s1, s2) = chamfer.ValueKind == JsonValueKind.Array
            ? (Field.Point(chamfer, $"{where}: chamfer").X, Field.Point(chamfer, $"{where}: chamfer").Y)
            : (Field.Positive(item, "chamfer", where), Field.Positive(item, "chamfer", where));
        if (s1 <= Sketch.Tolerance || s2 <= Sketch.Tolerance)
            throw new EdgeError($"{where}: chamfer setbacks must be positive.");
        return new CornerRequest(at, between.Count == 2 ? between : null, 0, s1, s2, id, where);
    }

    /// <summary>Replace the sharp joint of two lines with a tangent arc or a chamfer line.</summary>
    public static void Apply(SketchSpec spec, CornerRequest corner)
    {
        var where = corner.Where;
        List<int> lines;
        Pt p;
        if (corner.Between is { } between)
        {
            lines = between.Select(id => spec.Elements.FindIndex(e => e.Id == id)).ToList();
            if (lines.Any(i => i < 0 || spec.Elements[i] is not LineEl))
                throw new EdgeError($"{where}: between must name two lines of this sketch.");
            var (l1, l2) = ((LineEl)spec.Elements[lines[0]], (LineEl)spec.Elements[lines[1]]);
            var shared = new[] { l1.Start, l1.End }.Where(q => q.DistanceTo(l2.Start) <= Sketch.Tolerance || q.DistanceTo(l2.End) <= Sketch.Tolerance).ToList();
            if (shared.Count != 1)
                throw new EdgeError($"{where}: {between[0]} and {between[1]} do not share exactly one endpoint.");
            p = shared[0];
        }
        else
        {
            p = corner.At!.Value;
            var at = p;
            lines = spec.Elements.Select((e, i) => (e, i))
                .Where(x => Sketch.Ends(x.e) is { } ends && !x.e.External
                    && (ends.Start.DistanceTo(at) <= Sketch.Tolerance || ends.End.DistanceTo(at) <= Sketch.Tolerance))
                .Select(x => x.i).ToList();
            if (lines.Count != 2 || lines.Any(i => spec.Elements[i] is not LineEl))
                throw new EdgeError($"{where}: a corner needs exactly two lines meeting at [{Sketch.Format(at.X)}, {Sketch.Format(at.Y)}]; found {lines.Count} elements there.");
        }
        var a = (LineEl)spec.Elements[lines[0]];
        var b = (LineEl)spec.Elements[lines[1]];
        Pt Far(LineEl l) => l.Start.DistanceTo(p) <= Sketch.Tolerance ? l.End : l.Start;
        var u = (Far(a) - p).Unit;
        var v = (Far(b) - p).Unit;
        var angle = Math.Acos(Math.Clamp(Pt.Dot(u, v), -1, 1));
        if (angle < 1e-6 || Math.PI - angle < 1e-6)
            throw new EdgeError($"{where}: {a.Id} and {b.Id} are collinear at this joint; there is no corner.");
        double ta, tb;
        if (corner.Fillet > 0)
            ta = tb = corner.Fillet / Math.Tan(angle / 2);
        else
            (ta, tb) = (corner.Setback1, corner.Setback2);
        if (ta >= Far(a).DistanceTo(p) - Sketch.Tolerance || tb >= Far(b).DistanceTo(p) - Sketch.Tolerance)
            throw new EdgeError($"{where}: the corner is too large for {a.Id} or {b.Id} (needs {Sketch.Format(Math.Max(ta, tb))} mm of each).");
        Pt qa = p + u * ta, qb = p + v * tb;
        var id = corner.Id ?? Enumerable.Range(1, 10000).Select(k => $"corner{k}").First(c => spec.Find(c) is null);
        Element added;
        if (corner.Fillet > 0)
        {
            var bisector = (u + v).Unit;
            var along = p + bisector * (corner.Fillet / Math.Sin(angle / 2) - corner.Fillet);
            added = ArcEl.Through(id, qa, along, qb)!;
        }
        else
        {
            added = new LineEl(id, qa, qb);
        }
        LineEl Trim(LineEl l, Pt q) => l.Start.DistanceTo(p) <= Sketch.Tolerance ? l with { Start = q } : l with { End = q };
        spec.Elements[lines[0]] = Trim(a, qa);
        spec.Elements[lines[1]] = Trim(b, qb);
        spec.Add(added with { Construction = a.Construction, Group = a.Group == b.Group ? a.Group : null }, where);
        if (corner.Fillet > 0)
            spec.Relations.AddRange([new Relation("tangent", [a.Id, id], []), new Relation("tangent", [b.Id, id], [])]);
    }
}
