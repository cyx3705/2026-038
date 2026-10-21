namespace SolidEdgeMcp.Commands.Sketching;

/// <summary>What one drawing created: COM objects by sketch id, and Solid Edge's names for them.</summary>
public sealed class Drawing
{
    public Dictionary<string, object> Com { get; } = new(StringComparer.Ordinal);

    /// <summary>Solid Edge name (e.g. "Line2d 318") to sketch id; names survive Profile.End().</summary>
    public Dictionary<string, string> Ids { get; } = new(StringComparer.Ordinal);

    public List<(Dimension Dimension, object Com)> Dimensions { get; } = [];

    /// <summary>The revolve axis Profile.SetAxisOfRevolution returned, if the sketch has one.</summary>
    public object? Axis { get; set; }
}

/// <summary>Solid Edge side of a sketch: model references, drawing, relations, dimensions, read-back.</summary>
public static class SketchCom
{
    // ProfileValidationType: igProfileClosed | igProfileNoSelfIntersect | igProfileAllowNested.
    public const int Validation = 1 | 8 | 8192;

    private const double M = 1000; // COM works in meters

    // DimAxisModeConstants: implied (aligned with the measured points) and explicit (along Dimensions.Axis).
    private const int AxisImplied = 2;
    private const int AxisExplicit = 3;

    // The keyPoint switch of Dimensions.Add*BetweenObjects.
    private const int ElementPick = 0;
    private const int KeypointPick = 1;

    // DimTypeConstants that hold angles rather than lengths.
    private static bool AngularType(int type) => type is 3 or 7 or 11;

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

    // ---- model references -----------------------------------------------------------------

    /// <summary>Plane-local origin and axes of the profile, in model mm.</summary>
    public static (double[] Origin, double[] X, double[] Y, double[] Normal) Frame(object profileObject)
    {
        dynamic profile = profileObject;
        double[] At(double u, double v)
        {
            double x = 0, y = 0, z = 0;
            profile.Convert2DCoordinate(u, v, out x, out y, out z);
            return [x * M, y * M, z * M];
        }
        var o = At(0, 0);
        var ex = At(0.001, 0).Zip(o, (a, b) => a - b).ToArray();
        var ey = At(0, 0.001).Zip(o, (a, b) => a - b).ToArray();
        double[] n = [ex[1] * ey[2] - ex[2] * ey[1], ex[2] * ey[0] - ex[0] * ey[2], ex[0] * ey[1] - ex[1] * ey[0]];
        return (o, ex, ey, n);
    }

    public static JsonObject FrameJson(object profile)
    {
        var (o, x, y, n) = Frame(profile);
        JsonArray V(double[] v) => new(v.Select(c => (JsonNode)Sketch.Round(c)).ToArray());
        return new JsonObject { ["origin_mm"] = V(o), ["x_axis"] = V(x), ["y_axis"] = V(y), ["normal"] = V(n) };
    }

    /// <summary>
    /// Datums and references the description names, created in the profile before the plan is
    /// checked, so relations and dimensions are judged against the real model geometry.
    /// </summary>
    public static List<Element> Resolve(dynamic doc, dynamic profile, SketchSpec spec, Drawing drawing)
    {
        var result = new List<Element>();
        if (spec.UsesDatums)
            Datums(doc, profile, drawing, result);
        foreach (var reference in spec.References)
        {
            if (reference.Type == "plane")
            {
                int count = Int(doc.RefPlanes.Count);
                if (reference.PlaneIndex > count)
                    throw new EdgeError($"Reference {reference.Id}: plane_index must be between 1 and {count}.");
                object projected = profile.ProjectRefPlane(doc.RefPlanes.Item(reference.PlaneIndex));
                drawing.Com[reference.Id] = projected;
                result.Add(Read2d(projected, reference.Id) with { External = true, Construction = true });
                continue;
            }
            var edge = Edge(doc, reference);
            if (!reference.Include)
            {
                object projected = profile.ProjectEdge(edge);
                drawing.Com[reference.Id] = projected;
                result.Add(Read2d(projected, reference.Id) with { External = true, Construction = true });
                continue;
            }
            var before = Counts((object)profile);
            Invoke((object)profile, "IncludeEdge", new object?[] { edge, null }, 1);
            var after = Counts((object)profile);
            var added = Collections((object)profile).Zip(before.Zip(after)).Where(x => x.Second.Second > x.Second.First).ToList();
            if (added.Count != 1 || added[0].Second.Second != added[0].Second.First + 1)
                throw new EdgeError($"Reference {reference.Id}: including the edge did not add exactly one curve.");
            object included = added[0].First.Item(added[0].Second.Second);
            drawing.Com[reference.Id] = included;
            drawing.Ids[Name(included)] = reference.Id;
            result.Add(Read2d(included, reference.Id) with { External = true, SeName = Name(included) });
        }
        return result;
    }

    private static object Edge(dynamic doc, Reference reference)
    {
        if (Int(doc.Models.Count) == 0)
            throw new EdgeError($"Reference {reference.Id}: the part has no body yet.");
        var faces = doc.Models.Item(1).Body.Faces(1); // igQueryAll
        int faceCount = Int(faces.Count);
        if (reference.Face > faceCount)
            throw new EdgeError($"Reference {reference.Id}: face must be between 1 and {faceCount}; see the faces command.");
        var edges = faces.Item(reference.Face).Edges;
        int edgeCount = Int(edges.Count);
        if (reference.Edge > edgeCount)
            throw new EdgeError($"Reference {reference.Id}: face {reference.Face} has {edgeCount} edges.");
        return edges.Item(reference.Edge);
    }

    private static dynamic[] Collections(object profileObject)
    {
        dynamic profile = profileObject;
        return [profile.Lines2d, profile.Arcs2d, profile.Circles2d, profile.Ellipses2d, profile.BSplineCurves2d];
    }

    private static int[] Counts(object profile) => Collections(profile).Select(c => (int)Int(c.Count)).ToArray();

    /// <summary>
    /// Project the global reference planes perpendicular to the sketch: their traces are the plane's
    /// datum axes (@x_axis horizontal, @y_axis vertical), crossing at @origin.
    /// </summary>
    private static void Datums(dynamic doc, dynamic profile, Drawing drawing, List<Element> result)
    {
        var normal = Frame((object)profile).Normal;
        var length = Math.Sqrt(normal.Sum(c => c * c));
        int count = Math.Min(3, (int)Int(doc.RefPlanes.Count));
        for (var i = 1; i <= count; i++)
        {
            var plane = doc.RefPlanes.Item(i);
            Array n = new double[3];
            plane.GetNormal(ref n);
            var dot = Enumerable.Range(0, 3).Sum(k => Double(n.GetValue(k)!) * normal[k]) / length;
            if (Math.Abs(dot) > 1e-9)
                continue;
            object projected = profile.ProjectRefPlane(plane);
            var line = (LineEl)Read2d(projected, "datum");
            var d = line.Direction;
            var id = Math.Abs(d.Y) <= 1e-9 ? SketchSpec.XAxis : Math.Abs(d.X) <= 1e-9 ? SketchSpec.YAxis : null;
            if (id is null || drawing.Com.ContainsKey(id))
                continue;
            drawing.Com[id] = projected;
            result.Add(line with { Id = id, External = true, Construction = true });
        }
        var x = result.OfType<LineEl>().FirstOrDefault(e => e.Id == SketchSpec.XAxis);
        var y = result.OfType<LineEl>().FirstOrDefault(e => e.Id == SketchSpec.YAxis);
        if (x is null || y is null)
            throw new EdgeError("This plane is not aligned with the base reference planes, so @x_axis/@y_axis/@origin and x/y distances "
                + "are unavailable. Use a references entry (type plane) and aligned distances instead.");
        result.Add(new PointEl(SketchSpec.Origin, SketchAnalysis.Intersection(x, y)!.Value) { External = true, Construction = true });
    }

    /// <summary>Geometry of a 2D curve object (line, arc, circle) in plane-local mm.</summary>
    private static Element Read2d(object element, string id)
    {
        dynamic e = element;
        double cx = 0, cy = 0, sx = 0, sy = 0, ex = 0, ey = 0;
        try
        {
            e.GetCenterPoint(out cx, out cy);
        }
        catch (Exception)
        {
            e.GetStartPoint(out sx, out sy);
            e.GetEndPoint(out ex, out ey);
            return new LineEl(id, new Pt(sx * M, sy * M), new Pt(ex * M, ey * M));
        }
        var center = new Pt(cx * M, cy * M);
        double radius = Double(e.Radius) * M;
        try
        {
            e.GetStartPoint(out sx, out sy);
            e.GetEndPoint(out ex, out ey);
        }
        catch (Exception)
        {
            return new CircleEl(id, center, radius);
        }
        return ArcFrom(id, center, radius, new Pt(sx * M, sy * M), new Pt(ex * M, ey * M), Math.Abs(Double(e.SweepAngle)));
    }

    private static ArcEl ArcFrom(string id, Pt center, double radius, Pt start, Pt end, double sweep)
    {
        var a0 = start.AngleFrom(center);
        var ccw = Sketch.Normalize(end.AngleFrom(center) - a0);
        // Start/end are certain; the sweep's magnitude decides which way round the arc runs.
        var signed = Math.Abs(ccw - sweep) <= Math.Abs(2 * Math.PI - ccw - sweep) ? ccw : ccw - 2 * Math.PI;
        return new ArcEl(id, center, radius, a0, signed, start, end);
    }

    // ---- drawing --------------------------------------------------------------------------

    /// <summary>Create every element, mark construction geometry, and tie coincident endpoints.</summary>
    public static void Draw(dynamic profile, SketchSpec spec, Drawing drawing)
    {
        foreach (var element in spec.Elements.Where(e => !e.External))
        {
            object com = element switch
            {
                LineEl line => profile.Lines2d.AddBy2Points(line.Start.X / M, line.Start.Y / M, line.End.X / M, line.End.Y / M),
                // Three points fix the arc's side unambiguously, whatever the API's orientation convention.
                ArcEl arc => profile.Arcs2d.AddByStartAlongEnd(arc.Start.X / M, arc.Start.Y / M, arc.Mid.X / M, arc.Mid.Y / M,
                    arc.End.X / M, arc.End.Y / M),
                CircleEl circle => profile.Circles2d.AddByCenterRadius(circle.Center.X / M, circle.Center.Y / M, circle.Radius / M),
                // xMajor/yMajor is the semi-major axis as a vector from the center; Orientation 1 = counterclockwise.
                EllipseEl ellipse => profile.Ellipses2d.AddByCenter(ellipse.Center.X / M, ellipse.Center.Y / M,
                    ellipse.MajorRadius * Math.Cos(ellipse.Angle) / M, ellipse.MajorRadius * Math.Sin(ellipse.Angle) / M,
                    ellipse.MinorRadius / ellipse.MajorRadius, 1),
                SplineEl spline => AddSpline(profile, spline),
                PointEl point => profile.Points2d.Add(point.At.X / M, point.At.Y / M),
                _ => throw new EdgeError($"Unsupported element {element.Kind}."),
            };
            drawing.Com[element.Id] = com;
            drawing.Ids[Name(com)] = element.Id;
            if (element.Construction && element.Id != spec.Axis)
                profile.ToggleConstruction(com);
        }
        // Coincident coordinates alone can fail End(); every joint needs an explicit relation.
        var ends = new List<(Pt At, object Com, int Index)>();
        foreach (var element in spec.Elements.Where(e => drawing.Com.ContainsKey(e.Id) && !(e.External && e.Construction)))
        {
            if (Sketch.Ends(element) is { } pair)
            {
                ends.Add((pair.Start, drawing.Com[element.Id], Sketch.KeypointIndex(element, "start")));
                ends.Add((pair.End, drawing.Com[element.Id], Sketch.KeypointIndex(element, "end")));
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
        if (spec.Axis is { } axis)
            drawing.Axis = profile.SetAxisOfRevolution(drawing.Com[axis]);
    }

    private static object AddSpline(dynamic profile, SplineEl spline)
    {
        Array points = spline.Points.SelectMany(p => new[] { p.X / M, p.Y / M }).ToArray();
        var order = Math.Min(4, spline.Points.Count + (spline.Closed ? 1 : 0));
        return spline.Closed
            ? profile.BSplineCurves2d.AddByPointsWithCloseOption(order, spline.Points.Count, ref points, true)
            : profile.BSplineCurves2d.AddByPoints(order, spline.Points.Count, ref points);
    }

    // ---- relations and dimensions ---------------------------------------------------------

    /// <summary>The COM object and keypoint index of a keypoint reference.</summary>
    private static (object Com, int Index) Key(SketchAnalysis plan, Drawing drawing, KeyRef key)
    {
        var element = plan.Elements.FirstOrDefault(e => e.Id == key.Id) ?? plan.References.First(e => e.Id == key.Id);
        if (!drawing.Com.TryGetValue(key.Id, out var com))
            throw new EdgeError($"{key.Id} cannot be related directly; use @x_axis / @y_axis.");
        return (com, key.Key.Length == 0 ? 0 : Sketch.KeypointIndex(element, key.Key));
    }

    public static void Relate(dynamic profile, SketchAnalysis plan, Drawing drawing)
    {
        var relations = profile.Relations2d;
        object C(string id) => drawing.Com.TryGetValue(id, out var com) ? com
            : throw new EdgeError($"{id} cannot be related directly; use @x_axis / @y_axis.");
        foreach (var r in plan.Spec!.Relations)
        {
            var e = r.Entities;
            switch (r.Type)
            {
                case "horizontal" when e.Count == 1:
                    relations.AddHorizontal(C(e[0]));
                    break;
                case "vertical" when e.Count == 1:
                    relations.AddVertical(C(e[0]));
                    break;
                case "horizontal" or "vertical":
                {
                    var (a, b) = (Key(plan, drawing, r.Points[0]), Key(plan, drawing, r.Points[1]));
                    if (r.Type == "horizontal")
                        relations.AddHorizontalAlign(a.Com, a.Index, b.Com, b.Index);
                    else
                        relations.AddVerticalAlign(a.Com, a.Index, b.Com, b.Index);
                    break;
                }
                case "parallel":
                    relations.AddParallel(C(e[0]), C(e[1]));
                    break;
                case "perpendicular":
                    relations.AddPerpendicular(C(e[0]), C(e[1]));
                    break;
                case "collinear":
                    relations.AddColinear(C(e[0]), C(e[1]));
                    break;
                case "equal":
                    relations.AddEqual(C(e[0]), C(e[1]));
                    break;
                case "tangent":
                    relations.AddTangent(C(e[0]), C(e[1]));
                    break;
                case "concentric":
                    relations.AddConcentric(C(e[0]), C(e[1]));
                    break;
                case "symmetric":
                    relations.AddSymmetric(C(r.Axis!), C(e[0]), C(e[1]));
                    break;
                case "coincident":
                {
                    var points = r.Points.ToList();
                    var origin = points.FindIndex(p => p.Id == SketchSpec.Origin);
                    if (origin >= 0)
                    {
                        // The origin is the crossing of the two datum traces: pin the point on both.
                        var other = Key(plan, drawing, points[1 - origin]);
                        relations.AddPointOn(other.Com, other.Index, C(SketchSpec.XAxis));
                        relations.AddPointOn(other.Com, other.Index, C(SketchSpec.YAxis));
                        break;
                    }
                    var (a, b) = (Key(plan, drawing, points[0]), Key(plan, drawing, points[1]));
                    relations.AddKeypoint(a.Com, a.Index, b.Com, b.Index);
                    break;
                }
                case "point_on":
                {
                    var a = Key(plan, drawing, r.Points[0]);
                    relations.AddPointOn(a.Com, a.Index, C(r.Target!));
                    break;
                }
                case "fix" when e.Count == 1:
                    relations.AddFix(C(e[0]));
                    break;
                case "fix":
                {
                    var a = Key(plan, drawing, r.Points[0]);
                    relations.AddKeypointFix(a.Com, a.Index);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Create every dimension, make it driving, name its variable, and check Solid Edge measures
    /// what the plan measured: a mismatch means the API picked other points than intended.
    /// </summary>
    public static void Dimension(dynamic doc, dynamic profile, SketchAnalysis plan, Drawing drawing)
    {
        var dimensions = profile.Dimensions;
        object C(string id) => drawing.Com.TryGetValue(id, out var com) ? com
            : throw new EdgeError($"{id} cannot be dimensioned; dimension to @x_axis / @y_axis instead of @origin.");
        Element Geometry(string id) => plan.Elements.FirstOrDefault(e => e.Id == id) ?? plan.References.First(e => e.Id == id);
        foreach (var (spec, planned, _) in plan.DimensionValues)
        {
            var e = spec.Entities;
            object dimension;
            switch (spec.Type)
            {
                case "length":
                    dimension = dimensions.AddLength(C(e[0]));
                    break;
                case "radius":
                    dimension = dimensions.AddRadius(C(e[0]));
                    break;
                case "diameter":
                    dimension = Geometry(e[0]) is CircleEl ? dimensions.AddCircularDiameter(C(e[0])) : dimensions.AddRadialDiameter(C(e[0]));
                    break;
                case "angle" when e.Count == 1:
                    dimension = dimensions.AddAngle(C(e[0]));
                    break;
                case "angle":
                {
                    var (a, b) = ((LineEl)Geometry(e[0]), (LineEl)Geometry(e[1]));
                    var corner = SketchAnalysis.Intersection(a, b)!.Value;
                    // The measured angle is the one between the rays leaving the corner toward each line's far
                    // end: pick each line (keypoint flag 0, the element itself) halfway along that ray.
                    Pt Pick(LineEl l) => (corner + (l.Start.DistanceTo(corner) >= l.End.DistanceTo(corner) ? l.Start : l.End)) * 0.5;
                    var (pa, pb) = (Pick(a), Pick(b));
                    dimension = dimensions.AddAngleBetweenObjects(C(e[0]), pa.X / M, pa.Y / M, 0.0, ElementPick,
                        C(e[1]), pb.X / M, pb.Y / M, 0.0, ElementPick);
                    break;
                }
                default:
                {
                    // The keyPoint argument is a switch, not a KeypointIndexConstants value: non-zero snaps to the
                    // element's keypoint nearest the pick point. A whole line is measured through its start keypoint
                    // along the axis it is perpendicular to (live findings: element picks of two parallel lines
                    // measure nonsense, and a datum trace's middle keypoint moves as the trace resizes).
                    (object Com, Pt At, int Snap) End(KeyRef key)
                    {
                        var element = Geometry(key.Id);
                        var at = key.Key.Length > 0 ? Sketch.Keypoint(element, key.Key)
                            : element is LineEl line ? line.Start : ((PointEl)element).At;
                        return (C(key.Id), at, KeypointPick);
                    }
                    var (from, to) = (End(spec.From!), End(spec.To!));
                    var axis = spec.Axis;
                    if ((SketchAnalysis.WholeLine(plan, spec.From!) ?? SketchAnalysis.WholeLine(plan, spec.To!)) is { } whole)
                        axis = Math.Abs(whole.Direction.X) <= 1e-9 ? "x" : "y";
                    int mode = Int(dimensions.AxisMode);
                    try
                    {
                        if (axis == "aligned")
                        {
                            dimensions.AxisMode = AxisImplied;
                        }
                        else
                        {
                            dimensions.AxisMode = AxisExplicit;
                            dimensions.Axis = C(axis == "x" ? SketchSpec.XAxis : SketchSpec.YAxis);
                        }
                        dimension = dimensions.AddDistanceBetweenObjects(from.Com, from.At.X / M, from.At.Y / M, 0.0, from.Snap,
                            to.Com, to.At.X / M, to.At.Y / M, 0.0, to.Snap);
                    }
                    finally
                    {
                        if (axis != "aligned")
                            dimensions.ClearAxis();
                        dimensions.AxisMode = mode;
                    }
                    break;
                }
            }
            dynamic d = dimension;
            double actual = Value(d);
            if (spec.Type == "angle" && spec.Entities.Count == 2 && planned is { } angle && Math.Abs(360 - actual - angle) <= 1e-6)
            {
                // Solid Edge took the counterclockwise turn the long way round; its complement is the planned angle.
                d.AngleCompliment = !Convert.ToBoolean(d.AngleCompliment);
                actual = Value(d);
            }
            if (planned is { } value && Math.Abs(actual - value) > Math.Max(1e-6, Math.Abs(value) * 1e-7))
                throw new EdgeError($"Solid Edge measured {Sketch.Format(actual)} {SketchReport.Unit(spec)} for dimension "
                    + $"{spec.Json().ToJsonString()}, not the planned {Sketch.Format(value)}.");
            d.Constraint = spec.Driving;
            if (spec.Name is { } name)
                doc.Variables.PutName(dimension, name);
            drawing.Dimensions.Add((spec, dimension));
        }
    }

    /// <summary>Names of the part's variables, sketch dimensions of every profile set included.</summary>
    public static HashSet<string> VariableNames(object docObject)
    {
        dynamic doc = docObject;
        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var all = doc.Variables.Query("*");
            for (var i = 1; i <= Int(all.Count); i++)
                names.Add(Str(doc.Variables.GetName(all.Item(i))));
        }
        catch (Exception)
        {
            // Query is a convenience; the profile sets below cover sketch dimensions.
        }
        var sets = doc.ProfileSets;
        for (var i = 1; i <= Int(sets.Count); i++)
        {
            foreach (var dimension in ReadDimensions((object)sets.Item(i)))
                names.Add(dimension.Name);
        }
        return names;
    }

    /// <summary>Rename the named dimensions of a set; returns how to undo it.</summary>
    public static List<(object Com, string Name)> RenameDimensions(object docObject, object set, Func<string, string> rename)
    {
        dynamic doc = docObject;
        var renamed = new List<(object, string)>();
        foreach (var dimension in ReadDimensions(set).Where(d => d.Name != d.SystemName))
        {
            doc.Variables.PutName(dimension.Com, rename(dimension.Name));
            renamed.Add((dimension.Com, dimension.Name));
        }
        return renamed;
    }

    /// <summary>A dimension's value in mm or degrees.</summary>
    public static double Value(dynamic dimension)
    {
        double raw = Double(dimension.Value);
        return AngularType(Int(dimension.DimensionType)) ? Sketch.Deg(raw) : raw * M;
    }

    // ---- read-back ------------------------------------------------------------------------

    /// <summary>Read a profile's geometry; ids are the description's where the name is known, else Solid Edge's names.</summary>
    public static List<Element> ReadBack(dynamic profile, IReadOnlyDictionary<string, string>? ids = null)
    {
        var result = new List<Element>();
        string Id(string seName) => ids is not null && ids.TryGetValue(seName, out var id) ? id : seName;
        bool Construction(object element)
        {
            try
            {
                return Convert.ToBoolean(profile.IsConstructionElement(element));
            }
            catch (Exception)
            {
                return false;
            }
        }
        void Add(Element element, object com) =>
            result.Add(element with { SeName = Name(com), Construction = element is not PointEl && Construction(com) });

        var lines = profile.Lines2d;
        for (var i = 1; i <= Int(lines.Count); i++)
        {
            var line = lines.Item(i);
            double x0 = 0, y0 = 0, x1 = 0, y1 = 0;
            line.GetStartPoint(out x0, out y0);
            line.GetEndPoint(out x1, out y1);
            Add(new LineEl(Id(Name(line)), new Pt(x0 * M, y0 * M), new Pt(x1 * M, y1 * M)), line);
        }
        var arcs = profile.Arcs2d;
        for (var i = 1; i <= Int(arcs.Count); i++)
        {
            var arc = arcs.Item(i);
            double cx = 0, cy = 0, sx = 0, sy = 0, ex = 0, ey = 0;
            arc.GetCenterPoint(out cx, out cy);
            arc.GetStartPoint(out sx, out sy);
            arc.GetEndPoint(out ex, out ey);
            Add(ArcFrom(Id(Name(arc)), new Pt(cx * M, cy * M), Double(arc.Radius) * M, new Pt(sx * M, sy * M), new Pt(ex * M, ey * M),
                Math.Abs(Double(arc.SweepAngle))), arc);
        }
        var circles = profile.Circles2d;
        for (var i = 1; i <= Int(circles.Count); i++)
        {
            var circle = circles.Item(i);
            double cx = 0, cy = 0;
            circle.GetCenterPoint(out cx, out cy);
            Add(new CircleEl(Id(Name(circle)), new Pt(cx * M, cy * M), Double(circle.Radius) * M), circle);
        }
        var ellipses = profile.Ellipses2d;
        for (var i = 1; i <= Int(ellipses.Count); i++)
        {
            var ellipse = ellipses.Item(i);
            double cx = 0, cy = 0, mx = 0, my = 0;
            ellipse.GetCenterPoint(out cx, out cy);
            ellipse.GetMajorAxis(out mx, out my);
            Add(new EllipseEl(Id(Name(ellipse)), new Pt(cx * M, cy * M), Double(ellipse.MajorRadius) * M,
                Double(ellipse.MinorRadius) * M, Math.Atan2(my, mx)), ellipse);
        }
        var splines = profile.BSplineCurves2d;
        for (var i = 1; i <= Int(splines.Count); i++)
        {
            var spline = splines.Item(i);
            var nodes = new List<Pt>();
            for (var k = 1; k <= 1000; k++)
            {
                double x = 0, y = 0;
                try
                {
                    spline.GetNode(k, out x, out y);
                }
                catch (Exception)
                {
                    break; // past the last node
                }
                nodes.Add(new Pt(x * M, y * M));
            }
            var closed = nodes.Count > 2 && nodes[0].DistanceTo(nodes[^1]) <= 1e-6;
            if (closed)
                nodes.RemoveAt(nodes.Count - 1);
            Add(new SplineEl(Id(Name(spline)), nodes, closed), spline);
        }
        var points = profile.Points2d;
        for (var i = 1; i <= Int(points.Count); i++)
        {
            var point = points.Item(i);
            double x = 0, y = 0;
            point.GetPoint(out x, out y);
            Add(new PointEl(Id(Name(point)), new Pt(x * M, y * M)), point);
        }
        return result;
    }

    /// <summary>Dimensions of every profile in a set: COM object, variable name and value.</summary>
    public static List<(object Com, string Name, string SystemName, double Value, int Type, bool Driving)> ReadDimensions(dynamic set)
    {
        var result = new List<(object, string, string, double, int, bool)>();
        var profiles = set.Profiles;
        for (var p = 1; p <= Int(profiles.Count); p++)
        {
            var dimensions = profiles.Item(p).Dimensions;
            for (var i = 1; i <= Int(dimensions.Count); i++)
            {
                var dimension = dimensions.Item(i);
                result.Add(((object)dimension, Str(dimension.VariableTableName), Str(dimension.SystemName), Value(dimension),
                    Int(dimension.DimensionType), Convert.ToBoolean(dimension.Constraint)));
            }
        }
        return result;
    }

    /// <summary>Largest distance between intended and read-back defining points, in mm.</summary>
    public static double Deviation(IReadOnlyList<Element> intended, IReadOnlyList<Element> actual)
    {
        var worst = 0.0;
        foreach (var want in intended.Where(e => !e.External))
        {
            var got = actual.FirstOrDefault(a => a.Id == want.Id && a.Kind == want.Kind);
            if (got is null)
                return double.PositiveInfinity;
            static double Ends(Pt ws, Pt we, Pt gs, Pt ge) =>
                // An arc may come back with start and end swapped; compare the end points as a set.
                Math.Min(Math.Max(ws.DistanceTo(gs), we.DistanceTo(ge)), Math.Max(ws.DistanceTo(ge), we.DistanceTo(gs)));
            var d = (want, got) switch
            {
                (LineEl w, LineEl g) => Math.Max(w.Start.DistanceTo(g.Start), w.End.DistanceTo(g.End)),
                (ArcEl w, ArcEl g) => Math.Max(w.Center.DistanceTo(g.Center), Ends(w.Start, w.End, g.Start, g.End)),
                (CircleEl w, CircleEl g) => Math.Max(w.Center.DistanceTo(g.Center), Math.Abs(w.Radius - g.Radius)),
                (EllipseEl w, EllipseEl g) => new[]
                {
                    w.Center.DistanceTo(g.Center), Math.Abs(w.MajorRadius - g.MajorRadius), Math.Abs(w.MinorRadius - g.MinorRadius),
                    // The major axis has no direction: compare the axis end points both ways round.
                    Math.Min(w.At(0).DistanceTo(g.At(0)), w.At(0).DistanceTo(g.At(Math.PI))),
                }.Max(),
                (SplineEl w, SplineEl g) when w.Points.Count == g.Points.Count && w.Closed == g.Closed =>
                    w.Points.Zip(g.Points, (a, b) => a.DistanceTo(b)).Max(),
                (PointEl w, PointEl g) => w.At.DistanceTo(g.At),
                _ => double.PositiveInfinity,
            };
            // Solid Edge reports every Point2d as construction; for points the flag carries no meaning.
            if (want.Construction != got.Construction && want is not PointEl)
                d = double.PositiveInfinity;
            worst = Math.Max(worst, d);
        }
        return worst;
    }
}
