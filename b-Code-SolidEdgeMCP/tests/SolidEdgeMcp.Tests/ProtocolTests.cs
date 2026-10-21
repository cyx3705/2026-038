using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace SolidEdgeMcp.Tests;

/// <summary>Launch the real server over stdio and talk to it with the official MCP client.</summary>
public sealed class ProtocolTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("se-mcp-protocol-").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    private static string Text(CallToolResult result) => ((TextContentBlock)result.Content[^1]).Text;

    [Fact]
    public async Task RealStdioHandshakeShellToolsValidationAndErrors()
    {
        var switches = Path.Combine(_temp, "switches.json");
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "solid-edge",
            Command = Path.Combine(AppContext.BaseDirectory, "SolidEdgeMcp.exe"),
            Arguments = ["--output-dir", _temp, "--commands", CommandRegistryTests.BuiltCommands(), "--switches", switches],
        });
        await using var client = await McpClient.CreateAsync(transport);
        Assert.Equal("solid-edge-mcp", client.ServerInfo.Name);

        // The MCP surface is fixed; commands live behind it.
        var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name);
        Assert.Equal(["se_call", "se_find", "se_load", "se_unload"], tools.Keys.Order());
        Assert.True(tools["se_find"].ProtocolTool.Annotations!.ReadOnlyHint);
        Assert.True(tools["se_call"].ProtocolTool.Annotations!.DestructiveHint);

        var found = await client.CallToolAsync("se_find");
        Assert.Equal(30, found.StructuredContent!.Value.GetProperty("commands").GetArrayLength());
        var box = await client.CallToolAsync("se_find", new Dictionary<string, object?> { ["command"] = "create_box" });
        var width = box.StructuredContent!.Value.GetProperty("input_schema").GetProperty("properties").GetProperty("width_mm");
        Assert.Equal(0, width.GetProperty("exclusiveMinimum").GetDouble());

        ValueTask<CallToolResult> Call(string command, Dictionary<string, object?>? arguments = null) =>
            client.CallToolAsync("se_call", new Dictionary<string, object?> { ["command"] = command, ["arguments"] = arguments });

        foreach (var length in new[] { -1, 0 })
        {
            var invalid = await Call("create_box", new() { ["document_id"] = "unused", ["width_mm"] = length, ["depth_mm"] = 10, ["height_mm"] = 5 });
            Assert.True(invalid.IsError);
        }
        var wrongChoice = await Call("extrude", new()
        {
            ["document_id"] = "unused", ["profile_id"] = "unused", ["distance_mm"] = 10, ["operation"] = "intersect",
        });
        Assert.True(wrongChoice.IsError);
        Assert.True((await Call("get_document")).IsError);
        var misspelt = await Call("get_document", new() { ["documentid"] = "x" });
        Assert.Contains("documentid", Text(misspelt));
        Assert.True((await Call("no_such_command")).IsError);
        var disconnected = await Call("get_document", new() { ["document_id"] = "unknown" });
        Assert.True(disconnected.IsError);
        Assert.Contains("connect", Text(disconnected));

        var status = await Call("status");
        Assert.NotEqual(true, status.IsError);
        Assert.False(status.StructuredContent!.Value.GetProperty("connected").GetBoolean());
        Assert.Equal(Path.GetFullPath(_temp), status.StructuredContent!.Value.GetProperty("output_root").GetString());

        // Switching a command off and on again happens without reconnecting and is persisted.
        Assert.NotEqual(true, (await client.CallToolAsync("se_unload", new Dictionary<string, object?> { ["command"] = "status" })).IsError);
        Assert.Contains("switched off", Text(await Call("status")));
        Assert.Contains("\"status\": false", File.ReadAllText(switches));
        await client.CallToolAsync("se_load", new Dictionary<string, object?> { ["command"] = "status" });
        Assert.NotEqual(true, (await Call("status")).IsError);
        var reload = await client.CallToolAsync("se_load");
        Assert.True(reload.StructuredContent!.Value.GetProperty("unchanged").GetBoolean());

        var resource = await client.ReadResourceAsync("solidedge://guide");
        Assert.Contains("millimeters", ((TextResourceContents)resource.Contents[0]).Text);
    }
}
