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
/// The whole contract between the shell and the hot-swappable commands assembly. Every public,
/// non-abstract implementation with a parameterless constructor is discovered on (re)load.
/// Kept deliberately small: anything that may still change (parameter specs, validation,
/// CAD helpers) lives in the commands assembly, so changing it never needs a shell release.
/// </summary>
public interface ISeCommand
{
    string Name { get; }

    string Description { get; }

    CommandKind Kind { get; }

    /// <summary>JSON Schema of the arguments object, shown by se_find.</summary>
    JsonObject InputSchema { get; }

    /// <summary>Switch state before anyone has toggled it; a stored switch always wins.</summary>
    bool EnabledByDefault => true;

    /// <summary>Runs on the COM STA thread. The command validates <see cref="CommandContext.Arguments"/> itself.</summary>
    CommandOutput Execute(CommandContext context);
}

/// <summary>Result data plus an optional PNG the client should receive as an image block.</summary>
public sealed record CommandOutput(JsonNode Data, byte[]? Png = null)
{
    // Two arguments on purpose: new(data) would bind to the record's copy constructor through
    // this very conversion and recurse.
    public static implicit operator CommandOutput(JsonObject data) => new(data, null);
}

/// <summary>What a running command sees: the shared state, its raw arguments, and the other commands.</summary>
public sealed class CommandContext(SolidEdgeBackend edge, JsonObject arguments, Func<string, JsonObject?, CommandOutput> run)
{
    public SolidEdgeBackend Edge { get; } = edge;

    public JsonObject Arguments { get; } = arguments;

    /// <summary>Run another enabled command in this call. A switched-off command stays off here too.</summary>
    public CommandOutput Run(string command, JsonObject? arguments = null) => run(command, arguments);
}
