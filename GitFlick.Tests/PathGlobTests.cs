using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.Services;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>
/// The comma-separated glob used by the History search's path boxes, and the fact that an exclusion
/// also drops those paths from the File-scope pick list — offering the very paths you just excluded
/// is what makes an exclude look like it did nothing.
/// </summary>
public class PathGlobTests
{
    [Theory]
    [InlineData("docs/notes.md", "*.md", true)]           // a bare extension catches any depth
    [InlineData("README.md", "*.md", true)]
    [InlineData("GitFlick/App.axaml", "*.md", false)]
    [InlineData("docs/benchmark.md", "docs/", true)]       // folder pattern
    [InlineData("docs", "docs/", true)]
    [InlineData("GitFlick/docs/a.cs", "docs/", false)]     // only anchored at the root
    [InlineData(".vscode/launch.json", "*.json", true)]
    [InlineData("GitFlick/Services/PathGlob.cs", "**/*.cs", true)]
    [InlineData("a.cs", "*.cs", true)]
    [InlineData("src/a.cs", "src/*.cs", true)]
    [InlineData("src/deep/a.cs", "src/*.cs", false)]       // a single * stops at a separator
    [InlineData("src/deep/a.cs", "src/**", true)]
    public void Matches_one_pattern(string path, string pattern, bool expected) =>
        Assert.Equal(expected, PathGlob.Matches(path, pattern));

    [Fact]
    public void MatchesAny_takes_a_comma_separated_list()
    {
        Assert.True(PathGlob.MatchesAny("docs/notes.md", "*.md,*.json"));
        Assert.True(PathGlob.MatchesAny(".vscode/launch.json", "*.md, *.json"));
        Assert.False(PathGlob.MatchesAny("GitFlick/App.axaml", "*.md,*.json"));
    }

    [Fact]
    public void An_empty_or_blank_pattern_matches_nothing()
    {
        Assert.False(PathGlob.MatchesAny("a.cs", string.Empty));
        Assert.False(PathGlob.MatchesAny("a.cs", " , "));
    }

    [Fact]
    public async Task Excluded_paths_drop_out_of_the_file_pick_list_as_you_type()
    {
        var git = new FakeGitService();
        git.StubPaths.Add(".vscode/launch.json");
        git.StubPaths.Add("git-launcher-handoff.md");
        git.StubPaths.Add("GitFlick/Services/BrowserLauncher.cs");

        var vm = new WorkspaceViewModel(git, new RepositoryItem("r", Path.GetTempPath()));
        await vm.History.LoadHistoryAsync();
        await vm.History.UseFileSearchCommand.ExecuteAsync(null);
        vm.History.SearchText = "launch";

        Assert.Equal(3, vm.History.FilteredPathSuggestions.Count);

        // Typing the exclusion alone re-narrows the list — no Enter needed, since it's client-side.
        vm.History.ExcludeText = "*.md,*.json";

        var only = Assert.Single(vm.History.FilteredPathSuggestions);
        Assert.Equal("GitFlick/Services/BrowserLauncher.cs", only.Path);
    }

    [Theory]
    // The row leads with the file name and trails the folder, so a long path can't push the name
    // you're scanning for off the edge of the flyout.
    [InlineData("GitFlick/Services/BrowserLauncher.cs", "BrowserLauncher.cs", "GitFlick/Services")]
    [InlineData(".vscode/launch.json", "launch.json", ".vscode")]
    [InlineData("README.md", "README.md", "")]
    public void A_suggestion_splits_into_name_and_folder(string path, string name, string folder)
    {
        var suggestion = new PathSuggestion(path);

        Assert.Equal(name, suggestion.Name);
        Assert.Equal(folder, suggestion.Folder);
        Assert.Equal(folder.Length > 0, suggestion.HasFolder);
    }

    [Fact]
    public async Task The_pick_list_stays_closed_until_the_query_matches_something()
    {
        var git = new FakeGitService();
        git.StubPaths.Add(".vscode/launch.json");
        git.StubPaths.Add("GitFlick/App.axaml");

        var vm = new WorkspaceViewModel(git, new RepositoryItem("r", Path.GetTempPath()));
        await vm.History.LoadHistoryAsync();
        await vm.History.UseFileSearchCommand.ExecuteAsync(null);

        // Entering File scope must not drop the whole repo's paths open unprompted.
        Assert.False(vm.History.HasPathSuggestions);
        Assert.Empty(vm.History.FilteredPathSuggestions);

        vm.History.SearchText = "launch";
        Assert.True(vm.History.HasPathSuggestions);

        vm.History.SearchText = "zzzznomatch";
        Assert.False(vm.History.HasPathSuggestions);   // no matches -> stays closed

        vm.History.SearchText = string.Empty;
        Assert.False(vm.History.HasPathSuggestions);

        // Leaving File scope closes it too.
        vm.History.SearchText = "launch";
        await vm.History.UseMessageSearchCommand.ExecuteAsync(null);
        Assert.False(vm.History.HasPathSuggestions);
    }
}
