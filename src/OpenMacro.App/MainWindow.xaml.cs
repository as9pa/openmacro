using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Hardcodet.Wpf.TaskbarNotification;
using OpenMacro.Engine;
using SharpHook.Data;

namespace OpenMacro.App;

public partial class MainWindow : Window
{
    private static readonly Brush SubtleText = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
    private static readonly Brush BoundKeyBrush = new SolidColorBrush(
        Color.FromRgb(0xD5, 0xE3, 0xF2)
    );

    private readonly HookService hooks = new();
    private readonly List<Binding> bindings;
    private readonly Dictionary<KeyCode, Button> keyButtons = [];

    private Brush? defaultKeyBrush;
    private TaskbarIcon? tray;
    private MenuItem? trayArmItem;

    // Binding index the "Record steps" recording appends into; -1 when idle.
    private int recordTargetIndex = -1;

    // Drag-to-reorder state for the timeline.
    private Point dragStart;
    private int dragSourceIndex = -1;

    // UI events also fire when we rebuild controls in code; this guard keeps
    // those programmatic changes from being treated as user edits.
    private bool refreshing;

    public MainWindow()
    {
        InitializeComponent();
        bindings = ConfigStore.Load()?.ToList() ?? [];
        BuildKeyboard();
        SetupTray();
        RefreshBindingsList(bindings.Count > 0 ? 0 : -1);
        RefreshDetail();
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
        Status("disarmed — nothing is watching the keyboard");
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
        Status($"armed — {armable.Length} binding(s) live");
    }

    private void SaveAndRearm()
    {
        ConfigStore.Save(bindings);
        if (ArmToggle.IsChecked == true)
            _ = RearmAsync();
    }

    // ---- recording ----

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (recordTargetIndex >= 0)
            return; // a "Record steps" recording is in progress

        if (!hooks.IsRecording)
        {
            hooks.StartRecording();
            RecordButton.Content = "Stop recording";
            AppendRecordButton.IsEnabled = false;
            Status("recording — keys pass through; macro triggers are inert");
            return;
        }

        var macro = hooks.StopRecording($"recorded {DateTime.Now:HH:mm:ss}");
        RecordButton.Content = "Record";
        AppendRecordButton.IsEnabled = true;

        if (macro.Events.Count == 0)
        {
            Status("nothing recorded");
            return;
        }

        // No trigger yet, so it starts disabled: set a trigger to arm it.
        bindings.Add(new Binding(KeyCode.VcUndefined, macro, PlaybackMode.Once, Enabled: false));
        ConfigStore.Save(bindings);
        RefreshBindingsList(bindings.Count - 1);
        RefreshDetail();
        Status("recorded — set a trigger to arm it");
    }

    // ---- trigger capture ----

    private void TriggerButton_Click(object sender, RoutedEventArgs e)
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
        if (!refreshing)
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
        var i = IndexUnderMouse(BindingsList, e.GetPosition(BindingsList));
        if (i < 0)
        {
            BindingsList.ContextMenu = null;
            return;
        }

        BindingsList.SelectedIndex = i;

        var menu = new ContextMenu();
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
        var at = IndexUnderMouse(EventsList, e.GetPosition(EventsList));
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

    /// <summary>Swaps the selected row's label for a TextBox; Enter/focus-loss
    /// commits (via <paramref name="commit"/>, null = invalid), Esc cancels.</summary>
    private void BeginInlineEdit(string initial, Func<string, MacroEvent?> commit)
    {
        var at = EventsList.SelectedIndex;
        if (
            at < 0
            || EventsList.ItemContainerGenerator.ContainerFromIndex(at) is not ListBoxItem container
        )
            return;

        var box = new TextBox
        {
            Text = initial,
            FontFamily = new FontFamily("Consolas"),
            MinWidth = 120,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var done = false;
        void Finish(bool apply)
        {
            if (done)
                return;
            done = true;

            if (apply && commit(box.Text) is { } step)
            {
                ReplaceEvents(events => events[at] = step);
            }
            else
            {
                RefreshDetail(); // restore the plain label
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

        container.Content = box;
        box.SelectAll();
        box.Focus();
    }

    private void EventsList_RightClick(object sender, MouseButtonEventArgs e)
    {
        var at = IndexUnderMouse(EventsList, e.GetPosition(EventsList));
        if (at < 0)
        {
            EventsList.ContextMenu = null;
            return;
        }

        EventsList.SelectedIndex = at;

        var menu = new ContextMenu();
        menu.Items.Add(MenuItemFor("Edit…", BeginEditSelectedStep));
        switch (SelectedEvent())
        {
            case KeyDownEvent kd:
                menu.Items.Add(
                    MenuItemFor(
                        "Make release",
                        () =>
                            ReplaceEvents(events =>
                                events[EventsList.SelectedIndex] = new KeyUpEvent(kd.Key)
                            )
                    )
                );
                break;
            case KeyUpEvent ku:
                menu.Items.Add(
                    MenuItemFor(
                        "Make press",
                        () =>
                            ReplaceEvents(events =>
                                events[EventsList.SelectedIndex] = new KeyDownEvent(ku.Key)
                            )
                    )
                );
                break;
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor("Delete step", DeleteSelectedEvent));
        EventsList.ContextMenu = menu;
    }

    private void EventsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (
            e.Key == Key.Delete
            && EventsList.SelectedIndex >= 0
            && Keyboard.FocusedElement is not TextBox
        )
        {
            DeleteSelectedEvent();
            e.Handled = true;
        }
    }

    private void DeleteSelectedEvent()
    {
        var at = EventsList.SelectedIndex;
        if (SelectedEvent() is null)
            return;

        ReplaceEvents(events => events.RemoveAt(at));
        EventsList.SelectedIndex = Math.Min(at, EventsList.Items.Count - 1);
    }

    // ---- drag to reorder ----

    private void EventsList_MouseDown(object sender, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(EventsList);
        // Never start a drag from inside an inline editor.
        dragSourceIndex = IsInsideInlineEditor(e.OriginalSource)
            ? -1
            : IndexUnderMouse(EventsList, dragStart);
    }

    private void EventsList_MouseMove(object sender, MouseEventArgs e)
    {
        if (dragSourceIndex < 0 || e.LeftButton != MouseButtonState.Pressed)
            return;

        var position = e.GetPosition(EventsList);
        if (
            Math.Abs(position.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance
        )
            return;

        var from = dragSourceIndex;
        dragSourceIndex = -1;
        DragDrop.DoDragDrop(EventsList, from, DragDropEffects.Move);
    }

    private void EventsList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(int)) is not int from)
            return;

        var to = IndexUnderMouse(EventsList, e.GetPosition(EventsList));
        if (to < 0)
            to = EventsList.Items.Count - 1; // dropped past the end

        if (from == to || from < 0 || from >= EventsList.Items.Count)
            return;

        // The dragged step lands in the slot it was dropped on.
        ReplaceEvents(events =>
        {
            var step = events[from];
            events.RemoveAt(from);
            events.Insert(Math.Min(to, events.Count), step);
        });
        EventsList.SelectedIndex = to;
    }

    private static bool IsInsideInlineEditor(object source)
    {
        var node = source as DependencyObject;
        while (node is Visual)
        {
            if (node is TextBox)
                return true;
            node = VisualTreeHelper.GetParent(node);
        }

        return false;
    }

    private static int IndexUnderMouse(ListBox list, Point point)
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

    private void AddKey_Click(object sender, RoutedEventArgs e)
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
            RecordButton.IsEnabled = false;
            Status("recording steps into this macro — keys pass through");
            return;
        }

        var recorded = hooks.StopRecording("steps");
        var target = recordTargetIndex;
        recordTargetIndex = -1;
        AppendRecordButton.Content = "Record steps";
        RecordButton.IsEnabled = true;

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
                    Text =
                        $"{TriggerLabel(b)} · {ModeLabel(b.Mode)} · {b.Macro.Events.Count} events",
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

        trayArmItem = new MenuItem { Header = "Arm macros", IsCheckable = true };
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
            EventsList.Items.Add(Describe(macroEvent));

        refreshing = false;
    }

    private void Status(string message) => StatusText.Text = message;

    private static string Describe(MacroEvent e) =>
        e switch
        {
            KeyDownEvent k => $"press    {KeyName(k.Key)}",
            KeyUpEvent k => $"release  {KeyName(k.Key)}",
            DelayEvent d => $"wait     {d.Milliseconds} ms",
            TextEvent t => $"type     \"{t.Text}\"",
            _ => e.ToString() ?? "?",
        };

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
