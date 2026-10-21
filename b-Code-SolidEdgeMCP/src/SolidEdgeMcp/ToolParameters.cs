using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SolidEdgeMcp;

/// <summary>
/// One tool argument. The same spec emits the advertised JSON schema and validates incoming calls,
/// so what a client is told and what the server accepts cannot drift apart.
/// </summary>
public sealed record ToolParameter(
    string Name,
    string Type,
    bool Required = true,
    JsonNode? Default = null,
    double? Minimum = null,
    double? ExclusiveMinimum = null,
    double? Maximum = null,
    string[]? Choices = null,
    bool Nullable = false)
{
    public const double Limit = 1_000_000;

    public static ToolParameter Text(string name) => new(name, "string");

    public static ToolParameter OptionalText(string name) => new(name, "string", false, null, Nullable: true);

    public static ToolParameter Length(string name) => new(name, "number", ExclusiveMinimum: 0, Maximum: Limit);

    public static ToolParameter Coordinate(string name) => new(name, "number", false, 0, -Limit, Maximum: Limit);

    public static ToolParameter Finite(string name) => new(name, "number");

    public static ToolParameter Index(string name) => new(name, "integer", false, 1, 1);

    public static ToolParameter Integer(string name, int fallback, int minimum, int maximum) =>
        new(name, "integer", false, fallback, minimum, Maximum: maximum);

    public static ToolParameter Flag(string name, bool fallback) => new(name, "boolean", false, fallback);

    public static ToolParameter Choice(string name, params string[] choices) => new(name, "string", Choices: choices);

    public static ToolParameter Choice(string name, string fallback, params string[] choices) =>
        new(name, "string", false, fallback, Choices: choices);

    public JsonObject Schema()
    {
        var schema = new JsonObject();
        if (Nullable)
            schema["anyOf"] = new JsonArray(new JsonObject { ["type"] = Type }, new JsonObject { ["type"] = "null" });
        else
            schema["type"] = Type;
        if (Choices is not null)
            schema["enum"] = new JsonArray(Choices.Select(c => (JsonNode)c).ToArray());
        if (Minimum is { } minimum)
            schema["minimum"] = minimum;
        if (ExclusiveMinimum is { } exclusive)
            schema["exclusiveMinimum"] = exclusive;
        if (Maximum is { } maximum)
            schema["maximum"] = maximum;
        if (!Required)
            schema["default"] = Default?.DeepClone();
        return schema;
    }

    public static JsonElement ObjectSchema(IEnumerable<ToolParameter> parameters)
    {
        var list = parameters.ToList();
        var properties = new JsonObject();
        foreach (var parameter in list)
            properties[parameter.Name] = parameter.Schema();
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(list.Where(p => p.Required).Select(p => (JsonNode)p.Name).ToArray()),
        };
        return JsonSerializer.SerializeToElement(schema);
    }

    /// <summary>Check one argument against this spec and return it as a plain CLR value.</summary>
    public object? Read(IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || !arguments.TryGetValue(Name, out var value))
        {
            if (Required)
                throw new EdgeError($"Missing required argument: {Name}.");
            return Default is null ? null : Convert(JsonSerializer.SerializeToElement(Default));
        }
        return Convert(value);
    }

    private object? Convert(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null && Nullable)
            return null;
        object result = Type switch
        {
            "string" when value.ValueKind == JsonValueKind.String => value.GetString()!,
            "boolean" when value.ValueKind is JsonValueKind.True or JsonValueKind.False => value.GetBoolean(),
            "number" when value.ValueKind == JsonValueKind.Number => value.GetDouble(),
            "integer" when value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var whole) => whole,
            _ => throw new EdgeError($"Invalid argument {Name}: expected {Type}."),
        };
        if (Choices is not null && !Choices.Contains((string)result))
            throw new EdgeError($"Invalid argument {Name}: expected one of {string.Join(", ", Choices)}.");
        if (result is double or int)
        {
            var number = System.Convert.ToDouble(result, CultureInfo.InvariantCulture);
            if (!double.IsFinite(number))
                throw new EdgeError($"Invalid argument {Name}: must be finite.");
            if (number < Minimum)
                throw new EdgeError($"Invalid argument {Name}: must be at least {Minimum}.");
            if (number <= ExclusiveMinimum)
                throw new EdgeError($"Invalid argument {Name}: must be greater than {ExclusiveMinimum}.");
            if (number > Maximum)
                throw new EdgeError($"Invalid argument {Name}: must be at most {Maximum}.");
        }
        return result;
    }
}

/// <summary>Validated arguments for one call, read by name in the tool handler.</summary>
public sealed class ToolArguments(IReadOnlyDictionary<string, object?> values)
{
    public string Text(string name) => (string)values[name]!;

    public string? OptionalText(string name) => (string?)values[name];

    public double Number(string name) => (double)values[name]!;

    public int Integer(string name) => (int)values[name]!;

    public bool Flag(string name) => (bool)values[name]!;
}
