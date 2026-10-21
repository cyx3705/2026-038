using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SolidEdgeMcp;

/// <summary>
/// Hot-swappable command set. The commands assembly is read into memory (the file is never
/// locked) and loaded into a collectible context, so a rebuild can replace it while the MCP
/// connection and CAD state stay up. Each command also has an on/off switch persisted to disk.
/// </summary>
public sealed partial class CommandRegistry
{
    public const int MaxDepth = 8;

    private sealed record Generation(
        string Hash, DateTime LoadedAt, string Version, AssemblyLoadContext Context,
        IReadOnlyDictionary<string, ISeCommand> Commands);

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, bool> _switches;
    private Generation? _current;
    private string? _lastError;

    [ThreadStatic]
    private static int t_depth;

    public CommandRegistry(string assemblyPath, string? switchesPath)
    {
        AssemblyPath = Path.GetFullPath(assemblyPath);
        SwitchesPath = switchesPath is null ? null : Path.GetFullPath(switchesPath);
        _switches = ReadSwitches();
    }

    public string AssemblyPath { get; }

    /// <summary>Where toggled switches persist; null keeps them in memory only.</summary>
    public string? SwitchesPath { get; }

    /// <summary>Weak handle to the most recently released context; tests use it to prove collection.</summary>
    internal WeakReference? LastUnloaded { get; private set; }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$")]
    private static partial Regex NamePattern();

    /// <summary>
    /// Without a command: (re)load the commands assembly from disk. A build that fails to load
    /// never replaces the set already running. With a command: switch that command on.
    /// </summary>
    public JsonObject Load(string? command = null)
    {
        if (command is not null)
            return Switch(command, true);
        lock (_gate)
        {
            var previous = _current;
            Generation next;
            try
            {
                next = Read();
            }
            catch (Exception exc)
            {
                _lastError = Describe(exc);
                throw new EdgeError($"Commands assembly did not load: {_lastError}"
                    + (previous is null ? "" : " The previously loaded commands remain active."));
            }
            _lastError = null;
            if (previous?.Hash == next.Hash)
            {
                next.Context.Unload();
                return new JsonObject { ["assembly"] = AssemblyPath, ["hash"] = next.Hash, ["unchanged"] = true };
            }
            _current = next;
            if (previous is not null)
                Release(previous);
            var before = previous?.Commands.Keys.ToHashSet() ?? [];
            return new JsonObject
            {
                ["assembly"] = AssemblyPath,
                ["hash"] = next.Hash,
                ["version"] = next.Version,
                ["command_count"] = next.Commands.Count,
                ["added"] = Names(next.Commands.Keys.Where(n => !before.Contains(n))),
                ["removed"] = Names(before.Where(n => !next.Commands.ContainsKey(n))),
            };
        }
    }

    /// <summary>Without a command: drop the whole assembly. With a command: switch it off.</summary>
    public JsonObject Unload(string? command = null)
    {
        if (command is not null)
            return Switch(command, false);
        lock (_gate)
        {
            if (_current is null)
                throw new EdgeError("No commands assembly is loaded.");
            Release(_current);
            _current = null;
            return new JsonObject { ["assembly"] = AssemblyPath, ["unloaded"] = true };
        }
    }

    private JsonObject Switch(string command, bool enabled)
    {
        lock (_gate)
        {
            var entry = Find(command, requireEnabled: false);
            _switches[command] = enabled;
            WriteSwitches();
            return new JsonObject
            {
                ["command"] = command,
                ["enabled"] = enabled,
                ["default"] = entry.EnabledByDefault,
                ["persisted_to"] = SwitchesPath,
            };
        }
    }

    private bool Enabled(ISeCommand command) =>
        _switches.TryGetValue(command.Name, out var stored) ? stored : command.EnabledByDefault;

    private Generation Read()
    {
        if (!File.Exists(AssemblyPath))
            throw new EdgeError($"{AssemblyPath} not found. Build src/SolidEdgeMcp.Commands first.");
        var image = File.ReadAllBytes(AssemblyPath);
        var symbolsPath = Path.ChangeExtension(AssemblyPath, ".pdb");
        var symbols = File.Exists(symbolsPath) ? File.ReadAllBytes(symbolsPath) : null;
        var hash = Convert.ToHexString(SHA256.HashData(image))[..12].ToLowerInvariant();
        var context = new AssemblyLoadContext($"se-commands:{hash}", isCollectible: true);
        try
        {
            using var imageStream = new MemoryStream(image);
            using var symbolStream = symbols is null ? null : new MemoryStream(symbols);
            var assembly = context.LoadFromStream(imageStream, symbolStream);
            var commands = new Dictionary<string, ISeCommand>(StringComparer.Ordinal);
            foreach (var type in assembly.GetTypes())
            {
                if (!type.IsPublic || type.IsAbstract || !typeof(ISeCommand).IsAssignableFrom(type)
                    || type.GetConstructor(Type.EmptyTypes) is null)
                    continue;
                var command = (ISeCommand)Activator.CreateInstance(type)!;
                if (!NamePattern().IsMatch(command.Name))
                    throw new EdgeError($"{type.FullName}: invalid command name '{command.Name}' (lowercase, digits, _).");
                if (!commands.TryAdd(command.Name, command))
                    throw new EdgeError($"Duplicate command name {command.Name}.");
                var duplicate = command.Parameters.GroupBy(p => p.Name).FirstOrDefault(g => g.Count() > 1);
                if (duplicate is not null)
                    throw new EdgeError($"{command.Name}: duplicate parameter {duplicate.Key}.");
            }
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString() ?? "";
            return new Generation(hash, DateTime.Now, version, context, commands);
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    private void Release(Generation generation)
    {
        // A call still running keeps the old assembly alive until it returns; the context is
        // collected afterwards.
        generation.Context.Unload();
        LastUnloaded = new WeakReference(generation.Context);
    }

    private static string Describe(Exception exc) => exc switch
    {
        ReflectionTypeLoadException load => string.Join("; ", load.LoaderExceptions.Select(e => e?.Message).Distinct()),
        TargetInvocationException { InnerException: { } inner } => inner.Message,
        _ => exc.Message,
    };

    private static JsonArray Names(IEnumerable<string> names) =>
        new(names.Order(StringComparer.Ordinal).Select(n => (JsonNode)n).ToArray());

    private ISeCommand Find(string name, bool requireEnabled)
    {
        if (_current is null)
            throw new EdgeError("No commands assembly is loaded. Call se_load without arguments.");
        if (!_current.Commands.TryGetValue(name, out var command))
            throw new EdgeError($"Unknown command {name}. Call se_find to list commands.");
        if (requireEnabled && !Enabled(command))
            throw new EdgeError($"Command {name} is switched off. Call se_load with command={name} to switch it on.");
        return command;
    }

    /// <summary>An enabled command of the current generation.</summary>
    public ISeCommand Get(string name)
    {
        lock (_gate)
        {
            return Find(name, requireEnabled: true);
        }
    }

    public JsonObject Describe(string? name = null)
    {
        lock (_gate)
        {
            if (name is not null)
            {
                var command = Find(name, requireEnabled: false);
                var detail = Summary(command);
                detail["input_schema"] = JsonNode.Parse(ToolParameter.ObjectSchema(command.Parameters).GetRawText());
                return detail;
            }
            var result = new JsonObject
            {
                ["assembly"] = AssemblyPath,
                ["loaded"] = _current is not null,
                ["switches"] = SwitchesPath,
            };
            if (_current is not null)
            {
                result["hash"] = _current.Hash;
                result["version"] = _current.Version;
                result["loaded_at"] = _current.LoadedAt.ToString("s");
                result["commands"] = new JsonArray(_current.Commands.Values
                    .OrderBy(c => c.Name, StringComparer.Ordinal)
                    .Select(c => (JsonNode)Summary(c))
                    .ToArray());
            }
            if (_lastError is not null)
                result["last_load_error"] = _lastError;
            return result;
        }
    }

    private JsonObject Summary(ISeCommand command) => new()
    {
        ["command"] = command.Name,
        ["enabled"] = Enabled(command),
        ["kind"] = command.Kind.ToString().ToLowerInvariant(),
        ["description"] = command.Description,
    };

    /// <summary>Validate call arguments against the command's parameters; unknown names are rejected.</summary>
    public static ToolArguments Arguments(ISeCommand command, IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        var unknown = arguments?.Keys.Except(command.Parameters.Select(p => p.Name)).FirstOrDefault();
        if (unknown is not null)
            throw new EdgeError($"Unknown argument {unknown} for {command.Name}. Call se_find with command={command.Name}.");
        return new ToolArguments(command.Parameters.ToDictionary(p => p.Name, p => p.Read(arguments)));
    }

    /// <summary>Run a command on the current (STA) thread. Nested calls from commands come back here.</summary>
    public CommandOutput Execute(SolidEdgeBackend edge, ISeCommand command, ToolArguments arguments)
    {
        if (t_depth >= MaxDepth)
            throw new EdgeError($"Commands nested deeper than {MaxDepth} levels.");
        t_depth++;
        try
        {
            return command.Execute(new CommandContext(edge, arguments, (name, nested) =>
            {
                var target = Get(name);
                return Execute(edge, target, Arguments(target, CommandContext.Elements(nested)));
            }));
        }
        finally
        {
            t_depth--;
        }
    }

    private Dictionary<string, bool> ReadSwitches()
    {
        if (SwitchesPath is null || !File.Exists(SwitchesPath))
            return new(StringComparer.Ordinal);
        try
        {
            var stored = JsonNode.Parse(File.ReadAllText(SwitchesPath))?["commands"]?.AsObject();
            return stored?.ToDictionary(p => p.Key, p => (bool)p.Value!, StringComparer.Ordinal)
                ?? new(StringComparer.Ordinal);
        }
        catch (Exception exc) when (exc is JsonException or InvalidOperationException or FormatException)
        {
            throw new EdgeError($"Switch file {SwitchesPath} is not valid: {exc.Message}");
        }
    }

    private void WriteSwitches()
    {
        if (SwitchesPath is null)
            return;
        var document = new JsonObject
        {
            ["commands"] = new JsonObject(_switches.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(SwitchesPath)!);
        var staged = SwitchesPath + ".tmp";
        File.WriteAllText(staged, document.ToJsonString(Pretty) + "\n");
        File.Move(staged, SwitchesPath, overwrite: true);
    }
}
