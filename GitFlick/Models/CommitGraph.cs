using System.Collections.Generic;
using Avalonia;

namespace GitFlick.Models;

public enum GraphDotKind
{
    Normal,
    Head,
    Merge,
}

/// <summary>
/// A lane drawn as a polyline. Points are emitted only where the lane changes direction, so a
/// lane running straight for a thousand rows costs two points.
/// </summary>
public sealed class GraphPath(int color)
{
    public List<Point> Points { get; } = [];

    public int Color { get; } = color;
}

/// <summary>A merge arc from a merge commit's dot into a lane that already exists.</summary>
public sealed class GraphLink
{
    public required Point Start { get; init; }

    public required Point Control { get; init; }

    public required Point End { get; init; }

    public required int Color { get; init; }
}

public sealed class GraphDot
{
    public required Point Center { get; init; }

    public required int Color { get; init; }

    public GraphDotKind Kind { get; init; } = GraphDotKind.Normal;
}

/// <summary>
/// The drawable form of a commit history.
///
/// Coordinate convention, taken from SourceGit: <b>X is in pixels, Y is in row units</b> — row
/// <c>i</c> has its centre at Y = <c>i + 0.5</c>. The renderer multiplies Y by the row height,
/// so changing row height needs no regeneration.
/// </summary>
public sealed class CommitGraph
{
    public List<GraphPath> Paths { get; } = [];

    public List<GraphLink> Links { get; } = [];

    public List<GraphDot> Dots { get; } = [];

    /// <summary>Pixel width needed to draw every lane, so the list can indent past it.</summary>
    public double Width { get; set; }
}
