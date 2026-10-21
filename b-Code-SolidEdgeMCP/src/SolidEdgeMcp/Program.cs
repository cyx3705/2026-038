using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using ModelContextProtocol.Server;
using SolidEdgeMcp;

string? outputDir = null;
string? commandsPath = null;
string? switchesPath = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--doctor":
            return Doctor(Layout.Commands(null));
        case "--output-dir" when i + 1 < args.Length:
            outputDir = args[++i];
            break;
        case "--commands" when i + 1 < args.Length:
            commandsPath = args[++i];
            break;
        case "--switches" when i + 1 < args.Length:
            switchesPath = args[++i];
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
var registry = new CommandRegistry(Layout.Commands(commandsPath), switchesPath ?? Layout.Switches());
try
{
    registry.Load();
}
catch (EdgeError exc)
{
    // Keep serving: se_find reports the error and se_load retries after a fixed build.
    Console.Error.WriteLine(exc.Message);
}
using var com = new ComWorker<SolidEdgeBackend>(
    () => new SolidEdgeBackend(root), backend => backend.Disconnect(), TimeSpan.FromSeconds(120));
await using (var server = McpServer.Create(new StdioServerTransport(SolidEdgeServer.Name), SolidEdgeServer.CreateOptions(com, registry)))
{
    await server.RunAsync();
}
return 0;

static int Doctor(string commands)
{
    var result = new JsonObject
    {
        ["commands_assembly"] = commands,
        ["commands_built"] = File.Exists(commands),
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
    return (bool)result["registered"]! && (bool)result["commands_built"]! ? 0 : 1;
}

/// <summary>Default locations, found by walking up from the exe to the component root.</summary>
static class Layout
{
    public const string CommandsRelative = @"obj\commands\SolidEdgeMcp.Commands.dll";

    private static string? Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SolidEdgeMcp.sln")))
                return dir.FullName;
        }
        return null;
    }

    public static string Commands(string? explicitPath) =>
        explicitPath
        ?? Environment.GetEnvironmentVariable("SOLIDEDGE_MCP_COMMANDS")
        ?? Path.Combine(Root() ?? AppContext.BaseDirectory, CommandsRelative);

    public static string? Switches() =>
        Environment.GetEnvironmentVariable("SOLIDEDGE_MCP_SWITCHES")
        ?? (Root() is { } root ? Path.Combine(root, "config", "commands.json") : null);
}
