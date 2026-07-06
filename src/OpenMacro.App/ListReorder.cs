using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace OpenMacro.App;

/// <summary>
/// Live drag-to-reorder for a ListBox with uniform-height rows. Past the
/// drag threshold, a floating snapshot of the grabbed row rides with the
/// cursor, the in-list row becomes a translucent placeholder, and displaced
/// neighbours slide one slot with a short ease-out. On release the floating
/// row glides into its slot before the drop commits. Esc cancels; losing
/// capture (alt-tab, popup) commits what's shown.
///
/// Only the Items collection is reordered live — the drop is handed to
/// <c>commit(from, to)</c> to apply to the model (which should rebuild the
/// list); <c>cancel(from)</c> should rebuild to restore the model's order
/// and clear ghosting/transforms.
/// </summary>
internal sealed class ListReorder
{
    private const double GhostOpacity = 0.30;

    /// <summary>One shared duration for every reorder motion — neighbours
    /// sliding a slot and the released row gliding into place.</summary>
    internal const double SlideMilliseconds = 130;

    private readonly ListBox list;
    private readonly Func<object, bool> blocksDrag;
    private readonly Action<int, int> commit;
    private readonly Action<int> cancel;

    private Point dragStart;
    private int dragSourceIndex = -1;
    private int reorderFrom = -1;
    private int reorderCurrent = -1;
    private RowDragAdorner? adorner;
    private double grabOffsetY;
    private double rowHeight;
    private bool isDragging; // capture held, the row rides with the cursor
    private bool isSettling; // released, the row is gliding into its slot

    /// <summary>True from grab until the drop lands (drag + settle glide).
    /// Selection changes and the list's visual order during this are the
    /// drag's own bookkeeping — the model hasn't been touched yet.</summary>
    public bool IsReordering => isDragging || isSettling;

    /// <param name="blocksDrag">Given the press's OriginalSource, true when
    /// a drag must not start there (e.g. an inline editor or a checkbox).</param>
    public ListReorder(
        ListBox list,
        Func<object, bool> blocksDrag,
        Action<int, int> commit,
        Action<int> cancel
    )
    {
        this.list = list;
        this.blocksDrag = blocksDrag;
        this.commit = commit;
        this.cancel = cancel;

        list.PreviewMouseLeftButtonDown += OnMouseDown;
        list.PreviewMouseMove += OnMouseMove;
        list.PreviewMouseLeftButtonUp += OnMouseUp;
        list.PreviewKeyDown += OnKeyDown;

        // Losing capture mid-drag (alt-tab, popup) commits what's shown.
        // Checks isDragging, not IsReordering: Finish itself releases
        // capture, and this must not re-enter it during the settle.
        list.LostMouseCapture += (_, _) =>
        {
            if (isDragging)
                Finish(commitDrop: true);
        };
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        // While the previous drop is still gliding in, the list's visual
        // order is ahead of the model — swallow the press so a fast click
        // can't select or toggle the wrong row.
        if (isSettling)
        {
            dragSourceIndex = -1;
            e.Handled = true;
            return;
        }

        dragStart = e.GetPosition(list);
        dragSourceIndex = blocksDrag(e.OriginalSource) ? -1 : Rows.IndexUnderMouse(list, dragStart);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        // Already reordering: the floating row follows the cursor
        // continuously; the placeholder hops a slot whenever the cursor
        // crosses a neighbour, which slides over animated.
        if (isDragging)
        {
            var position = e.GetPosition(list);
            adorner?.MoveTo(position.Y - grabOffsetY);

            var target = TargetAt(position);
            if (target >= 0 && target != reorderCurrent)
                MoveGhost(reorderCurrent, target);

            return;
        }

        if (dragSourceIndex < 0 || e.LeftButton != MouseButtonState.Pressed)
            return;

        var position2 = e.GetPosition(list);
        if (
            Math.Abs(position2.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position2.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance
        )
            return;

        // Threshold crossed: enter live-reorder mode.
        isDragging = true;
        reorderFrom = dragSourceIndex;
        reorderCurrent = dragSourceIndex;
        dragSourceIndex = -1;
        list.SelectedIndex = reorderFrom;
        StartVisuals(reorderFrom);
        list.CaptureMouse();
        Mouse.OverrideCursor = Cursors.SizeAll;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (isDragging)
            Finish(commitDrop: true);
        dragSourceIndex = -1;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && isDragging)
        {
            Finish(commitDrop: false);
            e.Handled = true;
        }
    }

    /// <summary>Snapshots the grabbed row into a floating adorner and turns
    /// the in-list row into a translucent placeholder.</summary>
    private void StartVisuals(int index)
    {
        if (list.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem row)
            return;

        rowHeight = row.ActualHeight;
        var rowTop = row.TranslatePoint(new Point(0, 0), list).Y;
        grabOffsetY = dragStart.Y - rowTop;

        // Snapshot before ghosting, so the floating copy is full-strength.
        // Rendering the row directly would include its layout offset inside
        // the list — the content lands below a row-sized bitmap and comes
        // out blank — so paint it through a VisualBrush into a fresh visual
        // anchored at the origin.
        var width = Math.Max(1, (int)Math.Ceiling(row.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(row.ActualHeight));
        var rowAtOrigin = new DrawingVisual();
        using (var ctx = rowAtOrigin.RenderOpen())
            ctx.DrawRectangle(
                new VisualBrush(row),
                null,
                new Rect(0, 0, row.ActualWidth, row.ActualHeight)
            );
        var snapshot = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        snapshot.Render(rowAtOrigin);

        adorner = new RowDragAdorner(
            list,
            snapshot,
            new Size(row.ActualWidth, row.ActualHeight)
        ).AsFloating();
        AdornerLayer.GetAdornerLayer(list)?.Add(adorner);
        adorner.MoveTo(rowTop);

        row.Opacity = GhostOpacity;
    }

    /// <summary>Moves the placeholder to a new slot; the rows it displaces
    /// slide one slot with a short ease-out (FLIP: start at the old offset,
    /// animate to zero).</summary>
    private void MoveGhost(int from, int to)
    {
        var item = list.Items[from];
        list.Items.RemoveAt(from);
        list.Items.Insert(to, item);
        reorderCurrent = to;
        list.SelectedIndex = to;
        list.UpdateLayout();

        // Containers are recreated on insert — re-ghost the placeholder.
        if (list.ItemContainerGenerator.ContainerFromIndex(to) is ListBoxItem ghost)
            ghost.Opacity = GhostOpacity;

        var (lo, hi, fromOffset) =
            to > from
                ? (from, to - 1, rowHeight) // ghost went down; these rows slid up
                : (to + 1, from, -rowHeight); // ghost went up; these rows slid down

        for (var i = lo; i <= hi; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem row)
                continue;

            var slide = new TranslateTransform(0, fromOffset);
            row.RenderTransform = slide;
            slide.BeginAnimation(
                TranslateTransform.YProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(SlideMilliseconds))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                }
            );
        }
    }

    private void Finish(bool commitDrop)
    {
        isDragging = false;
        Mouse.OverrideCursor = null;
        list.ReleaseMouseCapture();

        var from = reorderFrom;
        var to = reorderCurrent;
        reorderFrom = reorderCurrent = -1;

        // Runs once the floating row is gone: swap the model/list back in.
        void Land()
        {
            isSettling = false;
            if (adorner is not null)
            {
                AdornerLayer.GetAdornerLayer(list)?.Remove(adorner);
                adorner = null;
            }

            if (commitDrop && from >= 0 && to >= 0 && from != to)
                commit(from, to);
            else
                cancel(from);
        }

        // On a drop, glide the floating row into its slot before landing;
        // it covers the placeholder exactly, so the swap is seamless. Esc
        // (and any path without a visible slot) lands immediately.
        if (
            commitDrop
            && adorner is not null
            && to >= 0
            && list.ItemContainerGenerator.ContainerFromIndex(to) is ListBoxItem slot
        )
        {
            isSettling = true;
            adorner.SettleTo(slot.TranslatePoint(new Point(0, 0), list).Y, Land);
        }
        else
        {
            Land();
        }
    }

    /// <summary>
    /// Target slot for the drag, from pure grid arithmetic anchored to the
    /// placeholder (which never animates). Hit-testing visual bounds here
    /// would see mid-slide rows still overlapping the cursor and flip-flop
    /// forever — rows are uniform height, so the math is exact.
    /// </summary>
    private int TargetAt(Point point)
    {
        var count = list.Items.Count;
        if (count == 0 || rowHeight <= 0 || reorderCurrent < 0)
            return -1;

        if (list.ItemContainerGenerator.ContainerFromIndex(reorderCurrent) is not ListBoxItem ghost)
            return -1;

        var firstTop = ghost.TranslatePoint(new Point(0, 0), list).Y - reorderCurrent * rowHeight;
        var target = (int)Math.Floor((point.Y - firstTop) / rowHeight);
        return Math.Clamp(target, 0, count - 1);
    }
}

/// <summary>Visual-tree lookups shared by the list interactions.</summary>
internal static class Rows
{
    internal static int IndexUnderMouse(ListBox list, Point point)
    {
        for (var i = 0; i < list.Items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem item)
            {
                var bounds = new Rect(item.TranslatePoint(new Point(0, 0), list), item.RenderSize);
                if (bounds.Contains(point))
                    return i;
            }
        }

        return -1;
    }

    internal static bool IsWithin<T>(object source)
        where T : DependencyObject
    {
        var node = source as DependencyObject;
        while (node is Visual)
        {
            if (node is T)
                return true;
            node = VisualTreeHelper.GetParent(node);
        }

        return false;
    }
}
