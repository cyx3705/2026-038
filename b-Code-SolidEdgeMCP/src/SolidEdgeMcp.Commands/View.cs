using System.Drawing;
using System.Drawing.Imaging;

namespace SolidEdgeMcp.Commands;

public sealed class SetView() : SeCommand("set_view", CommandKind.Create,
    "Activate this part, apply a named view, and fit the geometry in the window.",
    Text("document_id"), Choice("orientation", "Iso", "Iso", "Front", "Top", "Right"))
{
    public override CommandOutput Execute(CommandCall c)
    {
        var documentId = c.Args.Text("document_id");
        var orientation = c.Args.Text("orientation");
        var doc = c.Edge.Doc(documentId, part: true);
        doc.Activate();
        var view = c.Edge.RequireApp().ActiveWindow.View;
        view.ApplyNamedView(orientation);
        view.Fit();
        return new JsonObject { ["document_id"] = documentId, ["orientation"] = orientation };
    }
}

public sealed class CaptureViewport() : SeCommand("capture_viewport", CommandKind.Write,
    "Save a PNG of this part's current view and return the image to the AI client.",
    Text("document_id"), new ToolParameter("path", "string", false, "viewport.png"), Integer("width", 1280, 64, 4096),
    Integer("height", 960, 64, 4096), Flag("overwrite", false))
{
    public override CommandOutput Execute(CommandCall c)
    {
        var documentId = c.Args.Text("document_id");
        var overwrite = c.Args.Flag("overwrite");
        var doc = c.Edge.Doc(documentId, part: true);
        var target = c.Edge.Paths.Target(c.Args.Text("path"), [".png"], overwrite);
        var staged = Staged(target, ".bmp");
        var converted = Path.ChangeExtension(staged, ".png");
        doc.Activate();
        try
        {
            // Solid Edge 2020 silently appends .tif to a requested .png. BMP is supported natively.
            c.Edge.RequireApp().ActiveWindow.View.SaveAsImage(staged, c.Args.Integer("width"), c.Args.Integer("height"));
            OutputPaths.Verify(staged);
            using (var image = new Bitmap(staged))
                image.Save(converted, ImageFormat.Png);
            File.Move(converted, target, overwrite);
        }
        finally
        {
            File.Delete(staged);
            File.Delete(converted);
        }
        var saved = Merge(new JsonObject { ["document_id"] = documentId }, OutputPaths.Verify(target));
        return new CommandOutput(saved, File.ReadAllBytes(target));
    }
}
