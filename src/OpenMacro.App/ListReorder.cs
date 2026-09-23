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
/// drag threshold, a floating snapshot of the grabbed rows rides with the
/// cursor, the in-list rows become translucent placeholders, and displaced
/// neighbours slide one block with a short ease-out. On release the floating
/// rows glide into their slot before the drop commits. Esc cancels; losing
/// capture (alt-tab, popup) commits what's shown.
///
/// Grabbing a row that is part of a multi-selection drags the whole
/// selection: it is first gathered into one contiguous block (relative
/// order kept, even for a ctrl-click scattered selection), which then moves
/// as a unit.
///
/// Only the Items collection is reordered live — the drop is handed to
/// <c>commit(sourceIndices, insertAt)</c> to apply to the model (which
/// should rebuild the list): remove the items at <c>sourceIndices</c>
/// (ascending, in pre-drag model order), then insert them at
/// <c>insertAt</c>. <c>cancel(sourceIndices)</c> should rebuild to restore
/// the model's order and clear ghosting/transforms.
/// </summary>
internal sealed class ListReorder
{
    private const double GhostOpacity = 0.30;

    /// <summary>One shared duration for every reorder motion — neighbours
    /// sliding a slot and the released rows gliding into place.</summary>
    internal const double SlideMilliseconds = 130;

    private readonly ListBox list;
    private readonly Func<object, bool> blocksDrag;
    private readonly Action<int[], int> commit;
    private readonly Action<int[]> cancel;
    private readonly Func<bool>? refuses;
    private readonly Action? refused;

    private Point dragStart;
    private int dragSourceIndex = -1;
    private int[] pressSelection = [];
    private int reorderFrom = -1;
    private int reorderCurrent = -1;
    private int blockLength = 1;
    private int[] originalSelection = [];
    private RowDragAdorner? adorner;
    private double grabOffsetY;
    private double rowHeight;
    private double blockHeight;
    private bool isDragging; // capture held, the rows ride with the cursor
    private bool isSettling; // released, the rows are gliding into their slot

    /// <summary>True from grab until the drop lands (drag + settle glide).
    /// Selection changes and the list's visual order during this are the
    /// drag's own bookkeeping — the model hasn't been touched yet.</summary>
    public bool IsReordering => isDragging || isSettling;

    /// <param name="blocksDrag">Given the press's OriginalSource, true when
    /// a drag must not start there (e.g. an inline editor or a checkbox).</param>
    /// <param name="refuses">True while reordering is off altogether (e.g. the
    /// list shows a filtered view); checked when a drag crosses the threshold.</param>
    /// <param name="refused">Told once per drag that <paramref name="refuses"/>
    /// turned away, so the owner can say why nothing moved.</param>
    public ListReorder(
        ListBox list,
        Func<object, bool> blocksDrag,
        Action<int[], int> commit,
        Action<int[]> cancel,
        Func<bool>? refuses = null,
        Action? refused = null
    )
    {
        this.list = list;
        this.blocksDrag = blocksDrag;
        this.commit = commit;
        this.cancel = cancel;
        this.refuses = refuses;
        this.refused = refused;

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

        // Snapshot the selection now: this preview handler runs before the
        // ListBox's own mouse-down, which collapses a multi-selection to the
        // pressed row. If this press becomes a drag, it drags what the user
        // saw selected when they pressed.
        pressSelection = dragSourceIndex >= 0 ? SelectedIndices() : [];
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        // Already reordering: the floating block follows the cursor
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

        // Threshold crossed while reordering is off: this press is spent,
        // said once, and the rows stay put.
        if (refuses?.Invoke() == true)
        {
            dragSourceIndex = -1;
            refused?.Invoke();
            return;
        }

        // Threshold crossed: enter live-reorder mode.
        isDragging = true;
        (reorderFrom, blockLength, originalSelection) = GatherBlock(
            dragSourceIndex,
            pressSelection
        );
        reorderCurrent = reorderFrom;
        dragSourceIndex = -1;
        SelectBlock(reorderFrom);
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

    /// <summary>
    /// Works out what is being dragged. A press on a row inside a
    /// multi-selection drags the whole selection: the selected rows are
    /// gathered (in the Items collection only) into one contiguous block
    /// around the pressed row, relative order kept. Anything else drags
    /// just the pressed row.
    /// </summary>
    private (int Start, int Length, int[] Original) GatherBlock(int pressed, int[] selected)
    {
        if (
            list.SelectionMode == SelectionMode.Single
            || selected.Length <= 1
            || !selected.Contains(pressed)
        )
            return (pressed, 1, [pressed]);

        // The block starts where the pressed row lands once every selected
        // row above it collapses onto it.
        var start = pressed - selected.Count(i => i < pressed);
        var items = selected.Select(i => list.Items[i]).ToArray();
        for (var i = selected.Length - 1; i >= 0; i--)
            list.Items.RemoveAt(selected[i]);
        for (var i = 0; i < items.Length; i++)
            list.Items.Insert(start + i, items[i]);
        list.UpdateLayout();

        return (start, items.Length, selected);
    }

    private int[] SelectedIndices()
    {
        if (list.SelectionMode == SelectionMode.Single)
            return list.SelectedIndex >= 0 ? [list.SelectedIndex] : [];

        var indices = new List<int>();
        foreach (var item in list.SelectedItems)
            indices.Add(list.Items.IndexOf(item));
        indices.Sort();
        return [.. indices];
    }

    private void SelectBlock(int start)
    {
        if (blockLength == 1 || list.SelectionMode == SelectionMode.Single)
        {
            list.SelectedIndex = start;
            return;
        }

        list.SelectedItems.Clear();
        for (var i = 0; i < blockLength; i++)
            list.SelectedItems.Add(list.Items[start + i]);
    }

    /// <summary>Snapshots the grabbed block into a floating adorner and turns
    /// the in-list rows into translucent placeholders.</summary>
    private void StartVisuals(int start)
    {
        if (list.ItemContainerGenerator.ContainerFromIndex(start) is not ListBoxItem first)
            return;

        rowHeight = first.ActualHeight;
        blockHeight = rowHeight * blockLength;
        var blockTop = first.TranslatePoint(new Point(0, 0), list).Y;
        // Gathering may have shifted the pressed row; clamp so the block
        // still hangs from a point under the cursor.
        grabOffsetY = Math.Clamp(dragStart.Y - blockTop, 0, blockHeight);

        // Snapshot before ghosting, so the floating copy is full-strength.
        // Rendering a row directly would include its layout offset inside
        // the list — the content lands below a row-sized bitmap and comes
        // out blank — so paint each through a VisualBrush into a fresh
        // visual, stacked at the origin.
        var width = Math.Max(1, (int)Math.Ceiling(first.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(blockHeight));
        var blockAtOrigin = new DrawingVisual();
        using (var ctx = blockAtOrigin.RenderOpen())
        {
            for (var i = 0; i < blockLength; i++)
            {
                if (list.ItemContainerGenerator.ContainerFromIndex(start + i) is ListBoxItem row)
                    ctx.DrawRectangle(
                        new VisualBrush(row),
                        null,
                        new Rect(0, i * rowHeight, row.ActualWidth, rowHeight)
                    );
            }
        }
        var snapshot = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        snapshot.Render(blockAtOrigin);

        adorner = new RowDragAdorner(
            list,
            snapshot,
            new Size(first.ActualWidth, blockHeight)
        ).AsFloating();
        AdornerLayer.GetAdornerLayer(list)?.Add(adorner);
        adorner.MoveTo(blockTop);

        GhostBlock(start);
    }

    private void GhostBlock(int start)
    {
        for (var i = 0; i < blockLength; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(start + i) is ListBoxItem row)
                row.Opacity = GhostOpacity;
        }
    }

    /// <summary>Moves the placeholder block to a new slot; the rows it
    /// displaces slide one block with a short ease-out (FLIP: start at the
    /// old offset, animate to zero).</summary>
    private void MoveGhost(int from, int to)
    {
        var moving = new object[blockLength];
        for (var i = 0; i < blockLength; i++)
            moving[i] = list.Items[from + i];
        for (var i = 0; i < blockLength; i++)
            list.Items.RemoveAt(from);
        for (var i = 0; i < blockLength; i++)
            list.Items.Insert(to + i, moving[i]);
        reorderCurrent = to;
        SelectBlock(to);
        list.UpdateLayout();

        // Containers are recreated on insert — re-ghost the placeholders.
        GhostBlock(to);

        var (lo, hi, fromOffset) =
            to > from
                ? (from, to - 1, blockHeight) // block went down; these rows slid up
                : (to + blockLength, from + blockLength - 1, -blockHeight); // block went up

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
        var sources = originalSelection;
        reorderFrom = reorderCurrent = -1;
        originalSelection = [];

        // Runs once the floating block is gone: swap the model/list back in.
        void Land()
        {
            isSettling = false;
            blockLength = 1;
            if (adorner is not null)
            {
                AdornerLayer.GetAdornerLayer(list)?.Remove(adorner);
                adorner = null;
            }

            // A gathered scatter-selection is a reorder even if the block
            // didn't move afterwards — commit whenever the final layout
            // differs from the model, i.e. unless a single unmoved row.
            var moved = from != to || sources.Length > 1;
            if (commitDrop && from >= 0 && to >= 0 && moved)
                commit(sources, to);
            else
                cancel(sources);
        }

        // On a drop, glide the floating block into its slot before landing;
        // it covers the placeholders exactly, so the swap is seamless. Esc
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
    /// Target slot for the block's first row, from pure grid arithmetic
    /// anchored to the placeholder (which never animates). Hit-testing
    /// visual bounds here would see mid-slide rows still overlapping the
    /// cursor and flip-flop forever — rows are uniform height, so the math
    /// is exact. Uses the floating block's top edge so a tall block tracks
    /// its own position, not the cursor's row.
    /// </summary>
    private int TargetAt(Point point)
    {
        var count = list.Items.Count;
        if (count == 0 || rowHeight <= 0 || reorderCurrent < 0)
            return -1;

        if (list.ItemContainerGenerator.ContainerFromIndex(reorderCurrent) is not ListBoxItem ghost)
            return -1;

        var firstTop = ghost.TranslatePoint(new Point(0, 0), list).Y - reorderCurrent * rowHeight;
        var target = (int)Math.Round((point.Y - grabOffsetY - firstTop) / rowHeight);
        return Math.Clamp(target, 0, count - blockLength);
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
