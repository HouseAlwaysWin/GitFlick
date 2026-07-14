using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using GitFlick.Services;

namespace GitFlick.Views;

/// <summary>
/// Paints the +/- line backgrounds behind the diff text. TextMate handles the syntax colours;
/// this only tints whole lines so additions and removals are scannable at a glance.
/// Only visible lines are drawn, so a huge diff costs no more than a screenful.
/// </summary>
internal sealed class DiffLineBackgroundRenderer : IBackgroundRenderer
{
    private static readonly IBrush AddedBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x3F, 0xB9, 0x50));
    private static readonly IBrush RemovedBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xF8, 0x51, 0x49));
    private static readonly IBrush HunkBrush = new SolidColorBrush(Color.FromArgb(0x26, 0x58, 0x8C, 0xF0));
    private static readonly IBrush HeaderBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0x90, 0x90, 0x90));

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (textView.Document is null || !textView.VisualLinesValid)
        {
            return;
        }

        foreach (var visualLine in textView.VisualLines)
        {
            var documentLine = visualLine.FirstDocumentLine;
            var text = textView.Document.GetText(documentLine.Offset, documentLine.Length);

            var brush = DiffLineClassifier.Classify(text) switch
            {
                DiffLineKind.Added => AddedBrush,
                DiffLineKind.Removed => RemovedBrush,
                DiffLineKind.Hunk => HunkBrush,
                DiffLineKind.Header => HeaderBrush,
                _ => null,
            };

            if (brush is null)
            {
                continue;
            }

            var top = visualLine.VisualTop - textView.VerticalOffset;
            drawingContext.FillRectangle(
                brush,
                new Rect(0, top, textView.Bounds.Width, visualLine.Height));
        }
    }
}
