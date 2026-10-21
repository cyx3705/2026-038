namespace SolidEdgeMcp.Commands;

public sealed class SaveDocument() : SeCommand("save_document", CommandKind.Write,
    "Save a native .par inside the output root. Existing files need overwrite=true.",
    Text("document_id"), OptionalText("path"), Flag("overwrite", false))
{
    public override CommandOutput Execute(CommandCall c)
    {
        var documentId = c.Args.Text("document_id");
        var path = c.Args.OptionalText("path");
        var overwrite = c.Args.Flag("overwrite");
        var doc = c.Edge.Doc(documentId, part: true);
        string current = Str(doc.FullName);
        string target;
        if (path is null)
        {
            if (!Path.IsPathFullyQualified(current) || !File.Exists(current))
                throw new EdgeError("Unsaved part: supply a .par output path.");
            // Saving in place is a write too; retain the same output boundary and overwrite rule.
            target = c.Edge.Paths.Target(current, [".par"], overwrite);
        }
        else
        {
            target = c.Edge.Paths.Target(path, [".par"], overwrite);
        }
        c.Edge.WithoutAlerts(() =>
        {
            if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
                doc.Save();
            else
                doc.SaveAs(target);
        });
        return Merge((JsonObject)EdgeOps.Info(doc, documentId), OutputPaths.Verify(target));
    }
}

public sealed class ExportStep() : SeCommand("export_step", CommandKind.Write,
    "Export .step/.stp, verify a nonempty file, and retain the native part document path.",
    Text("document_id"), Text("path"), Flag("overwrite", false))
{
    public override CommandOutput Execute(CommandCall c)
    {
        var documentId = c.Args.Text("document_id");
        var overwrite = c.Args.Flag("overwrite");
        var doc = c.Edge.Doc(documentId, part: true);
        if ((double)c.Run("geometry", new JsonObject { ["document_id"] = documentId })["total_volume_mm3"]! <= 0)
            throw new EdgeError("STEP export requires a nonempty solid.");
        var target = c.Edge.Paths.Target(c.Args.Text("path"), [".stp", ".step"], overwrite);
        var staged = Staged(target, Path.GetExtension(target));
        string originalName = Str(doc.FullName);
        try
        {
            c.Edge.WithoutAlerts(() => doc.SaveCopyAs(staged));
            OutputPaths.Verify(staged);
            if (Str(doc.FullName) != originalName)
                throw new EdgeError("Export unexpectedly changed the native document path. Inspect it.");
            // Without overwrite, Move refuses a file that appeared during translation.
            File.Move(staged, target, overwrite);
        }
        finally
        {
            File.Delete(staged);
        }
        return Merge(new JsonObject { ["document_id"] = documentId, ["format"] = "STEP" }, OutputPaths.Verify(target));
    }
}
