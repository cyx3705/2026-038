using System.Text.Json;
using System.Text.Json.Nodes;
using SolidEdgeMcp.Commands.Sketching;

namespace SolidEdgeMcp.Tests;

/// <summary>Sketch parsing, topology, exact measures and drawing; pure geometry, no Solid Edge.</summary>
public sealed class SketchTests
{
    private static List<Element> Parse(string json) => Sketch.Parse(JsonDocument.Parse(json).RootElement);

    // An 80 x 20 slot with a semicircular right end (tangent joints) and a 4 mm hole.
    private const string Slot = """
        [
          {"id": "L1", "type": "line", "start": [0, 0], "end": [80, 0]},
          {"id": "A1", "type": "arc", "center": [80, 10], "start": [80, 0], "end": [80, 20]},
          {"id": "L2", "type": "line", "start": [80, 20], "end": [0, 20]},
          {"id": "L3", "type": "line", "start": [0, 20], "end": [0, 0]},
          {"id": "H1", "type": "circle", "center": [40, 10], "radius": 4}
        ]
        """;

    [Fact]
    public void SlotWithHoleHasExactAreasCentroidAndRoles()
    {
        var analysis = new SketchAnalysis(Parse(Slot));
        Assert.True(analysis.Valid, analysis.Issues.ToJsonString());
        var outer = analysis.Loops.Single(l => l.Role == "outer");
        var hole = analysis.Loops.Single(l => l.Role == "hole");
        var half = Math.PI * 100 / 2;
        Assert.Equal(1600 + half, Math.Abs(outer.SignedArea), 9);
        Assert.Equal(Math.PI * 16, Math.Abs(hole.SignedArea), 9);
        Assert.Equal(1600 + half - Math.PI * 16, analysis.NetArea, 9);
        var cx = (1600 * 40 + half * (80 + 40 / (3 * Math.PI))) / (1600 + half);
        Assert.Equal(cx, outer.Centroid.X, 9);
        Assert.Equal(10, outer.Centroid.Y, 9);
        Assert.Equal(["L1", "A1", "L2", "L3"], outer.Ids);
        Assert.Equal(90, analysis.Max.X, 9);
    }

    [Fact]
    public void AngleAndClockwiseArcFormsAgree()
    {
        var byAngles = Parse("""[{"id": "a", "type": "arc", "center": [0, 0], "radius": 5, "start_angle": 90, "end_angle": 0}]""");
        var clockwise = Parse("""[{"id": "a", "type": "arc", "center": [0, 0], "start": [5, 0], "end": [0, 5], "clockwise": true}]""");
        // 90 -> 0 counterclockwise is a 270 degree arc; the clockwise 0 -> 90 arc covers the same points.
        Assert.Equal(1.5 * Math.PI, ((ArcEl)byAngles[0]).Sweep, 12);
        Assert.Equal(-1.5 * Math.PI, ((ArcEl)clockwise[0]).Sweep, 12);
        var error = Assert.Throws<EdgeError>(() =>
            Parse("""[{"id": "a", "type": "arc", "center": [0, 0], "start": [5, 0], "end": [0, 6]}]"""));
        Assert.Contains("same distance", error.Message);
    }

    [Fact]
    public void OpenEndsBranchesAndCrossingsAreReported()
    {
        var open = new SketchAnalysis(Parse("""
            [{"id": "a", "type": "line", "start": [0, 0], "end": [10, 0]},
             {"id": "b", "type": "line", "start": [10, 0], "end": [10, 10]}]
            """));
        Assert.Equal(2, open.Issues.Count(i => (string)i!["type"]! == "open_end"));
        Assert.Equal("open", open.Loops.Single().Role);

        var bowTie = new SketchAnalysis(Parse("""
            [{"id": "a", "type": "line", "start": [0, 0], "end": [10, 10]},
             {"id": "b", "type": "line", "start": [10, 10], "end": [10, 0]},
             {"id": "c", "type": "line", "start": [10, 0], "end": [0, 10]},
             {"id": "d", "type": "line", "start": [0, 10], "end": [0, 0]}]
            """));
        var crossing = bowTie.Issues.Single();
        Assert.Equal("intersection", (string)crossing!["type"]!);
        Assert.Equal([5.0, 5.0], crossing["at"]!.AsArray().Select(v => (double)v!));

        var overlap = new SketchAnalysis(Parse("""
            [{"id": "c1", "type": "circle", "center": [0, 0], "radius": 3},
             {"id": "c2", "type": "circle", "center": [0, 0], "radius": 3}]
            """));
        Assert.Contains(overlap.Issues, i => (string)i!["type"]! == "overlap");
    }

    [Fact]
    public void DuplicateIdsAndDegenerateElementsAreRejected()
    {
        Assert.Throws<EdgeError>(() => Parse("""[{"id": "a", "type": "circle", "center": [0, 0], "radius": 1}, {"id": "a", "type": "circle", "center": [5, 0], "radius": 1}]"""));
        Assert.Throws<EdgeError>(() => Parse("""[{"id": "a", "type": "line", "start": [1, 1], "end": [1, 1]}]"""));
        Assert.Throws<EdgeError>(() => Parse("""[{"id": "a", "type": "spline", "start": [1, 1]}]"""));
    }

    [Fact]
    public void ReportCarriesSvgDrawnFromTheGeometry()
    {
        var analysis = new SketchAnalysis(Parse(Slot));
        var report = SketchReport.Json(analysis);
        var svg = (string)report["svg"]!;
        Assert.StartsWith("<svg xmlns='http://www.w3.org/2000/svg'", svg);
        Assert.DoesNotContain("\"", svg); // single-quoted attributes stay readable inside JSON
        Assert.Contains("id='A1'", svg);
        Assert.Contains("fill-rule='evenodd'", svg);
        // A1 runs counterclockwise from (80,0) to (80,20): y is negated, so the SVG sweep-flag is 0.
        Assert.Contains("M 80 0 A 10 10 0 0 0 80 -20", svg);
        Assert.Equal(Math.Round(1600 + Math.PI * 50 - Math.PI * 16, 6), (double)report["net_area_mm2"]!);
        Assert.Equal("hole", (string)report["loops"]![1]!["role"]!);

        var png = SketchReport.Png(analysis);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4));
    }
}
