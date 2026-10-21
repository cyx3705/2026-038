using System.Dynamic;
using System.Text.Json.Nodes;
using SolidEdgeMcp.Commands;

namespace SolidEdgeMcp.Tests;

/// <summary>Document bookkeeping against late-bound fakes; no Solid Edge process is involved.</summary>
public sealed class DocumentTests
{
    private readonly SolidEdgeBackend _backend = new(Path.GetTempPath());

    private static dynamic Collection(params object[] items)
    {
        dynamic collection = new ExpandoObject();
        collection.Count = items.Length;
        collection.Item = (Func<int, object>)(index => items[index - 1]);
        return collection;
    }

    private static dynamic App(params object[] documents)
    {
        dynamic app = new ExpandoObject();
        app.Documents = Collection(documents);
        return app;
    }

    private CommandOutput Run(ISeCommand command, JsonObject arguments) =>
        command.Execute(new CommandContext(_backend, arguments,
            (name, _) => throw new InvalidOperationException($"unexpected nested call to {name}")));

    [Fact]
    public void ClosedDocumentIdNeverTargetsAnotherActiveDocument()
    {
        var original = new ExpandoObject();
        var unrelated = new ExpandoObject();
        _backend.App = App(unrelated);
        _backend.Documents["original"] = original;
        _backend.Profiles["sketch"] = ("original", new object());
        var error = Assert.Throws<EdgeError>(() => _backend.Doc("original"));
        Assert.Contains("closed", error.Message);
        Assert.DoesNotContain("original", _backend.Documents.Keys);
        Assert.DoesNotContain("sketch", _backend.Profiles.Keys);
    }

    [Fact]
    public void UnsavedDocumentIsNotClosedImplicitly()
    {
        var closed = false;
        dynamic doc = new ExpandoObject();
        doc.Dirty = true;
        doc.Close = (Action<bool>)(_ => closed = true);
        _backend.App = App(doc);
        _backend.Documents["part"] = doc;
        var error = Assert.Throws<EdgeError>(() => Run(new CloseDocument(), new JsonObject { ["document_id"] = "part" }));
        Assert.Contains("unsaved changes", error.Message);
        Assert.False(closed);
    }

    [Fact]
    public void AlertsAreRestoredAfterFailure()
    {
        dynamic app = new ExpandoObject();
        app.DisplayAlerts = true;
        _backend.App = app;
        Assert.Throws<InvalidOperationException>(() => _backend.WithoutAlerts(() =>
        {
            Assert.False((bool)app.DisplayAlerts);
            throw new InvalidOperationException("translation failed");
        }));
        Assert.True((bool)app.DisplayAlerts);
    }

    [Fact]
    public void ProfileFromAnotherDocumentIsRejectedBeforeComMutation()
    {
        dynamic doc = new ExpandoObject();
        doc.Type = 1;
        _backend.App = App(doc);
        _backend.Documents["part"] = doc;
        _backend.Profiles["sketch"] = ("different-part", new object());
        var error = Assert.Throws<EdgeError>(() => Run(new Extrude(), new JsonObject
        {
            ["document_id"] = "part",
            ["profile_id"] = "sketch",
            ["distance_mm"] = 10,
        }));
        Assert.Contains("different document", error.Message);
    }
}
