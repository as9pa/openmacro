using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Hardcodet.Wpf.TaskbarNotification;
using OpenMacro.Engine;
using SharpHook.Data;
// System.Windows.Input has its own MouseButton; macro steps use SharpHook's.
using MouseButton = SharpHook.Data.MouseButton;

namespace OpenMacro.App;

public partial class MainWindow : Window
{
    private static readonly Brush SubtleText = new SolidColorBrush(Color.FromRgb(0xA3, 0x9B, 0x8E));
    private static readonly Brush BoundKeyBrush = new SolidColorBrush(
        Color.FromRgb(0x3B, 0x32, 0x26)
    );

    private readonly HookService hooks = new();
    private readonly List<Binding> bindings;
    private readonly Dictionary<KeyCode, Button> keyButtons = [];

    private Brush? defaultKeyBrush;
    private TaskbarIcon? tray;
    private MenuItem? trayArmItem;

    // Binding index the "Record steps" recording appends into; -1 when idle.
    private int recordTargetIndex = -1;

    // Live drag-to-reorder, one per list (see ListReorder for the visuals).
    private readonly ListReorder eventsReorder;
    private readonly ListReorder bindingsReorder;

    // UI events also fire when we rebuild controls in code; this guard keeps
    // those programmatic changes from being treated as user edits.
    private bool refreshing;

    public MainWindow()
    {
        InitializeComponent();
        bindings = ConfigStore.Load()?.ToList() ?? [];
        BuildKeyboard();
        SetupTray();

        // Both lists reorder by live drag: the timeline steps and the
        // bindings sidebar share the same machinery.
        eventsReorder = new ListReorder(
            EventsList,
            // Never start a drag from inside an inline editor.
            blocksDrag: Rows.IsWithin<TextBox>,
            commit: (from, to) =>
            {
                // The list already shows the final order; commit it to the model.
                ReplaceEvents(events =>
                {
                    var step = events[from];
                    events.RemoveAt(from);
                    events.Insert(to, step);
                });
                EventsList.SelectedIndex = to;
            },
            cancel: from =>
            {
                // Rebuild to restore the model's order and clear ghosting.
                RefreshDetail();
                EventsList.SelectedIndex = from;
            }
        );

        bindingsReorder = new ListReorder(
            BindingsList,
            // A press on the checkbox is a toggle, not a grab.
            blocksDrag: Rows.IsWithin<CheckBox>,
            commit: (from, to) =>
            {
                var moved = bindings[from];
                bindings.RemoveAt(from);
                bindings.Insert(to, moved);
                SaveAndRearm();
                RefreshBindingsList(to);
                RefreshDetail();
            },
            cancel: from =>
            {
                RefreshBindingsList(from);
                RefreshDetail();
            }
        );

        // Recording skips clicks on our own window (they operate the
        // recorder — e.g. pressing Stop — not the macro). The rect is cached
        // because the hook thread can't read WPF properties.
        LocationChanged += (_, _) => CacheWindowRect();
        SizeChanged += (_, _) => CacheWindowRect();
        Loaded += (_, _) => CacheWindowRect();
        hooks.IsOwnWindowPoint = (x, y) =>
        {
            var r = windowScreenRect;
            return x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom;
        };

        RefreshBindingsList(bindings.Count > 0 ? 0 : -1);
        RefreshDetail();
    }

    private sealed record ScreenRect(double Left, double Top, double Right, double Bottom);

    private ScreenRect windowScreenRect = new(0, 0, 0, 0);

    private void CacheWindowRect()
    {
        // Hook coordinates are physical pixels; WPF's are DIPs.
        var dpi = VisualTreeHelper.GetDpi(this);
        windowScreenRect = new ScreenRect(
            Left * dpi.DpiScaleX,
            Top * dpi.DpiScaleY,
            (Left + ActualWidth) * dpi.DpiScaleX,
            (Top + ActualHeight) * dpi.DpiScaleY
        );
    }

    private int Selected => BindingsList.SelectedIndex;

    // ---- arming ----

    private async void ArmToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (trayArmItem is not null)
            trayArmItem.IsChecked = true;
        await RearmAsync();
    }

    private async void ArmToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (trayArmItem is not null)
            trayArmItem.IsChecked = false;
        await hooks.DisarmAsync();
        Status("disabled — nothing is watching the keyboard");
    }

    private async Task RearmAsync()
    {
        // Only enabled bindings with a real trigger; first binding wins a
        // duplicate trigger (possible via hand-edited config).
        var armable = bindings
            .Where(b => b.Enabled && b.Trigger != KeyCode.VcUndefined)
            .DistinctBy(b => b.Trigger)
            .ToArray();

        await hooks.ArmAsync(armable);
        Status($"enabled — {armable.Length} macro(s) live");
    }

    private void SaveAndRearm()
    {
        ConfigStore.Save(bindings);
        if (ArmToggle.IsChecked == true)
            _ = RearmAsync();
    }

    // ---- new macro ----

    private void AddMacro_Click(object sender, RoutedEventArgs e)
    {
        // Starts trigger-less and disabled; record or insert steps next.
        bindings.Add(
            new Binding(
                KeyCode.VcUndefined,
                new Macro("new macro", []),
                PlaybackMode.Once,
                Enabled: false
            )
        );
        ConfigStore.Save(bindings);
        RefreshBindingsList(bindings.Count - 1);
        RefreshDetail();
        NameBox.Focus();
        NameBox.SelectAll();
        Status("new macro — name it, set a trigger, add steps");
    }

    // ---- trigger capture ----

    private void TriggerButton_Click(object sender, RoutedEventArgs e) => BeginTriggerCapture();

    private void BeginTriggerCapture()
    {
        if (Selected < 0)
            return;

        Status("press a key for the trigger — Esc unassigns");
        hooks.CaptureNextKey(key => Dispatcher.Invoke(() => TriggerCaptured(key)));
    }

    private void TriggerCaptured(KeyCode key)
    {
        var i = Selected;
        if (i < 0)
            return;

        // Esc is reserved as "unassign", so it can never be a trigger itself.
        if (key == KeyCode.VcEscape)
        {
            bindings[i] = bindings[i] with { Trigger = KeyCode.VcUndefined, Enabled = false };
            SaveAndRearm();
            RefreshBindingsList(i);
            RefreshDetail();
            Status("trigger unassigned");
            return;
        }

        var taken = bindings.Where((b, idx) => idx != i && b.Trigger == key).Any();
        if (taken)
        {
            Status($"{KeyName(key)} is already a trigger for another binding");
            return;
        }

        bindings[i] = bindings[i] with { Trigger = key, Enabled = true };
        SaveAndRearm();
        RefreshBindingsList(i);
        RefreshDetail();
        Status($"trigger set: {KeyName(key)}");
    }

    // ---- binding edits ----

    private void BindingsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Mid-drag selection changes track the placeholder, not the user —
        // the detail panel must keep showing the grabbed binding until the
        // drop commits the model.
        if (!refreshing && bindingsReorder is not { IsReordering: true })
            RefreshDetail();
    }

    private void NameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var i = Selected;
        if (refreshing || i < 0 || NameBox.Text == bindings[i].Macro.Name)
            return;

        bindings[i] = bindings[i] with { Macro = bindings[i].Macro with { Name = NameBox.Text } };
        SaveAndRearm();
        RefreshBindingsList(i);
    }

    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var i = Selected;
        if (refreshing || i < 0 || ModeBox.SelectedIndex < 0)
            return;

        bindings[i] = bindings[i] with { Mode = (PlaybackMode)ModeBox.SelectedIndex };
        SaveAndRearm();
        RefreshBindingsList(i);
    }

    private void EnabledChanged(object sender, RoutedEventArgs e)
    {
        if (refreshing || sender is not CheckBox { Tag: int i })
            return;

        bindings[i] = bindings[i] with { Enabled = ((CheckBox)sender).IsChecked == true };
        SaveAndRearm();
    }

    private void BindingsList_RightClick(object sender, MouseButtonEventArgs e)
    {
        var i = Rows.IndexUnderMouse(BindingsList, e.GetPosition(BindingsList));
        if (i < 0)
        {
            BindingsList.ContextMenu = null;
            return;
        }

        BindingsList.SelectedIndex = i;

        var menu = new ContextMenu();
        menu.Items.Add(MenuItemFor("Run now", RunSelectedBindingOnce));
        menu.Items.Add(new Separator());
        menu.Items.Add(
            MenuItemFor(
                "Rename",
                () =>
                {
                    NameBox.Focus();
                    NameBox.SelectAll();
                }
            )
        );
        menu.Items.Add(MenuItemFor("Change trigger…", BeginTriggerCapture));
        menu.Items.Add(MenuItemFor("Duplicate", DuplicateSelectedBinding));
        menu.Items.Add(MenuItemFor("Delete", DeleteSelectedBinding));
        menu.Items.Add(new Separator());
        menu.Items.Add(
            MenuItemFor(
                "Open config file location",
                () =>
                    System.Diagnostics.Process.Start(
                        "explorer.exe",
                        $"/select,\"{ConfigStore.DefaultPath}\""
                    )
            )
        );
        BindingsList.ContextMenu = menu;
    }

    private void BindingsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Selected >= 0 && Keyboard.FocusedElement is not TextBox)
        {
            DeleteSelectedBinding();
            e.Handled = true;
        }
    }

    private void BindingsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // The checkbox already toggled on the clicks themselves — don't
        // toggle a third time.
        if (Rows.IsWithin<CheckBox>(e.OriginalSource))
            return;

        var i = Rows.IndexUnderMouse(BindingsList, e.GetPosition(BindingsList));
        if (i < 0)
            return;

        bindings[i] = bindings[i] with { Enabled = !bindings[i].Enabled };
        SaveAndRearm();
        RefreshBindingsList(i);
        Status(
            bindings[i].Enabled
                ? $"{bindings[i].Macro.Name}: enabled"
                : $"{bindings[i].Macro.Name}: disabled"
        );
    }

    private async void RunSelectedBindingOnce()
    {
        var i = Selected;
        if (i < 0)
            return;

        // Fires the macro immediately, without arming or touching the hook —
        // the fast way to test edits. Output goes wherever focus is.
        var binding = bindings[i];
        Status($"running {binding.Macro.Name}…");
        await hooks.RunMacroOnceAsync(binding.Macro);
        Status($"{binding.Macro.Name}: done");
    }

    private void DuplicateSelectedBinding()
    {
        var i = Selected;
        if (i < 0)
            return;

        // The copy starts trigger-less and disabled so two bindings never
        // contend for one key.
        var copy = bindings[i] with
        {
            Trigger = KeyCode.VcUndefined,
            Enabled = false,
            Macro = bindings[i].Macro with { Name = $"{bindings[i].Macro.Name} (copy)" },
        };

        bindings.Insert(i + 1, copy);
        ConfigStore.Save(bindings);
        RefreshBindingsList(i + 1);
        RefreshDetail();
        Status("duplicated — set a trigger to arm the copy");
    }

    private void DeleteSelectedBinding()
    {
        var i = Selected;
        if (i < 0)
            return;

        bindings.RemoveAt(i);
        SaveAndRearm();
        RefreshBindingsList(Math.Min(i, bindings.Count - 1));
        RefreshDetail();
    }

    private static MenuItem MenuItemFor(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    // ---- timeline edits (double-click edits in place, drag reorders,
    //      Delete key deletes, right-click for the rest) ----

    private void EventsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var at = Rows.IndexUnderMouse(EventsList, e.GetPosition(EventsList));
        if (at < 0)
            return;

        EventsList.SelectedIndex = at;
        BeginEditSelectedStep();
    }

    private void BeginEditSelectedStep()
    {
        // "Engine." qualification is required: bare KeyDownEvent/KeyUpEvent in
        // a pattern resolve to UIElement's inherited RoutedEvent fields.
        switch (SelectedEvent())
        {
            case DelayEvent d:
                BeginInlineEdit(
                    d.Milliseconds.ToString(),
                    text => int.TryParse(text, out var ms) && ms >= 1 ? new DelayEvent(ms) : null
                );
                break;

            case TextEvent t:
                BeginInlineEdit(t.Text, text => text.Length > 0 ? new TextEvent(text) : null);
                break;

            case Engine.KeyDownEvent
            or Engine.KeyUpEvent:
                var isDown = SelectedEvent() is Engine.KeyDownEvent;
                Status("press the new key for this step — Esc cancels");
                hooks.CaptureNextKey(key =>
                    Dispatcher.Invoke(() =>
                    {
                        if (key == KeyCode.VcEscape)
                        {
                            Status("cancelled");
                            return;
                        }

                        if (SelectedEvent() is not (Engine.KeyDownEvent or Engine.KeyUpEvent))
                            return;

                        ReplaceEvents(events =>
                            events[EventsList.SelectedIndex] = isDown
                                ? new KeyDownEvent(key)
                                : new KeyUpEvent(key)
                        );
                        Status($"step now {(isDown ? "presses" : "releases")} {KeyName(key)}");
                    })
                );
                break;
        }
    }

    /// <summary>Swaps only the row's value text (the "500" in "wait 500 ms")
    /// for a TextBox sitting in its place, sized to the value and growing as
    /// you type. Enter/focus-loss commits (via <paramref name="commit"/>,
    /// null = invalid), Esc cancels.</summary>
    private void BeginInlineEdit(string initial, Func<string, MacroEvent?> commit)
    {
        var at = EventsList.SelectedIndex;
        if (
            at < 0
            || EventsList.ItemContainerGenerator.ContainerFromIndex(at) is not ListBoxItem container
            || container.Content is not Grid row
        )
            return;

        var parts = row.Children.OfType<StackPanel>().First();
        var valueText = parts.Children.OfType<TextBlock>().First(t => Equals(t.Tag, ValueTag));

        var box = new TextBox
        {
            Text = initial,
            // Sits where the value was; negative margins absorb the box's own
            // border+padding so the text doesn't jump when editing starts.
            MinWidth = Math.Max(36, valueText.ActualWidth + 14),
            Padding = new Thickness(3, 0, 3, 0),
            Margin = new Thickness(-4, -3, 0, -3),
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        var slot = parts.Children.IndexOf(valueText);
        valueText.Visibility = Visibility.Collapsed;
        parts.Children.Insert(slot, box);

        var done = false;
        void Finish(bool apply)
        {
            if (done)
                return;
            done = true;

            if (apply && commit(box.Text) is { } step)
            {
                ReplaceEvents(events => events[at] = step); // refreshes the row
            }
            else
            {
                parts.Children.Remove(box);
                valueText.Visibility = Visibility.Visible;
                EventsList.SelectedIndex = at;
            }
        }

        box.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter)
                Finish(true);
            else if (ke.Key == Key.Escape)
                Finish(false);
        };
        box.LostFocus += (_, _) => Finish(true);

        box.SelectAll();
        box.Focus();
    }

    private void EventsList_RightClick(object sender, MouseButtonEventArgs e)
    {
        var at = Rows.IndexUnderMouse(EventsList, e.GetPosition(EventsList));

        // Empty space is for inserting; rows are for editing.
        if (at < 0)
        {
            EventsList.ContextMenu = BuildInsertMenu();
            return;
        }

        // A right-click inside the current multi-selection keeps it; anywhere
        // else selects just the clicked row.
        if (!SelectedEventIndices().Contains(at))
            EventsList.SelectedIndex = at;

        var selected = SelectedEventIndices();
        var menu = new ContextMenu();

        if (selected.Count == 1)
        {
            menu.Items.Add(MenuItemFor("Edit…", BeginEditSelectedStep));
            switch (SelectedEvent())
            {
                case Engine.KeyDownEvent kd:
                    menu.Items.Add(
                        MenuItemFor(
                            "Make release",
                            () => ReplaceSelectedStep(new KeyUpEvent(kd.Key))
                        )
                    );
                    break;
                case Engine.KeyUpEvent ku:
                    menu.Items.Add(
                        MenuItemFor(
                            "Make press",
                            () => ReplaceSelectedStep(new KeyDownEvent(ku.Key))
                        )
                    );
                    break;
                case MouseDownEvent md:
                    menu.Items.Add(
                        MenuItemFor(
                            "Make release",
                            () => ReplaceSelectedStep(new MouseUpEvent(md.Button))
                        )
                    );
                    break;
                case MouseUpEvent mu:
                    menu.Items.Add(
                        MenuItemFor(
                            "Make press",
                            () => ReplaceSelectedStep(new MouseDownEvent(mu.Button))
                        )
                    );
                    break;
            }

            menu.Items.Add(new Separator());
        }

        menu.Items.Add(
            MenuItemFor(
                selected.Count > 1 ? $"Delete {selected.Count} steps" : "Delete step",
                DeleteSelectedEvents
            )
        );
        EventsList.ContextMenu = menu;
    }

    private ContextMenu BuildInsertMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItemFor("Insert key press", InsertKeyViaCapture));
        menu.Items.Add(MenuItemFor("Insert delay", () => InsertThenEdit(new DelayEvent(100))));
        menu.Items.Add(MenuItemFor("Insert text", () => InsertThenEdit(new TextEvent("text"))));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor("Insert left click", () => InsertClick(MouseButton.Button1)));
        menu.Items.Add(MenuItemFor("Insert right click", () => InsertClick(MouseButton.Button2)));
        menu.Items.Add(MenuItemFor("Insert middle click", () => InsertClick(MouseButton.Button3)));
        return menu;
    }

    /// <summary>Appends a step and immediately opens its value for editing.</summary>
    private void InsertThenEdit(MacroEvent step)
    {
        EventsList.SelectedIndex = -1; // empty-space insert goes to the end
        InsertEvents(step);
        // The new row's container doesn't exist until after layout.
        Dispatcher.InvokeAsync(
            BeginEditSelectedStep,
            System.Windows.Threading.DispatcherPriority.Background
        );
    }

    private void InsertClick(MouseButton button)
    {
        EventsList.SelectedIndex = -1;
        InsertEvents(new MouseDownEvent(button), new DelayEvent(30), new MouseUpEvent(button));
    }

    private void ReplaceSelectedStep(MacroEvent step) =>
        ReplaceEvents(events => events[EventsList.SelectedIndex] = step);

    private List<int> SelectedEventIndices()
    {
        var indices = new List<int>();
        foreach (var item in EventsList.SelectedItems)
            indices.Add(EventsList.Items.IndexOf(item));
        indices.Sort();
        return indices;
    }

    private void EventsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (
            e.Key == Key.Delete
            && EventsList.SelectedItems.Count > 0
            && Keyboard.FocusedElement is not TextBox
        )
        {
            DeleteSelectedEvents();
            e.Handled = true;
        }
    }

    private void DeleteSelectedEvents()
    {
        var selected = SelectedEventIndices();
        if (selected.Count == 0)
            return;

        ReplaceEvents(events =>
        {
            for (var j = selected.Count - 1; j >= 0; j--)
                events.RemoveAt(selected[j]);
        });
        EventsList.SelectedIndex = Math.Min(selected[0], EventsList.Items.Count - 1);
    }

    private void AddKey_Click(object sender, RoutedEventArgs e) => InsertKeyViaCapture();

    private void InsertKeyViaCapture()
    {
        if (Selected < 0)
            return;

        Status("press the key to insert as press+release — Esc cancels");
        hooks.CaptureNextKey(key =>
            Dispatcher.Invoke(() =>
            {
                if (key == KeyCode.VcEscape)
                {
                    Status("cancelled");
                    return;
                }

                InsertEvents(new KeyDownEvent(key), new DelayEvent(30), new KeyUpEvent(key));
                Status($"inserted press + release of {KeyName(key)}");
            })
        );
    }

    private void AddDelay_Click(object sender, RoutedEventArgs e) =>
        InsertEvents(new DelayEvent(100));

    private void AddText_Click(object sender, RoutedEventArgs e)
    {
        if (Selected < 0 || AddTextBox.Text.Length == 0)
            return;

        InsertEvents(new TextEvent(AddTextBox.Text));
        AddTextBox.Clear();
    }

    /// <summary>Inserts after the selected step, or at the end if none is selected.</summary>
    private void InsertEvents(params MacroEvent[] steps)
    {
        var i = Selected;
        if (i < 0 || steps.Length == 0)
            return;

        var at =
            EventsList.SelectedIndex >= 0
                ? EventsList.SelectedIndex + 1
                : bindings[i].Macro.Events.Count;

        ReplaceEventsAt(i, events => events.InsertRange(at, steps));
        EventsList.SelectedIndex = Math.Min(at + steps.Length - 1, EventsList.Items.Count - 1);
    }

    // ---- append recording into an existing macro ----

    private void AppendRecord_Click(object sender, RoutedEventArgs e)
    {
        if (recordTargetIndex < 0)
        {
            if (Selected < 0 || hooks.IsRecording)
                return;

            // Remember the target now: selection may change while recording.
            recordTargetIndex = Selected;
            hooks.StartRecording();
            AppendRecordButton.Content = "Stop";
            Status("recording steps into this macro — input passes through");
            return;
        }

        var recorded = hooks.StopRecording("steps");
        var target = recordTargetIndex;
        recordTargetIndex = -1;
        AppendRecordButton.Content = "Record steps";

        if (recorded.Events.Count == 0)
        {
            Status("nothing recorded");
            return;
        }

        ReplaceEventsAt(target, events => events.AddRange(recorded.Events));
        Status($"added {recorded.Events.Count} recorded steps");
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveEvent(-1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveEvent(+1);

    private void MoveEvent(int direction)
    {
        var from = EventsList.SelectedIndex;
        var to = from + direction;
        if (SelectedEvent() is null || to < 0 || to >= EventsList.Items.Count)
            return;

        ReplaceEvents(events => (events[from], events[to]) = (events[to], events[from]));
        EventsList.SelectedIndex = to;
    }

    private void DeleteEvent_Click(object sender, RoutedEventArgs e)
    {
        var at = EventsList.SelectedIndex;
        if (SelectedEvent() is null)
            return;

        ReplaceEvents(events => events.RemoveAt(at));
        EventsList.SelectedIndex = Math.Min(at, EventsList.Items.Count - 1);
    }

    private MacroEvent? SelectedEvent()
    {
        var i = Selected;
        var at = EventsList.SelectedIndex;
        return i >= 0 && at >= 0 ? bindings[i].Macro.Events[at] : null;
    }

    /// <summary>Mutates the selected binding's steps, preserving step selection.</summary>
    private void ReplaceEvents(Action<List<MacroEvent>> mutate)
    {
        var keepEventSelection = EventsList.SelectedIndex;
        ReplaceEventsAt(Selected, mutate);
        EventsList.SelectedIndex = Math.Min(keepEventSelection, EventsList.Items.Count - 1);
    }

    /// <summary>
    /// Mutates a specific binding's steps — the target is an index, not the
    /// selection, so "Record steps" still lands in the right macro if the
    /// selection changed while recording.
    /// </summary>
    private void ReplaceEventsAt(int i, Action<List<MacroEvent>> mutate)
    {
        if (i < 0 || i >= bindings.Count)
            return;

        var events = bindings[i].Macro.Events.ToList();
        mutate(events);
        bindings[i] = bindings[i] with { Macro = bindings[i].Macro with { Events = events } };

        SaveAndRearm();
        RefreshBindingsList(Selected); // keeps the "N events" label current
        RefreshDetail();
    }

    // ---- refresh ----

    private void RefreshBindingsList(int select)
    {
        refreshing = true;
        BindingsList.Items.Clear();

        for (var i = 0; i < bindings.Count; i++)
        {
            var b = bindings[i];

            var check = new CheckBox
            {
                IsChecked = b.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = i,
            };
            check.Checked += EnabledChanged;
            check.Unchecked += EnabledChanged;

            var labels = new StackPanel { Margin = new Thickness(8, 2, 0, 2) };
            labels.Children.Add(
                new TextBlock { Text = b.Macro.Name, FontWeight = FontWeights.SemiBold }
            );
            labels.Children.Add(
                new TextBlock
                {
                    Text = $"{TriggerLabel(b)} · {ModeLabel(b.Mode)}",
                    Foreground = SubtleText,
                    FontSize = 11,
                }
            );

            var row = new DockPanel { Margin = new Thickness(4, 2, 4, 2) };
            DockPanel.SetDock(check, Dock.Left);
            row.Children.Add(check);
            row.Children.Add(labels);

            BindingsList.Items.Add(new ListBoxItem { Content = row });
        }

        BindingsList.SelectedIndex = select;
        refreshing = false;
        RefreshKeyboard();
    }

    // ---- visual keyboard ----

    private void BuildKeyboard()
    {
        foreach (var row in KeyboardLayout.Rows)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var key in row)
            {
                var button = new Button
                {
                    Content = key.Label,
                    Tag = key.Code,
                    Width = key.Width * 38 - 3,
                    Height = 34,
                    Margin = new Thickness(1.5),
                    Padding = new Thickness(0),
                    FontSize = 11,
                };
                button.Click += KeyboardKey_Click;
                defaultKeyBrush ??= button.Background;
                keyButtons[key.Code] = button;
                panel.Children.Add(button);
            }

            KeyboardHost.Children.Add(panel);
        }
    }

    private void RefreshKeyboard()
    {
        foreach (var (code, button) in keyButtons)
        {
            var bound = bindings.FirstOrDefault(b => b.Trigger == code);
            button.Background = bound is null ? defaultKeyBrush : BoundKeyBrush;
            button.FontWeight = bound is null ? FontWeights.Normal : FontWeights.SemiBold;
            button.ToolTip = bound is null ? null : $"{bound.Macro.Name} · {ModeLabel(bound.Mode)}";
        }
    }

    private void KeyboardKey_Click(object sender, RoutedEventArgs e)
    {
        var key = (KeyCode)((Button)sender).Tag;
        var boundIndex = bindings.FindIndex(b => b.Trigger == key);

        // A bound key selects its binding; a free key becomes the selected
        // binding's trigger.
        if (boundIndex >= 0)
        {
            BindingsList.SelectedIndex = boundIndex;
            Status($"{KeyName(key)} → {bindings[boundIndex].Macro.Name}");
            return;
        }

        if (Selected < 0)
        {
            Status("select a binding first, then click a key to set its trigger");
            return;
        }

        TriggerCaptured(key);
    }

    // ---- tray ----

    private void SetupTray()
    {
        var show = new MenuItem { Header = "Show window" };
        show.Click += (_, _) => RestoreFromTray();

        trayArmItem = new MenuItem { Header = "Enable macros", IsCheckable = true };
        trayArmItem.Click += (_, _) => ArmToggle.IsChecked = trayArmItem.IsChecked;

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => Close();

        var menu = new ContextMenu();
        menu.Items.Add(show);
        menu.Items.Add(trayArmItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);

        tray = new TaskbarIcon
        {
            ToolTipText = "openmacro",
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenu = menu,
        };
        tray.TrayLeftMouseUp += (_, _) => RestoreFromTray();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        // Minimize means "get out of the way, keep macros live": hide from
        // the taskbar and live in the tray. Closing the window exits fully.
        if (WindowState == WindowState.Minimized)
            Hide();

        base.OnStateChanged(e);
    }

    private void RefreshDetail()
    {
        var i = Selected;
        if (i < 0)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            return;
        }

        refreshing = true;
        DetailPanel.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;

        var b = bindings[i];
        NameBox.Text = b.Macro.Name;
        TriggerButton.Content = $"Trigger: {TriggerLabel(b)}";
        ModeBox.SelectedIndex = (int)b.Mode;

        EventsList.Items.Clear();
        foreach (var macroEvent in b.Macro.Events)
            EventsList.Items.Add(new ListBoxItem { Content = BuildStepRow(macroEvent) });

        refreshing = false;
    }

    // Marks the editable part of a step row (see BeginInlineEdit).
    private const string ValueTag = "value";

    /// <summary>A timeline row: muted verb in a fixed column, then the value
    /// (tagged so inline edit can swap just that part) with muted quotes/units
    /// around it.</summary>
    private Grid BuildStepRow(MacroEvent macroEvent)
    {
        var (verb, prefix, value, suffix) = DescribeParts(macroEvent);

        var parts = new StackPanel { Orientation = Orientation.Horizontal };
        if (prefix.Length > 0)
            parts.Children.Add(new TextBlock { Text = prefix, Foreground = SubtleText });
        parts.Children.Add(new TextBlock { Text = value, Tag = ValueTag });
        if (suffix.Length > 0)
            parts.Children.Add(new TextBlock { Text = suffix, Foreground = SubtleText });

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var verbText = new TextBlock { Text = verb, Foreground = SubtleText };
        Grid.SetColumn(parts, 1);
        row.Children.Add(verbText);
        row.Children.Add(parts);
        return row;
    }

    private static (string Verb, string Prefix, string Value, string Suffix) DescribeParts(
        MacroEvent e
    ) =>
        e switch
        {
            Engine.KeyDownEvent k => ("press", "", KeyName(k.Key), ""),
            Engine.KeyUpEvent k => ("release", "", KeyName(k.Key), ""),
            MouseDownEvent m => ("press", "", MouseName(m.Button), ""),
            MouseUpEvent m => ("release", "", MouseName(m.Button), ""),
            DelayEvent d => ("wait", "", d.Milliseconds.ToString(), " ms"),
            TextEvent t => ("type", "“", t.Text, "”"),
            _ => ("?", "", e.ToString() ?? "", ""),
        };

    private static string MouseName(MouseButton button) =>
        button switch
        {
            MouseButton.Button1 => "left mouse",
            MouseButton.Button2 => "right mouse",
            MouseButton.Button3 => "middle mouse",
            MouseButton.Button4 => "mouse 4",
            MouseButton.Button5 => "mouse 5",
            _ => button.ToString(),
        };

    private void Status(string message)
    {
        StatusText.Text = message;
        UpdateLiveIndicators();
    }

    /// <summary>The theme's "LED": the rule under the top bar and the status
    /// dot go amber while armed, red while recording, off when idle. Every
    /// state change routes through <see cref="Status"/>, so this stays true.</summary>
    private void UpdateLiveIndicators()
    {
        var (dot, rule) =
            hooks.IsRecording ? ("DangerBrush", "DangerBrush")
            : ArmToggle.IsChecked == true ? ("AccentBrush", "AccentBrush")
            : ("InkFaintBrush", null);

        StatusDot.Fill = (Brush)FindResource(dot);
        LiveRule.Fill = rule is null ? Brushes.Transparent : (Brush)FindResource(rule);
    }

    // ---- dark title bar ----

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint hwnd,
        int attribute,
        ref int value,
        int size
    );

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Ask DWM for a dark caption and paint it to match the top bar; both
        // are best-effort (older Windows just keeps the default title bar).
        var hwnd = new WindowInteropHelper(this).Handle;
        var dark = 1;
        _ = DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        var caption = 0x00202123; // COLORREF (0x00BBGGRR) of the Bg token #232120
        _ = DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR
    }

    private static string TriggerLabel(Binding b) =>
        b.Trigger == KeyCode.VcUndefined ? "not set" : KeyName(b.Trigger);

    private static string KeyName(KeyCode key) =>
        key.ToString().StartsWith("Vc") ? key.ToString()[2..] : key.ToString();

    private static string ModeLabel(PlaybackMode mode) =>
        mode switch
        {
            PlaybackMode.Once => "once",
            PlaybackMode.WhileHeld => "while held",
            PlaybackMode.Toggle => "toggle",
            _ => mode.ToString(),
        };

    protected override void OnClosing(CancelEventArgs e)
    {
        // Uninstall the hook and hard-stop playback (releases held keys).
        // Blocking briefly here is fine — the window is closing.
        tray?.Dispose();
        hooks.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        base.OnClosing(e);
    }
}
