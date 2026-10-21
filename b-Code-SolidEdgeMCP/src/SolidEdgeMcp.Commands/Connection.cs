namespace SolidEdgeMcp.Commands;

public sealed class Status() : SeCommand("status", CommandKind.Read,
    "Get connection state, Solid Edge version, and output directory; never launch CAD.")
{
    public override CommandOutput Execute(CommandContext c)
    {
        var app = c.Edge.App;
        var result = new JsonObject { ["connected"] = app is not null, ["output_root"] = c.Edge.Paths.Root };
        if (app is not null)
        {
            result["version"] = Str(app.Version);
            result["document_count"] = Int(app.Documents.Count);
        }
        return result;
    }
}

public sealed class Connect() : SeCommand("connect", CommandKind.Create,
    "Attach to Solid Edge; optionally launch it. Keep CAD visible for interactive work.",
    Flag("start_if_needed", true), Flag("visible", true))
{
    public override CommandOutput Execute(CommandContext c)
    {
        var edge = c.Edge;
        if (edge.App is not null)
        {
            try
            {
                _ = edge.App.Version;
            }
            catch (COMException)
            {
                edge.Disconnect();
            }
        }
        var started = false;
        if (edge.App is null)
        {
            try
            {
                edge.App = GetActiveObject(SolidEdgeBackend.ProgId);
            }
            // Only MK_E_UNAVAILABLE means no running instance. Do not hide access errors.
            catch (COMException exc) when (exc.HResult == MkEUnavailable)
            {
                if (!c.Args.Flag("start_if_needed"))
                    throw new EdgeError("Solid Edge is not running. Use start_if_needed=true.");
                edge.App = Activator.CreateInstance(Type.GetTypeFromProgID(SolidEdgeBackend.ProgId, throwOnError: true)!);
                started = true;
            }
        }
        edge.App!.Visible = c.Args.Flag("visible");
        var result = c.Run("status");
        result["started"] = started;
        return result;
    }
}

public sealed class Disconnect() : SeCommand("disconnect", CommandKind.Create,
    "Release COM references and invalidate IDs. Leave Solid Edge and documents open.")
{
    public override CommandOutput Execute(CommandContext c) => c.Edge.Disconnect();
}
