namespace SolidEdgeMcp.Tests;

public sealed class OutputPathsTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("se-mcp-paths-").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    [Theory]
    [InlineData("../escape.par")]
    [InlineData("nested/../../escape.par")]
    [InlineData("file.exe")]
    public void RejectInvalidOutput(string name)
    {
        var paths = new OutputPaths(Path.Combine(_temp, "output"));
        Assert.Throws<EdgeError>(() => paths.Target(name, [".par"]));
    }

    [Fact]
    public void RejectAbsolutePathOutsideRoot()
    {
        var paths = new OutputPaths(Path.Combine(_temp, "output"));
        var error = Assert.Throws<EdgeError>(() => paths.Target(Path.Combine(_temp, "outside.par"), [".par"]));
        Assert.Contains("inside", error.Message);
        // A sibling directory sharing the root's name prefix is still outside.
        Assert.Throws<EdgeError>(() => paths.Target(Path.Combine(_temp, "output-2", "x.par"), [".par"]));
    }

    [Fact]
    public void ExistingFileRequiresExplicitOverwrite()
    {
        var paths = new OutputPaths(_temp);
        var target = paths.Target("sub/part.PAR", [".par"]);
        File.WriteAllBytes(target, "original"u8.ToArray());
        var error = Assert.Throws<EdgeError>(() => paths.Target("sub/part.PAR", [".par"]));
        Assert.Contains("already exists", error.Message);
        Assert.Equal("original"u8.ToArray(), File.ReadAllBytes(target));
        Assert.Equal(target, paths.Target("sub/part.PAR", [".par"], overwrite: true));
    }

    [Fact]
    public void NonemptyFileVerification()
    {
        var target = Path.Combine(_temp, "empty.par");
        File.WriteAllBytes(target, []);
        Assert.Contains("nonempty", Assert.Throws<EdgeError>(() => OutputPaths.Verify(target)).Message);
        File.WriteAllBytes(target, "native"u8.ToArray());
        Assert.Equal(6, (long)OutputPaths.Verify(target)["size_bytes"]!);
    }
}
