using System.Text.Json;

namespace SolidEdgeMcp.Commands.Sketching;

/// <summary>Offset of a chain or loop, and the clean-up of relations and dimensions after edits.</summary>
public static partial class SketchEdit
{
    /// <summary>
    /// Offset a closed loop (positive distance grows it) or an open chain (positive goes to the
    /// left of the chain's direction). Lines shift, arcs keep their centers, and neighbours are
    /// re-joined where their offset curves meet, so sharp corners stay sharp.
    /// </summary>
    private static List<string> Offset(SketchSpec spec, JsonElement op, string where)
    {
        Field.Only(op, where, "op", "entities", "distance", "copy", "suffix");
        var selection = Select(spec, op, where, required: true);
        var distance = Field.Number(op, "distance", where);
        if (Math.Abs(distance) <= Sketch.Tolerance)
            throw new EdgeError($"{where}: distance must not be zero.");
        var (steps, closed) = Chain(selection.Select(i => spec.Elements[i]).ToList(), where);
        List<Element> made;
        if (steps is [{ Element: CircleEl circle }])
        {
            var radius = circle.Radius + distance;
            if (radius <= Sketch.Tolerance)
                throw new EdgeError($"{where}: offsetting {circle.Id} by {Sketch.Format(distance)} leaves no circle.");
            made = [circle with { Radius = radius, SeName = null }];
        }
        else
        {
            var left = distance;
            if (closed)
            {
                // Outward is to the right of a counterclockwise walk.
                var polygon = SketchAnalysis.Polygon(new Loop { Steps = steps, Closed = true });
                var area = polygon.Select((p, k) => Pt.Cross(p, polygon[(k + 1) % polygon.Count])).Sum() / 2;
                left = area > 0 ? -distance : distance;
            }
            made = OffsetChain(steps, closed, left, where);
        }
        var byId = made.ToDictionary(e => e.Id);
        if (Field.Flag(op, "copy", true))
        {
            var ordered = steps.Select(s => spec.Elements.FindIndex(e => e.Id == s.Element.Id)).ToList();
            return Copy(spec, ordered, e => byId[e.Id], Field.OptionalText(op, "suffix", where) ?? "_o", where);
        }
        foreach (var element in made)
            spec.Elements[spec.Elements.FindIndex(e => e.Id == element.Id)] = element;
        return made.Select(e => e.Id).ToList();
    }

    /// <summary>Order lines and arcs into one walk; a single circle is a loop by itself.</summary>
    private static (List<Step> Steps, bool Closed) Chain(List<Element> parts, string where)
    {
        if (parts is [CircleEl only])
            return ([new Step(only, true)], true);
        if (parts.Any(p => p is not (LineEl or ArcEl)))
            throw new EdgeError($"{where}: offset works on one chain of lines and arcs, or one circle.");
        var nodes = new List<Pt>();
        int Node(Pt p)
        {
            var i = nodes.FindIndex(n => n.DistanceTo(p) <= Sketch.Tolerance);
            if (i >= 0)
                return i;
            nodes.Add(p);
            return nodes.Count - 1;
        }
        var edges = parts.Select(p => (Element: p, From: Node(Sketch.Ends(p)!.Value.Start), To: Node(Sketch.Ends(p)!.Value.End))).ToList();
        var degree = new int[nodes.Count];
        foreach (var (_, from, to) in edges)
        {
            degree[from]++;
            degree[to]++;
        }
        if (degree.Any(d => d > 2))
            throw new EdgeError($"{where}: the elements branch; offset needs a single chain or loop.");
        var ends = Enumerable.Range(0, nodes.Count).Where(n => degree[n] == 1).ToList();
        if (ends.Count is not (0 or 2))
            throw new EdgeError($"{where}: the elements are not one connected chain.");
        var closed = ends.Count == 0;
        var at = closed ? edges[0].From : ends[0];
        var used = new bool[edges.Count];
        var steps = new List<Step>();
        while (steps.Count < edges.Count)
        {
            var next = Enumerable.Range(0, edges.Count).FirstOrDefault(j => !used[j] && (edges[j].From == at || edges[j].To == at), -1);
            if (next < 0)
                throw new EdgeError($"{where}: the elements are not one connected chain.");
            used[next] = true;
            var forward = edges[next].From == at;
            steps.Add(new Step(edges[next].Element, forward));
            at = forward ? edges[next].To : edges[next].From;
        }
        return (steps, closed);
    }

    private static List<Element> OffsetChain(List<Step> steps, bool closed, double left, string where)
    {
        var n = steps.Count;
        var carriers = new Element[n];
        var starts = new Pt[n];
        var ends = new Pt[n];
        var walks = new double[n];
        for (var k = 0; k < n; k++)
        {
            var element = steps[k].Element;
            var (s, e) = steps[k].Forward ? Sketch.Ends(element)!.Value : (Sketch.Ends(element)!.Value.End, Sketch.Ends(element)!.Value.Start);
            if (element is LineEl)
            {
                var t = (e - s).Unit;
                var shift = new Pt(-t.Y, t.X) * left;
                carriers[k] = new LineEl(element.Id, s + shift, e + shift);
                (starts[k], ends[k]) = (s + shift, e + shift);
            }
            else
            {
                var arc = (ArcEl)element;
                walks[k] = steps[k].Forward ? arc.Sweep : -arc.Sweep;
                // Walking counterclockwise keeps the center on the left: a left offset shrinks the arc.
                var radius = walks[k] > 0 ? arc.Radius - left : arc.Radius + left;
                if (radius <= Sketch.Tolerance)
                    throw new EdgeError($"{where}: the offset collapses arc {arc.Id} (radius {Sketch.Format(arc.Radius)}).");
                carriers[k] = new CircleEl(element.Id, arc.Center, radius);
                (starts[k], ends[k]) = (arc.Center + (s - arc.Center).Unit * radius, arc.Center + (e - arc.Center).Unit * radius);
            }
        }
        var points = new Pt[n + 1];
        points[0] = starts[0];
        points[n] = ends[n - 1];
        for (var k = 0; k < (closed ? n : n - 1); k++)
        {
            var next = (k + 1) % n;
            Pt joint;
            if (ends[k].DistanceTo(starts[next]) <= 1e-9 * Math.Max(1, Math.Abs(left)))
            {
                joint = (ends[k] + starts[next]) * 0.5; // tangent joint: the offsets still meet
            }
            else
            {
                var hits = Hits(carriers[k], carriers[next]);
                if (hits.Count == 0)
                    throw new EdgeError($"{where}: offset {steps[k].Element.Id} and {steps[next].Element.Id} no longer meet.");
                var guess = (ends[k] + starts[next]) * 0.5;
                joint = hits.OrderBy(h => h.DistanceTo(guess)).First();
            }
            points[k + 1] = joint;
            if (closed && next == 0)
                points[0] = joint;
        }
        if (closed)
            points[n] = points[0];
        var made = new List<Element>();
        for (var k = 0; k < n; k++)
        {
            var original = steps[k].Element;
            var (p, q) = (points[k], points[k + 1]);
            Element element;
            if (original is LineEl)
            {
                var before = steps[k].Forward ? ((LineEl)original).Direction : ((LineEl)original).Direction * -1;
                if (p.DistanceTo(q) <= Sketch.Tolerance || Pt.Dot(q - p, before) <= 0)
                    throw new EdgeError($"{where}: the offset collapses line {original.Id}.");
                element = new LineEl(original.Id, p, q);
            }
            else
            {
                var circle = (CircleEl)carriers[k];
                var a0 = p.AngleFrom(circle.Center);
                var sweep = walks[k] > 0 ? Sketch.Normalize(q.AngleFrom(circle.Center) - a0) : -Sketch.Normalize(a0 - q.AngleFrom(circle.Center));
                if (Math.Abs(sweep) <= 1e-9 || Math.Abs(sweep - walks[k]) > Math.PI)
                    throw new EdgeError($"{where}: the offset collapses arc {original.Id}.");
                element = new ArcEl(original.Id, circle.Center, circle.Radius, a0, sweep, p, q);
            }
            made.Add(element with { Construction = original.Construction, Group = original.Group });
        }
        return made;
    }

    /// <summary>
    /// After edits: drop relations and dimensions whose elements are gone, and relations that no
    /// longer hold (a rotated rectangle is no longer horizontal). Dimensions take the new values.
    /// Datum relations are judged later against the model, when the sketch is redrawn.
    /// </summary>
    private static JsonArray Reconcile(SketchSpec spec)
    {
        var dropped = new JsonArray();
        var known = spec.Elements.Select(e => e.Id).Concat(spec.References.Select(r => r.Id))
            .Concat([SketchSpec.XAxis, SketchSpec.YAxis, SketchSpec.Origin]).ToHashSet(StringComparer.Ordinal);
        foreach (var relation in spec.Relations.Where(r => r.Ids.Any(id => !known.Contains(id))).ToList())
        {
            spec.Relations.Remove(relation);
            dropped.Add(new JsonObject { ["relation"] = relation.Json(), ["reason"] = "an element it names was removed" });
        }
        foreach (var dimension in spec.Dimensions.Where(d => d.Ids.Any(id => !known.Contains(id))).ToList())
        {
            spec.Dimensions.Remove(dimension);
            dropped.Add(new JsonObject { ["dimension"] = dimension.Json(), ["reason"] = "an element it names was removed" });
        }
        var plan = Sketches.Plan(spec);
        foreach (var (relation, failure) in plan.RelationResults.Where(r => r.Failure is not null && !r.Relation.Ids.Any(id => id.StartsWith('@'))))
        {
            spec.Relations.Remove(relation);
            dropped.Add(new JsonObject { ["relation"] = relation.Json(), ["reason"] = failure });
        }
        foreach (var (dimension, _, failure) in plan.DimensionValues.Where(d => d.Failure is not null && !d.Dimension.Ids.Any(id => id.StartsWith('@'))))
        {
            spec.Dimensions.Remove(dimension);
            dropped.Add(new JsonObject { ["dimension"] = dimension.Json(), ["reason"] = failure });
        }
        return dropped;
    }
}
