using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GitFlick.Models;

namespace GitFlick.Views;

/// <summary>
/// Draws the commit graph as a plain overlay — no per-row visuals, one Render pass.
///
/// The graph's Y coordinates are in ROW units (row i is centred at i + 0.5), so this control
/// multiplies them by <see cref="RowHeight"/> at draw time. That is what lets the row height
/// change without regenerating the graph.
/// </summary>
public sealed class CommitGraphView : Control
{
    public static readonly StyledProperty<CommitGraph?> GraphProperty =
        AvaloniaProperty.Register<CommitGraphView, CommitGraph?>(nameof(Graph));

    public static readonly StyledProperty<double> RowHeightProperty =
        AvaloniaProperty.Register<CommitGraphView, double>(nameof(RowHeight), 26);

    /// <summary>How far the commit list is scrolled, in pixels.</summary>
    public static readonly StyledProperty<double> ScrollOffsetProperty =
        AvaloniaProperty.Register<CommitGraphView, double>(nameof(ScrollOffset));

    /// <summary>Filled behind each dot so the lane lines don't show through it.</summary>
    public static readonly StyledProperty<IBrush> DotBackgroundProperty =
        AvaloniaProperty.Register<CommitGraphView, IBrush>(
            nameof(DotBackground), new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)));

    // Lane colours, cycled per lane (never tied to branch names).
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0xF0, 0x84, 0x2E),   // orange
        Color.FromRgb(0x3F, 0xB9, 0x50),   // green
        Color.FromRgb(0x2E, 0xC4, 0xC4),   // turquoise
        Color.FromRgb(0xB5, 0xB5, 0x3F),   // olive
        Color.FromRgb(0xD1, 0x5C, 0xD1),   // magenta
        Color.FromRgb(0xE5, 0x51, 0x51),   // red
        Color.FromRgb(0xD1, 0xBE, 0x6E),   // khaki
        Color.FromRgb(0x8A, 0xD1, 0x3F),   // lime
        Color.FromRgb(0x58, 0x8C, 0xF0),   // royal blue
        Color.FromRgb(0x38, 0xB2, 0xA0),   // teal
    ];

    private static readonly Pen[] Pens = CreatePens();

    static CommitGraphView()
    {
        AffectsRender<CommitGraphView>(
            GraphProperty, RowHeightProperty, ScrollOffsetProperty, DotBackgroundProperty);
    }

    public CommitGraph? Graph
    {
        get => GetValue(GraphProperty);
        set => SetValue(GraphProperty, value);
    }

    public double RowHeight
    {
        get => GetValue(RowHeightProperty);
        set => SetValue(RowHeightProperty, value);
    }

    public double ScrollOffset
    {
        get => GetValue(ScrollOffsetProperty);
        set => SetValue(ScrollOffsetProperty, value);
    }

    public IBrush DotBackground
    {
        get => GetValue(DotBackgroundProperty);
        set => SetValue(DotBackgroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (Graph is not { } graph || graph.Dots.Count == 0)
        {
            return;
        }

        var rowHeight = RowHeight;
        var top = ScrollOffset;

        using (context.PushClip(new Rect(Bounds.Size)))
        using (context.PushTransform(Matrix.CreateTranslation(0, -top)))
        {
            DrawPaths(context, graph, rowHeight);
            DrawLinks(context, graph, rowHeight);
            DrawDots(context, graph, rowHeight);
        }
    }

    private static void DrawPaths(DrawingContext context, CommitGraph graph, double rowHeight)
    {
        foreach (var path in graph.Paths)
        {
            if (path.Points.Count < 2)
            {
                continue;
            }

            var geometry = new StreamGeometry();

            using (var ctx = geometry.Open())
            {
                var last = Scale(path.Points[0], rowHeight);
                ctx.BeginFigure(last, false);

                for (var i = 1; i < path.Points.Count; i++)
                {
                    var current = Scale(path.Points[i], rowHeight);

                    if (current.X > last.X)
                    {
                        // Stepping right: turn at the row edge, then run down.
                        ctx.QuadraticBezierTo(new Point(current.X, last.Y), current);
                    }
                    else if (current.X < last.X)
                    {
                        if (i < path.Points.Count - 1)
                        {
                            // Shifting left mid-lane: an S-curve reads far better than a corner.
                            var midY = (last.Y + current.Y) / 2;
                            ctx.CubicBezierTo(
                                new Point(last.X, midY + 4),
                                new Point(current.X, midY - 4),
                                current);
                        }
                        else
                        {
                            // Final segment: hook down-then-left into the commit dot.
                            ctx.QuadraticBezierTo(new Point(last.X, current.Y), current);
                        }
                    }
                    else
                    {
                        ctx.LineTo(current);
                    }

                    last = current;
                }
            }

            context.DrawGeometry(null, PenFor(path.Color), geometry);
        }
    }

    private static void DrawLinks(DrawingContext context, CommitGraph graph, double rowHeight)
    {
        foreach (var link in graph.Links)
        {
            var geometry = new StreamGeometry();

            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(Scale(link.Start, rowHeight), false);
                ctx.QuadraticBezierTo(Scale(link.Control, rowHeight), Scale(link.End, rowHeight));
            }

            context.DrawGeometry(null, PenFor(link.Color), geometry);
        }
    }

    private void DrawDots(DrawingContext context, CommitGraph graph, double rowHeight)
    {
        var background = DotBackground;

        foreach (var dot in graph.Dots)
        {
            var center = Scale(dot.Center, rowHeight);
            var brush = new SolidColorBrush(Palette[dot.Color % Palette.Length]);

            switch (dot.Kind)
            {
                case GraphDotKind.Head:
                    // A ring, so HEAD reads differently at a glance.
                    context.DrawEllipse(background, PenFor(dot.Color), center, 5.5, 5.5);
                    context.DrawEllipse(brush, null, center, 2.5, 2.5);
                    break;

                case GraphDotKind.Merge:
                    context.DrawEllipse(brush, null, center, 5, 5);
                    context.DrawEllipse(background, null, center, 2, 2);
                    break;

                default:
                    context.DrawEllipse(brush, null, center, 3.5, 3.5);
                    break;
            }
        }
    }

    // X is already pixels; Y is a row index, so it scales by the row height.
    private static Point Scale(Point point, double rowHeight) => new(point.X, point.Y * rowHeight);

    private static Pen PenFor(int color) => Pens[color % Pens.Length];

    private static Pen[] CreatePens()
    {
        var pens = new Pen[Palette.Length];

        for (var i = 0; i < Palette.Length; i++)
        {
            pens[i] = new Pen(new SolidColorBrush(Palette[i]), 2);
        }

        return pens;
    }
}
