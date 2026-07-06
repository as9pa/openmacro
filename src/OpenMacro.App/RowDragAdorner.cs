using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace OpenMacro.App;

/// <summary>
/// A floating snapshot of the dragged timeline row that rides with the
/// cursor. Rendered on the adorner layer so it floats above the list
/// without being part of it.
/// </summary>
internal sealed class RowDragAdorner(UIElement adorned, ImageSource snapshot, Size size)
    : Adorner(adorned)
{
    private static readonly Brush Backing = new SolidColorBrush(
        Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)
    );
    private static readonly Pen Outline = new(
        new SolidColorBrush(Color.FromRgb(0xB5, 0xB5, 0xB5)),
        1
    );

    private double y;

    public RowDragAdorner Initialized()
    {
        IsHitTestVisible = false;
        return this;
    }

    /// <summary>Moves the floating row; clamped inside the list.</summary>
    public void MoveTo(double newY)
    {
        var max = Math.Max(0, AdornedElement.RenderSize.Height - size.Height);
        y = Math.Clamp(newY, 0, max);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var rect = new Rect(new Point(0, y), size);
        dc.DrawRectangle(Backing, Outline, rect);
        dc.DrawImage(snapshot, rect);
    }
}
