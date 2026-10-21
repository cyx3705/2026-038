namespace SolidEdgeMcp.Commands.Sketching;

/// <summary>
/// Relations and dimensions against the geometry. A relation must already hold, so the solver
/// never has to move anything; a dimension's value is what the geometry measures (and must match
/// a stated value). Both are checked again on the geometry read back from Solid Edge.
/// </summary>
public sealed partial class SketchAnalysis
{
    public SketchSpec? Spec { get; }

    /// <summary>Model geometry the sketch refers to without owning it: datums and projected edges.</summary>
    public List<Element> References { get; }

    public List<(Relation Relation, string? Failure)> RelationResults { get; } = [];

    public List<(Dimension Dimension, double? Value, string? Failure)> DimensionValues { get; } = [];

    /// <summary>Datum stand-ins for a plan made without the model: the plane's own axes through its origin.</summary>
    public static List<Element> DefaultDatums(double reach = 1e4) =>
    [
        new LineEl(SketchSpec.XAxis, new Pt(-reach, 0), new Pt(reach, 0)) { External = true, Construction = true },
        new LineEl(SketchSpec.YAxis, new Pt(0, -reach), new Pt(0, reach)) { External = true, Construction = true },
        new PointEl(SketchSpec.Origin, new Pt(0, 0)) { External = true, Construction = true },
    ];

    private double Linear => Math.Max(_tol, Math.Max(Max.X - Min.X, Max.Y - Min.Y) * 1e-9);

    private double Angular => Math.Max(1e-9, Linear / Math.Max(1, Math.Max(Max.X - Min.X, Max.Y - Min.Y)));

    private Element Lookup(string id) =>
        Elements.FirstOrDefault(e => e.Id == id) ?? References.FirstOrDefault(e => e.Id == id)
        ?? throw new EdgeError($"unknown entity {id}");

    private Pt Point(KeyRef key)
    {
        var element = Lookup(key.Id);
        if (key.Key.Length == 0)
            return element is PointEl point ? point.At : throw new EdgeError($"{key.Id} needs a keypoint ({string.Join(", ", Sketch.Keys(element))}).");
        return Sketch.Keypoint(element, key.Key);
    }

    private T As<T>(string id, string what) where T : Element =>
        Lookup(id) as T ?? throw new EdgeError($"{id} must be {what}.");

    private static (Pt Center, double Radius)? RoundOf(Element element) => element switch
    {
        ArcEl arc => (arc.Center, arc.Radius),
        CircleEl circle => (circle.Center, circle.Radius),
        _ => null,
    };

    private static double LineDistance(Pt p, LineEl line) => Math.Abs(Cross(p - line.Start, line.Direction));

    private static Pt Reflect(Pt p, LineEl axis)
    {
        var d = axis.Direction;
        var offset = p - axis.Start;
        return axis.Start + d * (2 * Dot(offset, d)) - offset;
    }

    private void Constraints()
    {
        if (Spec is null)
            return;
        foreach (var relation in Spec.Relations)
        {
            string? failure;
            try
            {
                failure = Check(relation);
            }
            catch (EdgeError exc)
            {
                failure = exc.Message;
            }
            RelationResults.Add((relation, failure));
            if (failure is not null)
            {
                Issues.Add(new JsonObject
                {
                    ["type"] = "relation_unsatisfied",
                    ["relation"] = relation.Json(),
                    ["detail"] = failure,
                    ["entities"] = new JsonArray(relation.Ids.Distinct().Select(i => (JsonNode)i).ToArray()),
                });
            }
        }
        foreach (var dimension in Spec.Dimensions)
        {
            double? value = null;
            string? failure = null;
            try
            {
                value = Measure(dimension);
                if (dimension.Value is { } stated && Math.Abs(stated - value.Value) > Math.Max(1e-6, Math.Abs(stated) * 1e-9))
                    failure = $"geometry measures {Sketch.Format(value.Value)}, not the stated {Sketch.Format(stated)}";
            }
            catch (EdgeError exc)
            {
                failure = exc.Message;
            }
            DimensionValues.Add((dimension, value, failure));
            if (failure is not null)
            {
                Issues.Add(new JsonObject
                {
                    ["type"] = value is null ? "dimension_invalid" : "dimension_mismatch",
                    ["dimension"] = dimension.Json(),
                    ["detail"] = failure,
                    ["entities"] = new JsonArray(dimension.Ids.Distinct().Select(i => (JsonNode)i).ToArray()),
                });
            }
        }
    }

    /// <summary>Null when the relation holds; otherwise why it does not.</summary>
    private string? Check(Relation r)
    {
        var lin = Linear;
        var ang = Angular;
        string? Fail(bool ok, string why) => ok ? null : why;
        switch (r.Type)
        {
            case "horizontal" or "vertical":
            {
                var vertical = r.Type == "vertical";
                if (r.Entities.Count == 1)
                {
                    var d = As<LineEl>(r.Entities[0], "a line").Direction;
                    return Fail(Math.Abs(vertical ? d.X : d.Y) <= ang, $"{r.Entities[0]} is not {r.Type}");
                }
                var (p, q) = (Point(r.Points[0]), Point(r.Points[1]));
                return Fail(Math.Abs(vertical ? p.X - q.X : p.Y - q.Y) <= lin, $"{r.Points[0]} and {r.Points[1]} are not {r.Type}ly aligned");
            }
            case "parallel" or "perpendicular" or "collinear":
            {
                var a = As<LineEl>(r.Entities[0], "a line");
                var b = As<LineEl>(r.Entities[1], "a line");
                var cross = Math.Abs(Cross(a.Direction, b.Direction));
                return r.Type switch
                {
                    "parallel" => Fail(cross <= ang, "lines are not parallel"),
                    "perpendicular" => Fail(Math.Abs(Dot(a.Direction, b.Direction)) <= ang, "lines are not perpendicular"),
                    _ => Fail(cross <= ang && LineDistance(b.Start, a) <= lin, "lines are not on one line"),
                };
            }
            case "equal":
            {
                var (a, b) = (Lookup(r.Entities[0]), Lookup(r.Entities[1]));
                if (a is LineEl la && b is LineEl lb)
                    return Fail(Math.Abs(la.Length - lb.Length) <= lin, $"lengths differ ({Sketch.Format(la.Length)} vs {Sketch.Format(lb.Length)})");
                if (RoundOf(a) is { } ra && RoundOf(b) is { } rb)
                    return Fail(Math.Abs(ra.Radius - rb.Radius) <= lin, $"radii differ ({Sketch.Format(ra.Radius)} vs {Sketch.Format(rb.Radius)})");
                throw new EdgeError("equal relates two lines, or two arcs/circles");
            }
            case "concentric":
            {
                Pt Center(Element e) => e switch
                {
                    ArcEl arc => arc.Center,
                    CircleEl circle => circle.Center,
                    EllipseEl ellipse => ellipse.Center,
                    _ => throw new EdgeError($"{e.Id} has no center"),
                };
                return Fail(Center(Lookup(r.Entities[0])).DistanceTo(Center(Lookup(r.Entities[1]))) <= lin, "centers differ");
            }
            case "tangent":
            {
                var (a, b) = (Lookup(r.Entities[0]), Lookup(r.Entities[1]));
                if (a is LineEl && RoundOf(b) is not null)
                    (a, b) = (b, a);
                if (RoundOf(a) is { } round && b is LineEl line)
                    return Fail(Math.Abs(LineDistance(round.Center, line) - round.Radius) <= lin, $"{line.Id} does not touch {a.Id}");
                if (RoundOf(a) is { } r1 && RoundOf(b) is { } r2)
                {
                    var d = r1.Center.DistanceTo(r2.Center);
                    return Fail(Math.Abs(d - (r1.Radius + r2.Radius)) <= lin || Math.Abs(d - Math.Abs(r1.Radius - r2.Radius)) <= lin,
                        "circles do not touch");
                }
                throw new EdgeError("tangent relates a line and an arc/circle, or two arcs/circles");
            }
            case "symmetric":
            {
                var axis = As<LineEl>(r.Axis!, "a line (the symmetry axis)");
                var (a, b) = (Lookup(r.Entities[0]), Lookup(r.Entities[1]));
                bool Same(Pt p, Pt q) => p.DistanceTo(q) <= lin;
                bool SameEnds((Pt, Pt) x, (Pt, Pt) y) => (Same(x.Item1, y.Item1) && Same(x.Item2, y.Item2)) || (Same(x.Item1, y.Item2) && Same(x.Item2, y.Item1));
                var ok = (a, b) switch
                {
                    (PointEl p, PointEl q) => Same(Reflect(p.At, axis), q.At),
                    (LineEl p, LineEl q) => SameEnds((Reflect(p.Start, axis), Reflect(p.End, axis)), (q.Start, q.End)),
                    (ArcEl p, ArcEl q) => Same(Reflect(p.Center, axis), q.Center) && Math.Abs(p.Radius - q.Radius) <= lin
                        && SameEnds((Reflect(p.Start, axis), Reflect(p.End, axis)), (q.Start, q.End)),
                    (CircleEl p, CircleEl q) => Same(Reflect(p.Center, axis), q.Center) && Math.Abs(p.Radius - q.Radius) <= lin,
                    _ => throw new EdgeError("symmetric relates two elements of the same kind (point, line, arc, circle)"),
                };
                return Fail(ok, $"{a.Id} and {b.Id} are not mirror images about {axis.Id}");
            }
            case "coincident":
                return Fail(Point(r.Points[0]).DistanceTo(Point(r.Points[1])) <= lin, $"{r.Points[0]} and {r.Points[1]} do not coincide");
            case "point_on":
            {
                var p = Point(r.Points[0]);
                var target = Lookup(r.Target!);
                var gap = target switch
                {
                    LineEl line => LineDistance(p, line),
                    ArcEl or CircleEl => Math.Abs(p.DistanceTo(RoundOf(target)!.Value.Center) - RoundOf(target)!.Value.Radius),
                    PointEl point => p.DistanceTo(point.At),
                    _ => throw new EdgeError("point_on targets a line, arc, circle or point"),
                };
                return Fail(gap <= lin, $"{r.Points[0]} is {Sketch.Format(gap)} mm off {target.Id}");
            }
            case "fix":
                return null;
            default:
                throw new EdgeError($"unknown relation {r.Type}");
        }
    }

    /// <summary>The value a dimension takes on this geometry: mm, or degrees for angles.</summary>
    public double Measure(Dimension d)
    {
        switch (d.Type)
        {
            case "length":
                return As<LineEl>(d.Entities[0], "a line").Length;
            case "radius" or "diameter":
            {
                var round = RoundOf(Lookup(d.Entities[0])) ?? throw new EdgeError($"{d.Entities[0]} must be an arc or circle.");
                return d.Type == "radius" ? round.Radius : 2 * round.Radius;
            }
            case "angle" when d.Entities.Count == 1:
                return Sketch.Deg(Math.Abs(As<ArcEl>(d.Entities[0], "an arc (or give two lines)").Sweep));
            case "angle":
            {
                // Solid Edge measures between the lines' start-to-end directions (live finding); the
                // supplementary angle is not reachable, so the description uses the same definition.
                var (a, b) = (As<LineEl>(d.Entities[0], "a line"), As<LineEl>(d.Entities[1], "a line"));
                if (Intersection(a, b) is null)
                    throw new EdgeError($"{a.Id} and {b.Id} are parallel; there is no angle.");
                return Sketch.Deg(Math.Acos(Math.Clamp(Dot(a.Direction, b.Direction), -1, 1)));
            }
            case "distance":
                return Distance(d);
            default:
                throw new EdgeError($"unknown dimension {d.Type}");
        }
    }

    /// <summary>Where two lines (extended) cross; null when parallel.</summary>
    public static Pt? Intersection(LineEl a, LineEl b)
    {
        var r = a.End - a.Start;
        var s = b.End - b.Start;
        var denominator = Cross(r, s);
        if (Math.Abs(denominator) <= 1e-12 * r.Length * s.Length)
            return null;
        return a.Start + r * (Cross(b.Start - a.Start, s) / denominator);
    }

    /// <summary>Direction from the corner along the line, toward its far end.</summary>
    public static Pt Ray(LineEl line, Pt corner) =>
        line.Start.DistanceTo(corner) >= line.End.DistanceTo(corner) ? (line.Start - corner).Unit : (line.End - corner).Unit;

    /// <summary>The line a distance end names as a whole (no keypoint), if it does.</summary>
    public static LineEl? WholeLine(SketchAnalysis analysis, KeyRef key) =>
        key.Key.Length == 0 ? (analysis.Elements.FirstOrDefault(e => e.Id == key.Id) ?? analysis.References.FirstOrDefault(e => e.Id == key.Id)) as LineEl : null;

    private double Distance(Dimension d)
    {
        var lin = Linear;
        var ang = Angular;
        var (from, to) = (d.From!, d.To!);
        var wholeA = WholeLine(this, from);
        var wholeB = WholeLine(this, to);
        // Solid Edge holds a distance to a whole line only along the axis the line is perpendicular to.
        foreach (var line in new[] { wholeA, wholeB }.OfType<LineEl>())
        {
            if (Math.Abs(line.Direction.X) > ang && Math.Abs(line.Direction.Y) > ang)
                throw new EdgeError($"{line.Id} is slanted; a distance to a whole line needs a horizontal or vertical line. Dimension keypoints instead.");
        }
        bool Along(LineEl line) => d.Axis switch
        {
            "x" => Math.Abs(line.Direction.X) <= ang, // measuring x needs a vertical line
            "y" => Math.Abs(line.Direction.Y) <= ang,
            _ => true,
        };
        if (wholeA is not null && wholeB is not null)
        {
            if (Math.Abs(Cross(wholeA.Direction, wholeB.Direction)) > ang)
                throw new EdgeError($"{wholeA.Id} and {wholeB.Id} are not parallel; dimension a keypoint instead.");
            if (!Along(wholeA))
                throw new EdgeError($"axis {d.Axis} between lines needs lines perpendicular to it.");
            return LineDistance(wholeB.Start, wholeA);
        }
        if (wholeA is not null || wholeB is not null)
        {
            var line = (wholeA ?? wholeB)!;
            var p = Point(wholeA is null ? from : to);
            if (!Along(line))
                throw new EdgeError($"axis {d.Axis} to {line.Id} needs {line.Id} perpendicular to that axis.");
            return LineDistance(p, line);
        }
        var (a, b) = (Point(from), Point(to));
        var distance = d.Axis switch
        {
            "x" => Math.Abs(b.X - a.X),
            "y" => Math.Abs(b.Y - a.Y),
            _ => a.DistanceTo(b),
        };
        return distance > lin ? distance : throw new EdgeError($"{from} and {to} coincide; a distance needs two separate points.");
    }
}
