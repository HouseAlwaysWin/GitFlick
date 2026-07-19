using System.IO;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.Services;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>
/// "File history" from the file lists is just the History file filter driven programmatically —
/// one mechanism, not a parallel scope. These pin that wiring.
/// </summary>
public class FileHistoryTests
{
    [Fact]
    public async Task Show_file_history_drives_the_file_filter()
    {
        var git = new FakeGitService();
        var vm = new WorkspaceViewModel(git, new RepositoryItem("r", Path.GetTempPath()));

        await vm.ShowFileHistory("src/app.cs");
        await vm.HistoryLoad;

        Assert.True(vm.IsHistoryMode);          // jumps to History even if invoked from Changes
        Assert.True(vm.IsFileSearch);           // the search dropdown reflects the File scope
        Assert.Equal("src/app.cs", vm.FileFilter);
        Assert.True(vm.HasFileFilter);
        Assert.False(vm.ShowGraph);             // a path-filtered log isn't parent-closed
        Assert.Equal("src/app.cs", git.LastPathFilter);   // reached git as a path-filtered log
    }

    [Fact]
    public async Task Show_file_history_shows_only_that_files_commits()
    {
        using var repo = new TestRepo();
        repo.WriteFile("app.cs", "v1"); repo.Git("add", "-A"); repo.Git("commit", "-m", "create app");
        repo.WriteFile("other.cs", "x"); repo.Git("add", "-A"); repo.Git("commit", "-m", "other");
        repo.WriteFile("app.cs", "v2"); repo.Git("add", "-A"); repo.Git("commit", "-m", "edit app");

        var vm = new WorkspaceViewModel(new GitService(), new RepositoryItem("r", repo.Path));
        await vm.ShowFileHistory("app.cs");
        await vm.HistoryLoad;

        Assert.Equal(2, vm.Commits.Count);      // create + edit; "other" excluded
        Assert.All(vm.Commits, c => Assert.Contains("app", c.Subject));
        Assert.Equal("app.cs", vm.FileFilter);
    }
}
