using System;
using System.Collections.Generic;
using Avalonia;
using GitFlick.Models;

namespace GitFlick.Services;

/// <summary>
/// Turns a commit list into lanes and connecting lines.
///
/// The algorithm is adapted from SourceGit's <c>Models/CommitGraph.cs</c> (spec §5⑦ says to
/// study it rather than invent a lane algorithm). The shape of it:
///
/// <list type="bullet">
/// <item>A lane is an open line waiting for a specific SHA (<see cref="Lane.Next"/>). The lane
///       table is an ordered list — its index <i>is</i> the column.</item>
/// <item>For each commit, the <b>leftmost</b> lane waiting for it wins and continues, re-aimed at
///       the commit's first parent. Any other lane waiting for the same commit collapses into it.</item>
/// <item>Freeing a lane and shifting the lanes right of it leftwards are the same operation:
///       the running X is simply not advanced for a lane that dies.</item>
/// <item>Extra parents of a merge take one of two paths: if the parent already has a lane, emit a
///       curved <see cref="GraphLink"/> into it; if not, open a new lane. Handling only one of the
///       two loses half the merge arcs.</item>
/// </list>
///
/// Requires commits ordered so a parent never precedes its child (<c>--date-order</c> or
/// <c>--topo-order</c>); otherwise lanes wait forever for a SHA that already went past.
/// </summary>
internal static class CommitGraphBuilder
{
    private const double UnitWidth = 12;
    private const double HalfWidth = 6;
    private const double UnitHeight = 1;   // one ROW, not one pixel
    private const double HalfHeight = 0.5;

    /// <summary>Number of lane colours before they start being reused.</summary>
    public const int PaletteSize = 10;

    public static CommitGraph Build(IReadOnlyList<CommitInfo> commits, bool firstParentOnly)
    {
        var graph = new CommitGraph();
        var unsolved = new List<Lane>();
        var ended = new List<Lane>();
        var colors = new ColorPicker();

        var offsetY = -HalfHeight;
        var maxWidth = 0.0;

        foreach (var commit in commits)
        {
            Lane? major = null;
            offsetY += UnitHeight;

            var offsetX = 4 - HalfWidth;
            var maxOffsetOld = unsolved.Count > 0 ? unsolved[^1].LastX : offsetX + UnitWidth;

            foreach (var lane in unsolved)
            {
                if (lane.Next == commit.Sha)
                {
                    if (major is null)
                    {
                        // Leftmost waiting lane wins: the commit sits here and the lane carries on.
                        offsetX += UnitWidth;
                        major = lane;

                        if (commit.Parents.Count > 0)
                        {
                            major.Next = commit.Parents[0];
                            major.Goto(offsetX, offsetY, HalfHeight);
                        }
                        else
                        {
                            major.End(offsetX, offsetY, HalfHeight);
                            ended.Add(lane);
                        }
                    }
                    else
                    {
                        // Another lane was also waiting for this commit: collapse it into the winner.
                        lane.End(major.LastX, offsetY, HalfHeight);
                        ended.Add(lane);
                    }
                }
                else
                {
                    // Not this commit's lane — it just passes through this row and keeps its slot.
                    offsetX += UnitWidth;
                    lane.Pass(offsetX, offsetY, HalfHeight);
                }
            }

            foreach (var lane in ended)
            {
                colors.Recycle(lane.Path.Color);
                unsolved.Remove(lane);
            }

            ended.Clear();

            // Nothing was waiting for it, so it's a branch tip: open a new lane on the right.
            if (major is null && commit.Parents.Count > 0)
            {
                offsetX += UnitWidth;
                major = new Lane(commit.Parents[0], colors.Next(), new Point(offsetX, offsetY));
                unsolved.Add(major);
                graph.Paths.Add(major.Path);
            }
            else if (major is null)
            {
                // A root commit nobody references: a lone dot, no lane at all.
                offsetX += UnitWidth;
            }

            var position = new Point(major?.LastX ?? offsetX, offsetY);

            graph.Dots.Add(new GraphDot
            {
                Center = position,
                Color = major?.Path.Color ?? 0,
                Kind = commit.IsHead
                    ? GraphDotKind.Head
                    : commit.Parents.Count > 1
                        ? GraphDotKind.Merge
                        : GraphDotKind.Normal,
            });

            // Parent 0 already continued the major lane. The rest are the merge arcs.
            if (!firstParentOnly)
            {
                for (var i = 1; i < commit.Parents.Count; i++)
                {
                    var parentSha = commit.Parents[i];
                    var parentLane = unsolved.Find(l => l.Next == parentSha);

                    if (parentLane is not null)
                    {
                        graph.Links.Add(new GraphLink
                        {
                            Start = position,
                            End = new Point(parentLane.LastX, offsetY + HalfHeight),
                            Control = new Point(parentLane.LastX, position.Y),

                            // The arc takes the PARENT lane's colour, not the merge commit's.
                            Color = parentLane.Path.Color,
                        });
                    }
                    else
                    {
                        offsetX += UnitWidth;

                        var lane = new Lane(
                            parentSha,
                            colors.Next(),
                            position,
                            new Point(offsetX, position.Y + HalfHeight));

                        unsolved.Add(lane);
                        graph.Paths.Add(lane.Path);
                    }
                }
            }

            maxWidth = Math.Max(maxWidth, Math.Max(offsetX, maxOffsetOld));
        }

        // Lanes still open when the history runs out are drawn off the bottom edge.
        var endY = (commits.Count - HalfHeight) * UnitHeight;

        for (var i = 0; i < unsolved.Count; i++)
        {
            var lane = unsolved[i];

            if (lane.Path.Points.Count == 1 && Math.Abs(lane.Path.Points[0].Y - endY) < 0.0001)
            {
                continue;
            }

            lane.End((i + HalfHeight) * UnitWidth + 4, endY + HalfHeight, HalfHeight);
        }

        graph.Width = maxWidth + HalfWidth + 2;
        return graph;
    }

    /// <summary>An open line, waiting for <see cref="Next"/> to show up.</summary>
    private sealed class Lane
    {
        private double _lastY;
        private double _endY;

        public Lane(string next, int color, Point start)
        {
            Next = next;
            Path = new GraphPath(color);
            Path.Points.Add(start);
            LastX = start.X;
            _lastY = start.Y;
            _endY = start.Y;
        }

        /// <summary>For a merge parent with no lane yet: starts at the merge dot, then steps out.</summary>
        public Lane(string next, int color, Point start, Point to)
        {
            Next = next;
            Path = new GraphPath(color);
            Path.Points.Add(start);
            Path.Points.Add(to);
            LastX = to.X;
            _lastY = to.Y;
            _endY = to.Y;
        }

        public GraphPath Path { get; }

        public string Next { get; set; }

        public double LastX { get; private set; }

        /// <summary>No commit on this row — carry the lane through, shifting column if needed.</summary>
        public void Pass(double x, double y, double halfHeight)
        {
            if (x > LastX)
            {
                Add(LastX, _lastY);
                Add(x, y - halfHeight);
            }
            else if (x < LastX)
            {
                Add(LastX, y - halfHeight);
                y += halfHeight;
                Add(x, y);
            }

            LastX = x;
            _lastY = y;
        }

        /// <summary>The commit sits on this lane and the lane continues to its first parent.</summary>
        public void Goto(double x, double y, double halfHeight)
        {
            if (x > LastX)
            {
                Add(LastX, _lastY);
                Add(x, y - halfHeight);
            }
            else if (x < LastX)
            {
                var minY = y - halfHeight;
                if (minY > _lastY)
                {
                    minY -= halfHeight;
                }

                Add(LastX, minY);
                Add(x, y);
            }

            LastX = x;
            _lastY = y;
        }

        /// <summary>The lane terminates here.</summary>
        public void End(double x, double y, double halfHeight)
        {
            if (x > LastX)
            {
                Add(LastX, _lastY);
                Add(x, y - halfHeight);
            }
            else if (x < LastX)
            {
                Add(LastX, y - halfHeight);
            }

            Add(x, y);

            LastX = x;
            _lastY = y;
        }

        // Points are only worth recording where the line turns, and Y must keep increasing.
        private void Add(double x, double y)
        {
            if (_endY < y)
            {
                Path.Points.Add(new Point(x, y));
                _endY = y;
            }
        }
    }

    /// <summary>
    /// Hands out lane colours round-robin and returns a dead lane's colour to the back of the
    /// queue, so a colour is reused as late as possible.
    /// </summary>
    private sealed class ColorPicker
    {
        private readonly Queue<int> _queue = new();

        public int Next()
        {
            if (_queue.Count == 0)
            {
                for (var i = 0; i < PaletteSize; i++)
                {
                    _queue.Enqueue(i);
                }
            }

            return _queue.Dequeue();
        }

        public void Recycle(int color)
        {
            if (!_queue.Contains(color))
            {
                _queue.Enqueue(color);
            }
        }
    }
}
