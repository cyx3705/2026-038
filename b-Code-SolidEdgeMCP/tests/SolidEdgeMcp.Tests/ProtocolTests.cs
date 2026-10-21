using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace SolidEdgeMcp.Tests;

/// <summary>Launch the real server over stdio and talk to it with the official MCP client.</summary>
public sealed class ProtocolTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("se-mcp-protocol-").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    [Fact]
    public async Task RealStdioHandshakeToolsValidationAndErrors()
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "solid-edge",
            Command = Path.Combine(AppContext.BaseDirectory, "SolidEdgeMcp.exe"),
            Arguments = ["--output-dir", _temp],
        });
        await using var client = await McpClient.CreateAsync(transport);
        Assert.Equal("solid-edge-mcp", client.ServerInfo.Name);

        var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name);
        Assert.Equal(23, tools.Count);
        Assert.True(tools["se_geometry"].ProtocolTool.Annotations!.ReadOnlyHint);
        Assert.True(tools["se_close_document"].ProtocolTool.Annotations!.DestructiveHint);
        var width = tools["se_create_box"].JsonSchema.GetProperty("properties").GetProperty("width_mm");
        Assert.Equal(0, width.GetProperty("exclusiveMinimum").GetDouble());

        foreach (var length in new[] { -1, 0 })
        {
            var invalid = await client.CallToolAsync("se_create_box", new Dictionary<string, object?>
            {
                ["document_id"] = "unused",
                ["width_mm"] = length,
                ["depth_mm"] = 10,
                ["height_mm"] = 5,
            });
            Assert.True(invalid.IsError);
        }
        var wrongChoice = await client.CallToolAsync("se_extrude", new Dictionary<string, object?>
        {
            ["document_id"] = "unused",
            ["profile_id"] = "unused",
            ["distance_mm"] = 10,
            ["operation"] = "intersect",
        });
        Assert.True(wrongChoice.IsError);
        var missing = await client.CallToolAsync("se_get_document", new Dictionary<string, object?>());
        Assert.True(missing.IsError);
        var disconnected = await client.CallToolAsync("se_get_document", new Dictionary<string, object?>
        {
            ["document_id"] = "unknown",
        });
        Assert.True(disconnected.IsError);
        Assert.Contains("se_connect", ((TextContentBlock)disconnected.Content[0]).Text);

        var status = await client.CallToolAsync("se_status");
        Assert.NotEqual(true, status.IsError);
        Assert.False(status.StructuredContent!.Value.GetProperty("connected").GetBoolean());
        Assert.Equal(Path.GetFullPath(_temp), status.StructuredContent!.Value.GetProperty("output_root").GetString());

        var resource = await client.ReadResourceAsync("solidedge://guide");
        Assert.Contains("millimeters", ((TextResourceContents)resource.Contents[0]).Text);
    }
}
