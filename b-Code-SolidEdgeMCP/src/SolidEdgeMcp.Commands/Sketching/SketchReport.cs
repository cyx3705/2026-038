using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using static SolidEdgeMcp.Commands.Sketching.Sketch;

namespace SolidEdgeMcp.Commands.Sketching;

/// <summary>
/// Turns an analysed sketch into the command result: structured JSON plus an SVG drawn from the
/// same geometry (y up, plane-local mm), and on request a PNG of that drawing.
/// </summary>
public static class SketchReport
{
    private const string Fill = "#dce9f5";
    private const string Ink = "#1f4e79";
    private const string Fault = "#c0392b";
    private const string Faint = "#8a8a8a";
    private const string Note = "#7a4f01";

    public static JsonObject Json(SketchAnalysis analysis)
    {
        var size = analysis.Max - analysis.Min;
        var json = new JsonObject
        {
            ["entities"] = new JsonArray(analysis.Elements.Select(e => (JsonNode)Entity(e)).ToArray()),
            ["loops"] = new JsonArray(analysis.Loops.Select(l => (JsonNode)LoopJson(l)).ToArray()),
            ["net_area_mm2"] = Round(analysis.NetArea),
            ["bbox_mm"] = new JsonObject
            {
                ["min"] = SketchAnalysis.Json(analysis.Min),
                ["max"] = SketchAnalysis.Json(analysis.Max),
                ["size"] = SketchAnalysis.Json(size),
            },
        };
        if (analysis.Approximate)
            json["approximate"] = "Splines are measured along a stand-in curve through their points; areas are close, not exact.";
        if (analysis.Spec is { } spec)
        {
            if (spec.Axis is not null)
                json["axis"] = spec.Axis;
            if (analysis.RelationResults.Count > 0)
                json["relations"] = new JsonArray(analysis.RelationResults.Select(r => (JsonNode)RelationJson(r.Relation, r.Failure)).ToArray());
            if (analysis.DimensionValues.Count > 0)
                json["dimensions"] = new JsonArray(analysis.DimensionValues.Select(d => (JsonNode)DimensionJson(d.Dimension, d.Value, d.Failure)).ToArray());
        }
        var references = analysis.References.Where(r => !r.Id.StartsWith('@')).ToList();
        if (references.Count > 0)
            json["references"] = new JsonArray(references.Select(r => (JsonNode)Entity(r)).ToArray());
        json["issues"] = analysis.Issues.DeepClone();
        json["svg"] = Svg(analysis);
        return json;
    }

    private static JsonObject RelationJson(Relation relation, string? failure)
    {
        var json = relation.Json();
        json["satisfied"] = failure is null;
        if (failure is not null)
            json["detail"] = failure;
        return json;
    }

    public static string Unit(Dimension dimension) => dimension.Angular ? "deg" : "mm";

    private static JsonObject DimensionJson(Dimension dimension, double? value, string? failure)
    {
        var json = dimension.Json();
        json["value"] = value is null ? null : Round(value.Value);
        json["unit"] = Unit(dimension);
        if (failure is not null)
            json["detail"] = failure;
        return json;
    }

    public static JsonObject Entity(Element element)
    {
        var json = new JsonObject { ["id"] = element.Id };
        if (element.SeName is { } name)
            json["se_name"] = name;
        json["type"] = element.Kind;
        switch (element)
        {
            case LineEl line:
                json["start"] = SketchAnalysis.Json(line.Start);
                json["end"] = SketchAnalysis.Json(line.End);
                json["length"] = Round(line.Length);
                break;
            case ArcEl arc:
                json["center"] = SketchAnalysis.Json(arc.Center);
                json["radius"] = Round(arc.Radius);
                json["start"] = SketchAnalysis.Json(arc.Start);
                json["end"] = SketchAnalysis.Json(arc.End);
                json["start_angle"] = Round(Deg(Normalize(arc.StartAngle)));
                json["sweep_angle"] = Round(Deg(arc.Sweep));
                json["length"] = Round(Math.Abs(arc.Sweep) * arc.Radius);
                break;
            case CircleEl circle:
                json["center"] = SketchAnalysis.Json(circle.Center);
                json["radius"] = Round(circle.Radius);
                break;
            case EllipseEl ellipse:
                json["center"] = SketchAnalysis.Json(ellipse.Center);
                json["major_radius"] = Round(ellipse.MajorRadius);
                json["minor_radius"] = Round(ellipse.MinorRadius);
                json["angle"] = Round(Deg(ellipse.Angle));
                break;
            case SplineEl spline:
                json["points"] = new JsonArray(spline.Points.Select(p => (JsonNode)SketchAnalysis.Json(p)).ToArray());
                json["closed"] = spline.Closed;
                break;
            case PointEl point:
                json["at"] = SketchAnalysis.Json(point.At);
                break;
        }
        if (element.Construction)
            json["construction"] = true;
        if (element.External)
            json["external"] = true;
        if (element.Group is { } group)
            json["group"] = group;
        return json;
    }

    private static JsonObject LoopJson(Loop loop)
    {
        var json = new JsonObject
        {
            ["entities"] = new JsonArray(loop.Ids.Select(id => (JsonNode)id).ToArray()),
            ["closed"] = loop.Closed,
            ["role"] = loop.Role,
        };
        if (loop.Closed)
        {
            json["orientation"] = loop.SignedArea >= 0 ? "ccw" : "cw";
            json["area_mm2"] = Round(Math.Abs(loop.SignedArea));
            json["centroid_mm"] = SketchAnalysis.Json(loop.Centroid);
        }
        return json;
    }

    // ---- shared drawing helpers -----------------------------------------------------------

    private sealed record Frame(double X0, double Top, double Width, double Height, double Size);

    /// <summary>Drawing frame: sketch bounds plus the plane origin, with a margin.</summary>
    private static Frame View(SketchAnalysis analysis)
    {
        var min = new Pt(Math.Min(analysis.Min.X, 0), Math.Min(analysis.Min.Y, 0));
        var max = new Pt(Math.Max(analysis.Max.X, 0), Math.Max(analysis.Max.Y, 0));
        var size = Math.Max(Math.Max(max.X - min.X, max.Y - min.Y), 1e-3);
        var pad = size * 0.08;
        return new Frame(min.X - pad, max.Y + pad, max.X - min.X + 2 * pad, max.Y - min.Y + 2 * pad, size);
    }

    private static HashSet<string> Faulty(SketchAnalysis analysis) => analysis.Issues
        .SelectMany(i => i!["entities"]?.AsArray().Select(n => (string)n!) ?? [])
        .ToHashSet(StringComparer.Ordinal);

    private static Pt LabelAnchor(Element element) => element switch
    {
        LineEl line => (line.Start + line.End) * 0.5,
        ArcEl arc => arc.Mid,
        CircleEl circle => Pt.Polar(circle.Center, circle.Radius, Math.PI / 4),
        EllipseEl ellipse => ellipse.At(Math.PI / 4),
        SplineEl spline => SketchAnalysis.Trace(spline)[SketchAnalysis.Trace(spline).Count / 2],
        PointEl point => point.At,
        _ => default,
    };

    /// <summary>Where a dimension's text goes and what it says.</summary>
    private static (Pt At, string Text)? DimensionLabel(SketchAnalysis analysis, Dimension dimension, double? value)
    {
        if (value is null)
            return null;
        Element? Get(string id) => analysis.Elements.FirstOrDefault(e => e.Id == id) ?? analysis.References.FirstOrDefault(e => e.Id == id);
        Pt? KeyPoint(KeyRef key)
        {
            var element = Get(key.Id);
            if (element is null)
                return null;
            return key.Key.Length == 0 ? LabelAnchor(element) : Keypoint(element, key.Key);
        }
        var shown = dimension.Type switch
        {
            "radius" => $"R{Format(value.Value)}",
            "diameter" => $"⌀{Format(value.Value)}",
            "angle" => $"{Format(value.Value)}°",
            _ => Format(value.Value),
        };
        if (dimension.Name is { } name)
            shown = $"{name}={shown}";
        Pt? at = dimension.Type == "distance"
            ? KeyPoint(dimension.From!) is { } a && KeyPoint(dimension.To!) is { } b ? (a + b) * 0.5 : null
            : Get(dimension.Entities[0]) is { } element ? LabelAnchor(element) : null;
        return at is null ? null : (at.Value, shown);
    }

    /// <summary>
    /// Dimension labels in drawing order, each moved down until it no longer covers an earlier one
    /// (several distances often share a midpoint). Heights and widths are in sketch units.
    /// </summary>
    private static List<(Pt At, string Text)> Spread(SketchAnalysis analysis, double lineHeight, double charWidth)
    {
        var placed = new List<(Pt At, string Text)>();
        foreach (var label in analysis.DimensionValues.Select(d => DimensionLabel(analysis, d.Dimension, d.Value)).OfType<(Pt At, string Text)>())
        {
            var at = label.At;
            bool Covers((Pt At, string Text) other) =>
                Math.Abs(other.At.Y - at.Y) < lineHeight
                && Math.Abs(other.At.X - at.X) < (other.Text.Length + label.Text.Length) * charWidth / 2;
            while (placed.Any(Covers))
                at = at with { Y = at.Y - lineHeight };
            placed.Add((at, label.Text));
        }
        return placed;
    }

    // ---- SVG ------------------------------------------------------------------------------

    private static string Escape(string text) => text
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("'", "&apos;").Replace("\"", "&quot;");

    /// <summary>SVG point: x as is, y negated so the sketch's +y points up on screen.</summary>
    private static string P(Pt p) => $"{Format(p.X)} {Format(-p.Y)}";

    private static string ArcTo(Pt center, double radius, double from, double sweep)
    {
        var end = Pt.Polar(center, radius, from + sweep);
        // With y negated a counterclockwise sketch arc turns the negative-angle way in SVG: sweep-flag 0.
        return $"A {Format(radius)} {Format(radius)} 0 {(Math.Abs(sweep) > Math.PI ? 1 : 0)} {(sweep > 0 ? 0 : 1)} {P(end)}";
    }

    private static string StepPath(Step step)
    {
        switch (step.Element)
        {
            case LineEl line:
                return $"L {P(step.Forward ? line.End : line.Start)}";
            case ArcEl arc:
                return step.Forward
                    ? ArcTo(arc.Center, arc.Radius, arc.StartAngle, arc.Sweep)
                    : ArcTo(arc.Center, arc.Radius, arc.EndAngle, -arc.Sweep);
            case CircleEl circle:
                return $"{ArcTo(circle.Center, circle.Radius, 0, Math.PI)} {ArcTo(circle.Center, circle.Radius, Math.PI, Math.PI)}";
            case EllipseEl e:
            {
                var half = $"A {Format(e.MajorRadius)} {Format(e.MinorRadius)} {Format(-Deg(e.Angle))} 0 0";
                return $"{half} {P(e.At(Math.PI))} {half} {P(e.At(0))}";
            }
            case SplineEl spline:
            {
                var spans = Beziers(spline);
                if (!step.Forward)
                    spans = spans.AsEnumerable().Reverse().Select(s => (s.P2, s.C2, s.C1, s.P1)).ToList();
                return string.Join(" ", spans.Select(s => $"C {P(s.C1)} {P(s.C2)} {P(s.P2)}"));
            }
            default:
                return "";
        }
    }

    private static Pt StepStart(Step step) => step.Element switch
    {
        LineEl line => step.Forward ? line.Start : line.End,
        ArcEl arc => step.Forward ? arc.Start : arc.End,
        CircleEl circle => circle.Center + new Pt(circle.Radius, 0),
        EllipseEl ellipse => ellipse.At(0),
        SplineEl spline => step.Forward ? spline.Start : spline.End,
        _ => default,
    };

    private static string ElementPath(Element element) =>
        $"M {P(StepStart(new Step(element, true)))} {StepPath(new Step(element, true))}";

    public static string Svg(SketchAnalysis analysis)
    {
        var f = View(analysis);
        var stroke = f.Size / 250;
        var font = f.Size / 28;
        var faulty = Faulty(analysis);
        var height = Math.Round(640 * f.Height / f.Width);
        var svg = new StringBuilder();
        svg.Append($"<svg xmlns='http://www.w3.org/2000/svg' viewBox='{Format(f.X0)} {Format(-f.Top)} {Format(f.Width)} {Format(f.Height)}' ");
        svg.Append($"width='640' height='{Format(Math.Clamp(height, 80, 1600))}' font-family='sans-serif'>");
        svg.Append("<desc>Plane-local sketch, millimeters; +x right, +y up.</desc>");

        var axis = f.Size * 0.12;
        svg.Append($"<g id='origin' stroke-width='{Format(stroke * 0.8)}'>");
        svg.Append($"<line x1='0' y1='0' x2='{Format(axis)}' y2='0' stroke='#d0021b'/>");
        svg.Append($"<line x1='0' y1='0' x2='0' y2='{Format(-axis)}' stroke='#2e8b57'/></g>");

        var regions = analysis.Loops.Where(l => l.Closed)
            .Select(l => $"M {P(StepStart(l.Steps[0]))} {string.Join(" ", l.Steps.Select(StepPath))} Z");
        svg.Append($"<path id='region' fill='{Fill}' fill-rule='evenodd' stroke='none' d='{string.Join(" ", regions)}'/>");

        var references = analysis.References.Where(r => r is LineEl or ArcEl or CircleEl).ToList();
        if (references.Count > 0)
        {
            svg.Append($"<g id='references' fill='none' stroke='{Faint}' stroke-width='{Format(stroke * 0.6)}' stroke-dasharray='{Format(stroke * 8)} {Format(stroke * 3)} {Format(stroke)} {Format(stroke * 3)}'>");
            foreach (var reference in references)
                svg.Append($"<path id='{Escape(reference.Id)}' d='{ElementPath(reference)}'/>");
            svg.Append("</g>");
        }

        svg.Append($"<g id='elements' fill='none' stroke-width='{Format(stroke)}' stroke-linecap='round'>");
        foreach (var element in analysis.Elements.Where(e => e is not PointEl))
        {
            var color = faulty.Contains(element.Id) ? Fault : element.Construction ? Faint : Ink;
            var dash = element.Construction ? $" stroke-dasharray='{Format(stroke * 4)} {Format(stroke * 3)}'" : "";
            var se = element.SeName is { } name ? $" data-se='{Escape(name)}'" : "";
            svg.Append($"<path id='{Escape(element.Id)}'{se} stroke='{color}'{dash} d='{ElementPath(element)}'/>");
        }
        foreach (var point in analysis.Elements.OfType<PointEl>())
        {
            var color = faulty.Contains(point.Id) ? Fault : Ink;
            svg.Append($"<circle id='{Escape(point.Id)}' cx='{Format(point.At.X)}' cy='{Format(-point.At.Y)}' r='{Format(stroke * 2)}' fill='{color}' stroke='none'/>");
        }
        svg.Append("</g>");

        svg.Append($"<g id='labels' font-size='{Format(font)}' fill='#333' text-anchor='middle'>");
        foreach (var element in analysis.Elements)
        {
            var at = LabelAnchor(element);
            svg.Append($"<text x='{Format(at.X)}' y='{Format(-at.Y - font * 0.4)}'>{Escape(element.Id)}</text>");
        }
        svg.Append("</g>");

        var labels = Spread(analysis, font * 1.1, font * 0.5);
        if (labels.Count > 0)
        {
            svg.Append($"<g id='dimensions' font-size='{Format(font * 0.9)}' fill='{Note}' text-anchor='middle'>");
            foreach (var (at, text) in labels)
                svg.Append($"<text x='{Format(at.X)}' y='{Format(-at.Y + font * 1.1)}'>{Escape(text)}</text>");
            svg.Append("</g>");
        }

        var marks = analysis.Issues.Where(i => i!["at"] is not null).ToList();
        if (marks.Count > 0)
        {
            svg.Append($"<g id='issues' fill='none' stroke='{Fault}' stroke-width='{Format(stroke)}'>");
            foreach (var issue in marks)
            {
                var at = issue!["at"]!.AsArray();
                svg.Append($"<circle cx='{Format((double)at[0]!)}' cy='{Format(-(double)at[1]!)}' r='{Format(stroke * 4)}'/>");
            }
            svg.Append("</g>");
        }
        svg.Append("</svg>");
        return svg.ToString();
    }

    // ---- PNG ------------------------------------------------------------------------------

    /// <summary>Raster of the same drawing, for clients that only display bitmap images.</summary>
    public static byte[] Png(SketchAnalysis analysis, int width = 800)
    {
        var f = View(analysis);
        var k = width / f.Width;
        var height = (int)Math.Clamp(Math.Round(f.Height * k), 80, 2400);
        PointF Map(Pt p) => new((float)((p.X - f.X0) * k), (float)((f.Top - p.Y) * k));
        using var bitmap = new Bitmap(width, height);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        g.Clear(Color.White);

        void AddStep(GraphicsPath path, Step step)
        {
            switch (step.Element)
            {
                case LineEl line:
                    path.AddLine(Map(step.Forward ? line.Start : line.End), Map(step.Forward ? line.End : line.Start));
                    break;
                case ArcEl arc:
                {
                    var (from, sweep) = step.Forward ? (arc.StartAngle, arc.Sweep) : (arc.EndAngle, -arc.Sweep);
                    var box = Map(arc.Center - new Pt(arc.Radius, -arc.Radius));
                    var side = (float)(2 * arc.Radius * k);
                    // GDI+ angles run clockwise on screen, i.e. the negated sketch angle.
                    path.AddArc(box.X, box.Y, side, side, (float)-Deg(from), (float)-Deg(sweep));
                    break;
                }
                case CircleEl circle:
                {
                    var box = Map(circle.Center - new Pt(circle.Radius, -circle.Radius));
                    var side = (float)(2 * circle.Radius * k);
                    path.AddEllipse(box.X, box.Y, side, side);
                    break;
                }
                case EllipseEl or SplineEl:
                {
                    var trace = SketchAnalysis.Trace(step.Element);
                    if (!step.Forward)
                        trace.Reverse();
                    path.AddLines(trace.Select(Map).ToArray());
                    break;
                }
            }
        }

        using (var region = new GraphicsPath(FillMode.Alternate))
        {
            foreach (var loop in analysis.Loops.Where(l => l.Closed))
            {
                region.StartFigure();
                foreach (var step in loop.Steps)
                    AddStep(region, step);
                region.CloseFigure();
            }
            using var brush = new SolidBrush(ColorTranslator.FromHtml(Fill));
            g.FillPath(brush, region);
        }

        using (var red = new Pen(ColorTranslator.FromHtml("#d0021b"), 1.5f))
        using (var green = new Pen(ColorTranslator.FromHtml("#2e8b57"), 1.5f))
        {
            var axis = f.Size * 0.12;
            g.DrawLine(red, Map(new Pt(0, 0)), Map(new Pt(axis, 0)));
            g.DrawLine(green, Map(new Pt(0, 0)), Map(new Pt(0, axis)));
        }

        using (var faint = new Pen(ColorTranslator.FromHtml(Faint), 1f) { DashStyle = DashStyle.DashDot })
        {
            foreach (var reference in analysis.References.Where(r => r is LineEl or ArcEl or CircleEl))
            {
                using var path = new GraphicsPath();
                AddStep(path, new Step(reference, true));
                g.DrawPath(faint, path);
            }
        }

        var faulty = Faulty(analysis);
        using var font = new Font("Arial", 11f, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.FromArgb(0x33, 0x33, 0x33));
        using var noteBrush = new SolidBrush(ColorTranslator.FromHtml(Note));
        var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Far };
        foreach (var element in analysis.Elements)
        {
            var color = ColorTranslator.FromHtml(faulty.Contains(element.Id) ? Fault : element.Construction ? Faint : Ink);
            if (element is PointEl point)
            {
                using var dot = new SolidBrush(color);
                var at = Map(point.At);
                g.FillEllipse(dot, at.X - 3, at.Y - 3, 6, 6);
            }
            else
            {
                using var pen = new Pen(color, element.Construction ? 1f : 2f);
                if (element.Construction)
                    pen.DashStyle = DashStyle.Dash;
                using var path = new GraphicsPath();
                AddStep(path, new Step(element, true));
                g.DrawPath(pen, path);
            }
            var label = Map(LabelAnchor(element));
            g.DrawString(element.Id, font, textBrush, label.X, label.Y - 2, centered);
        }
        var below = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near };
        foreach (var (point, text) in Spread(analysis, 13 / k, 6 / k))
        {
            var at = Map(point);
            g.DrawString(text, font, noteBrush, at.X, at.Y + 2, below);
        }
        using (var pen = new Pen(ColorTranslator.FromHtml(Fault), 2f))
        {
            foreach (var issue in analysis.Issues.Where(i => i!["at"] is not null))
            {
                var at = issue!["at"]!.AsArray();
                var p = Map(new Pt((double)at[0]!, (double)at[1]!));
                g.DrawEllipse(pen, p.X - 6, p.Y - 6, 12, 12);
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
