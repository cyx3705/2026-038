using System.Globalization;
using System.Text.Json;

namespace SolidEdgeMcp.Commands.Sketching;

/// <summary>A point in plane-local millimeters.</summary>
public readonly record struct Pt(double X, double Y)
{
    public static Pt operator +(Pt a, Pt b) => new(a.X + b.X, a.Y + b.Y);

    public static Pt operator -(Pt a, Pt b) => new(a.X - b.X, a.Y - b.Y);

    public static Pt operator *(Pt a, double k) => new(a.X * k, a.Y * k);

    public double Length => Math.Sqrt(X * X + Y * Y);

    public double DistanceTo(Pt other) => (this - other).Length;

    public static Pt Polar(Pt center, double radius, double angle) =>
        new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
}

/// <summary>One sketch element. Coordinates are millimeters, angles radians, counterclockwise positive.</summary>
public abstract record Element(string Id)
{
    /// <summary>Solid Edge's own name once the element exists in a profile.</summary>
    public string? SeName { get; init; }

    public abstract string Kind { get; }
}

public sealed record LineEl(string Id, Pt Start, Pt End) : Element(Id)
{
    public override string Kind => "line";

    public double Length => Start.DistanceTo(End);
}

/// <summary>Arc from <see cref="Start"/> to <see cref="End"/>; Sweep is signed (+ counterclockwise).</summary>
public sealed record ArcEl(string Id, Pt Center, double Radius, double StartAngle, double Sweep, Pt Start, Pt End) : Element(Id)
{
    public override string Kind => "arc";

    public double EndAngle => StartAngle + Sweep;

    public Pt Mid => Pt.Polar(Center, Radius, StartAngle + Sweep / 2);

    public static ArcEl FromAngles(string id, Pt center, double radius, double startAngle, double sweep) =>
        new(id, center, radius, startAngle, sweep,
            Pt.Polar(center, radius, startAngle), Pt.Polar(center, radius, startAngle + sweep));

    /// <summary>True when <paramref name="angle"/> lies on the arc, allowing <paramref name="slack"/> radians.</summary>
    public bool Contains(double angle, double slack)
    {
        var offset = Sketch.Normalize(Sweep >= 0 ? angle - StartAngle : StartAngle - angle);
        var span = Math.Abs(Sweep);
        return offset <= span + slack || offset >= 2 * Math.PI - slack;
    }
}

public sealed record CircleEl(string Id, Pt Center, double Radius) : Element(Id)
{
    public override string Kind => "circle";
}

/// <summary>Parsing of the declarative sketch description and shared numeric helpers.</summary>
public static class Sketch
{
    public const double Tolerance = 1e-6; // mm; endpoints closer than this are the same point
    public const double MaxCoordinate = 1_000_000;
    public const int MaxElements = 500;

    public static double Normalize(double angle)
    {
        var result = angle % (2 * Math.PI);
        return result < 0 ? result + 2 * Math.PI : result;
    }

    public static double Rad(double degrees) => degrees * Math.PI / 180;

    public static double Deg(double radians) => radians * 180 / Math.PI;

    /// <summary>Six decimals: enough for CAD, and it hides binary noise such as 119999.99999999999.</summary>
    public static double Round(double value) => Math.Round(value, 6) + 0.0;

    public static string Format(double value) => Round(value).ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>JSON schema for the "entities" argument, advertised through se_find.</summary>
    public static JsonObject EntitiesSchema()
    {
        JsonObject Point(string description) => new()
        {
            ["type"] = "array",
            ["items"] = new JsonObject { ["type"] = "number" },
            ["minItems"] = 2,
            ["maxItems"] = 2,
            ["description"] = description,
        };
        return new JsonObject
        {
            ["minItems"] = 1,
            ["maxItems"] = MaxElements,
            ["description"] =
                "Sketch elements in plane-local mm. line: start,end. circle: center,radius. "
                + "arc: either center+start+end (counterclockwise from start to end unless clockwise=true; "
                + "start and end must be equidistant from center) or center+radius+start_angle+end_angle "
                + "(degrees, counterclockwise from start_angle to end_angle). Endpoints that coincide are "
                + "joined automatically; every chain must close. Inner closed loops become holes.",
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("id", "type"),
                ["properties"] = new JsonObject
                {
                    ["id"] = new JsonObject { ["type"] = "string", ["description"] = "Your name for the element, echoed in the result." },
                    ["type"] = new JsonObject { ["enum"] = new JsonArray("line", "arc", "circle") },
                    ["start"] = Point("[x, y] start point (line, arc)"),
                    ["end"] = Point("[x, y] end point (line, arc)"),
                    ["center"] = Point("[x, y] center (arc, circle)"),
                    ["radius"] = new JsonObject { ["type"] = "number", ["exclusiveMinimum"] = 0 },
                    ["start_angle"] = new JsonObject { ["type"] = "number", ["description"] = "degrees" },
                    ["end_angle"] = new JsonObject { ["type"] = "number", ["description"] = "degrees" },
                    ["clockwise"] = new JsonObject { ["type"] = "boolean", ["default"] = false },
                },
            },
        };
    }

    public static List<Element> Parse(JsonElement entities)
    {
        var result = new List<Element>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in entities.EnumerateArray())
        {
            var where = $"entities[{index++}]";
            if (index > MaxElements)
                throw new EdgeError($"At most {MaxElements} sketch elements per call.");
            if (item.ValueKind != JsonValueKind.Object)
                throw new EdgeError($"{where}: expected an object.");
            var id = Text(item, "id", where);
            if (id.Length is 0 or > 64 || !ids.Add(id))
                throw new EdgeError($"{where}: id must be unique and 1-64 characters.");
            where = $"{where} ({id})";
            var kind = Text(item, "type", where);
            Element element = kind switch
            {
                "line" => Line(item, id, where),
                "circle" => new CircleEl(id, Point(item, "center", where), Positive(item, "radius", where)),
                "arc" => Arc(item, id, where),
                _ => throw new EdgeError($"{where}: type must be line, arc or circle."),
            };
            result.Add(element);
        }
        if (result.Count == 0)
            throw new EdgeError("entities must contain at least one element.");
        return result;
    }

    private static LineEl Line(JsonElement item, string id, string where)
    {
        var line = new LineEl(id, Point(item, "start", where), Point(item, "end", where));
        if (line.Length <= Tolerance)
            throw new EdgeError($"{where}: start and end coincide.");
        return line;
    }

    private static ArcEl Arc(JsonElement item, string id, string where)
    {
        var center = Point(item, "center", where);
        if (item.TryGetProperty("start_angle", out _) || item.TryGetProperty("end_angle", out _))
        {
            var radius = Positive(item, "radius", where);
            var from = Rad(Number(item, "start_angle", where));
            var sweep = Normalize(Rad(Number(item, "end_angle", where)) - from);
            if (sweep * radius <= Tolerance)
                throw new EdgeError($"{where}: start_angle and end_angle give an empty arc; use a circle for 360 degrees.");
            return ArcEl.FromAngles(id, center, radius, from, sweep);
        }
        var start = Point(item, "start", where);
        var end = Point(item, "end", where);
        var r = start.DistanceTo(center);
        if (r <= Tolerance)
            throw new EdgeError($"{where}: start coincides with center.");
        if (Math.Abs(end.DistanceTo(center) - r) > Math.Max(Tolerance, r * 1e-9))
            throw new EdgeError($"{where}: start and end are not the same distance from center "
                + $"({Format(r)} vs {Format(end.DistanceTo(center))}).");
        if (start.DistanceTo(end) <= Tolerance)
            throw new EdgeError($"{where}: start and end coincide; use a circle for a full turn.");
        var a0 = Math.Atan2(start.Y - center.Y, start.X - center.X);
        var a1 = Math.Atan2(end.Y - center.Y, end.X - center.X);
        var clockwise = item.TryGetProperty("clockwise", out var cw) && cw.ValueKind == JsonValueKind.True;
        var span = clockwise ? -Normalize(a0 - a1) : Normalize(a1 - a0);
        return new ArcEl(id, center, r, a0, span, start, end);
    }

    private static string Text(JsonElement item, string name, string where) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new EdgeError($"{where}: {name} must be a string.");

    private static double Number(JsonElement item, string name, string where)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
            throw new EdgeError($"{where}: {name} must be a number.");
        var number = value.GetDouble();
        return double.IsFinite(number) && Math.Abs(number) <= MaxCoordinate
            ? number
            : throw new EdgeError($"{where}: {name} is out of range.");
    }

    private static double Positive(JsonElement item, string name, string where)
    {
        var number = Number(item, name, where);
        return number > Tolerance ? number : throw new EdgeError($"{where}: {name} must be positive.");
    }

    private static Pt Point(JsonElement item, string name, string where)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2
            || value[0].ValueKind != JsonValueKind.Number || value[1].ValueKind != JsonValueKind.Number)
            throw new EdgeError($"{where}: {name} must be [x, y].");
        double x = value[0].GetDouble(), y = value[1].GetDouble();
        if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > MaxCoordinate || Math.Abs(y) > MaxCoordinate)
            throw new EdgeError($"{where}: {name} is out of range.");
        return new Pt(x, y);
    }
}
