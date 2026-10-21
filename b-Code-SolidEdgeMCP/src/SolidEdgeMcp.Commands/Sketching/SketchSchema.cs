namespace SolidEdgeMcp.Commands.Sketching;

/// <summary>JSON schemas of the sketch description, advertised through se_find.</summary>
public static class SketchSchema
{
    private static JsonObject Point(string description) => new()
    {
        ["type"] = "array",
        ["items"] = new JsonObject { ["type"] = "number" },
        ["minItems"] = 2,
        ["maxItems"] = 2,
        ["description"] = description,
    };

    private static JsonObject Number(string description) => new() { ["type"] = "number", ["description"] = description };

    private static JsonObject Text(string description) => new() { ["type"] = "string", ["description"] = description };

    private static JsonObject Ids(string description) => new()
    {
        ["type"] = "array",
        ["items"] = new JsonObject { ["type"] = "string" },
        ["description"] = description,
    };

    private static JsonObject Key(string description) => new()
    {
        ["description"] = description + " Keypoint: \"id\" (a point, datum or whole line) or [\"id\", \"start|end|mid|center\"].",
        ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "string" },
            new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["minItems"] = 1, ["maxItems"] = 2 }),
    };

    public static JsonObject Entities() => new()
    {
        ["minItems"] = 1,
        ["maxItems"] = Sketch.MaxElements,
        ["description"] =
            "Sketch elements in plane-local mm (+x right, +y up). Primitives: line (start, end); arc (center+start+end, "
            + "counterclockwise unless clockwise=true; or center+radius+start_angle+end_angle in degrees); circle (center, radius); "
            + "ellipse (center, major_radius, minor_radius, angle of the major axis); spline (points it passes through, closed); "
            + "point (at). Shorthands expand to lines/arcs with ids <id>.<part> and their own relations: rectangle (corner or "
            + "center, size [w,h], angle; parts .b .r .t .l), polygon (center, radius to vertices unless inscribed=false, sides, "
            + "angle of the first vertex, default 90; parts .1..n), polyline (points, closed; parts .1..n), slot (start+end arc "
            + "centers, or center+length+angle; width; parts .a .e .b .s). corner_radius / corner_chamfer round or cut every "
            + "corner of a rectangle, polygon or polyline. construction=true elements guide relations but never form regions. "
            + "Endpoints that coincide are joined automatically; every chain must close; inner loops become holes.",
        ["items"] = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("id", "type"),
            ["properties"] = new JsonObject
            {
                ["id"] = Text("Your name for the element (no spaces, not starting with @), used by relations and dimensions."),
                ["type"] = new JsonObject { ["enum"] = new JsonArray(SketchShapes.Types.Select(t => (JsonNode)t).ToArray()) },
                ["construction"] = new JsonObject { ["type"] = "boolean", ["default"] = false },
                ["start"] = Point("[x, y]"),
                ["end"] = Point("[x, y]"),
                ["center"] = Point("[x, y]"),
                ["corner"] = Point("[x, y] rectangle corner (lower-left before rotation)"),
                ["at"] = Point("[x, y] point position"),
                ["size"] = Point("[width, height] of a rectangle"),
                ["radius"] = new JsonObject { ["type"] = "number", ["exclusiveMinimum"] = 0 },
                ["start_angle"] = Number("degrees"),
                ["end_angle"] = Number("degrees"),
                ["clockwise"] = new JsonObject { ["type"] = "boolean", ["default"] = false },
                ["major_radius"] = Number("ellipse semi-major axis"),
                ["minor_radius"] = Number("ellipse semi-minor axis"),
                ["angle"] = Number("degrees: ellipse major axis, rectangle/slot rotation, polygon first vertex"),
                ["points"] = new JsonObject { ["type"] = "array", ["items"] = Point("[x, y]"), ["description"] = "spline or polyline points" },
                ["closed"] = new JsonObject { ["type"] = "boolean", ["default"] = false },
                ["sides"] = new JsonObject { ["type"] = "integer", ["minimum"] = 3, ["maximum"] = 128 },
                ["inscribed"] = new JsonObject { ["type"] = "boolean", ["default"] = true },
                ["width"] = Number("slot width"),
                ["length"] = Number("slot overall length (with center)"),
                ["corner_radius"] = Number("fillet every corner of a rectangle, polygon or polyline"),
                ["corner_chamfer"] = Number("chamfer every corner of a rectangle, polygon or polyline"),
            },
        },
    };

    public static JsonObject Corners() => new()
    {
        ["description"] = "Round (fillet radius) or cut (chamfer setback, or [setback1, setback2]) the joint of two lines, "
            + "found by at: [x, y] or between: [line1, line2]. A fillet adds a tangent arc (id, default cornerN).",
        ["items"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["id"] = Text("id of the new arc or chamfer line"),
                ["at"] = Point("[x, y] joint"),
                ["between"] = Ids("[line1, line2]"),
                ["fillet"] = Number("radius"),
                ["chamfer"] = new JsonObject { ["description"] = "setback, or [setback on line1, setback on line2]" },
            },
        },
    };

    public static JsonObject Relations() => new()
    {
        ["description"] = "Geometric relations; each must already hold for the given geometry (it is checked before drawing). "
            + "horizontal/vertical: entity (a line) or points (two keypoints aligned). parallel, perpendicular, collinear, equal, "
            + "tangent, concentric: entities [a, b]. symmetric: axis (a line) and entities [a, b]. coincident: points [k1, k2]. "
            + "point_on: point (a keypoint) and target. fix: entity or point. Datums @x_axis, @y_axis (the traces of the base "
            + "planes) and @origin can be named, e.g. coincident [[\"R.l\",\"start\"], \"@origin\"].",
        ["items"] = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("type"),
            ["properties"] = new JsonObject
            {
                ["type"] = new JsonObject
                {
                    ["enum"] = new JsonArray("horizontal", "vertical", "parallel", "perpendicular", "collinear", "equal", "tangent",
                        "concentric", "symmetric", "coincident", "point_on", "fix"),
                },
                ["entity"] = Text("element id"),
                ["entities"] = Ids("element ids"),
                ["point"] = Key("One keypoint."),
                ["points"] = new JsonObject { ["type"] = "array", ["items"] = Key("A keypoint."), ["minItems"] = 2, ["maxItems"] = 2 },
                ["axis"] = Text("symmetry line id"),
                ["target"] = Text("element the point lies on"),
            },
        },
    };

    public static JsonObject Dimensions() => new()
    {
        ["description"] = "Driving dimensions; the value is measured from the geometry (a stated value must match). "
            + "length: entity (line). radius / diameter: entity (arc or circle). angle: entities [line, line] (0-180 between "
            + "the lines' start-to-end directions, as Solid Edge measures it: a polygon's consecutive sides give the exterior "
            + "angle) or entity (arc sweep). distance: from and to (keypoints, or a whole line for "
            + "perpendicular distance), axis x | y | aligned. name makes it a variable that set_variable, sketch_dimensions and "
            + "formulas can use. Values are mm and degrees.",
        ["items"] = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("type"),
            ["properties"] = new JsonObject
            {
                ["type"] = new JsonObject { ["enum"] = new JsonArray("length", "radius", "diameter", "angle", "distance") },
                ["name"] = Text("variable name: letters, digits, _"),
                ["entity"] = Text("element id"),
                ["entities"] = Ids("two lines for an angle"),
                ["from"] = Key("Distance start."),
                ["to"] = Key("Distance end."),
                ["axis"] = new JsonObject { ["enum"] = new JsonArray("x", "y", "aligned"), ["default"] = "aligned" },
                ["value"] = Number("optional check: the expected value"),
                ["driving"] = new JsonObject { ["type"] = "boolean", ["default"] = true },
            },
        },
    };

    public static JsonObject References() => new()
    {
        ["description"] = "Model geometry brought into the sketch under your id: type plane (plane_index; its trace line) or "
            + "type edge (face and edge indices from the faces command; mode project = a reference for relations and "
            + "dimensions, include = real profile geometry).",
        ["items"] = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("id", "type"),
            ["properties"] = new JsonObject
            {
                ["id"] = Text("your id for it"),
                ["type"] = new JsonObject { ["enum"] = new JsonArray("plane", "edge") },
                ["plane_index"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
                ["face"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
                ["edge"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
                ["mode"] = new JsonObject { ["enum"] = new JsonArray("project", "include"), ["default"] = "project" },
            },
        },
    };

    public static JsonObject Operations() => new()
    {
        ["minItems"] = 1,
        ["description"] =
            "Applied in order to the sketch's description, then the sketch is redrawn and verified. "
            + "move {entities?, by:[dx,dy]}; rotate {entities?, center?, angle}; scale {entities?, center?, factor} "
            + "(entities default to all); mirror {entities?, axis: line id | @x_axis | @y_axis | [[x1,y1],[x2,y2]], copy=true, "
            + "suffix=_m} (copies about a sketch line stay symmetric to their originals); offset {entities: one chain, loop or "
            + "circle, distance (+ grows a loop / goes left of a chain), copy=true, suffix=_o}; delete {entities}; "
            + "add {entities?, corners?, relations?, dimensions?, references?, axis?}; split {entity, at:[x,y], new_id?}; "
            + "trim {entity, pick:[x,y]} removes the piece around pick between crossings; extend {entity, end: start|end, to?}; "
            + "corner {at | between, fillet | chamfer, id?}. Relations that stop holding are dropped and reported.",
        ["items"] = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("op"),
            ["properties"] = new JsonObject
            {
                ["op"] = new JsonObject
                {
                    ["enum"] = new JsonArray("move", "rotate", "scale", "mirror", "offset", "delete", "add", "split", "trim", "extend", "corner"),
                },
            },
        },
    };
}
