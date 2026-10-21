using System.Text.Json;
using System.Text.Json.Nodes;
using SolidEdgeMcp.Commands.Sketching;

namespace SolidEdgeMcp.Tests;

/// <summary>Sketch parsing, topology, exact measures and drawing; pure geometry, no Solid Edge.</summary>
public sealed class SketchTests
{
    private static SketchSpec Spec(string json) => SketchSpec.Read(JsonDocument.Parse(json).RootElement);

    private static List<Element> Parse(string entities) => Spec($$"""{"entities": {{entities}}}""").Elements;

    private static SketchAnalysis Plan(string json) => Sketches.Plan(Spec(json));

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
        Assert.Throws<EdgeError>(() => Parse("""[{"id": "a", "type": "nurbs", "start": [1, 1]}]"""));
        Assert.Throws<EdgeError>(() => Parse("""[{"id": "a", "type": "line", "start": [0, 0], "end": [1, 1], "radius": 2}]"""));
        Assert.Throws<EdgeError>(() => Parse("""[{"id": "@a", "type": "circle", "center": [0, 0], "radius": 1}]"""));
    }

    [Fact]
    public void ShorthandsExpandWithExactAreasAndHoldingRelations()
    {
        // Rectangle 40 x 20 with R5 corners: area w*h - (4 - pi) r^2, tangent relations hold.
        var plate = Plan("""{"entities": [{"id": "P", "type": "rectangle", "corner": [0, 0], "size": [40, 20], "corner_radius": 5}]}""");
        Assert.True(plate.Valid, plate.Issues.ToJsonString());
        Assert.Equal(800 - (4 - Math.PI) * 25, plate.NetArea, 9);
        Assert.Equal(8, plate.Elements.Count);
        Assert.All(plate.RelationResults, r => Assert.Null(r.Failure));
        Assert.Contains(plate.RelationResults, r => r.Relation.Type == "tangent");
        Assert.Equal("P", plate.Elements[0].Group);

        // Hexagon: (3 sqrt 3 / 2) R^2, six equal sides.
        var hexagon = Plan("""{"entities": [{"id": "H", "type": "polygon", "center": [0, 0], "radius": 10, "sides": 6}]}""");
        Assert.Equal(1.5 * Math.Sqrt(3) * 100, hexagon.NetArea, 9);
        Assert.Equal(5, hexagon.RelationResults.Count(r => r.Relation.Type == "equal" && r.Failure is null));

        // Slot by center/length/width: (L - w) w + pi (w/2)^2.
        var slot = Plan("""{"entities": [{"id": "S", "type": "slot", "center": [5, 5], "length": 30, "width": 10, "angle": 30}]}""");
        Assert.True(slot.Valid, slot.Issues.ToJsonString());
        Assert.Equal(20 * 10 + Math.PI * 25, slot.NetArea, 9);

        // Chamfered polyline triangle and a rotated rectangle stay valid.
        var rotated = Plan("""{"entities": [{"id": "R", "type": "rectangle", "center": [0, 0], "size": [10, 6], "angle": 25, "corner_chamfer": 1}]}""");
        Assert.True(rotated.Valid, rotated.Issues.ToJsonString());
        Assert.Equal(60 - 4 * 0.5, rotated.NetArea, 9);
    }

    [Fact]
    public void CornersNeedTwoLinesAndRoom()
    {
        var spec = Spec("""
            {"entities": [{"id": "T", "type": "polyline", "points": [[0, 0], [30, 0], [0, 30]], "closed": true}],
             "corners": [{"at": [0, 0], "fillet": 4, "id": "F"}]}
            """);
        var fillet = (ArcEl)spec.Find("F")!;
        Assert.Equal(4, fillet.Radius, 9);
        Assert.True(Sketches.Plan(spec).Valid);
        var tooBig = Assert.Throws<EdgeError>(() => Spec("""
            {"entities": [{"id": "T", "type": "polyline", "points": [[0, 0], [3, 0], [0, 3]], "closed": true}],
             "corners": [{"at": [0, 0], "fillet": 10}]}
            """));
        Assert.Contains("too large", tooBig.Message);
    }

    [Fact]
    public void RelationsMustHoldAndDimensionsMeasureTheGeometry()
    {
        var bad = Plan("""
            {"entities": [{"id": "T", "type": "polyline", "points": [[0, 0], [30, 1], [0, 30]], "closed": true}],
             "relations": [{"type": "horizontal", "entity": "T.1"}]}
            """);
        Assert.Contains(bad.Issues, i => (string)i!["type"]! == "relation_unsatisfied");

        var plan = Plan("""
            {"entities": [
               {"id": "R", "type": "rectangle", "corner": [10, 5], "size": [40, 20]},
               {"id": "H", "type": "circle", "center": [30, 15], "radius": 3},
               {"id": "D", "type": "line", "start": [10, 5], "end": [25, 31], "construction": true}],
             "relations": [{"type": "point_on", "point": ["H", "center"], "target": "D"}],
             "dimensions": [
               {"type": "length", "entity": "R.b", "name": "Width", "value": 40},
               {"type": "diameter", "entity": "H"},
               {"type": "distance", "from": "@y_axis", "to": ["H", "center"], "axis": "x"},
               {"type": "distance", "from": ["R.b", "start"], "to": ["H", "center"]},
               {"type": "angle", "entities": ["R.b", "D"]}]}
            """);
        // The construction diagonal does not reach the hole center, so point_on fails; everything else measures.
        Assert.Single(plan.Issues);
        var values = plan.DimensionValues.Select(d => d.Value!.Value).ToList();
        Assert.Equal(40, values[0], 9);
        Assert.Equal(6, values[1], 9);
        Assert.Equal(30, values[2], 9);
        Assert.Equal(Math.Sqrt(400 + 100), values[3], 9);
        Assert.Equal(Sketch.Deg(Math.Atan2(26, 15)), values[4], 9);
        // The construction line neither closes a loop nor counts as a crossing.
        Assert.Equal(800 - Math.PI * 9, plan.NetArea, 9);

        var mismatch = Plan("""
            {"entities": [{"id": "R", "type": "rectangle", "corner": [0, 0], "size": [40, 20]}],
             "dimensions": [{"type": "length", "entity": "R.l", "value": 25}]}
            """);
        Assert.Contains(mismatch.Issues, i => (string)i!["type"]! == "dimension_mismatch");
        Assert.Throws<EdgeError>(() => Spec("""{"entities": [{"id": "a", "type": "point", "at": [0, 0]}], "relations": [{"type": "fix", "entity": "nope"}]}"""));
    }

    [Fact]
    public void EllipsesSplinesPointsAndAxis()
    {
        var plan = Plan("""
            {"entities": [
               {"id": "E", "type": "ellipse", "center": [0, 0], "major_radius": 20, "minor_radius": 10, "angle": 30},
               {"id": "K", "type": "spline", "points": [[40, 0], [50, 10], [60, 0], [50, -10]], "closed": true},
               {"id": "P", "type": "point", "at": [100, 0]},
               {"id": "X", "type": "line", "start": [-30, -30], "end": [-30, 30]}],
             "axis": "X"}
            """);
        Assert.True(plan.Valid, plan.Issues.ToJsonString());
        Assert.True(plan.Approximate);
        Assert.Equal(2, plan.Loops.Count(l => l.Closed));
        Assert.Equal(Math.PI * 200, Math.Abs(plan.Loops.First(l => l.Ids.Single() == "E").SignedArea), 9);
        // The spline diamond through (40,0),(50,10),(60,0),(50,-10) bulges past the 200 mm2 polygon.
        var spline = Math.Abs(plan.Loops.First(l => l.Ids.Single() == "K").SignedArea);
        Assert.InRange(spline, 200, 330);
        Assert.True(plan.Elements.Single(e => e.Id == "X").Construction);

        // The SVG's Bezier spans are the same curve the measures sample.
        var curve = (SplineEl)plan.Elements.Single(e => e.Id == "K");
        var samples = Sketch.Sample(curve, 16);
        foreach (var (span, k) in Sketch.Beziers(curve).Select((s, k) => (s, k)))
        {
            var mid = (span.P1 + span.C1 * 3 + span.C2 * 3 + span.P2) * 0.125;
            Assert.True(mid.DistanceTo(samples[k * 16 + 8]) < 1e-9);
        }

        var crossing = Plan("""
            {"entities": [
               {"id": "E", "type": "ellipse", "center": [0, 0], "major_radius": 20, "minor_radius": 10},
               {"id": "C", "type": "circle", "center": [20, 0], "radius": 5}]}
            """);
        Assert.Contains(crossing.Issues, i => (string)i!["type"]! == "intersection");
    }

    [Fact]
    public void DescriptionRoundTripsThroughJson()
    {
        var spec = Spec("""
            {"entities": [
               {"id": "R", "type": "rectangle", "corner": [0, 0], "size": [40, 20], "corner_radius": 3},
               {"id": "E", "type": "ellipse", "center": [20, 10], "major_radius": 6, "minor_radius": 3},
               {"id": "A", "type": "line", "start": [-5, 0], "end": [-5, 10]}],
             "dimensions": [{"type": "length", "entity": "R.b", "name": "W"}],
             "axis": "A"}
            """);
        var again = SketchSpec.Read(JsonSerializer.SerializeToElement(spec.ToJson()));
        Assert.Equal(spec.ToJson().ToJsonString(), again.ToJson().ToJsonString());
        Assert.Equal(Sketches.Plan(spec).NetArea, Sketches.Plan(again).NetArea, 9);
        Assert.Equal("A", again.Axis);
        Assert.Equal("R", again.Find("R.f1")!.Group);
    }

    private static SketchEdit.Outcome Edit(SketchSpec spec, string operations) =>
        SketchEdit.Apply(spec, JsonDocument.Parse(operations).RootElement);

    [Fact]
    public void OffsetGrowsAFilletedRectangleExactly()
    {
        var spec = Spec("""{"entities": [{"id": "R", "type": "rectangle", "corner": [0, 0], "size": [40, 20], "corner_radius": 5}]}""");
        var ids = spec.Elements.Select(e => e.Id).ToList();
        var outcome = Edit(spec, $$"""[{"op": "offset", "entities": {{JsonSerializer.Serialize(ids)}}, "distance": 2}]""");
        var plan = Sketches.Plan(outcome.Spec);
        Assert.True(plan.Valid, plan.Issues.ToJsonString());
        var outer = plan.Loops.Single(l => l.Role == "outer");
        // Offset of a rounded rectangle: sides grow by 2d, radii by d.
        Assert.Equal(44 * 24 - (4 - Math.PI) * 49, Math.Abs(outer.SignedArea), 9);
        Assert.Equal(800 - (4 - Math.PI) * 25, Math.Abs(plan.Loops.Single(l => l.Role == "hole").SignedArea), 9);
        Assert.Contains(outcome.Spec.Elements, e => e.Id == "R.b_o");

        var inward = Edit(spec, $$"""[{"op": "offset", "entities": {{JsonSerializer.Serialize(ids)}}, "distance": -2, "copy": false}]""");
        Assert.Equal(36 * 16 - (4 - Math.PI) * 9, Sketches.Plan(inward.Spec).NetArea, 9);
        Assert.Throws<EdgeError>(() => Edit(spec, $$"""[{"op": "offset", "entities": {{JsonSerializer.Serialize(ids)}}, "distance": -12}]"""));
    }

    [Fact]
    public void MirrorTrimExtendSplitAndRotate()
    {
        var spec = Spec("""
            {"entities": [
               {"id": "T", "type": "polyline", "points": [[5, 0], [20, 0], [20, 10]], "closed": true},
               {"id": "M", "type": "line", "start": [0, -5], "end": [0, 15], "construction": true}],
             "relations": [{"type": "horizontal", "entity": "T.1"}]}
            """);
        var mirrored = Edit(spec, """[{"op": "mirror", "entities": ["T.1", "T.2", "T.3"], "axis": "M"}]""");
        var plan = Sketches.Plan(mirrored.Spec);
        Assert.True(plan.Valid, plan.Issues.ToJsonString());
        Assert.Equal(150, plan.NetArea, 9);
        Assert.Equal(3, plan.RelationResults.Count(r => r.Relation.Type == "symmetric" && r.Failure is null));
        Assert.Contains(plan.RelationResults, r => r.Relation.Type == "horizontal" && r.Relation.Entities[0] == "T.1_m");

        var rotated = Edit(spec, """[{"op": "rotate", "angle": 30}]""");
        Assert.Single(rotated.Dropped);
        Assert.Equal(75, Sketches.Plan(rotated.Spec).NetArea, 9);

        var lines = Spec("""
            {"entities": [
               {"id": "A", "type": "line", "start": [0, 0], "end": [30, 0]},
               {"id": "B", "type": "line", "start": [10, -5], "end": [10, 5]},
               {"id": "C", "type": "line", "start": [20, -5], "end": [20, 5]},
               {"id": "D", "type": "line", "start": [40, -5], "end": [40, 5]}]}
            """);
        var trimmed = Edit(lines, """[{"op": "trim", "entity": "A", "pick": [15, 0]}]""").Spec;
        Assert.Equal(new Pt(10, 0), ((LineEl)trimmed.Find("A")!).End);
        Assert.Equal(new Pt(20, 0), ((LineEl)trimmed.Find("A_2")!).Start);
        var extended = Edit(lines, """[{"op": "extend", "entity": "A", "end": "end"}]""").Spec;
        Assert.Equal(new Pt(40, 0), ((LineEl)extended.Find("A")!).End);
        var split = Edit(lines, """[{"op": "split", "entity": "A", "at": [12, 0], "new_id": "A2"}]""").Spec;
        Assert.Equal(12, ((LineEl)split.Find("A")!).Length, 12);
        Assert.Equal(18, ((LineEl)split.Find("A2")!).Length, 12);

        var circle = Spec("""
            {"entities": [{"id": "C", "type": "circle", "center": [0, 0], "radius": 10},
                          {"id": "L", "type": "line", "start": [-20, 0], "end": [20, 0]}]}
            """);
        var half = (ArcEl)Edit(circle, """[{"op": "trim", "entity": "C", "pick": [0, -10]}]""").Spec.Find("C")!;
        Assert.Equal(Math.PI, Math.Abs(half.Sweep), 9);
        Assert.True(half.Mid.Y > 0);
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

        var dimensioned = Plan("""
            {"entities": [{"id": "R", "type": "rectangle", "corner": [0, 0], "size": [40, 20]},
                          {"id": "G", "type": "line", "start": [0, 0], "end": [40, 20], "construction": true},
                          {"id": "P", "type": "point", "at": [5, 5]}],
             "dimensions": [{"type": "length", "entity": "R.b", "name": "W"}]}
            """);
        var drawn = SketchReport.Json(dimensioned);
        Assert.Contains("W=40", (string)drawn["svg"]!);
        Assert.Contains("stroke-dasharray", (string)drawn["svg"]!);
        Assert.Equal(40, (double)drawn["dimensions"]![0]!["value"]!);

        var png = SketchReport.Png(dimensioned);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4));
    }
}
