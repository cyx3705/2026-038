using System.Text.Json.Nodes;

namespace SolidEdgeMcp;

/// <summary>Confine generated files to the configured project data directory.</summary>
public sealed class OutputPaths(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    public string Target(string value, IReadOnlyCollection<string> suffixes, bool overwrite = false)
    {
        var path = Path.GetFullPath(Path.IsPathFullyQualified(value) ? value : Path.Combine(Root, value));
        var prefix = Path.EndsInDirectorySeparator(Root) ? Root : Root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new EdgeError($"Output must be inside {Root}");
        if (!suffixes.Contains(Path.GetExtension(path).ToLowerInvariant()))
            throw new EdgeError($"Expected extension: {string.Join(", ", suffixes.Order())}");
        if ((File.Exists(path) && !overwrite) || Directory.Exists(path))
            throw new EdgeError($"Output already exists: {path}. Use overwrite=true explicitly.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    public static JsonObject Verify(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0)
            throw new EdgeError($"Solid Edge did not produce a nonempty file: {path}");
        return new JsonObject { ["path"] = file.FullName, ["size_bytes"] = file.Length };
    }
}
