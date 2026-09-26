using Neo.Companion.Cli;
using Xunit;

namespace Neo.Companion.Tests;

public sealed class FeatureGeneratorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "neo-generator-" + Guid.NewGuid().ToString("N"));
    public FeatureGeneratorTests() => Directory.CreateDirectory(root);
    [Theory]
    [InlineData("../escape", "Demo.App", "items")]
    [InlineData("class", "Demo.App", "items")]
    [InlineData("Book", "Demo;Bad", "items")]
    [InlineData("Book", "Demo.App", "{action}")]
    [InlineData("Guid", "Demo.App", "items")]
    public void Rejects_code_route_and_identifier_injection_before_writing(string name, string ns, string route)
    {
        Assert.Throws<ArgumentException>(() => FeatureGenerator.Generate(name, ns, route, Path.Combine(root, "output"), root));
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }
    [Fact]
    public void Existing_output_is_preserved()
    {
        var sentinel = Path.Combine(root, "important.txt"); File.WriteAllText(sentinel, "keep");
        Assert.Throws<IOException>(() => FeatureGenerator.Generate("Book", "Example.Inventory", "books", root, root));
        Assert.Equal("keep", File.ReadAllText(sentinel));
    }
    [Fact]
    public void Missing_source_checkout_does_not_leave_partial_output()
    {
        Assert.Throws<ArgumentException>(() => FeatureGenerator.Generate("Book", "Example.Inventory", "books", Path.Combine(root, "output"), root));
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }
    [Fact]
    public void Linked_output_parent_is_rejected()
    {
        var target = Path.Combine(root, "target"); Directory.CreateDirectory(target);
        var link = Path.Combine(root, "linked");
        try { Directory.CreateSymbolicLink(link, target); }
        catch (UnauthorizedAccessException) { Assert.Skip("Symbolic-link creation is unavailable on this Windows host."); return; }
        catch (IOException ex) when (OperatingSystem.IsWindows() && (ex.HResult & 0xffff) == 1314)
        { Assert.Skip("This Windows account lacks the symbolic-link creation privilege; Linux CI exercises the link guard."); return; }
        try { Assert.Throws<IOException>(() => FeatureGenerator.Generate("Book", "Example.Inventory", "books", Path.Combine(link, "output"), root)); }
        finally { Directory.Delete(link); }
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }
    public void Dispose() => Directory.Delete(root, recursive: true);
}
