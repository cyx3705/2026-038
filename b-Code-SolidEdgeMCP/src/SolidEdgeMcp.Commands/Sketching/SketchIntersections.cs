namespace SolidEdgeMcp.Commands.Sketching;

/// <summary>Exact pairwise crossings and overlaps; contact at a shared endpoint is a joint, not a defect.</summary>
public sealed partial class SketchAnalysis
{
    private static double Cross(Pt a, Pt b) => a.X * b.Y - a.Y * b.X;

    private static double Dot(Pt a, Pt b) => a.X * b.X + a.Y * b.Y;

    private static (Pt Center, double Radius)? Round(Element element) => element switch
    {
        ArcEl arc => (arc.Center, arc.Radius),
        CircleEl circle => (circle.Center, circle.Radius),
        _ => null,
    };

    private static bool OnCurve(Element element, Pt p, double tol) => element switch
    {
        ArcEl arc => arc.Contains(Math.Atan2(p.Y - arc.Center.Y, p.X - arc.Center.X), tol / arc.Radius),
        _ => true,
    };

    private void Intersections()
    {
        var region = Region.ToList();
        var size = Math.Max(Max.X - Min.X, Max.Y - Min.Y);
        // Root finding near tangency loses precision, so joints get a wider radius than node merging.
        var joint = Math.Max(1e-4, size * 1e-7);
        for (var i = 0; i < region.Count; i++)
        {
            for (var j = i + 1; j < region.Count; j++)
            {
                var (a, b) = (region[i], region[j]);
                var shared = new List<Pt>();
                if (Sketch.Ends(a) is { } ea && Sketch.Ends(b) is { } eb)
                {
                    foreach (var p in new[] { ea.Start, ea.End })
                    {
                        if (p.DistanceTo(eb.Start) <= _tol || p.DistanceTo(eb.End) <= _tol)
                            shared.Add(p);
                    }
                }
                var traced = a is EllipseEl or SplineEl || b is EllipseEl or SplineEl;
                var (points, overlap) = traced ? (TracedCrossings(a, b), false) : Crossings(a, b);
                if (overlap)
                {
                    Issues.Add(new JsonObject { ["type"] = "overlap", ["entities"] = new JsonArray(a.Id, b.Id) });
                    continue;
                }
                var reported = new List<Pt>();
                var near = traced ? Math.Max(joint, size * 1e-4) : joint;
                foreach (var p in points)
                {
                    if (!traced && (!OnCurve(a, p, _tol) || !OnCurve(b, p, _tol)))
                        continue;
                    if (shared.Any(s => s.DistanceTo(p) <= near) || reported.Any(r => r.DistanceTo(p) <= near))
                        continue;
                    reported.Add(p);
                    Issues.Add(new JsonObject { ["type"] = "intersection", ["entities"] = new JsonArray(a.Id, b.Id), ["at"] = Json(p) });
                }
            }
        }
    }

    /// <summary>Crossings of the traced polylines, for curves without a closed-form intersection.</summary>
    private static List<Pt> TracedCrossings(Element a, Element b)
    {
        var (pa, pb) = (Trace(a), Trace(b));
        var result = new List<Pt>();
        for (var i = 1; i < pa.Count; i++)
        {
            for (var j = 1; j < pb.Count; j++)
            {
                var r = pa[i] - pa[i - 1];
                var s = pb[j] - pb[j - 1];
                var denominator = Cross(r, s);
                if (Math.Abs(denominator) < 1e-15)
                    continue;
                var qp = pb[j - 1] - pa[i - 1];
                var t = Cross(qp, s) / denominator;
                var u = Cross(qp, r) / denominator;
                if (t >= 0 && t < 1 && u >= 0 && u < 1)
                    result.Add(pa[i - 1] + r * t);
            }
        }
        return result;
    }

    private (List<Pt> Points, bool Overlap) Crossings(Element a, Element b)
    {
        if (a is LineEl la && b is LineEl lb)
            return LineLine(la, lb);
        if (a is LineEl line && Round(b) is { } rb1)
            return (LineCircle(line, rb1.Center, rb1.Radius), false);
        if (b is LineEl line2 && Round(a) is { } ra1)
            return (LineCircle(line2, ra1.Center, ra1.Radius), false);
        var (ca, ra) = Round(a)!.Value;
        var (cb, rb) = Round(b)!.Value;
        return CircleCircle(a, ca, ra, b, cb, rb);
    }

    private (List<Pt>, bool) LineLine(LineEl a, LineEl b)
    {
        var r = a.End - a.Start;
        var s = b.End - b.Start;
        var qp = b.Start - a.Start;
        var denominator = Cross(r, s);
        if (Math.Abs(denominator) <= 1e-12 * r.Length * s.Length)
        {
            if (Math.Abs(Cross(qp, r)) / r.Length > _tol)
                return ([], false); // parallel, apart
            // Collinear: overlapping projections longer than the tolerance are a defect.
            var length = r.Length;
            var t0 = Dot(b.Start - a.Start, r) / length;
            var t1 = Dot(b.End - a.Start, r) / length;
            var overlap = Math.Min(length, Math.Max(t0, t1)) - Math.Max(0, Math.Min(t0, t1));
            return ([], overlap > _tol);
        }
        var t = Cross(qp, s) / denominator;
        var u = Cross(qp, r) / denominator;
        var slackA = _tol / r.Length;
        var slackB = _tol / s.Length;
        return t >= -slackA && t <= 1 + slackA && u >= -slackB && u <= 1 + slackB
            ? ([a.Start + r * t], false)
            : ([], false);
    }

    private List<Pt> LineCircle(LineEl line, Pt center, double radius)
    {
        var d = line.End - line.Start;
        var f = line.Start - center;
        double a = Dot(d, d), b = 2 * Dot(f, d), c = Dot(f, f) - radius * radius;
        var discriminant = b * b - 4 * a * c;
        var slack = _tol / Math.Sqrt(a);
        if (discriminant < -1e-12 * a * radius * radius)
            return [];
        var root = Math.Sqrt(Math.Max(0, discriminant));
        return new[] { (-b - root) / (2 * a), (-b + root) / (2 * a) }
            .Where(t => t >= -slack && t <= 1 + slack)
            .Select(t => line.Start + d * t)
            .ToList();
    }

    private (List<Pt>, bool) CircleCircle(Element a, Pt ca, double ra, Element b, Pt cb, double rb)
    {
        var distance = ca.DistanceTo(cb);
        if (distance <= _tol && Math.Abs(ra - rb) <= _tol)
            return ([], SameCircleOverlap(a, b));
        if (distance <= _tol || distance > ra + rb + _tol || distance < Math.Abs(ra - rb) - _tol)
            return ([], false);
        var along = (distance * distance + ra * ra - rb * rb) / (2 * distance);
        var h = Math.Sqrt(Math.Max(0, ra * ra - along * along));
        var unit = (cb - ca) * (1 / distance);
        var foot = ca + unit * along;
        var normal = new Pt(-unit.Y, unit.X);
        return ([foot + normal * h, foot - normal * h], false);
    }

    /// <summary>Two pieces of one circle overlap when either holds an interior point of the other.</summary>
    private bool SameCircleOverlap(Element a, Element b)
    {
        if (a is CircleEl || b is CircleEl)
            return true;
        var (x, y) = ((ArcEl)a, (ArcEl)b);
        bool Interior(ArcEl arc, ArcEl other) =>
            new[] { 0.25, 0.5, 0.75 }.Any(k => other.Contains(arc.StartAngle + arc.Sweep * k, -_tol / other.Radius));
        return Interior(x, y) || Interior(y, x);
    }
}
