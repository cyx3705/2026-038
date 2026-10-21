using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using static SolidEdgeMcp.ToolParameter;

namespace SolidEdgeMcp;

/// <summary>MCP tools, argument schemas, the guide resource, and their binding to the COM worker.</summary>
public static class SolidEdgeServer
{
    public const string Name = "solid-edge-mcp";
    public const string GuideUri = "solidedge://guide";

    public const string Instructions = """
        Control Siemens Solid Edge through its Windows COM API.
        Call se_connect, then se_new_part or se_list_documents. Use the returned document_id
        for every document operation. IDs expire on disconnect, close, or server restart.
        All modeling lengths are millimeters; variable angles are degrees. Plane and model
        indices are 1-based. Query se_reference_planes; sketch coordinates are plane-local.
        This release models ordered, single-body .par parts. The first extrusion creates a
        base solid; later add/cut extrusions modify it. A profile is consumed after extrusion.
        Verify geometry after changing dimensions and inspect the returned feature status.
        Writes are confined to SOLIDEDGE_MCP_OUTPUT_DIR. Existing files require overwrite=true.
        Errors are MCP isError responses. After a COM error or timeout, inspect the document
        before retrying writes: COM modeling is not transactional and may have changed it.
        No tool runs arbitrary scripts, shell commands, or undocumented COM member chains.

        """;

    private static readonly ToolAnnotations Read = new() { ReadOnlyHint = true, DestructiveHint = false, OpenWorldHint = false };
    private static readonly ToolAnnotations Write = new() { ReadOnlyHint = false, DestructiveHint = true, OpenWorldHint = false };
    private static readonly ToolAnnotations Create = new() { ReadOnlyHint = false, DestructiveHint = false, OpenWorldHint = false };

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed record ToolSpec(
        string Name,
        string Description,
        ToolAnnotations Annotations,
        ToolParameter[] Parameters,
        Func<ToolArguments, Task<CallToolResult>> Handler);

    public static McpServerOptions CreateOptions(ComWorker<SolidEdgeBackend> com)
    {
        var tools = Tools(com).ToDictionary(t => t.Name);
        var advertised = tools.Values.Select(t => new Tool
        {
            Name = t.Name,
            Description = t.Description,
            Annotations = t.Annotations,
            InputSchema = ObjectSchema(t.Parameters),
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
                        var arguments = request.Params?.Arguments as IReadOnlyDictionary<string, JsonElement>;
                        var values = tool.Parameters.ToDictionary(p => p.Name, p => p.Read(arguments));
                        return await tool.Handler(new ToolArguments(values));
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

    private static CallToolResult Success(JsonNode result) => new()
    {
        Content = [new TextContentBlock { Text = result.ToJsonString(Pretty) }],
        StructuredContent = JsonSerializer.SerializeToElement(result),
    };

    private static IEnumerable<ToolSpec> Tools(ComWorker<SolidEdgeBackend> com)
    {
        async Task<CallToolResult> Call(Func<SolidEdgeBackend, JsonNode> operation) => Success(await com.CallAsync(operation));

        ToolSpec Tool(string name, string description, ToolAnnotations annotations, ToolParameter[] parameters,
            Func<ToolArguments, Func<SolidEdgeBackend, JsonNode>> operation) =>
            new(name, description, annotations, parameters, a => Call(operation(a)));

        var documentId = Text("document_id");

        yield return Tool("se_status",
            "Get server connection, Solid Edge version, and output directory; never launch CAD.",
            Read, [], _ => b => b.Status());
        yield return Tool("se_connect",
            "Attach to Solid Edge; optionally launch it. Keep CAD visible for interactive work.",
            Create, [Flag("start_if_needed", true), Flag("visible", true)],
            a => b => b.Connect(a.Flag("start_if_needed"), a.Flag("visible")));
        yield return Tool("se_disconnect",
            "Release COM references and invalidate IDs. Leave Solid Edge and documents open.",
            Create, [], _ => b => b.Disconnect());
        yield return Tool("se_list_documents",
            "List open documents and their stable IDs for this server connection.",
            Read, [], _ => b => b.ListDocuments());
        yield return Tool("se_get_document",
            "Read document name, path, type, and unsaved-change status.",
            Read, [documentId], a => b => b.GetDocument(a.Text("document_id")));
        yield return Tool("se_new_part",
            "Create a new empty ordered part using Solid Edge's default part template.",
            Create, [], _ => b => b.NewPart());
        yield return Tool("se_open_document",
            "Open an existing .par file. Supply its absolute Windows path.",
            Create, [Text("path")], a => b => b.OpenDocument(a.Text("path")));
        yield return Tool("se_reference_planes",
            "List actual reference plane names and 1-based indices in this part.",
            Read, [documentId], a => b => b.ReferencePlanes(a.Text("document_id")));
        yield return Tool("se_create_rectangle",
            "Create a rectangle from its lower-left corner, using local plane coordinates.",
            Create, [documentId, Length("width_mm"), Length("height_mm"), Coordinate("x_mm"), Coordinate("y_mm"), Index("plane_index")],
            a => b => b.Rectangle(a.Text("document_id"), a.Number("width_mm"), a.Number("height_mm"),
                a.Number("x_mm"), a.Number("y_mm"), a.Integer("plane_index")));
        yield return Tool("se_create_circle",
            "Create a closed circle profile. x_mm/y_mm specify its center on the local plane.",
            Create, [documentId, Length("radius_mm"), Coordinate("x_mm"), Coordinate("y_mm"), Index("plane_index")],
            a => b => b.Circle(a.Text("document_id"), a.Number("radius_mm"), a.Number("x_mm"), a.Number("y_mm"),
                a.Integer("plane_index")));
        yield return Tool("se_extrude",
            "Extrude a closed profile and verify volume change. Consumes the profile on completion.",
            Write, [documentId, Text("profile_id"), Length("distance_mm"), Choice("operation", "add", "add", "cut"),
                Choice("direction", "normal", "normal", "reverse")],
            a => b => b.Extrude(a.Text("document_id"), a.Text("profile_id"), a.Number("distance_mm"),
                a.Text("operation"), a.Text("direction")));
        yield return Tool("se_create_box",
            "Create a box in an EMPTY part; width/depth follow plane axes, height its normal.",
            Create, [documentId, Length("width_mm"), Length("depth_mm"), Length("height_mm"), Index("plane_index")],
            a => b => b.Box(a.Text("document_id"), a.Number("width_mm"), a.Number("depth_mm"), a.Number("height_mm"),
                a.Integer("plane_index")));
        yield return Tool("se_create_cylinder",
            "Create a cylinder in an EMPTY part, centered at the plane origin.",
            Create, [documentId, Length("radius_mm"), Length("height_mm"), Index("plane_index")],
            a => b => b.Cylinder(a.Text("document_id"), a.Number("radius_mm"), a.Number("height_mm"), a.Integer("plane_index")));
        yield return Tool("se_geometry",
            "Measure solid volume (mm^3), face count, and world-coordinate bounding box (mm).",
            Read, [documentId], a => b => b.Geometry(a.Text("document_id")));
        yield return Tool("se_features",
            "List ordered features, model indices, and Solid Edge feature status codes.",
            Read, [documentId], a => b => b.Features(a.Text("document_id")));
        yield return Tool("se_list_variables",
            "List numeric variables/dimensions, exact names, formulas, values, and units.",
            Read, [documentId], a => b => b.ListVariables(a.Text("document_id")));
        yield return Tool("se_set_extrusion_depth",
            "Change a finite extrusion/cutout depth by feature name; read back depth and geometry.",
            Write, [documentId, Text("feature_name"), Length("depth_mm")],
            a => b => b.SetExtrusionDepth(a.Text("document_id"), a.Text("feature_name"), a.Number("depth_mm")));
        yield return Tool("se_set_variable",
            "Set an existing variable and read it back; unit must match se_list_variables exactly.",
            Write, [documentId, Text("name"), Finite("value"), Choice("unit", "mm", "deg", "raw")],
            a => b => b.SetVariable(a.Text("document_id"), a.Text("name"), a.Number("value"), a.Text("unit")));
        yield return Tool("se_save_document",
            "Save a native .par inside the output root. Existing files need overwrite=true.",
            Write, [documentId, OptionalText("path"), Flag("overwrite", false)],
            a => b => b.SaveDocument(a.Text("document_id"), a.OptionalText("path"), a.Flag("overwrite")));
        yield return Tool("se_export_step",
            "Export .step/.stp, verify a nonempty file, and retain the native part document path.",
            Write, [documentId, Text("path"), Flag("overwrite", false)],
            a => b => b.ExportStep(a.Text("document_id"), a.Text("path"), a.Flag("overwrite")));
        yield return Tool("se_set_view",
            "Activate this part, apply a named view, and fit the geometry in the window.",
            Create, [documentId, Choice("orientation", "Iso", "Iso", "Front", "Top", "Right")],
            a => b => b.SetView(a.Text("document_id"), a.Text("orientation")));
        yield return new ToolSpec("se_capture_viewport",
            "Save a PNG of this part's current view and return the image to the AI client.",
            Write,
            [documentId, new ToolParameter("path", "string", false, "viewport.png"), Integer("width", 1280, 64, 4096),
                Integer("height", 960, 64, 4096), Flag("overwrite", false)],
            async a =>
            {
                var saved = await com.CallAsync(b => b.CaptureViewport(a.Text("document_id"), a.Text("path"),
                    a.Integer("width"), a.Integer("height"), a.Flag("overwrite")));
                var bytes = await File.ReadAllBytesAsync((string)saved["path"]!);
                return new CallToolResult
                {
                    Content =
                    [
                        ImageContentBlock.FromBytes(bytes, "image/png"),
                        new TextContentBlock { Text = saved.ToJsonString(Pretty) },
                    ],
                };
            });
        yield return Tool("se_close_document",
            "Close this document. Refuse unsaved changes unless discard_changes is explicitly true.",
            Write, [documentId, Flag("discard_changes", false)],
            a => b => b.CloseDocument(a.Text("document_id"), a.Flag("discard_changes")));
    }
}
