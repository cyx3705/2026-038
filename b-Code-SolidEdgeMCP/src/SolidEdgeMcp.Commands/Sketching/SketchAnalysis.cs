namespace SolidEdgeMcp.Commands.Sketching;

/// <summary>An element traversed in loop order; Forward=false walks it from End to Start.</summary>
public readonly record struct Step(Element Element, bool Forward);

public sealed class Loop
{
    public required List<Step> Steps { get; init; }
    public required bool Closed { get; init; }
    public double SignedArea { get; set; }
    public Pt Centroid { get; set; }
    public int Depth { get; set; }

    public string Role => !Closed ? "open" : Depth % 2 == 0 ? "outer" : "hole";

    public IEnumerable<string> Ids => Steps.Select(s => s.Element.Id);
}

/// <summary>
/// Topology and measures of a sketch: endpoints joined into loops, exact area and centroid
/// (Green's theorem, arcs included), outer/hole nesting, and every defect found on the way.
/// </summary>
public sealed partial class SketchAnalysis
{
    public List<Element> Elements { get; }
    public List<Loop> Loops { get; } = [];
    public JsonArray Issues { get; } = [];
    public Pt Min { get; private set; }
    public Pt Max { get; private set; }

    /// <summary>Area of the region an extrusion would use: outer loops minus their holes.</summary>
    public double NetArea => Loops.Where(l => l.Closed).Sum(l => (l.Depth % 2 == 0 ? 1 : -1) * Math.Abs(l.SignedArea));

    public bool Valid => Issues.Count == 0;

    private readonly double _tol;

    public SketchAnalysis(List<Element> elements, double tolerance = Sketch.Tolerance)
    {
        Elements = elements;
        _tol = tolerance;
        Bounds();
        BuildLoops();
        foreach (var loop in Loops.Where(l => l.Closed))
            Measure(loop);
        Nest();
        // Outer loops first, then holes by depth, open chains last; ties keep element order.
        var ordered = Loops.OrderBy(l => l.Closed ? l.Depth : int.MaxValue)
            .ThenBy(l => Elements.IndexOf(l.Steps[0].Element)).ToList();
        Loops.Clear();
        Loops.AddRange(ordered);
        Intersections();
    }

    // ---- topology -------------------------------------------------------------------------

    private static (Pt Start, Pt End)? Ends(Element element) => element switch
    {
        LineEl line => (line.Start, line.End),
        ArcEl arc => (arc.Start, arc.End),
        _ => null,
    };

    private void BuildLoops()
    {
        var nodes = new List<Pt>();
        int Node(Pt p)
        {
            for (var i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].DistanceTo(p) <= _tol)
                    return i;
            }
            nodes.Add(p);
            return nodes.Count - 1;
        }

        var edges = new List<(Element Element, int From, int To)>();
        foreach (var element in Elements)
        {
            if (element is CircleEl)
                Loops.Add(new Loop { Steps = [new Step(element, true)], Closed = true });
            else if (Ends(element) is { } ends)
                edges.Add((element, Node(ends.Start), Node(ends.End)));
        }
        var touching = new List<int>[nodes.Count];
        for (var i = 0; i < nodes.Count; i++)
            touching[i] = [];
        for (var e = 0; e < edges.Count; e++)
        {
            touching[edges[e].From].Add(e);
            touching[edges[e].To].Add(e);
        }
        for (var n = 0; n < nodes.Count; n++)
        {
            var ids = new JsonArray(touching[n].Select(e => (JsonNode)edges[e].Element.Id).ToArray());
            if (touching[n].Count == 1)
                Issues.Add(new JsonObject { ["type"] = "open_end", ["at"] = Json(nodes[n]), ["entities"] = ids });
            else if (touching[n].Count > 2)
                Issues.Add(new JsonObject { ["type"] = "branch", ["at"] = Json(nodes[n]), ["entities"] = ids });
        }

        var used = new bool[edges.Count];
        for (var first = 0; first < edges.Count; first++)
        {
            if (used[first])
                continue;
            // Collect the connected component, then walk it if every node has exactly two edges.
            var component = new List<int>();
            var queue = new Queue<int>([first]);
            used[first] = true;
            while (queue.Count > 0)
            {
                var e = queue.Dequeue();
                component.Add(e);
                foreach (var n in new[] { edges[e].From, edges[e].To })
                {
                    foreach (var next in touching[n].Where(x => !used[x]))
                    {
                        used[next] = true;
                        queue.Enqueue(next);
                    }
                }
            }
            var closed = component.All(e => touching[edges[e].From].Count == 2 && touching[edges[e].To].Count == 2);
            if (!closed)
            {
                Loops.Add(new Loop { Steps = component.Select(e => new Step(edges[e].Element, true)).ToList(), Closed = false });
                continue;
            }
            var steps = new List<Step>();
            var current = first;
            var at = edges[first].To;
            steps.Add(new Step(edges[first].Element, true));
            while (true)
            {
                var next = touching[at].First(x => x != current);
                if (next == first)
                    break;
                var forward = edges[next].From == at;
                steps.Add(new Step(edges[next].Element, forward));
                at = forward ? edges[next].To : edges[next].From;
                current = next;
            }
            Loops.Add(new Loop { Steps = steps, Closed = true });
        }
    }

    // ---- measures -------------------------------------------------------------------------

    /// <summary>Contour integrals of (x dy - y dx)/2, x^2 dy and y^2 dx along one oriented step.</summary>
    private static (double Area, double Mx, double My) Integrals(Step step)
    {
        switch (step.Element)
        {
            case LineEl line:
            {
                var (p, q) = step.Forward ? (line.Start, line.End) : (line.End, line.Start);
                double dx = q.X - p.X, dy = q.Y - p.Y;
                return ((p.X * q.Y - q.X * p.Y) / 2,
                    dy * (p.X * p.X + p.X * dx + dx * dx / 3),
                    dx * (p.Y * p.Y + p.Y * dy + dy * dy / 3));
            }
            case ArcEl arc:
                return step.Forward
                    ? ArcIntegrals(arc.Center, arc.Radius, arc.StartAngle, arc.EndAngle)
                    : ArcIntegrals(arc.Center, arc.Radius, arc.EndAngle, arc.StartAngle);
            case CircleEl circle:
                return ArcIntegrals(circle.Center, circle.Radius, 0, 2 * Math.PI);
            default:
                throw new InvalidOperationException(step.Element.Kind);
        }
    }

    private static (double Area, double Mx, double My) ArcIntegrals(Pt c, double r, double t1, double t2)
    {
        double S(double t) => Math.Sin(t);
        double C(double t) => Math.Cos(t);
        double Between(Func<double, double> f) => f(t2) - f(t1);
        var area = 0.5 * (r * r * (t2 - t1) + c.X * r * Between(S) - c.Y * r * Between(C));
        // x = cx + r cos t, dy = r cos t dt ; y = cy + r sin t, dx = -r sin t dt
        var mx = c.X * c.X * r * Between(S)
            + 2 * c.X * r * r * Between(t => t / 2 + Math.Sin(2 * t) / 4)
            + r * r * r * Between(t => S(t) - Math.Pow(S(t), 3) / 3);
        var my = -(c.Y * c.Y * r * Between(t => -C(t))
            + 2 * c.Y * r * r * Between(t => t / 2 - Math.Sin(2 * t) / 4)
            + r * r * r * Between(t => -C(t) + Math.Pow(C(t), 3) / 3));
        return (area, mx, my);
    }

    private static void Measure(Loop loop)
    {
        double area = 0, mx = 0, my = 0;
        foreach (var step in loop.Steps)
        {
            var (a, x, y) = Integrals(step);
            area += a;
            mx += x;
            my += y;
        }
        loop.SignedArea = area;
        loop.Centroid = Math.Abs(area) > 0 ? new Pt(mx / (2 * area), -my / (2 * area)) : default;
    }

    // ---- nesting --------------------------------------------------------------------------

    /// <summary>Polyline of a loop in walk order; arcs split every 2 degrees or less.</summary>
    public static List<Pt> Polygon(Loop loop)
    {
        var points = new List<Pt>();
        foreach (var step in loop.Steps)
        {
            switch (step.Element)
            {
                case LineEl line:
                    points.Add(step.Forward ? line.Start : line.End);
                    break;
                case ArcEl arc:
                {
                    var n = Math.Max(2, (int)Math.Ceiling(Math.Abs(arc.Sweep) / Sketch.Rad(2)));
                    for (var i = 0; i < n; i++)
                    {
                        var k = step.Forward ? i : n - i;
                        points.Add(Pt.Polar(arc.Center, arc.Radius, arc.StartAngle + arc.Sweep * k / n));
                    }
                    break;
                }
                case CircleEl circle:
                    for (var i = 0; i < 180; i++)
                        points.Add(Pt.Polar(circle.Center, circle.Radius, Sketch.Rad(2 * i)));
                    break;
            }
        }
        return points;
    }

    private static bool Inside(List<Pt> polygon, Pt p)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var (a, b) = (polygon[i], polygon[j]);
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    private static Pt Sample(Loop loop) => loop.Steps[0].Element switch
    {
        LineEl line => (line.Start + line.End) * 0.5,
        ArcEl arc => arc.Mid,
        CircleEl circle => circle.Center + new Pt(circle.Radius, 0),
        _ => default,
    };

    private void Nest()
    {
        var closed = Loops.Where(l => l.Closed).ToList();
        var polygons = closed.ToDictionary(l => l, Polygon);
        foreach (var loop in closed)
        {
            var sample = Sample(loop);
            loop.Depth = closed.Count(other => other != loop && Inside(polygons[other], sample));
        }
    }

    // ---- bounds ---------------------------------------------------------------------------

    private void Bounds()
    {
        var points = new List<Pt>();
        foreach (var element in Elements)
        {
            switch (element)
            {
                case LineEl line:
                    points.Add(line.Start);
                    points.Add(line.End);
                    break;
                case ArcEl arc:
                    points.Add(arc.Start);
                    points.Add(arc.End);
                    for (var quarter = 0; quarter < 4; quarter++)
                    {
                        var angle = quarter * Math.PI / 2;
                        if (arc.Contains(angle, 0))
                            points.Add(Pt.Polar(arc.Center, arc.Radius, angle));
                    }
                    break;
                case CircleEl circle:
                    points.Add(circle.Center - new Pt(circle.Radius, circle.Radius));
                    points.Add(circle.Center + new Pt(circle.Radius, circle.Radius));
                    break;
            }
        }
        Min = new Pt(points.Min(p => p.X), points.Min(p => p.Y));
        Max = new Pt(points.Max(p => p.X), points.Max(p => p.Y));
    }

    public static JsonArray Json(Pt p) => new(Sketch.Round(p.X), Sketch.Round(p.Y));
}
