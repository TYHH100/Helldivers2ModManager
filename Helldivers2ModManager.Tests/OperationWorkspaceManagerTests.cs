using Helldivers2ModManager.Infrastructure.TemporaryFiles;
using Xunit;

namespace Helldivers2ModManager.Tests;

public sealed class OperationWorkspaceManagerTests
{
    [Fact]
    public void DisposingWorkspaceDeletesOnlyItsOwnedOperationDirectory()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var unrelated = System.IO.Path.Combine(temporaryDirectory.Path, "unrelated");
        Directory.CreateDirectory(unrelated);
        var manager = new OperationWorkspaceManager();
        var workspace = manager.Create(temporaryDirectory.Path, "test");
        var workspaceRoot = Directory.GetParent(workspace.DirectoryPath)!.FullName;
        Assert.True(Directory.Exists(workspace.DirectoryPath));

        workspace.Dispose();

        Assert.False(Directory.Exists(workspaceRoot));
        Assert.True(Directory.Exists(unrelated));
    }

    [Fact]
    public void CleanupDoesNotDeleteActiveOrUnownedDirectories()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var manager = new OperationWorkspaceManager();
        using var workspace = manager.Create(temporaryDirectory.Path, "active-test");
        var unowned = System.IO.Path.Combine(
            temporaryDirectory.Path,
            "hd2mm_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unowned);

        var deleted = manager.CleanupAbandoned(temporaryDirectory.Path);

        Assert.Equal(0, deleted);
        Assert.True(Directory.Exists(workspace.DirectoryPath));
        Assert.True(Directory.Exists(unowned));
    }
}
