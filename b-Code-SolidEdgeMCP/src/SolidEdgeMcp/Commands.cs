using System.Text.Json;
using System.Text.Json.Nodes;

namespace SolidEdgeMcp;

/// <summary>How a command treats the CAD state; surfaced by se_find so the client can judge risk.</summary>
public enum CommandKind
{
    Read,
    Create,
    Write,
}

/// <summary>
/// One internal command. Every public, non-abstract implementation with a parameterless
/// constructor in the commands assembly is discovered when that assembly is (re)loaded.
/// </summary>
public interface ISeCommand
{
    string Name { get; }

    string Description { get; }

    CommandKind Kind { get; }

    IReadOnlyList<ToolParameter> Parameters { get; }

    /// <summary>Switch state before anyone has toggled it; a stored switch always wins.</summary>
    bool EnabledByDefault => true;

    /// <summary>Runs on the COM STA thread with already validated arguments.</summary>
    CommandOutput Execute(CommandContext context);
}

/// <summary>Convenience base: name, kind, description and parameters in one constructor line.</summary>
public abstract class SeCommand(string name, CommandKind kind, string description, params ToolParameter[] parameters)
    : ISeCommand
{
    public string Name { get; } = name;

    public string Description { get; } = description;

    public CommandKind Kind { get; } = kind;

    public IReadOnlyList<ToolParameter> Parameters { get; } = parameters;

    public virtual bool EnabledByDefault => true;

    public abstract CommandOutput Execute(CommandContext context);
}

/// <summary>Result data plus an optional PNG the client should receive as an image block.</summary>
public sealed record CommandOutput(JsonNode Data, byte[]? Png = null)
{
    // Two arguments on purpose: new(data) would bind to the record's copy constructor through
    // this very conversion and recurse.
    public static implicit operator CommandOutput(JsonObject data) => new(data, null);
}

/// <summary>What a running command sees: the shared kernel, its arguments, and the other commands.</summary>
public sealed class CommandContext(SolidEdgeBackend edge, ToolArguments args, Func<string, JsonObject?, CommandOutput> run)
{
    public SolidEdgeBackend Edge { get; } = edge;

    public ToolArguments Args { get; } = args;

    /// <summary>
    /// Run another enabled command in this call; arguments are validated against its parameters.
    /// A switched-off command stays off here too.
    /// </summary>
    public JsonObject Run(string command, JsonObject? arguments = null) =>
        run(command, arguments).Data as JsonObject
        ?? throw new EdgeError($"Command {command} did not return a JSON object.");

    public static IReadOnlyDictionary<string, JsonElement> Elements(JsonObject? arguments) =>
        arguments is null
            ? new Dictionary<string, JsonElement>()
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString())!;
}
