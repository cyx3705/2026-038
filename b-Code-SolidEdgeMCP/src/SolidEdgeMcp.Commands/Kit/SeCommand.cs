using System.Text.Json;

namespace SolidEdgeMcp.Commands;

/// <summary>
/// Base for commands declared with <see cref="ToolParameter"/> specs: the specs produce the input
/// schema and validate the raw arguments before <see cref="Execute(CommandCall)"/> runs.
/// </summary>
public abstract class SeCommand(string name, CommandKind kind, string description, params ToolParameter[] parameters)
    : ISeCommand
{
    public string Name { get; } = name;

    public string Description { get; } = description;

    public CommandKind Kind { get; } = kind;

    public IReadOnlyList<ToolParameter> Parameters { get; } = parameters;

    public JsonObject InputSchema => ToolParameter.ObjectSchema(Parameters);

    public virtual bool EnabledByDefault => true;

    public abstract CommandOutput Execute(CommandCall call);

    public CommandOutput Execute(CommandContext context) => Execute(new CommandCall(context, Bind(context.Arguments)));

    /// <summary>Validate raw arguments against the specs; unknown names are rejected.</summary>
    public ToolArguments Bind(JsonObject arguments)
    {
        var duplicate = Parameters.GroupBy(p => p.Name).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new EdgeError($"{Name}: duplicate parameter {duplicate.Key}.");
        var unknown = arguments.Select(p => p.Key).Except(Parameters.Select(p => p.Name)).FirstOrDefault();
        if (unknown is not null)
            throw new EdgeError($"Unknown argument {unknown} for {Name}. Call se_find with command={Name}.");
        var elements = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString())!;
        return new ToolArguments(Parameters.ToDictionary(p => p.Name, p => p.Read(elements)));
    }
}

/// <summary>One validated invocation: shared state, typed arguments, and nested calls.</summary>
public sealed class CommandCall(CommandContext context, ToolArguments args)
{
    public SolidEdgeBackend Edge => context.Edge;

    public ToolArguments Args { get; } = args;

    /// <summary>Run another enabled command and return its JSON object result.</summary>
    public JsonObject Run(string command, JsonObject? arguments = null) =>
        context.Run(command, arguments).Data as JsonObject
        ?? throw new EdgeError($"Command {command} did not return a JSON object.");
}
