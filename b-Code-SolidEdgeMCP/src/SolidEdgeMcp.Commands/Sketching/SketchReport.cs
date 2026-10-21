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

    public static JsonObject Json(SketchAnalysis analysis)
    {
        var size = analysis.Max - analysis.Min;
        return new JsonObject
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
            ["issues"] = analysis.Issues.DeepClone(),
            ["svg"] = Svg(analysis),
        };
    }

    private static JsonObject Entity(Element element)
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
        }
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

    // ---- SVG ------------------------------------------------------------------------------

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

    private static string StepPath(Step step) => step.Element switch
    {
        LineEl line => $"L {P(step.Forward ? line.End : line.Start)}",
        ArcEl arc => step.Forward
            ? ArcTo(arc.Center, arc.Radius, arc.StartAngle, arc.Sweep)
            : ArcTo(arc.Center, arc.Radius, arc.EndAngle, -arc.Sweep),
        CircleEl circle => $"{ArcTo(circle.Center, circle.Radius, 0, Math.PI)} {ArcTo(circle.Center, circle.Radius, Math.PI, Math.PI)}",
        _ => "",
    };

    private static Pt StepStart(Step step) => step.Element switch
    {
        LineEl line => step.Forward ? line.Start : line.End,
        ArcEl arc => step.Forward ? arc.Start : arc.End,
        CircleEl circle => circle.Center + new Pt(circle.Radius, 0),
        _ => default,
    };

    private static string ElementPath(Element element) =>
        $"M {P(StepStart(new Step(element, true)))} {StepPath(new Step(element, true))}";

    private static Pt LabelAnchor(Element element) => element switch
    {
        LineEl line => (line.Start + line.End) * 0.5,
        ArcEl arc => arc.Mid,
        CircleEl circle => Pt.Polar(circle.Center, circle.Radius, Math.PI / 4),
        _ => default,
    };

    public static string Svg(SketchAnalysis analysis)
    {
        var f = View(analysis);
        var stroke = f.Size / 250;
        var font = f.Size / 28;
        var faulty = analysis.Issues
            .SelectMany(i => i!["entities"]?.AsArray().Select(n => (string)n!) ?? [])
            .ToHashSet(StringComparer.Ordinal);
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

        svg.Append($"<g id='elements' fill='none' stroke-width='{Format(stroke)}' stroke-linecap='round'>");
        foreach (var element in analysis.Elements)
        {
            var color = faulty.Contains(element.Id) ? Fault : Ink;
            var se = element.SeName is { } name ? $" data-se='{Escape(name)}'" : "";
            svg.Append($"<path id='{Escape(element.Id)}'{se} stroke='{color}' d='{ElementPath(element)}'/>");
        }
        svg.Append("</g>");

        svg.Append($"<g id='labels' font-size='{Format(font)}' fill='#333' text-anchor='middle'>");
        foreach (var element in analysis.Elements)
        {
            var at = LabelAnchor(element);
            svg.Append($"<text x='{Format(at.X)}' y='{Format(-at.Y - font * 0.4)}'>{Escape(element.Id)}</text>");
        }
        svg.Append("</g>");

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

        var faulty = analysis.Issues
            .SelectMany(i => i!["entities"]?.AsArray().Select(n => (string)n!) ?? [])
            .ToHashSet(StringComparer.Ordinal);
        using var font = new Font("Arial", 11f, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.FromArgb(0x33, 0x33, 0x33));
        var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Far };
        foreach (var element in analysis.Elements)
        {
            using var pen = new Pen(ColorTranslator.FromHtml(faulty.Contains(element.Id) ? Fault : Ink), 2f);
            using var path = new GraphicsPath();
            AddStep(path, new Step(element, true));
            g.DrawPath(pen, path);
            var at = Map(LabelAnchor(element));
            g.DrawString(element.Id, font, textBrush, at.X, at.Y - 2, centered);
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
