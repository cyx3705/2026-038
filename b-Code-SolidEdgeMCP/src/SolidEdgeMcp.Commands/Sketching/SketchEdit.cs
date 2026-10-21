using System.Text.Json;

namespace SolidEdgeMcp.Commands.Sketching;

/// <summary>
/// Edits of a remembered description: transforms, mirror, offset, trim, extend, split, corners,
/// deletions and additions. Every edit is exact geometry computed here; the sketch is then
/// redrawn and verified like a new one. (Solid Edge's own Offset2d silently does nothing once a
/// profile is ended, so edits never rely on in-place COM operations.)
/// </summary>
public static partial class SketchEdit
{
    public sealed record Outcome(SketchSpec Spec, JsonArray Applied, JsonArray Dropped);

    private static readonly string[] Operations = ["move", "rotate", "scale", "mirror", "offset", "delete", "add", "split", "trim", "extend", "corner"];

    public static Outcome Apply(SketchSpec original, JsonElement operations)
    {
        if (operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() == 0)
            throw new EdgeError("operations must be a non-empty array.");
        var spec = original.Clone();
        var applied = new JsonArray();
        var index = 0;
        foreach (var op in operations.EnumerateArray())
        {
            var where = $"operations[{index++}]";
            if (op.ValueKind != JsonValueKind.Object)
                throw new EdgeError($"{where}: expected an object.");
            var name = Field.Text(op, "op", where);
            where = $"{where} ({name})";
            var touched = name switch
            {
                "move" => Transform(spec, op, where, "by"),
                "rotate" => Transform(spec, op, where, "center", "angle"),
                "scale" => Transform(spec, op, where, "center", "factor"),
                "mirror" => Mirror(spec, op, where),
                "offset" => Offset(spec, op, where),
                "delete" => Delete(spec, op, where),
                "add" => Add(spec, op, where),
                "split" => Split(spec, op, where),
                "trim" => Trim(spec, op, where),
                "extend" => Extend(spec, op, where),
                "corner" => Corner(spec, op, where),
                _ => throw new EdgeError($"{where}: op must be one of {string.Join(", ", Operations)}."),
            };
            applied.Add(new JsonObject { ["op"] = name, ["entities"] = new JsonArray(touched.Select(t => (JsonNode)t).ToArray()) });
        }
        var dropped = Reconcile(spec);
        return new Outcome(spec, applied, dropped);
    }

    // ---- selection and copying -------------------------------------------------------------

    private static List<int> Select(SketchSpec spec, JsonElement op, string where, bool required = false)
    {
        var ids = Field.Ids(op, where);
        if (ids.Count == 0)
        {
            if (required)
                throw new EdgeError($"{where}: name the elements in entities.");
            return spec.Elements.Select((e, i) => (e, i)).Where(x => !x.e.External).Select(x => x.i).ToList();
        }
        return ids.Select(id =>
        {
            var i = spec.Elements.FindIndex(e => e.Id == id);
            return i >= 0 && !spec.Elements[i].External ? i : throw new EdgeError($"{where}: {id} is not an element of this sketch.");
        }).Distinct().ToList();
    }

    private static Element Map(Element element, Func<Pt, Pt> f, double scale)
    {
        Element result = element switch
        {
            LineEl l => l with { Start = f(l.Start), End = f(l.End) },
            ArcEl a => ArcEl.Through(a.Id, f(a.Start), f(a.Mid), f(a.End))!,
            CircleEl c => c with { Center = f(c.Center), Radius = c.Radius * scale },
            EllipseEl e => e with
            {
                Center = f(e.Center),
                MajorRadius = e.MajorRadius * scale,
                MinorRadius = e.MinorRadius * scale,
                Angle = (f(e.At(0)) - f(e.Center)).AngleFrom(new Pt(0, 0)),
            },
            SplineEl s => new SplineEl(s.Id, s.Points.Select(f).ToList(), s.Closed),
            PointEl p => p with { At = f(p.At) },
            _ => element,
        };
        return result with { Construction = element.Construction, Group = element.Group, SeName = null };
    }

    /// <summary>Copies of the selected elements under new ids, with the relations among them copied too.</summary>
    private static List<string> Copy(SketchSpec spec, List<int> selection, Func<Element, Element> make, string suffix, string where)
    {
        var map = selection.ToDictionary(i => spec.Elements[i].Id, i => spec.Elements[i].Id + suffix);
        var copies = selection.Select(i =>
        {
            var copy = make(spec.Elements[i]);
            return copy with { Id = map[spec.Elements[i].Id], Group = copy.Group is null ? null : copy.Group + suffix };
        }).ToList();
        foreach (var copy in copies)
            spec.Add(copy, where);
        string Rename(string id) => map.TryGetValue(id, out var renamed) ? renamed : id;
        foreach (var relation in spec.Relations.Where(r => r.Ids.All(map.ContainsKey)).ToList())
        {
            spec.Relations.Add(relation with
            {
                Entities = relation.Entities.Select(Rename).ToList(),
                Points = relation.Points.Select(p => p with { Id = Rename(p.Id) }).ToList(),
                Axis = relation.Axis is null ? null : Rename(relation.Axis),
                Target = relation.Target is null ? null : Rename(relation.Target),
            });
        }
        return copies.Select(c => c.Id).ToList();
    }

    // ---- transforms ---------------------------------------------------------------------------

    private static List<string> Transform(SketchSpec spec, JsonElement op, string where, params string[] fields)
    {
        Field.Only(op, where, ["op", "entities", .. fields]);
        var selection = Select(spec, op, where);
        Func<Pt, Pt> f;
        var scale = 1.0;
        if (fields[0] == "by")
        {
            var by = Field.Point(op, "by", where);
            f = p => p + by;
        }
        else
        {
            var center = op.TryGetProperty("center", out _) ? Field.Point(op, "center", where) : new Pt(0, 0);
            if (fields[1] == "angle")
            {
                var angle = Sketch.Rad(Field.Number(op, "angle", where));
                double c = Math.Cos(angle), s = Math.Sin(angle);
                f = p => center + new Pt((p.X - center.X) * c - (p.Y - center.Y) * s, (p.X - center.X) * s + (p.Y - center.Y) * c);
            }
            else
            {
                scale = Field.Positive(op, "factor", where);
                f = p => center + (p - center) * scale;
            }
        }
        foreach (var i in selection)
            spec.Elements[i] = Map(spec.Elements[i], f, scale);
        return selection.Select(i => spec.Elements[i].Id).ToList();
    }

    private static List<string> Mirror(SketchSpec spec, JsonElement op, string where)
    {
        Field.Only(op, where, "op", "entities", "axis", "copy", "suffix");
        var selection = Select(spec, op, where);
        LineEl axis;
        string? axisId = null;
        if (op.TryGetProperty("axis", out var a) && a.ValueKind == JsonValueKind.String)
        {
            axisId = a.GetString()!;
            axis = axisId switch
            {
                // The plane's own axes through its origin.
                SketchSpec.XAxis => new LineEl(axisId, new Pt(0, 0), new Pt(1, 0)),
                SketchSpec.YAxis => new LineEl(axisId, new Pt(0, 0), new Pt(0, 1)),
                _ => spec.Find(axisId) as LineEl ?? throw new EdgeError($"{where}: axis {axisId} must be a line of this sketch."),
            };
            if (axisId.StartsWith('@'))
                axisId = null;
        }
        else if (a.ValueKind == JsonValueKind.Array)
        {
            var points = Field.PointList(op, "axis", where, 2);
            axis = SketchShapes.Line("axis", points[0], points[1], where);
        }
        else
        {
            throw new EdgeError($"{where}: axis must be a line id, @x_axis, @y_axis, or [[x1, y1], [x2, y2]].");
        }
        selection.Remove(spec.Elements.FindIndex(e => e.Id == axisId));
        var d = axis.Direction;
        Pt Reflect(Pt p)
        {
            var offset = p - axis.Start;
            return axis.Start + d * (2 * Pt.Dot(offset, d)) - offset;
        }
        if (!Field.Flag(op, "copy", true))
        {
            foreach (var i in selection)
                spec.Elements[i] = Map(spec.Elements[i], Reflect, 1);
            return selection.Select(i => spec.Elements[i].Id).ToList();
        }
        var originals = selection.Select(i => spec.Elements[i]).ToList();
        var copies = Copy(spec, selection, e => Map(e, Reflect, 1), Field.OptionalText(op, "suffix", where) ?? "_m", where);
        if (axisId is not null)
        {
            // A mirror about a sketch line keeps its intent: each copy is symmetric to its original.
            foreach (var (original, copy) in originals.Zip(copies))
            {
                if (original is PointEl or LineEl or ArcEl or CircleEl)
                    spec.Relations.Add(new Relation("symmetric", [original.Id, copy], [], axisId));
            }
        }
        return copies;
    }

    private static List<string> Delete(SketchSpec spec, JsonElement op, string where)
    {
        Field.Only(op, where, "op", "entities");
        var ids = Select(spec, op, where, required: true).Select(i => spec.Elements[i].Id).ToList();
        spec.Elements.RemoveAll(e => ids.Contains(e.Id));
        if (spec.Axis is { } axis && ids.Contains(axis))
            spec.Axis = null;
        return ids;
    }

    private static List<string> Add(SketchSpec spec, JsonElement op, string where)
    {
        var before = spec.Elements.Select(e => e.Id).ToHashSet();
        var body = new JsonObject();
        foreach (var property in op.EnumerateObject().Where(p => p.Name != "op"))
            body[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        SketchSpec.Read(JsonSerializer.SerializeToElement(body), spec);
        return spec.Elements.Where(e => !before.Contains(e.Id)).Select(e => e.Id).ToList();
    }

    private static List<string> Corner(SketchSpec spec, JsonElement op, string where)
    {
        var body = new JsonObject();
        foreach (var property in op.EnumerateObject().Where(p => p.Name != "op"))
            body[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        var before = spec.Elements.Select(e => e.Id).ToHashSet();
        SketchShapes.Apply(spec, SketchShapes.ReadCorner(JsonSerializer.SerializeToElement(body), where));
        return spec.Elements.Where(e => !before.Contains(e.Id)).Select(e => e.Id).ToList();
    }

    // ---- curve arithmetic for split, trim, extend, offset ---------------------------------------

    /// <summary>Position along an element in [0, 1]: by length for lines, by sweep for arcs.</summary>
    private static double Param(Element element, Pt p) => element switch
    {
        LineEl l => Pt.Dot(p - l.Start, l.End - l.Start) / Math.Pow(l.Length, 2),
        ArcEl a => Sketch.Normalize(a.Sweep >= 0 ? p.AngleFrom(a.Center) - a.StartAngle : a.StartAngle - p.AngleFrom(a.Center)) / Math.Abs(a.Sweep),
        _ => throw new InvalidOperationException(),
    };

    /// <summary>The piece of a line or arc between two parameters (which may lie beyond [0, 1] for arcs up to a full turn).</summary>
    private static Element Piece(Element element, double t0, double t1, string id) => element switch
    {
        LineEl l => l with { Id = id, Start = l.Start + (l.End - l.Start) * t0, End = l.Start + (l.End - l.Start) * t1, SeName = null },
        ArcEl a => ArcEl.FromAngles(id, a.Center, a.Radius, a.StartAngle + a.Sweep * t0, a.Sweep * (t1 - t0))
            with { Construction = a.Construction, Group = a.Group },
        _ => throw new InvalidOperationException(),
    };

    /// <summary>Where two curves (lines extended, circles full) cross.</summary>
    private static List<Pt> Hits(Element a, Element b)
    {
        (Pt C, double R)? Round(Element e) => e switch
        {
            ArcEl arc => (arc.Center, arc.Radius),
            CircleEl circle => (circle.Center, circle.Radius),
            _ => null,
        };
        if (a is LineEl la && b is LineEl lb)
            return SketchAnalysis.Intersection(la, lb) is { } p ? [p] : [];
        if (a is LineEl line && Round(b) is { } rb)
            return LineCircle(line, rb.C, rb.R);
        if (b is LineEl line2 && Round(a) is { } ra)
            return LineCircle(line2, ra.C, ra.R);
        if (Round(a) is { } r1 && Round(b) is { } r2)
            return CircleCircle(r1.C, r1.R, r2.C, r2.R);
        return [];
    }

    private static List<Pt> LineCircle(LineEl line, Pt center, double radius)
    {
        var d = line.Direction;
        var foot = line.Start + d * Pt.Dot(center - line.Start, d);
        var h2 = radius * radius - Math.Pow(foot.DistanceTo(center), 2);
        if (h2 < -1e-12)
            return [];
        var h = Math.Sqrt(Math.Max(0, h2));
        return h < 1e-12 ? [foot] : [foot - d * h, foot + d * h];
    }

    private static List<Pt> CircleCircle(Pt c1, double r1, Pt c2, double r2)
    {
        var distance = c1.DistanceTo(c2);
        if (distance < 1e-12 || distance > r1 + r2 + 1e-12 || distance < Math.Abs(r1 - r2) - 1e-12)
            return [];
        var along = (distance * distance + r1 * r1 - r2 * r2) / (2 * distance);
        var h = Math.Sqrt(Math.Max(0, r1 * r1 - along * along));
        var unit = (c2 - c1) * (1 / distance);
        var foot = c1 + unit * along;
        var normal = new Pt(-unit.Y, unit.X);
        return h < 1e-12 ? [foot] : [foot + normal * h, foot - normal * h];
    }

    /// <summary>True when p lies on the element itself (not just its carrier).</summary>
    private static bool On(Element element, Pt p) => element switch
    {
        LineEl l => Param(l, p) is var t && t >= -1e-9 && t <= 1 + 1e-9,
        ArcEl a => a.Contains(p.AngleFrom(a.Center), 1e-9),
        CircleEl => true,
        _ => false,
    };

    private static int IndexOf(SketchSpec spec, JsonElement op, string field, string where)
    {
        var id = Field.Text(op, field, where);
        var i = spec.Elements.FindIndex(e => e.Id == id);
        return i >= 0 && !spec.Elements[i].External ? i : throw new EdgeError($"{where}: {id} is not an element of this sketch.");
    }

    private static string FreshId(SketchSpec spec, string id)
    {
        for (var k = 2; ; k++)
        {
            if (spec.Find($"{id}_{k}") is null)
                return $"{id}_{k}";
        }
    }

    private static List<string> Split(SketchSpec spec, JsonElement op, string where)
    {
        Field.Only(op, where, "op", "entity", "at", "new_id");
        var i = IndexOf(spec, op, "entity", where);
        var element = spec.Elements[i];
        if (element is not (LineEl or ArcEl))
            throw new EdgeError($"{where}: split works on lines and arcs; trim a circle instead.");
        var t = Param(element, Field.Point(op, "at", where));
        if (t <= 1e-9 || t >= 1 - 1e-9)
            throw new EdgeError($"{where}: at must lie inside {element.Id}, not at or past an end.");
        var second = Field.OptionalText(op, "new_id", where) ?? FreshId(spec, element.Id);
        spec.Elements[i] = Piece(element, 0, t, element.Id);
        spec.Elements.Insert(i + 1, Piece(element, t, 1, second));
        if (spec.Elements.Count(e => e.Id == second) > 1)
            throw new EdgeError($"{where}: id {second} is already used.");
        return [element.Id, second];
    }

    /// <summary>Remove the part of an element between its nearest crossings on either side of pick, like CAD trim.</summary>
    private static List<string> Trim(SketchSpec spec, JsonElement op, string where)
    {
        Field.Only(op, where, "op", "entity", "pick");
        var i = IndexOf(spec, op, "entity", where);
        var element = spec.Elements[i];
        var pick = Field.Point(op, "pick", where);
        var others = spec.Elements.Where(e => e.Id != element.Id && e is LineEl or ArcEl or CircleEl).ToList();
        if (element is CircleEl circle)
        {
            var angles = others.SelectMany(o => Hits(circle, o).Where(p => On(o, p)))
                .Select(p => Sketch.Normalize(p.AngleFrom(circle.Center))).Distinct().OrderBy(x => x).ToList();
            if (angles.Count < 2)
                throw new EdgeError($"{where}: {circle.Id} needs at least two crossings to trim.");
            var at = Sketch.Normalize(pick.AngleFrom(circle.Center));
            var after = angles.FirstOrDefault(x => x > at, angles[0]);
            var before = angles.LastOrDefault(x => x < at, angles[^1]);
            // Keep the arc from the crossing after pick round to the crossing before it.
            spec.Elements[i] = ArcEl.FromAngles(circle.Id, circle.Center, circle.Radius, after, Sketch.Normalize(before - after))
                with { Construction = circle.Construction, Group = circle.Group };
            return [circle.Id];
        }
        if (element is not (LineEl or ArcEl))
            throw new EdgeError($"{where}: trim works on lines, arcs and circles.");
        var cuts = others.SelectMany(o => Hits(element, o).Where(p => On(o, p) && On(element, p)))
            .Select(p => Param(element, p)).Where(t => t > 1e-9 && t < 1 - 1e-9).Distinct().OrderBy(t => t).ToList();
        var tp = Param(element, pick);
        var lo = cuts.LastOrDefault(t => t < tp, 0);
        var hi = cuts.FirstOrDefault(t => t > tp, 1);
        if (lo == 0 && hi == 1)
        {
            spec.Elements.RemoveAt(i);
            return [element.Id];
        }
        if (lo == 0)
        {
            spec.Elements[i] = Piece(element, hi, 1, element.Id);
            return [element.Id];
        }
        if (hi == 1)
        {
            spec.Elements[i] = Piece(element, 0, lo, element.Id);
            return [element.Id];
        }
        var second = FreshId(spec, element.Id);
        spec.Elements[i] = Piece(element, 0, lo, element.Id);
        spec.Elements.Insert(i + 1, Piece(element, hi, 1, second));
        return [element.Id, second];
    }

    /// <summary>Lengthen a line or arc at one end to the nearest crossing with the target (or any element).</summary>
    private static List<string> Extend(SketchSpec spec, JsonElement op, string where)
    {
        Field.Only(op, where, "op", "entity", "end", "to");
        var i = IndexOf(spec, op, "entity", where);
        var element = spec.Elements[i];
        if (element is not (LineEl or ArcEl))
            throw new EdgeError($"{where}: extend works on lines and arcs.");
        var atEnd = (Field.OptionalText(op, "end", where) ?? "end") switch
        {
            "end" => true,
            "start" => false,
            _ => throw new EdgeError($"{where}: end must be start or end."),
        };
        var targets = Field.OptionalText(op, "to", where) is { } to
            ? [spec.Find(to) ?? throw new EdgeError($"{where}: {to} is not an element of this sketch.")]
            : spec.Elements.Where(e => e.Id != element.Id && e is LineEl or ArcEl or CircleEl).ToList();
        double Beyond(Pt p)
        {
            if (element is LineEl)
                return Param(element, p);
            // For arcs, measure past the end by angle, up to a full turn.
            var a = (ArcEl)element;
            var offset = Sketch.Normalize(a.Sweep >= 0 ? p.AngleFrom(a.Center) - a.StartAngle : a.StartAngle - p.AngleFrom(a.Center));
            var t = offset / Math.Abs(a.Sweep);
            return atEnd ? t : t - 2 * Math.PI / Math.Abs(a.Sweep);
        }
        var candidates = targets.SelectMany(o => Hits(element, o).Where(p => On(o, p))).Select(Beyond)
            .Where(t => atEnd ? t > 1 + 1e-9 : t < -1e-9).ToList();
        if (candidates.Count == 0)
            throw new EdgeError($"{where}: extending {element.Id} at its {(atEnd ? "end" : "start")} meets nothing.");
        var reach = atEnd ? candidates.Min() : candidates.Max();
        if (element is ArcEl arc && Math.Abs(arc.Sweep) * (atEnd ? reach : 1 - reach) >= 2 * Math.PI - 1e-9)
            throw new EdgeError($"{where}: extending {arc.Id} would close a full circle.");
        spec.Elements[i] = atEnd ? Piece(element, 0, reach, element.Id) : Piece(element, reach, 1, element.Id);
        return [element.Id];
    }
}
