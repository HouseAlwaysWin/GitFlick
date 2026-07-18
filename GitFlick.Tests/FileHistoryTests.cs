using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.Services;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>Per-file history (git log --follow), scoped from the file lists.</summary>
public class FileHistoryTests
{
    [Fact]
    public async Task Show_file_history_scopes_and_hides_the_graph()
    {
        var git = new FakeGitService();
        var vm = new WorkspaceViewModel(git, new RepositoryItem("r", Path.GetTempPath()));

        await vm.ShowFileHistory("src/app.cs");

        Assert.Equal("src/app.cs", git.LastFileHistoryPath);
        Assert.True(vm.IsFileHistory);
        Assert.True(vm.IsHistoryMode);
        Assert.False(vm.ShowGraph);

        await vm.ShowHistoryCommand.ExecuteAsync(null);   // the toolbar History button clears the scope
        Assert.False(vm.IsFileHistory);
    }

    [Fact]
    public async Task File_history_follows_a_rename()
    {
        using var repo = new TestRepo();
        repo.WriteFile("old.cs", "v1"); repo.Git("add", "-A"); repo.Git("commit", "-m", "create");
        repo.Git("mv", "old.cs", "new.cs"); repo.Git("commit", "-m", "rename");
        repo.WriteFile("new.cs", "v2"); repo.Git("add", "-A"); repo.Git("commit", "-m", "edit");

        var vm = new WorkspaceViewModel(new GitService(), new RepositoryItem("r", repo.Path));
        await vm.ShowFileHistory("new.cs");
        await vm.HistoryLoad;

        // create + rename + edit all touch the file — "create" only appears because --follow crosses the rename.
        Assert.Equal(3, vm.Commits.Count);
        Assert.Contains(vm.Commits, c => c.Subject == "create");
    }
}
