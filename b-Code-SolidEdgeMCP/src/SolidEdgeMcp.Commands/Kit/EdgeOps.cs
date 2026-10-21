namespace SolidEdgeMcp.Commands;

/// <summary>
/// Operations on the shell's shared state. They live here, not in the shell, so they can change
/// with a command reload. Callers passing dynamic arguments must use the static form
/// (EdgeOps.X(edge, ...)): the runtime binder does not see extension methods.
/// </summary>
public static class EdgeOps
{
    public static dynamic RequireApp(this SolidEdgeBackend edge) =>
        edge.App ?? throw new EdgeError("Not connected. Call the connect command first.");

    public static string Register(this SolidEdgeBackend edge, object doc)
    {
        foreach (var (key, stored) in edge.Documents)
        {
            if (Same(stored, doc))
                return key;
        }
        var id = SolidEdgeBackend.NewId();
        edge.Documents[id] = doc;
        return id;
    }

    public static void Forget(this SolidEdgeBackend edge, string documentId)
    {
        edge.Documents.Remove(documentId);
        foreach (var key in edge.Profiles.Where(p => p.Value.DocumentId == documentId).Select(p => p.Key).ToList())
            edge.Profiles.Remove(key);
    }

    public static dynamic Doc(this SolidEdgeBackend edge, string documentId, bool part = false)
    {
        var app = edge.RequireApp();
        if (!edge.Documents.TryGetValue(documentId, out var doc))
            throw new EdgeError("Unknown document_id. Call list_documents to obtain current IDs.");
        // Never silently redirect a stale ID to whichever document happens to be active.
        var alive = false;
        int count = Int(app.Documents.Count);
        for (var i = 1; i <= count && !alive; i++)
            alive = Same((object)app.Documents.Item(i), doc);
        if (!alive)
        {
            edge.Forget(documentId);
            throw new EdgeError("Document was closed. Its document_id and profile IDs are invalid.");
        }
        dynamic d = doc;
        if (part && Int(d.Type) != 1)
            throw new EdgeError("This modeling operation requires a .par part document.");
        return d;
    }

    public static JsonObject Info(dynamic doc, string documentId) => new()
    {
        ["document_id"] = documentId,
        ["name"] = Str(doc.Name),
        ["full_name"] = Str(doc.FullName),
        ["type"] = Int(doc.Type),
        ["dirty"] = Convert.ToBoolean(doc.Dirty),
    };

    public static dynamic NewProfile(this SolidEdgeBackend edge, string documentId, int planeIndex)
    {
        var doc = edge.Doc(documentId, part: true);
        if (Int(doc.ModelingMode) != 2)
            throw new EdgeError("Ordered modeling mode is required. Use new_part for a new ordered part.");
        int count = Int(doc.RefPlanes.Count);
        if (planeIndex < 1 || planeIndex > count)
            throw new EdgeError($"plane_index must be between 1 and {count}.");
        doc.Activate();
        return doc.ProfileSets.Add().Profiles.Add(doc.RefPlanes.Item(planeIndex));
    }

    public static JsonObject FinishProfile(SolidEdgeBackend edge, string documentId, object profile)
    {
        int code = Int(((dynamic)profile).End(1)); // igProfileClosed
        if (code != 0)
            throw new EdgeError($"Profile was created but is not closed (validation code {code}).");
        var key = SolidEdgeBackend.NewId();
        edge.Profiles[key] = (documentId, profile);
        return new JsonObject
        {
            ["profile_id"] = key,
            ["document_id"] = documentId,
            ["validation_code"] = code,
            ["validation_passed"] = code == 0,
            ["note"] = "Extrusion verifies that the constructed loop produces the expected solid.",
        };
    }

    public static void WithoutAlerts(this SolidEdgeBackend edge, Action action)
    {
        var app = edge.RequireApp();
        var previous = app.DisplayAlerts;
        app.DisplayAlerts = false;
        try
        {
            action();
        }
        finally
        {
            app.DisplayAlerts = previous;
        }
    }
}

/// <summary>Small pure helpers shared by command files (imported statically into every command).</summary>
public static class CommandKit
{
    public static bool IsClose(double a, double b) =>
        Math.Abs(a - b) <= Math.Max(1e-8 * Math.Max(Math.Abs(a), Math.Abs(b)), 1e-8);

    public static JsonArray Numbers(IEnumerable<double> values) => new(values.Select(v => (JsonNode)v).ToArray());

    /// <summary>Move every entry of <paramref name="second"/> into <paramref name="first"/>.</summary>
    public static JsonObject Merge(JsonObject first, JsonObject second)
    {
        foreach (var (key, value) in second.ToList())
        {
            second.Remove(key);
            first[key] = value;
        }
        return first;
    }

    /// <summary>A hidden sibling of <paramref name="target"/> to write into before the final move.</summary>
    public static string Staged(string target, string extension) =>
        Path.Combine(Path.GetDirectoryName(target)!,
            $".{Path.GetFileNameWithoutExtension(target)}-{SolidEdgeBackend.NewId()}{extension}");

    public static JsonObject VariableInfo(dynamic variables, dynamic variable)
    {
        string name = Str(variables.GetName(variable));
        double raw = Double(variable.Value);
        int kind = Int(variable.UnitsType);
        var unit = kind switch { 1 => "mm", 2 => "deg", _ => "raw" };
        var value = kind switch { 1 => raw * 1000, 2 => raw * 180 / Math.PI, _ => raw };
        return new JsonObject
        {
            ["name"] = name,
            ["value"] = value,
            ["unit"] = unit,
            ["units_type"] = kind,
            ["raw_value"] = raw,
            ["formula"] = Str(variable.Formula),
        };
    }
}
