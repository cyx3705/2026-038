namespace SolidEdgeMcp.Commands.Sketching;

/// <summary>Solid Edge side of a sketch: draw elements into a profile and read a profile back.</summary>
public static class SketchCom
{
    // fwksupp.tlb KeypointIndexConstants: igLineStart=0, igLineEnd=1, igArcStart=1, igArcEnd=2.
    private static (int Start, int End) KeypointIndex(Element element) => element is ArcEl ? (1, 2) : (0, 1);

    // ProfileValidationType: igProfileClosed | igProfileNoSelfIntersect | igProfileAllowNested.
    public const int Validation = 1 | 8 | 8192;

    private const double M = 1000; // COM works in meters

    /// <summary>Create every element and tie coincident endpoints with keypoint relations.</summary>
    public static void Draw(dynamic profile, IReadOnlyList<Element> elements)
    {
        var created = new List<(Element Element, object Com)>();
        foreach (var element in elements)
        {
            object com = element switch
            {
                LineEl line => profile.Lines2d.AddBy2Points(line.Start.X / M, line.Start.Y / M, line.End.X / M, line.End.Y / M),
                // Three points fix the arc's side unambiguously, whatever the API's orientation convention.
                ArcEl arc => profile.Arcs2d.AddByStartAlongEnd(arc.Start.X / M, arc.Start.Y / M, arc.Mid.X / M, arc.Mid.Y / M,
                    arc.End.X / M, arc.End.Y / M),
                CircleEl circle => profile.Circles2d.AddByCenterRadius(circle.Center.X / M, circle.Center.Y / M, circle.Radius / M),
                _ => throw new EdgeError($"Unsupported element {element.Kind}."),
            };
            created.Add((element, com));
        }
        // Coincident coordinates alone can fail End(); every joint needs an explicit relation.
        var ends = new List<(Pt At, object Com, int Index)>();
        foreach (var (element, com) in created)
        {
            var (startIndex, endIndex) = KeypointIndex(element);
            switch (element)
            {
                case LineEl line:
                    ends.Add((line.Start, com, startIndex));
                    ends.Add((line.End, com, endIndex));
                    break;
                case ArcEl arc:
                    ends.Add((arc.Start, com, startIndex));
                    ends.Add((arc.End, com, endIndex));
                    break;
            }
        }
        var joined = new bool[ends.Count];
        for (var i = 0; i < ends.Count; i++)
        {
            for (var j = i + 1; j < ends.Count; j++)
            {
                if (joined[j] || ReferenceEquals(ends[i].Com, ends[j].Com) || ends[i].At.DistanceTo(ends[j].At) > Sketch.Tolerance)
                    continue;
                profile.Relations2d.AddKeypoint(ends[i].Com, ends[i].Index, ends[j].Com, ends[j].Index);
                joined[j] = true;
            }
        }
    }

    private static string Name(object element)
    {
        try
        {
            return Str(((dynamic)element).Name);
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>Read a profile's lines, arcs and circles; ids are Solid Edge's names.</summary>
    public static List<Element> ReadBack(dynamic profile)
    {
        var result = new List<Element>();
        static string Id(string kind, int ordinal, string seName) => seName.Length > 0 ? seName : $"{kind}{ordinal + 1}";

        var lines = profile.Lines2d;
        int count = Int(lines.Count);
        for (var i = 1; i <= count; i++)
        {
            var line = lines.Item(i);
            double x0 = 0, y0 = 0, x1 = 0, y1 = 0;
            line.GetStartPoint(out x0, out y0);
            line.GetEndPoint(out x1, out y1);
            string name = Name(line);
            result.Add(new LineEl(Id("line", i - 1, name), new Pt(x0 * M, y0 * M), new Pt(x1 * M, y1 * M)) { SeName = name });
        }

        var arcs = profile.Arcs2d;
        count = Int(arcs.Count);
        for (var i = 1; i <= count; i++)
        {
            var arc = arcs.Item(i);
            double cx = 0, cy = 0, sx = 0, sy = 0, ex = 0, ey = 0;
            arc.GetCenterPoint(out cx, out cy);
            arc.GetStartPoint(out sx, out sy);
            arc.GetEndPoint(out ex, out ey);
            double sweep = Math.Abs(Double(arc.SweepAngle));
            var center = new Pt(cx * M, cy * M);
            var start = new Pt(sx * M, sy * M);
            var end = new Pt(ex * M, ey * M);
            var a0 = Math.Atan2(start.Y - center.Y, start.X - center.X);
            var ccw = Sketch.Normalize(Math.Atan2(end.Y - center.Y, end.X - center.X) - a0);
            // Start/end are certain; the sweep's magnitude decides which way round the arc runs.
            var signed = Math.Abs(ccw - sweep) <= Math.Abs(2 * Math.PI - ccw - sweep) ? ccw : ccw - 2 * Math.PI;
            string name = Name(arc);
            result.Add(new ArcEl(Id("arc", i - 1, name), center, Double(arc.Radius) * M, a0, signed, start, end) { SeName = name });
        }

        var circles = profile.Circles2d;
        count = Int(circles.Count);
        for (var i = 1; i <= count; i++)
        {
            var circle = circles.Item(i);
            double cx = 0, cy = 0;
            circle.GetCenterPoint(out cx, out cy);
            string name = Name(circle);
            result.Add(new CircleEl(Id("circle", i - 1, name), new Pt(cx * M, cy * M), Double(circle.Radius) * M) { SeName = name });
        }

        return result;
    }

    /// <summary>Largest distance between intended and read-back defining points, in mm.</summary>
    public static double Deviation(IReadOnlyList<Element> intended, IReadOnlyList<Element> actual)
    {
        var worst = 0.0;
        foreach (var want in intended)
        {
            var got = actual.FirstOrDefault(a => a.Id == want.Id && a.Kind == want.Kind);
            if (got is null)
                return double.PositiveInfinity;
            var d = (want, got) switch
            {
                (LineEl w, LineEl g) => Math.Max(w.Start.DistanceTo(g.Start), w.End.DistanceTo(g.End)),
                // An arc may come back with start and end swapped; compare the end points as a set.
                (ArcEl w, ArcEl g) => Math.Max(w.Center.DistanceTo(g.Center), Math.Min(
                    Math.Max(w.Start.DistanceTo(g.Start), w.End.DistanceTo(g.End)),
                    Math.Max(w.Start.DistanceTo(g.End), w.End.DistanceTo(g.Start)))),
                (CircleEl w, CircleEl g) => Math.Max(w.Center.DistanceTo(g.Center), Math.Abs(w.Radius - g.Radius)),
                _ => double.PositiveInfinity,
            };
            worst = Math.Max(worst, d);
        }
        return worst;
    }
}
