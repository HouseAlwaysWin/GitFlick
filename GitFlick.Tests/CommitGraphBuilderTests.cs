using System;
using System.Collections.Generic;
using System.Linq;
using GitFlick.Models;
using GitFlick.Services;

namespace GitFlick.Tests;

/// <summary>
/// The lane algorithm is pure, so the topologies that matter can be pinned down exactly.
/// Commits are listed newest-first, the way --date-order emits them.
/// </summary>
public class CommitGraphBuilderTests
{
    private static CommitInfo C(string sha, params string[] parents) => new()
    {
        Sha = sha,
        Parents = parents,
        Author = "tester",
        When = DateTimeOffset.UnixEpoch,
        Subject = sha,
    };

    private static List<double> DotXs(CommitGraph graph) => graph.Dots.Select(d => d.Center.X).ToList();

    [Fact]
    public void Empty_history_produces_an_empty_graph()
    {
        var graph = CommitGraphBuilder.Build([], firstParentOnly: false);

        Assert.Empty(graph.Dots);
        Assert.Empty(graph.Paths);
        Assert.Empty(graph.Links);
    }

    [Fact]
    public void Linear_history_stays_in_one_lane()
    {
        //  C -> B -> A
        var graph = CommitGraphBuilder.Build([C("C", "B"), C("B", "A"), C("A")], firstParentOnly: false);

        Assert.Equal(3, graph.Dots.Count);
        Assert.Single(graph.Paths);
        Assert.Empty(graph.Links);

        // Every commit sits in lane 0, so every dot shares one X.
        Assert.Single(DotXs(graph).Distinct());
    }

    [Fact]
    public void Rows_advance_one_unit_at_a_time_in_row_space()
    {
        var graph = CommitGraphBuilder.Build([C("C", "B"), C("B", "A"), C("A")], firstParentOnly: false);

        // Y is in ROW units: row i is centred at i + 0.5, not at pixels.
        Assert.Equal([0.5, 1.5, 2.5], graph.Dots.Select(d => d.Center.Y));
    }

    [Fact]
    public void A_branch_and_merge_opens_a_second_lane_and_collapses_it_again()
    {
        //  M      merge of B and F
        //  |\
        //  B F
        //  |/
        //  A
        var graph = CommitGraphBuilder.Build(
            [C("M", "B", "F"), C("B", "A"), C("F", "A"), C("A")],
            firstParentOnly: false);

        Assert.Equal(4, graph.Dots.Count);
        Assert.Equal(2, graph.Paths.Count);   // the second lane was opened for F

        // The merge commit is drawn as a merge dot.
        Assert.Equal(GraphDotKind.Merge, graph.Dots[0].Kind);

        // B stays in lane 0 while F is pushed out to lane 1...
        var xs = DotXs(graph);
        Assert.Equal(xs[1], xs[0]);        // M and B share the first lane
        Assert.True(xs[2] > xs[1]);        // F is further right
        Assert.Equal(xs[0], xs[3]);        // ...and A collapses back to lane 0
    }

    [Fact]
    public void A_merge_into_a_lane_that_already_exists_emits_a_link()
    {
        //  X     (tip, waiting for A)
        //  M     (merge of B and A — A already has a lane, from X)
        //  B
        //  A
        var graph = CommitGraphBuilder.Build(
            [C("X", "A"), C("M", "B", "A"), C("B", "A"), C("A")],
            firstParentOnly: false);

        // A's lane already existed when M was processed, so the second parent becomes a curved
        // link rather than a new lane. Missing this branch of the algorithm loses merge arcs.
        var link = Assert.Single(graph.Links);
        Assert.Equal(graph.Dots[1].Center, link.Start);   // starts at the merge dot
        Assert.True(link.End.X < link.Start.X);           // and curves back left into A's lane
    }

    [Fact]
    public void First_parent_only_hides_the_side_lanes()
    {
        var commits = new[] { C("M", "B", "F"), C("B", "A"), C("A") };

        var full = CommitGraphBuilder.Build(commits, firstParentOnly: false);
        var collapsed = CommitGraphBuilder.Build(commits, firstParentOnly: true);

        Assert.Equal(2, full.Paths.Count);
        Assert.Single(collapsed.Paths);      // F never gets a lane
        Assert.Empty(collapsed.Links);

        // The merge is still recognisable as a merge, just without its side branch drawn.
        Assert.Equal(GraphDotKind.Merge, collapsed.Dots[0].Kind);
    }

    [Fact]
    public void Head_gets_its_own_dot_kind()
    {
        var commits = new[]
        {
            C("B", "A") with { IsHead = true },
            C("A"),
        };

        var graph = CommitGraphBuilder.Build(commits, firstParentOnly: false);

        Assert.Equal(GraphDotKind.Head, graph.Dots[0].Kind);
        Assert.Equal(GraphDotKind.Normal, graph.Dots[1].Kind);
    }

    [Fact]
    public void Two_independent_roots_get_separate_lanes()
    {
        // Unrelated histories (e.g. an orphan branch) must not be forced into one lane.
        var graph = CommitGraphBuilder.Build(
            [C("B", "A"), C("A"), C("Y", "X"), C("X")],
            firstParentOnly: false);

        Assert.Equal(4, graph.Dots.Count);
        Assert.Equal(2, graph.Paths.Count);
    }

    [Fact]
    public void Lane_freed_by_a_merge_is_reclaimed_by_the_lanes_to_its_right()
    {
        //  M    merge of B and F -> after A, the F lane dies and nothing should drift right forever
        var graph = CommitGraphBuilder.Build(
            [C("M", "B", "F"), C("B", "A"), C("F", "A"), C("A"), C("Z0", "Z1"), C("Z1")],
            firstParentOnly: false);

        // Once the branch collapses, later commits are back in the leftmost lane rather than
        // stranded in a column that was never reclaimed.
        var xs = DotXs(graph);
        Assert.Equal(xs[0], xs[3]);   // A back in lane 0
        Assert.Equal(xs[0], xs[4]);   // and the next tip reuses lane 0 too
    }

    [Fact]
    public void Points_are_only_emitted_where_a_lane_changes_direction()
    {
        // 50 commits in a straight line: a naive per-row emitter would produce ~50 points.
        var commits = new List<CommitInfo>();
        for (var i = 0; i < 50; i++)
        {
            commits.Add(C($"c{i}", $"c{i + 1}"));
        }

        commits.Add(C("c50"));

        var graph = CommitGraphBuilder.Build(commits, firstParentOnly: false);
        var path = Assert.Single(graph.Paths);

        Assert.True(path.Points.Count <= 3, $"straight lane should be a couple of points, got {path.Points.Count}");
    }

    [Fact]
    public void Width_covers_every_lane()
    {
        var graph = CommitGraphBuilder.Build(
            [C("M", "B", "F"), C("B", "A"), C("F", "A"), C("A")],
            firstParentOnly: false);

        Assert.True(graph.Width >= DotXs(graph).Max(), "graph width must span the rightmost lane");
    }
}
