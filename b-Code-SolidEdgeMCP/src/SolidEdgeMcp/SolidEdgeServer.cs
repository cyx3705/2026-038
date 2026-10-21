using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SolidEdgeMcp;

/// <summary>
/// The fixed MCP surface: four shell tools that find, call, load and unload internal commands.
/// The tool list never changes at runtime, so clients never need to reconnect for new commands.
/// </summary>
public static class SolidEdgeServer
{
    public const string Name = "solid-edge-mcp";
    public const string GuideUri = "solidedge://guide";

    public const string Instructions = """
        Control Siemens Solid Edge through its Windows COM API via internal commands.
        se_find lists commands (name, kind, enabled, description); se_find with command=<name>
        returns that command's input schema. se_call runs a command with an arguments object.
        se_load with no arguments hot-reloads the commands assembly after a rebuild; with
        command=<name> it switches that command on. se_unload with command=<name> switches it
        off; with no arguments it unloads the whole commands assembly.
        Typical flow: connect, then new_part or list_documents. Use the returned document_id
        for every document operation. IDs expire on disconnect, close, or server restart; a
        command reload keeps them. All modeling lengths are millimeters; variable angles are
        degrees. Plane and model indices are 1-based; sketch coordinates are plane-local.
        This release models ordered, single-body .par parts. The first extrusion creates a
        base solid; later add/cut extrusions modify it. A profile is consumed after extrusion.
        Writes are confined to the output root. Existing files require overwrite=true.
        Errors are MCP isError responses. After a COM error or timeout, inspect the document
        before retrying writes: COM modeling is not transactional and may have changed it.
        Commands are compiled code from the configured commands assembly; there is no tool
        for arbitrary scripts, shell commands, or undocumented COM member chains.

        """;

    private static readonly ToolAnnotations ReadOnly = new() { ReadOnlyHint = true, DestructiveHint = false, OpenWorldHint = false };
    private static readonly ToolAnnotations Write = new() { ReadOnlyHint = false, DestructiveHint = true, OpenWorldHint = false };
    private static readonly ToolAnnotations Toggle = new() { ReadOnlyHint = false, DestructiveHint = false, OpenWorldHint = false };

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed record ToolSpec(
        string Name,
        string Description,
        ToolAnnotations Annotations,
        JsonObject Schema,
        Func<Args, Task<CallToolResult>> Handler);

    /// <summary>Raw arguments of one of the four shell tools.</summary>
    private sealed class Args(IReadOnlyDictionary<string, JsonElement>? values)
    {
        public string? Text(string name, bool required = false)
        {
            if (values is null || !values.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
                return required ? throw new EdgeError($"Missing required argument: {name}.") : null;
            return value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : throw new EdgeError($"Invalid argument {name}: expected string.");
        }

        public JsonObject Object(string name)
        {
            if (values is null || !values.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
                return [];
            return value.ValueKind == JsonValueKind.Object
                ? JsonNode.Parse(value.GetRawText())!.AsObject()
                : throw new EdgeError($"Invalid argument {name}: expected object.");
        }
    }

    private static JsonObject Nullable(string type) => new()
    {
        ["anyOf"] = new JsonArray(new JsonObject { ["type"] = type }, new JsonObject { ["type"] = "null" }),
        ["default"] = null,
    };

    /// <summary>Schema of a shell tool: "command" (required or optional) plus extra properties.</summary>
    private static JsonObject Schema(bool commandRequired, params (string Name, JsonObject Schema)[] extra)
    {
        var properties = new JsonObject
        {
            ["command"] = commandRequired ? new JsonObject { ["type"] = "string" } : Nullable("string"),
        };
        foreach (var (name, schema) in extra)
            properties[name] = schema;
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = commandRequired ? new JsonArray("command") : new JsonArray(),
        };
    }

    public static McpServerOptions CreateOptions(ComWorker<SolidEdgeBackend> com, CommandRegistry registry)
    {
        var tools = Tools(com, registry).ToDictionary(t => t.Name);
        var advertised = tools.Values.Select(t => new Tool
        {
            Name = t.Name,
            Description = t.Description,
            Annotations = t.Annotations,
            InputSchema = JsonSerializer.SerializeToElement(t.Schema),
        }).ToList();

        return new McpServerOptions
        {
            ServerInfo = new Implementation { Name = Name, Version = typeof(SolidEdgeServer).Assembly.GetName().Version!.ToString(3) },
            ServerInstructions = Instructions,
            Capabilities = new ServerCapabilities { Tools = new(), Resources = new() },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = advertised }),
                CallToolHandler = async (request, _) =>
                {
                    var name = request.Params?.Name ?? "";
                    if (!tools.TryGetValue(name, out var tool))
                        return Error($"Unknown tool: {name}");
                    try
                    {
                        return await tool.Handler(new Args(request.Params?.Arguments as IReadOnlyDictionary<string, JsonElement>));
                    }
                    catch (EdgeError exc)
                    {
                        return Error(exc.Message);
                    }
                },
                ListResourcesHandler = (_, _) => ValueTask.FromResult(new ListResourcesResult
                {
                    Resources =
                    [
                        new Resource
                        {
                            Uri = GuideUri,
                            Name = "guide",
                            Description = "Units, scope, workflow, and failure semantics.",
                            MimeType = "text/plain",
                        },
                    ],
                }),
                ReadResourceHandler = (request, _) =>
                {
                    if (request.Params?.Uri != GuideUri)
                        throw new ArgumentException($"Unknown resource: {request.Params?.Uri}");
                    return ValueTask.FromResult(new ReadResourceResult
                    {
                        Contents = [new TextResourceContents { Uri = GuideUri, MimeType = "text/plain", Text = Instructions }],
                    });
                },
            },
        };
    }

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };

    private static CallToolResult Success(JsonNode result, byte[]? png = null)
    {
        var text = new TextContentBlock { Text = result.ToJsonString(Pretty) };
        return new CallToolResult
        {
            Content = png is null ? [text] : [ImageContentBlock.FromBytes(png, "image/png"), text],
            StructuredContent = JsonSerializer.SerializeToElement(result),
        };
    }

    private static IEnumerable<ToolSpec> Tools(ComWorker<SolidEdgeBackend> com, CommandRegistry registry)
    {
        yield return new ToolSpec("se_find",
            "List internal commands with kind, on/off switch and description; with command=<name>, return its input schema.",
            ReadOnly, Schema(false),
            a => Task.FromResult(Success(registry.Describe(a.Text("command")))));
        yield return new ToolSpec("se_call",
            "Run one enabled internal command. arguments must match the schema from se_find.",
            Write, Schema(true, ("arguments", Nullable("object"))),
            async a =>
            {
                var command = registry.Get(a.Text("command", required: true)!);
                var arguments = a.Object("arguments");
                CommandOutput? output = null;
                var data = await com.CallAsync(b => (output = registry.Execute(b, command, arguments)).Data);
                return Success(data, output?.Png);
            });
        yield return new ToolSpec("se_load",
            "No arguments: hot-reload the commands assembly after a rebuild. command=<name>: switch that command on.",
            Toggle, Schema(false),
            a => Task.FromResult(Success(registry.Load(a.Text("command")))));
        yield return new ToolSpec("se_unload",
            "command=<name>: switch that command off. No arguments: unload the whole commands assembly.",
            Toggle, Schema(false),
            a => Task.FromResult(Success(registry.Unload(a.Text("command")))));
    }
}
