using System.Linq;
using GitFlick.Models;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>Turning git's flat path list into the folder tree the file list can show.</summary>
public class CommitFileTreeTests
{
    private static CommitFileEntry F(string path) => new(path, "M");

    [Fact]
    public void A_run_of_single_child_folders_collapses_into_one_row()
    {
        // Real commit paths are deep and narrow; without collapsing this is five levels of indentation
        // wrapping a single file.
        var nodes = CommitFileNode.Build([F("src/GimmeCapture/Services/Core/Infrastructure/Names.cs")]);

        var folder = Assert.Single(nodes);
        Assert.True(folder.IsFolder);
        Assert.Equal("src/GimmeCapture/Services/Core/Infrastructure", folder.Name);

        var file = Assert.Single(folder.Children);
        Assert.False(file.IsFolder);
        Assert.Equal("Names.cs", file.Name);
    }

    [Fact]
    public void Collapsing_stops_where_the_tree_actually_branches()
    {
        var nodes = CommitFileNode.Build([F("src/a/one.cs"), F("src/b/two.cs")]);

        // "src" has two children, so it can't merge with either.
        var src = Assert.Single(nodes);
        Assert.Equal("src", src.Name);
        Assert.Equal(["a", "b"], src.Children.Select(c => c.Name));
        Assert.Equal("one.cs", Assert.Single(src.Children[0].Children).Name);
    }

    [Fact]
    public void A_folder_holding_both_files_and_a_subfolder_keeps_its_own_level()
    {
        var nodes = CommitFileNode.Build([F("src/sub/deep.cs"), F("src/top.cs")]);

        var src = Assert.Single(nodes);
        Assert.Equal("src", src.Name);   // has a file of its own, so it must not merge with "sub"

        // Folders come before files.
        Assert.Equal(["sub", "top.cs"], src.Children.Select(c => c.Name));
    }

    [Fact]
    public void Root_level_files_stay_at_the_root()
    {
        var nodes = CommitFileNode.Build([F("README.md"), F("src/a.cs")]);

        Assert.Equal(["src", "README.md"], nodes.Select(n => n.Name));
        Assert.True(nodes[0].IsFolder);
        Assert.False(nodes[1].IsFolder);
    }

    [Fact]
    public void Every_file_survives_the_round_trip_and_keeps_its_entry()
    {
        CommitFileEntry[] files =
        [
            F("src/GimmeCapture/Services/Core/Infrastructure/CaptureFileNameService.cs"),
            F("tests/GimmeCapture.Tests/CaptureFileNameServiceTests.cs"),
            F("README.md"),
        ];

        var leaves = Leaves(CommitFileNode.Build(files)).ToList();

        Assert.Equal(files.Length, leaves.Count);
        Assert.Equal(
            files.Select(f => f.Path).OrderBy(p => p),
            leaves.Select(l => l.File!.Path).OrderBy(p => p));
    }

    [Fact]
    public void An_empty_commit_produces_an_empty_tree() =>
        Assert.Empty(CommitFileNode.Build([]));

    private static System.Collections.Generic.IEnumerable<CommitFileNode> Leaves(
        System.Collections.Generic.IEnumerable<CommitFileNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.IsFolder)
            {
                foreach (var leaf in Leaves(node.Children))
                {
                    yield return leaf;
                }
            }
            else
            {
                yield return node;
            }
        }
    }
}
