using Helldivers2ModManager.Core.Security;
using Helldivers2ModManager.Infrastructure.Security;
using Xunit;

namespace Helldivers2ModManager.Tests;

public sealed class SafePathPolicyTests
{
    private readonly SafePathPolicy _policy = new();

    [Fact]
    public void ResolveUnderRootAcceptsNestedRelativePath()
    {
        using var temporaryDirectory = new TemporaryDirectory();

        var result = _policy.ResolveUnderRoot(temporaryDirectory.Path, @"option\data.patch_0");

        Assert.Equal(
            System.IO.Path.Combine(temporaryDirectory.Path, "option", "data.patch_0"),
            result,
            ignoreCase: true);
    }

    [Theory]
    [InlineData(@"..\escape.txt")]
    [InlineData(@"option\..\escape.txt")]
    [InlineData(@"C:\escape.txt")]
    [InlineData(@"\\server\share\escape.txt")]
    [InlineData("/escape.txt")]
    public void ResolveUnderRootRejectsUnsafePaths(string unsafePath)
    {
        using var temporaryDirectory = new TemporaryDirectory();

        Assert.Throws<SafePathViolationException>(() =>
            _policy.ResolveUnderRoot(temporaryDirectory.Path, unsafePath));
    }

    [Fact]
    public void IsUnderRootUsesDirectoryBoundaryAndIgnoresCase()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var sibling = temporaryDirectory.Path + "-sibling";

        Assert.True(_policy.IsUnderRoot(
            temporaryDirectory.Path.ToUpperInvariant(),
            System.IO.Path.Combine(temporaryDirectory.Path, "child")));
        Assert.False(_policy.IsUnderRoot(temporaryDirectory.Path, sibling));
    }

    [Fact]
    public void ResolveUnderRootRejectsExistingSymbolicLinkWhenSupported()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var outsideDirectory = new TemporaryDirectory();
        var link = System.IO.Path.Combine(temporaryDirectory.Path, "link");
        try
        {
            Directory.CreateSymbolicLink(link, outsideDirectory.Path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return;
        }

        Assert.Throws<SafePathViolationException>(() =>
            _policy.ResolveUnderRoot(temporaryDirectory.Path, @"link\escape.txt"));
    }

    [Fact]
    public void SharedCorePolicyMatchesInfrastructureBoundaryChecks()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var sharedPolicy = new SharedSafePathPolicy();

        Assert.True(sharedPolicy.IsUnderRoot(
            temporaryDirectory.Path,
            Path.Combine(temporaryDirectory.Path, "data")));
        Assert.False(sharedPolicy.IsUnderRoot(
            temporaryDirectory.Path,
            temporaryDirectory.Path + "-sibling"));
        Assert.Throws<SafePathViolationException>(() =>
            sharedPolicy.ResolveUnderRoot(temporaryDirectory.Path, "..\\escape.txt"));
    }
}
