using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>
/// Drives <see cref="ConflictResolverViewModel"/> against a fake git layer plus real files on disk
/// (the resolver reads/writes the working tree directly, so the files have to exist). The real-git
/// side of the same feature lives in <see cref="ConflictOperationGitTests"/>.
/// </summary>
public class ConflictResolverViewModelTests
{
    private static GitStatusEntry Unmerged(string path, string code) => new()
    {
        Path = path,
        Kind = GitChangeKind.Unmerged,
        UnmergedCode = code,
    };

    private const string Markers =
        "<<<<<<< HEAD\nmine\n=======\ntheirs\n>>>>>>> feature\n";

    [Fact]
    public async Task Load_lists_unmerged_paths_and_classifies_each_kind()
    {
        using var repo = new TestRepo();
        repo.WriteFile("text.txt", Markers);                         // UU + text  -> Text
        File.WriteAllBytes(Path.Combine(repo.Path, "img.bin"),        // UU + NUL   -> Binary
            [0x89, 0x50, 0x00, 0x01]);
        repo.WriteFile("kept.txt", "still here\n");                  // UD         -> ModifyDelete
        // both-deleted has no working file on purpose (DD)          //            -> Other

        var git = new FakeGitService
        {
            StubStatus = new GitStatus
            {
                Entries =
                [
                    Unmerged("text.txt", "UU"),
                    Unmerged("img.bin", "UU"),
                    Unmerged("kept.txt", "UD"),
                    Unmerged("both-gone.txt", "DD"),
                ],
            },
        };

        var vm = new ConflictResolverViewModel(git, repo.Path, ConflictOperation.Merge);
        await vm.LoadAsync();

        Assert.Equal(4, vm.Conflicts.Count);
        Assert.Equal(ConflictFileKind.Text, vm.Conflicts.Single(c => c.Path == "text.txt").Kind);
        Assert.Equal(ConflictFileKind.Binary, vm.Conflicts.Single(c => c.Path == "img.bin").Kind);
        Assert.Equal(ConflictFileKind.ModifyDelete, vm.Conflicts.Single(c => c.Path == "kept.txt").Kind);
        Assert.Equal(ConflictFileKind.Other, vm.Conflicts.Single(c => c.Path == "both-gone.txt").Kind);
        Assert.True(vm.HasUnresolved);
    }

    [Fact]
    public async Task Selecting_a_text_conflict_loads_the_file_and_reads_the_marker_labels()
    {
        using var repo = new TestRepo();
        repo.WriteFile("text.txt", Markers);
        var git = new FakeGitService { StubStatus = new GitStatus { Entries = [Unmerged("text.txt", "UU")] } };

        var vm = new ConflictResolverViewModel(git, repo.Path, ConflictOperation.Merge);
        string editor = "";
        vm.SetEditorText = t => editor = t;
        vm.GetEditorText = () => editor;

        await vm.LoadAsync();   // auto-selects the sole conflict

        Assert.True(vm.IsTextConflict);
        Assert.Equal(Markers, editor);
        Assert.Equal("HEAD", vm.OursLabel);
        Assert.Equal("feature", vm.TheirsLabel);
        Assert.True(vm.HasStaleMarkers);
    }

    [Fact]
    public async Task MarkResolved_writes_the_editor_then_stages()
    {
        using var repo = new TestRepo();
        repo.WriteFile("text.txt", Markers);
        var git = new FakeGitService { StubStatus = new GitStatus { Entries = [Unmerged("text.txt", "UU")] } };

        var vm = new ConflictResolverViewModel(git, repo.Path, ConflictOperation.Merge);
        string editor = "";
        vm.SetEditorText = t => editor = t;
        vm.GetEditorText = () => editor;
        await vm.LoadAsync();

        editor = "resolved by hand\n";   // the user edited away the markers
        await vm.MarkResolvedCommand.ExecuteAsync(null);

        Assert.Equal("resolved by hand\n", File.ReadAllText(Path.Combine(repo.Path, "text.txt")));
        Assert.Contains("text.txt", git.StagedPaths);
    }

    [Fact]
    public async Task Save_writes_the_file_but_does_not_stage()
    {
        using var repo = new TestRepo();
        repo.WriteFile("text.txt", Markers);
        var git = new FakeGitService { StubStatus = new GitStatus { Entries = [Unmerged("text.txt", "UU")] } };

        var vm = new ConflictResolverViewModel(git, repo.Path, ConflictOperation.Merge);
        string editor = "";
        vm.SetEditorText = t => editor = t;
        vm.GetEditorText = () => editor;
        await vm.LoadAsync();

        editor = "work in progress\n";
        vm.SaveCommand.Execute(null);

        Assert.Equal("work in progress\n", File.ReadAllText(Path.Combine(repo.Path, "text.txt")));
        Assert.Empty(git.StagedPaths);
    }

    [Fact]
    public async Task TakeOurs_checks_out_that_side_then_stages()
    {
        using var repo = new TestRepo();
        repo.WriteFile("text.txt", Markers);
        var git = new FakeGitService { StubStatus = new GitStatus { Entries = [Unmerged("text.txt", "UU")] } };

        var vm = new ConflictResolverViewModel(git, repo.Path, ConflictOperation.Merge);
        await vm.LoadAsync();

        await vm.TakeOursCommand.ExecuteAsync(null);

        Assert.Equal("text.txt", git.LastTakeOurs);
        Assert.Contains("text.txt", git.StagedPaths);
    }

    [Fact]
    public async Task AcceptDeletion_on_a_both_deleted_file_runs_git_rm()
    {
        using var repo = new TestRepo();
        var git = new FakeGitService { StubStatus = new GitStatus { Entries = [Unmerged("both-gone.txt", "DD")] } };

        var vm = new ConflictResolverViewModel(git, repo.Path, ConflictOperation.Merge);
        await vm.LoadAsync();

        Assert.True(vm.ShowAcceptDeletion);
        await vm.AcceptDeletionCommand.ExecuteAsync(null);

        Assert.Equal("both-gone.txt", git.LastRemovedFile);
    }

    [Fact]
    public async Task Complete_is_blocked_while_conflicts_remain()
    {
        using var repo = new TestRepo();
        repo.WriteFile("text.txt", Markers);
        var git = new FakeGitService { StubStatus = new GitStatus { Entries = [Unmerged("text.txt", "UU")] } };

        var vm = new ConflictResolverViewModel(git, repo.Path, ConflictOperation.Merge);
        var finished = false;
        vm.Finished += (_, _) => finished = true;
        await vm.LoadAsync();

        await vm.CompleteMergeCommand.ExecuteAsync(null);

        Assert.Null(git.LastContinued);          // it did not run `--continue`
        Assert.False(finished);
        Assert.False(string.IsNullOrEmpty(vm.StatusText));
    }

    [Fact]
    public async Task Complete_continues_the_operation_and_finishes_when_clean()
    {
        using var repo = new TestRepo();
        var git = new FakeGitService
        {
            StubStatus = new GitStatus(),                 // nothing unmerged
            StubConflictOperation = ConflictOperation.Rebase,
        };

        var vm = new ConflictResolverViewModel(git, repo.Path, ConflictOperation.Rebase);
        var finished = false;
        vm.Finished += (_, _) => finished = true;
        await vm.LoadAsync();

        await vm.CompleteMergeCommand.ExecuteAsync(null);

        Assert.Equal(ConflictOperation.Rebase, git.LastContinued);
        Assert.True(finished);
    }

    [Fact]
    public async Task Abort_confirmed_aborts_and_finishes()
    {
        using var repo = new TestRepo();
        var git = new FakeGitService { StubStatus = new GitStatus { Entries = [Unmerged("text.txt", "UU")] } };

        var vm = new ConflictResolverViewModel(git, repo.Path, ConflictOperation.CherryPick)
        {
            ConfirmAbort = () => Task.FromResult(true),
        };
        var finished = false;
        vm.Finished += (_, _) => finished = true;
        await vm.LoadAsync();

        await vm.AbortMergeCommand.ExecuteAsync(null);

        Assert.Equal(ConflictOperation.CherryPick, git.LastAborted);
        Assert.True(finished);
    }

    [Fact]
    public async Task Abort_cancelled_does_nothing()
    {
        using var repo = new TestRepo();
        var git = new FakeGitService { StubStatus = new GitStatus { Entries = [Unmerged("text.txt", "UU")] } };

        var vm = new ConflictResolverViewModel(git, repo.Path, ConflictOperation.Merge)
        {
            ConfirmAbort = () => Task.FromResult(false),
        };
        var finished = false;
        vm.Finished += (_, _) => finished = true;
        await vm.LoadAsync();

        await vm.AbortMergeCommand.ExecuteAsync(null);

        Assert.Null(git.LastAborted);
        Assert.False(finished);
    }
}
