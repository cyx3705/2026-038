namespace SolidEdgeMcp.Commands;

public sealed class ListDocuments() : SeCommand("list_documents", CommandKind.Read,
    "List open documents and their stable IDs for this server connection.")
{
    public override CommandOutput Execute(CommandContext c)
    {
        var edge = c.Edge;
        var app = edge.RequireApp();
        var result = new JsonArray();
        var alive = new HashSet<string>();
        int count = Int(app.Documents.Count);
        for (var i = 1; i <= count; i++)
        {
            object doc = app.Documents.Item(i);
            var key = edge.Register(doc);
            alive.Add(key);
            result.Add(SolidEdgeBackend.Info(doc, key));
        }
        foreach (var key in edge.Documents.Keys.Except(alive).ToList())
            edge.Forget(key);
        return new JsonObject { ["documents"] = result };
    }
}

public sealed class GetDocument() : SeCommand("get_document", CommandKind.Read,
    "Read document name, path, type, and unsaved-change status.",
    Text("document_id"))
{
    public override CommandOutput Execute(CommandContext c)
    {
        var documentId = c.Args.Text("document_id");
        return (JsonObject)SolidEdgeBackend.Info(c.Edge.Doc(documentId), documentId);
    }
}

public sealed class NewPart() : SeCommand("new_part", CommandKind.Create,
    "Create a new empty ordered part using Solid Edge's default part template.")
{
    public override CommandOutput Execute(CommandContext c)
    {
        object doc = c.Edge.RequireApp().Documents.Add("SolidEdge.PartDocument");
        var documentId = c.Edge.Register(doc);
        try
        {
            ((dynamic)doc).ModelingMode = 2; // seModelingModeOrdered; needed for editable extrusion features.
        }
        catch (Exception exc)
        {
            throw new EdgeError($"Part {documentId} was created, but ordered mode failed: {exc.Message}");
        }
        return (JsonObject)SolidEdgeBackend.Info(doc, documentId);
    }
}

public sealed class OpenDocument() : SeCommand("open_document", CommandKind.Create,
    "Open an existing .par file. Supply its absolute Windows path.",
    Text("path"))
{
    public override CommandOutput Execute(CommandContext c)
    {
        var path = c.Args.Text("path");
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)
            || !Path.GetExtension(path).Equals(".par", StringComparison.OrdinalIgnoreCase))
            throw new EdgeError("Supply an existing absolute path to a .par file.");
        object doc = c.Edge.RequireApp().Documents.Open(Path.GetFullPath(path));
        return (JsonObject)SolidEdgeBackend.Info(doc, c.Edge.Register(doc));
    }
}

public sealed class CloseDocument() : SeCommand("close_document", CommandKind.Write,
    "Close this document. Refuse unsaved changes unless discard_changes is explicitly true.",
    Text("document_id"), Flag("discard_changes", false))
{
    public override CommandOutput Execute(CommandContext c)
    {
        var documentId = c.Args.Text("document_id");
        var doc = c.Edge.Doc(documentId);
        if (Convert.ToBoolean(doc.Dirty) && !c.Args.Flag("discard_changes"))
            throw new EdgeError("Document has unsaved changes. Save it or set discard_changes=true.");
        c.Edge.WithoutAlerts(() => doc.Close(false));
        c.Edge.Forget(documentId);
        return new JsonObject { ["document_id"] = documentId, ["closed"] = true };
    }
}
