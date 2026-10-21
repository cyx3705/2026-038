using System.Text.Json.Nodes;
using SolidEdgeMcp.Commands;

namespace SolidEdgeMcp.Tests;

/// <summary>Test-only commands. They live in this assembly, so loading it acts as a second build.</summary>
public sealed class EchoCommand() : SeCommand("echo", CommandKind.Read, "Return the text argument.", ToolParameter.Text("text"))
{
    public override CommandOutput Execute(CommandCall call) => new JsonObject { ["echo"] = call.Args.Text("text") };
}

public sealed class RelayCommand() : SeCommand("relay", CommandKind.Read, "Call echo as a nested command.")
{
    public override CommandOutput Execute(CommandCall call) => call.Run("echo", new JsonObject { ["text"] = "nested" });
}

public sealed class DormantCommand() : SeCommand("dormant", CommandKind.Read, "Switched off until enabled.")
{
    public override bool EnabledByDefault => false;

    public override CommandOutput Execute(CommandCall call) => new JsonObject();
}

/// <summary>Hot swap, switches and argument rules of the command registry; no Solid Edge involved.</summary>
public sealed class CommandRegistryTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("se-mcp-registry-").FullName;
    private readonly SolidEdgeBackend _backend = new(Path.GetTempPath());

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    public static string ComponentRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SolidEdgeMcp.sln")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("SolidEdgeMcp.sln not found above the test output.");
    }

    public static string BuiltCommands() => Path.Combine(ComponentRoot(), "obj", "commands", "SolidEdgeMcp.Commands.dll");

    private string Dll => Path.Combine(_temp, "commands.dll");

    private string Switches => Path.Combine(_temp, "switches.json");

    private void Install(string source) => File.Copy(source, Dll, overwrite: true);

    private static string TestAssembly => typeof(EchoCommand).Assembly.Location;

    private JsonObject Call(CommandRegistry registry, string command, JsonObject? arguments = null)
    {
        return (JsonObject)registry.Execute(_backend, registry.Get(command), arguments ?? []).Data;
    }

    [Fact]
    public void BuiltAssemblyProvidesAllCommandsEnabled()
    {
        Install(BuiltCommands());
        var registry = new CommandRegistry(Dll, null);
        var loaded = registry.Load();
        Assert.Equal(30, (int)loaded["command_count"]!);
        var commands = registry.Describe()["commands"]!.AsArray();
        Assert.All(commands, c => Assert.True((bool)c!["enabled"]!));
        var box = registry.Describe("create_box");
        Assert.Equal("create", (string)box["kind"]!);
        Assert.Equal(0, (double)box["input_schema"]!["properties"]!["width_mm"]!["exclusiveMinimum"]!);
    }

    [Fact]
    public void RebuildIsSwappedInPlaceWithoutLockingTheFile()
    {
        Install(BuiltCommands());
        var registry = new CommandRegistry(Dll, null);
        registry.Load();
        Assert.True((bool)registry.Load()["unchanged"]!);

        // Overwriting the loaded file must succeed: the registry never holds it open.
        Install(TestAssembly);
        var swapped = registry.Load();
        Assert.Contains("echo", swapped["added"]!.AsArray().Select(n => (string)n!));
        Assert.Contains("create_box", swapped["removed"]!.AsArray().Select(n => (string)n!));
        Assert.Equal("hi", (string)Call(registry, "echo", new JsonObject { ["text"] = "hi" })["echo"]!);
        Assert.Throws<EdgeError>(() => registry.Get("create_box"));
    }

    [Fact]
    public void BrokenBuildKeepsThePreviousCommands()
    {
        Install(TestAssembly);
        var registry = new CommandRegistry(Dll, null);
        registry.Load();
        File.WriteAllText(Dll, "not an assembly");
        var error = Assert.Throws<EdgeError>(() => registry.Load());
        Assert.Contains("remain active", error.Message);
        Assert.Equal("still", (string)Call(registry, "echo", new JsonObject { ["text"] = "still" })["echo"]!);
        Assert.NotNull(registry.Describe()["last_load_error"]);
    }

    [Fact]
    public void ReplacedAssemblyIsCollected()
    {
        Install(TestAssembly);
        var registry = new CommandRegistry(Dll, null);
        registry.Load();
        Install(BuiltCommands());
        registry.Load();
        var released = registry.LastUnloaded!;
        for (var i = 0; i < 20 && released.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(released.IsAlive);
    }

    [Fact]
    public void SwitchesPersistAndApplyToNestedCalls()
    {
        Install(TestAssembly);
        var registry = new CommandRegistry(Dll, Switches);
        registry.Load();
        Assert.Equal("nested", (string)Call(registry, "relay")["echo"]!);

        registry.Unload("echo");
        var off = Assert.Throws<EdgeError>(() => Call(registry, "relay"));
        Assert.Contains("switched off", off.Message);
        Assert.False((bool)JsonNode.Parse(File.ReadAllText(Switches))!["commands"]!["echo"]!);

        // A restarted server reads the stored switch, and a default-off command stays off.
        var restarted = new CommandRegistry(Dll, Switches);
        restarted.Load();
        Assert.Throws<EdgeError>(() => restarted.Get("echo"));
        Assert.Throws<EdgeError>(() => restarted.Get("dormant"));
        restarted.Load("echo");
        restarted.Load("dormant");
        Assert.Equal("nested", (string)Call(restarted, "relay")["echo"]!);
        Assert.Empty(Call(restarted, "dormant"));
    }

    [Fact]
    public void UnknownArgumentsAndCommandsAreRejected()
    {
        Install(TestAssembly);
        var registry = new CommandRegistry(Dll, null);
        Assert.Throws<EdgeError>(() => registry.Get("echo"));
        registry.Load();
        var unknown = Assert.Throws<EdgeError>(() => Call(registry, "echo", new JsonObject { ["text"] = "x", ["txet"] = "y" }));
        Assert.Contains("txet", unknown.Message);
        Assert.Throws<EdgeError>(() => registry.Load("missing"));
        registry.Unload();
        Assert.Throws<EdgeError>(() => registry.Get("echo"));
    }
}
