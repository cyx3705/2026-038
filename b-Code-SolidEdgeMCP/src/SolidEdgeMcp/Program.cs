using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using ModelContextProtocol.Server;
using SolidEdgeMcp;

string? outputDir = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--doctor":
            return Doctor();
        case "--output-dir" when i + 1 < args.Length:
            outputDir = args[++i];
            break;
        case "-h" or "--help":
            Console.Error.WriteLine("Solid Edge MCP server (stdio)\n"
                + "  --output-dir <dir>  Root directory for generated CAD files\n"
                + "  --doctor            Check environment without starting CAD");
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 2;
    }
}

// Command line wins over SOLIDEDGE_MCP_OUTPUT_DIR; stdout carries only the MCP protocol.
var root = outputDir
    ?? Environment.GetEnvironmentVariable("SOLIDEDGE_MCP_OUTPUT_DIR")
    ?? Path.Combine(Environment.CurrentDirectory, "data");
using var com = new ComWorker<SolidEdgeBackend>(
    () => new SolidEdgeBackend(root), backend => backend.Disconnect(), TimeSpan.FromSeconds(120));
await using (var server = McpServer.Create(new StdioServerTransport(SolidEdgeServer.Name), SolidEdgeServer.CreateOptions(com)))
{
    await server.RunAsync();
}
return 0;

static int Doctor()
{
    var result = new JsonObject
    {
        ["runtime"] = RuntimeInformation.FrameworkDescription,
        ["platform"] = RuntimeInformation.OSDescription,
        ["mcp"] = typeof(McpServer).Assembly.GetName().Version?.ToString(3),
        ["supported_platform"] = OperatingSystem.IsWindows(),
        ["registered"] = false,
    };
    using (var key = Registry.ClassesRoot.OpenSubKey(@"SolidEdge.Application\CLSID"))
    {
        if (key?.GetValue(null) is string clsid)
        {
            result["clsid"] = clsid;
            result["registered"] = true;
        }
        else
        {
            result["error"] = @"HKEY_CLASSES_ROOT\SolidEdge.Application\CLSID not found";
        }
    }
    Console.WriteLine(result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    return (bool)result["registered"]! ? 0 : 1;
}
