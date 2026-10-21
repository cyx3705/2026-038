// Exercise a real stdio MCP client against a licensed Solid Edge instance.
// Creates only new parts, writes to a new run directory, and closes only its own documents.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

var outputDir = Path.Combine("data", DateTime.Now.ToString("'smoke-'yyyyMMdd-HHmmss"));
var keepOpen = false;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--output-dir" && i + 1 < args.Length)
        outputDir = args[++i];
    else if (args[i] == "--keep-open")
        keepOpen = true;
    else
        throw new ArgumentException($"Unknown argument: {args[i]}");
}
outputDir = Path.GetFullPath(outputDir);
if (Directory.Exists(outputDir))
    throw new IOException($"Output directory already exists: {outputDir}");
Directory.CreateDirectory(outputDir);

var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
var compact = new JsonSerializerOptions(json) { WriteIndented = false };
var steps = new JsonArray();
var report = new JsonObject { ["passed"] = false, ["steps"] = steps };
var created = new List<string>();

void Check(bool condition, string what)
{
    if (!condition)
        throw new InvalidOperationException($"Check failed: {what}");
}

static bool Close(double actual, double expected) => Math.Abs(actual - expected) <= 1e-7 * Math.Abs(expected);

try
{
    var transport = new StdioClientTransport(new StdioClientTransportOptions
    {
        Name = "solid-edge",
        Command = Path.Combine(AppContext.BaseDirectory, "SolidEdgeMcp.exe"),
        // Switches go to the run directory so the smoke never edits the user's config/commands.json.
        Arguments = ["--output-dir", outputDir, "--switches", Path.Combine(outputDir, "switches.json")],
    });
    await using var session = await McpClient.CreateAsync(transport);
    report["tool_count"] = (await session.ListToolsAsync()).Count;
    report["command_count"] = (await session.CallToolAsync("se_find")).StructuredContent!.Value.GetProperty("commands").GetArrayLength();

    // Every CAD operation goes through the fixed se_call tool.
    ValueTask<CallToolResult> Raw(string command, Dictionary<string, object?>? arguments = null) =>
        session.CallToolAsync("se_call", new Dictionary<string, object?> { ["command"] = command, ["arguments"] = arguments ?? [] });

    async Task<JsonNode> Call(string tool, Dictionary<string, object?>? arguments = null)
    {
        arguments ??= [];
        var result = await Raw(tool, arguments);
        if (result.IsError == true)
            throw new InvalidOperationException($"{tool}: {string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text))}");
        JsonNode data = result.StructuredContent is { } structured
            ? JsonNode.Parse(structured.GetRawText())!
            : new JsonObject { ["content_types"] = new JsonArray(result.Content.Select(c => (JsonNode)c.Type).ToArray()) };
        Console.WriteLine(new JsonObject { ["command"] = tool, ["result"] = data.DeepClone() }.ToJsonString(compact));
        steps.Add(new JsonObject
        {
            ["command"] = tool,
            ["arguments"] = JsonSerializer.SerializeToNode(arguments),
            ["result"] = data,
        });
        return data;
    }

    static double Volume(JsonNode geometry) => (double)geometry["total_volume_mm3"]!;

    try
    {
        await Call("status");
        report["connection"] = (await Call("connect")).DeepClone();
        var docId = (string)(await Call("new_part"))["document_id"]!;
        created.Add(docId);
        await Call("reference_planes", new() { ["document_id"] = docId });
        await Call("create_box", new() { ["document_id"] = docId, ["width_mm"] = 100, ["depth_mm"] = 60, ["height_mm"] = 20 });
        var geometry = await Call("geometry", new() { ["document_id"] = docId });
        Check(Close(Volume(geometry), 120000), "box volume");
        var size = geometry["models"]![0]!["size_mm"]!.AsArray().Select(v => Math.Round((double)v!, 6)).Order().ToArray();
        Check(size.SequenceEqual([20.0, 60.0, 100.0]), "box size");
        var variables = await Call("list_variables", new() { ["document_id"] = docId });
        var accuracy = variables["variables"]!.AsArray().First(v => (string)v!["name"]! == "PhysicalProperties_Accuracy")!;
        await Call("set_variable", new()
        {
            ["document_id"] = docId, ["name"] = (string)accuracy["name"]!, ["value"] = 0.98, ["unit"] = "raw",
        });
        var features = await Call("features", new() { ["document_id"] = docId });
        await Call("set_extrusion_depth", new()
        {
            ["document_id"] = docId, ["feature_name"] = (string)features["features"]![0]!["name"]!, ["depth_mm"] = 25,
        });
        geometry = await Call("geometry", new() { ["document_id"] = docId });
        Check(Close(Volume(geometry), 150000), "volume after depth change");
        var circle = await Call("create_circle", new() { ["document_id"] = docId, ["radius_mm"] = 6, ["x_mm"] = 50, ["y_mm"] = 30 });
        await Call("extrude", new()
        {
            ["document_id"] = docId, ["profile_id"] = (string)circle["profile_id"]!, ["distance_mm"] = 25, ["operation"] = "cut",
        });
        geometry = await Call("geometry", new() { ["document_id"] = docId });
        var expected = 150000 - Math.PI * 6 * 6 * 25;
        Check(Close(Volume(geometry), expected), "volume after cut");
        // Hot swap with CAD state live: switch one command off and on, then unload and reload the
        // whole commands assembly. Document IDs belong to the shell and must survive.
        async Task<JsonElement> Shell(string tool, string? command = null)
        {
            var result = await session.CallToolAsync(tool, command is null ? null : new Dictionary<string, object?> { ["command"] = command });
            if (result.IsError == true)
                throw new InvalidOperationException($"{tool}: {string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text))}");
            steps.Add(new JsonObject { ["shell"] = tool, ["command"] = command, ["result"] = JsonNode.Parse(result.StructuredContent!.Value.GetRawText()) });
            return result.StructuredContent!.Value;
        }
        await Shell("se_unload", "geometry");
        Check((await Raw("geometry", new() { ["document_id"] = docId })).IsError == true, "switched-off command refused");
        await Shell("se_load", "geometry");
        await Shell("se_unload");
        var reloaded = await Shell("se_load");
        Check(reloaded.GetProperty("command_count").GetInt32() == (int)report["command_count"]!, "reloaded command set");
        Check(Close(Volume(await Call("geometry", new() { ["document_id"] = docId })), expected), "document survives reload");
        await Call("features", new() { ["document_id"] = docId });
        await Call("set_view", new() { ["document_id"] = docId });
        await Call("capture_viewport", new() { ["document_id"] = docId, ["path"] = "plate-with-hole.png" });
        await Call("save_document", new() { ["document_id"] = docId, ["path"] = "plate-with-hole.par" });
        await Call("export_step", new() { ["document_id"] = docId, ["path"] = "plate-with-hole.step" });
        var header = File.ReadAllBytes(Path.Combine(outputDir, "plate-with-hole.step")).Take(13).ToArray();
        Check(header.SequenceEqual("ISO-10303-21;"u8.ToArray()), "STEP header");
        // Overwrite guards and document reopening exercise persistence through MCP.
        var duplicate = await Raw("export_step", new Dictionary<string, object?>
        {
            ["document_id"] = docId, ["path"] = "plate-with-hole.step",
        });
        Check(duplicate.IsError == true, "overwrite guard");
        await Call("close_document", new() { ["document_id"] = docId });
        created.Remove(docId);
        var reopened = await Call("open_document", new() { ["path"] = Path.Combine(outputDir, "plate-with-hole.par") });
        docId = (string)reopened["document_id"]!;
        created.Add(docId);
        await Call("get_document", new() { ["document_id"] = docId });
        var listing = await Call("list_documents");
        Check(listing["documents"]!.AsArray().Any(d => (string)d!["document_id"]! == docId), "reopened document listed");
        Check(Close(Volume(await Call("geometry", new() { ["document_id"] = docId })), expected), "reopened volume");
        // A second part exercises IDs while another document is active.
        var cylinderId = (string)(await Call("new_part"))["document_id"]!;
        created.Add(cylinderId);
        await Call("create_cylinder", new() { ["document_id"] = cylinderId, ["radius_mm"] = 10, ["height_mm"] = 30 });
        var cylinder = Math.PI * 100 * 30;
        Check(Close(Volume(await Call("geometry", new() { ["document_id"] = cylinderId })), cylinder), "cylinder volume");
        var rectangle = await Call("create_rectangle", new() { ["document_id"] = cylinderId, ["width_mm"] = 5, ["height_mm"] = 5 });
        Check((bool)rectangle["validation_passed"]!, "rectangle closed");
        var added = await Call("extrude", new()
        {
            ["document_id"] = cylinderId, ["profile_id"] = (string)rectangle["profile_id"]!, ["distance_mm"] = 40,
        });
        Check(Close(Volume(added["geometry"]!), cylinder + 250), "cylinder plus rectangle volume");
        Check(Close(Volume(await Call("geometry", new() { ["document_id"] = docId })), expected), "plate untouched");
        await Call("close_document", new() { ["document_id"] = cylinderId, ["discard_changes"] = true });
        created.Remove(cylinderId);
        // Sketch: a slot with a tangent arc end and a hole, read back from CAD, then extruded.
        var slotId = (string)(await Call("new_part"))["document_id"]!;
        created.Add(slotId);
        var entities = JsonNode.Parse("""
            [
              {"id": "L1", "type": "line", "start": [0, 0], "end": [80, 0]},
              {"id": "A1", "type": "arc", "center": [80, 10], "start": [80, 0], "end": [80, 20]},
              {"id": "L2", "type": "line", "start": [80, 20], "end": [0, 20]},
              {"id": "L3", "type": "line", "start": [0, 20], "end": [0, 0]},
              {"id": "H1", "type": "circle", "center": [40, 10], "radius": 4}
            ]
            """);
        var slot = await Call("sketch", new() { ["document_id"] = slotId, ["entities"] = entities });
        var net = 1600 + Math.PI * 50 - Math.PI * 16;
        Check(Close((double)slot["net_area_mm2"]!, net), "sketch net area");
        Check(slot["issues"]!.AsArray().Count == 0 && (double)slot["max_deviation_mm"]! < 1e-6, "sketch read back as described");
        Check(((string)slot["svg"]!).StartsWith("<svg"), "sketch svg");
        var listed = await Call("view_sketch", new() { ["document_id"] = slotId });
        Check(listed["sketches"]!.AsArray().Count == 1, "one sketch listed");
        var viewed = await Raw("view_sketch", new() { ["document_id"] = slotId, ["sketch_index"] = 1, ["png"] = true });
        Check(viewed.IsError != true && viewed.Content.OfType<ImageContentBlock>().Any(), "view_sketch png");
        File.WriteAllBytes(Path.Combine(outputDir, "sketch-slot.png"), viewed.Content.OfType<ImageContentBlock>().First().DecodedData.ToArray());
        File.WriteAllText(Path.Combine(outputDir, "sketch-slot.svg"), (string)slot["svg"]!);
        var bowTie = await Raw("sketch", new()
        {
            ["document_id"] = slotId,
            ["entities"] = JsonNode.Parse("""
                [{"id": "a", "type": "line", "start": [0, 0], "end": [10, 10]},
                 {"id": "b", "type": "line", "start": [10, 10], "end": [10, 0]},
                 {"id": "c", "type": "line", "start": [10, 0], "end": [0, 10]},
                 {"id": "d", "type": "line", "start": [0, 10], "end": [0, 0]}]
                """),
        });
        Check(bowTie.IsError == true, "self-intersecting sketch refused");
        Check((await Call("view_sketch", new() { ["document_id"] = slotId }))["sketches"]!.AsArray().Count == 1, "refused sketch left nothing");
        var slotSolid = await Call("extrude", new() { ["document_id"] = slotId, ["profile_id"] = (string)slot["profile_id"]!, ["distance_mm"] = 10 });
        Check(Close(Volume(slotSolid["geometry"]!), net * 10), "slot volume = net area x depth");
        var afterExtrude = await Call("view_sketch", new() { ["document_id"] = slotId, ["sketch_index"] = 1 });
        Check(Close((double)afterExtrude["net_area_mm2"]!, net), "consumed sketch still readable");
        await Call("close_document", new() { ["document_id"] = slotId, ["discard_changes"] = true });
        created.Remove(slotId);
        foreach (var orientation in new[] { "Front", "Top", "Right", "Iso" })
            await Call("set_view", new() { ["document_id"] = docId, ["orientation"] = orientation });
        report["unique_commands_exercised"] = new JsonArray(
            steps.Where(s => s!["shell"] is null).Select(s => (string)s!["command"]!).Distinct().Order().Select(t => (JsonNode)t).ToArray());
        report["passed"] = true;
    }
    finally
    {
        if (!(keepOpen && (bool)report["passed"]!))
        {
            foreach (var documentId in created)
            {
                await Raw("close_document", new Dictionary<string, object?>
                {
                    ["document_id"] = documentId, ["discard_changes"] = true,
                });
            }
        }
        var disconnected = await Raw("disconnect");
        Check(disconnected.IsError != true, "disconnect");
    }
}
catch (Exception exc)
{
    report["error"] = exc.Message;
    Console.Error.WriteLine(exc);
}
finally
{
    File.WriteAllText(Path.Combine(outputDir, "verification.json"), report.ToJsonString(json));
}
Console.WriteLine($"passed={report["passed"]} output={outputDir}");
return (bool)report["passed"]! ? 0 : 1;
