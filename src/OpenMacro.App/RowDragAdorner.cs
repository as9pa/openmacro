using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OpenMacro.App;

/// <summary>
/// A floating snapshot of the dragged list row that rides with the
/// cursor. Rendered on the adorner layer so it floats above the list
/// without being part of it.
/// </summary>
internal sealed class RowDragAdorner(UIElement adorned, ImageSource snapshot, Size size)
    : Adorner(adorned)
{
    // Read once per drag rather than cached statically, so a theme change
    // between drags is picked up.
    private readonly Brush backing = ThemeManager.Brush("DragBacking");
    private readonly Pen outline = new(ThemeManager.Brush("Surface3"), 1);

    // A dependency property (not a plain field) so SettleTo can drive it
    // through WPF's animation system.
    private static readonly DependencyProperty YProperty = DependencyProperty.Register(
        "Y",
        typeof(double),
        typeof(RowDragAdorner),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender)
    );

    // Named to avoid hiding the inherited Initialized event (CS0108).
    public RowDragAdorner AsFloating()
    {
        IsHitTestVisible = false;
        return this;
    }

    /// <summary>Moves the floating row; clamped inside the list.</summary>
    public void MoveTo(double newY)
    {
        var max = Math.Max(0, AdornedElement.RenderSize.Height - size.Height);
        SetValue(YProperty, Math.Clamp(newY, 0, max));
    }

    /// <summary>Glides the floating row from wherever it is into its slot,
    /// then runs <paramref name="landed"/> (which swaps in the real row).</summary>
    public void SettleTo(double targetY, Action landed)
    {
        var glide = new DoubleAnimation(
            targetY,
            TimeSpan.FromMilliseconds(ListReorder.SlideMilliseconds)
        )
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        glide.Completed += (_, _) => landed();
        BeginAnimation(YProperty, glide);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var rect = new Rect(new Point(0, (double)GetValue(YProperty)), size);
        dc.DrawRectangle(backing, outline, rect);
        dc.DrawImage(snapshot, rect);
    }
}
