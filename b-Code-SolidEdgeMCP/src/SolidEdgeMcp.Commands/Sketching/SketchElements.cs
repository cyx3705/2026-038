using System.Globalization;

namespace SolidEdgeMcp.Commands.Sketching;

/// <summary>A point in plane-local millimeters.</summary>
public readonly record struct Pt(double X, double Y)
{
    public static Pt operator +(Pt a, Pt b) => new(a.X + b.X, a.Y + b.Y);

    public static Pt operator -(Pt a, Pt b) => new(a.X - b.X, a.Y - b.Y);

    public static Pt operator *(Pt a, double k) => new(a.X * k, a.Y * k);

    public double Length => Math.Sqrt(X * X + Y * Y);

    public double DistanceTo(Pt other) => (this - other).Length;

    public Pt Unit => this * (1 / Length);

    public static double Dot(Pt a, Pt b) => a.X * b.X + a.Y * b.Y;

    public static double Cross(Pt a, Pt b) => a.X * b.Y - a.Y * b.X;

    public static Pt Polar(Pt center, double radius, double angle) =>
        new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));

    public double AngleFrom(Pt center) => Math.Atan2(Y - center.Y, X - center.X);
}

/// <summary>
/// One sketch element. Coordinates are millimeters, angles radians, counterclockwise positive.
/// Construction elements and points never form regions; external elements come from the model
/// (projected or included edges, datum planes) and are not drawn by the description.
/// </summary>
public abstract record Element(string Id)
{
    /// <summary>Solid Edge's own name once the element exists in a profile.</summary>
    public string? SeName { get; init; }

    public bool Construction { get; init; }

    /// <summary>True for geometry that already exists in the model (references, datums).</summary>
    public bool External { get; init; }

    /// <summary>The shorthand (rectangle, slot...) this element was expanded from.</summary>
    public string? Group { get; init; }

    public abstract string Kind { get; }

    /// <summary>Takes part in loops, areas and crossing checks.</summary>
    public bool Profile => !Construction && this is not PointEl;
}

public sealed record LineEl(string Id, Pt Start, Pt End) : Element(Id)
{
    public override string Kind => "line";

    public double Length => Start.DistanceTo(End);

    public Pt Direction => (End - Start).Unit;
}

/// <summary>Arc from <see cref="Start"/> to <see cref="End"/>; Sweep is signed (+ counterclockwise).</summary>
public sealed record ArcEl(string Id, Pt Center, double Radius, double StartAngle, double Sweep, Pt Start, Pt End) : Element(Id)
{
    public override string Kind => "arc";

    public double EndAngle => StartAngle + Sweep;

    public Pt Mid => Pt.Polar(Center, Radius, StartAngle + Sweep / 2);

    public static ArcEl FromAngles(string id, Pt center, double radius, double startAngle, double sweep) =>
        new(id, center, radius, startAngle, sweep,
            Pt.Polar(center, radius, startAngle), Pt.Polar(center, radius, startAngle + sweep));

    /// <summary>Arc through three points: start, a point on the arc, end.</summary>
    public static ArcEl? Through(string id, Pt start, Pt along, Pt end)
    {
        var d = 2 * Pt.Cross(along - start, end - start);
        if (Math.Abs(d) < 1e-12)
            return null;
        double a2 = Pt.Dot(start, start), b2 = Pt.Dot(along, along), c2 = Pt.Dot(end, end);
        var center = new Pt(
            (a2 * (along.Y - end.Y) + b2 * (end.Y - start.Y) + c2 * (start.Y - along.Y)) / d,
            (a2 * (end.X - along.X) + b2 * (start.X - end.X) + c2 * (along.X - start.X)) / d);
        var a0 = start.AngleFrom(center);
        var ccw = Sketch.Normalize(end.AngleFrom(center) - a0);
        var viaAlong = Sketch.Normalize(along.AngleFrom(center) - a0) < ccw;
        return new ArcEl(id, center, start.DistanceTo(center), a0, viaAlong ? ccw : ccw - 2 * Math.PI, start, end);
    }

    /// <summary>True when <paramref name="angle"/> lies on the arc, allowing <paramref name="slack"/> radians.</summary>
    public bool Contains(double angle, double slack)
    {
        var offset = Sketch.Normalize(Sweep >= 0 ? angle - StartAngle : StartAngle - angle);
        var span = Math.Abs(Sweep);
        return offset <= span + slack || offset >= 2 * Math.PI - slack;
    }
}

public sealed record CircleEl(string Id, Pt Center, double Radius) : Element(Id)
{
    public override string Kind => "circle";
}

/// <summary>Full ellipse; <see cref="Angle"/> is the direction of the major axis.</summary>
public sealed record EllipseEl(string Id, Pt Center, double MajorRadius, double MinorRadius, double Angle) : Element(Id)
{
    public override string Kind => "ellipse";

    public Pt At(double t)
    {
        double c = Math.Cos(Angle), s = Math.Sin(Angle);
        double x = MajorRadius * Math.Cos(t), y = MinorRadius * Math.Sin(t);
        return new Pt(Center.X + x * c - y * s, Center.Y + x * s + y * c);
    }
}

/// <summary>Curve through <see cref="Points"/>. Its exact shape is Solid Edge's own fit; measures are approximate.</summary>
public sealed record SplineEl(string Id, IReadOnlyList<Pt> Points, bool Closed) : Element(Id)
{
    public override string Kind => "spline";

    public Pt Start => Points[0];

    public Pt End => Closed ? Points[0] : Points[^1];

    public bool Equals(SplineEl? other) =>
        other is not null && Id == other.Id && Closed == other.Closed && Points.SequenceEqual(other.Points);

    public override int GetHashCode() => HashCode.Combine(Id, Closed, Points.Count);
}

public sealed record PointEl(string Id, Pt At) : Element(Id)
{
    public override string Kind => "point";
}

/// <summary>Numeric helpers shared by the sketch code.</summary>
public static class Sketch
{
    public const double Tolerance = 1e-6; // mm; endpoints closer than this are the same point
    public const double MaxCoordinate = 1_000_000;
    public const int MaxElements = 500;

    public static double Normalize(double angle)
    {
        var result = angle % (2 * Math.PI);
        return result < 0 ? result + 2 * Math.PI : result;
    }

    public static double Rad(double degrees) => degrees * Math.PI / 180;

    public static double Deg(double radians) => radians * 180 / Math.PI;

    /// <summary>Six decimals: enough for CAD, and it hides binary noise such as 119999.99999999999.</summary>
    public static double Round(double value) => Math.Round(value, 6) + 0.0;

    public static string Format(double value) => Round(value).ToString("0.######", CultureInfo.InvariantCulture);

    public static (Pt Start, Pt End)? Ends(Element element) => element switch
    {
        LineEl line => (line.Start, line.End),
        ArcEl arc => (arc.Start, arc.End),
        SplineEl { Closed: false } spline => (spline.Start, spline.End),
        _ => null,
    };

    /// <summary>Keypoint names accepted in relations and dimensions, per element kind.</summary>
    public static string[] Keys(Element element) => element switch
    {
        LineEl => ["start", "end", "mid"],
        ArcEl => ["center", "start", "end", "mid"],
        CircleEl or EllipseEl => ["center"],
        SplineEl { Closed: false } => ["start", "end"],
        PointEl => ["point"],
        _ => [],
    };

    public static Pt Keypoint(Element element, string key) => (element, key) switch
    {
        (LineEl l, "start") => l.Start,
        (LineEl l, "end") => l.End,
        (LineEl l, "mid") => (l.Start + l.End) * 0.5,
        (ArcEl a, "center") => a.Center,
        (ArcEl a, "start") => a.Start,
        (ArcEl a, "end") => a.End,
        (ArcEl a, "mid") => a.Mid,
        (CircleEl c, "center") => c.Center,
        (EllipseEl e, "center") => e.Center,
        (SplineEl s, "start") => s.Start,
        (SplineEl s, "end") => s.End,
        (PointEl p, "point") => p.At,
        _ => throw new EdgeError($"{element.Id} has no keypoint '{key}'; use one of {string.Join(", ", Keys(element))}."),
    };

    /// <summary>constant.tlb KeypointIndexConstants for the same names.</summary>
    public static int KeypointIndex(Element element, string key) => (element, key) switch
    {
        (LineEl, "start") => 0,
        (LineEl, "end") => 1,
        (LineEl, "mid") => 2,
        (ArcEl, "center") => 0,
        (ArcEl, "start") => 1,
        (ArcEl, "end") => 2,
        (ArcEl, "mid") => 3,
        (SplineEl, "start") => 0,
        (SplineEl, "end") => 1,
        _ => 0,
    };

    /// <summary>
    /// Centripetal Catmull-Rom samples through the spline's points: the stand-in for Solid Edge's
    /// own fit wherever a measure needs the curve between the points.
    /// </summary>
    public static List<Pt> Sample(SplineEl spline, int perSpan = 16)
    {
        var p = spline.Points;
        var n = p.Count;
        Pt Get(int i) => spline.Closed ? p[((i % n) + n) % n] : p[Math.Clamp(i, 0, n - 1)];
        var spans = spline.Closed ? n : n - 1;
        var result = new List<Pt>();
        for (var i = 0; i < spans; i++)
        {
            Pt p0 = Get(i - 1), p1 = Get(i), p2 = Get(i + 1), p3 = Get(i + 2);
            if (!spline.Closed && i == 0)
                p0 = p1 - (p2 - p1);
            if (!spline.Closed && i == spans - 1)
                p3 = p2 + (p2 - p1);
            double Knot(double t, Pt a, Pt b) => t + Math.Sqrt(Math.Max(a.DistanceTo(b), 1e-12));
            double t0 = 0, t1 = Knot(t0, p0, p1), t2 = Knot(t1, p1, p2), t3 = Knot(t2, p2, p3);
            for (var k = 0; k < perSpan; k++)
            {
                var t = t1 + (t2 - t1) * k / perSpan;
                Pt Lerp(Pt a, Pt b, double ta, double tb) => a * ((tb - t) / (tb - ta)) + b * ((t - ta) / (tb - ta));
                var a1 = Lerp(p0, p1, t0, t1);
                var a2 = Lerp(p1, p2, t1, t2);
                var a3 = Lerp(p2, p3, t2, t3);
                var b1 = Lerp(a1, a2, t0, t2);
                var b2 = Lerp(a2, a3, t1, t3);
                result.Add(Lerp(b1, b2, t1, t2));
            }
        }
        result.Add(spline.Closed ? p[0] : p[^1]);
        return result;
    }

    /// <summary>
    /// The same stand-in curve as cubic Bezier spans (start, control 1, control 2, end): the
    /// Barry-Goldman construction above is a cubic Hermite span with these end tangents.
    /// </summary>
    public static List<(Pt P1, Pt C1, Pt C2, Pt P2)> Beziers(SplineEl spline)
    {
        var p = spline.Points;
        var n = p.Count;
        Pt Get(int i) => spline.Closed ? p[((i % n) + n) % n] : p[Math.Clamp(i, 0, n - 1)];
        var spans = spline.Closed ? n : n - 1;
        var result = new List<(Pt, Pt, Pt, Pt)>();
        for (var i = 0; i < spans; i++)
        {
            Pt p0 = Get(i - 1), p1 = Get(i), p2 = Get(i + 1), p3 = Get(i + 2);
            if (!spline.Closed && i == 0)
                p0 = p1 - (p2 - p1);
            if (!spline.Closed && i == spans - 1)
                p3 = p2 + (p2 - p1);
            double Gap(Pt a, Pt b) => Math.Sqrt(Math.Max(a.DistanceTo(b), 1e-12));
            double d0 = Gap(p0, p1), d1 = Gap(p1, p2), d2 = Gap(p2, p3);
            var m1 = (p1 - p0) * (1 / d0) - (p2 - p0) * (1 / (d0 + d1)) + (p2 - p1) * (1 / d1);
            var m2 = (p2 - p1) * (1 / d1) - (p3 - p1) * (1 / (d1 + d2)) + (p3 - p2) * (1 / d2);
            result.Add((p1, p1 + m1 * (d1 / 3), p2 - m2 * (d1 / 3), p2));
        }
        return result;
    }

    public static List<Pt> Sample(EllipseEl ellipse, int count = 180) =>
        Enumerable.Range(0, count + 1).Select(i => ellipse.At(2 * Math.PI * i / count)).ToList();
}
