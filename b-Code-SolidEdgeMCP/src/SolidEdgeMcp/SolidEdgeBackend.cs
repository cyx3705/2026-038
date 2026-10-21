using System.Text.Json.Nodes;

namespace SolidEdgeMcp;

/// <summary>
/// State that must survive command reloads: the COM connection, document IDs, open profiles and
/// the output root. Behaviour on top of it lives in the commands assembly. Instances live entirely
/// on ComWorker's STA.
/// </summary>
public sealed class SolidEdgeBackend(string outputRoot)
{
    public const string ProgId = "SolidEdge.Application";

    public OutputPaths Paths { get; } = new(outputRoot);

    public dynamic? App { get; set; }

    public Dictionary<string, object> Documents { get; } = new();

    public Dictionary<string, (string DocumentId, object Profile)> Profiles { get; } = new();

    /// <summary>
    /// Room for state that new commands need without a shell release. Store only framework, shell
    /// or COM objects here: a type from the commands assembly would pin an unloaded version.
    /// </summary>
    public Dictionary<string, object> State { get; } = new();

    public static string NewId() => Guid.NewGuid().ToString("N");

    public JsonObject Disconnect()
    {
        Profiles.Clear();
        Documents.Clear();
        State.Clear();
        App = null;
        return new JsonObject { ["connected"] = false, ["application_closed"] = false };
    }
}
