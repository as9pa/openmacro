using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenMacro.Engine;
using SharpHook.Data;

namespace OpenMacro.App;

public partial class MainWindow : Window
{
    private static readonly Brush SubtleText = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));

    private readonly HookService hooks = new();
    private readonly List<Binding> bindings;

    // UI events also fire when we rebuild controls in code; this guard keeps
    // those programmatic changes from being treated as user edits.
    private bool refreshing;

    public MainWindow()
    {
        InitializeComponent();
        bindings = ConfigStore.Load()?.ToList() ?? [];
        RefreshBindingsList(bindings.Count > 0 ? 0 : -1);
        RefreshDetail();
    }

    private int Selected => BindingsList.SelectedIndex;

    // ---- arming ----

    private async void ArmToggle_Checked(object sender, RoutedEventArgs e) => await RearmAsync();

    private async void ArmToggle_Unchecked(object sender, RoutedEventArgs e)
    {
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
        if (!hooks.IsRecording)
        {
            hooks.StartRecording();
            RecordButton.Content = "Stop recording";
            Status("recording — keys pass through; macro triggers are inert");
            return;
        }

        var macro = hooks.StopRecording($"recorded {DateTime.Now:HH:mm:ss}");
        RecordButton.Content = "Record";

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

        Status("press any key to use it as the trigger…");
        hooks.CaptureNextKey(key => Dispatcher.Invoke(() => TriggerCaptured(key)));
    }

    private void TriggerCaptured(KeyCode key)
    {
        var i = Selected;
        if (i < 0)
            return;

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

    private void DeleteBinding_Click(object sender, RoutedEventArgs e)
    {
        var i = Selected;
        if (i < 0)
            return;

        bindings.RemoveAt(i);
        SaveAndRearm();
        RefreshBindingsList(Math.Min(i, bindings.Count - 1));
        RefreshDetail();
    }

    // ---- timeline edits ----

    private void EventsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (refreshing)
            return;

        // Progressive disclosure: the delay editor appears only for delays.
        if (SelectedEvent() is DelayEvent d)
        {
            DelayBox.Text = d.Milliseconds.ToString();
            DelayEditor.Visibility = Visibility.Visible;
        }
        else
        {
            DelayEditor.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyDelay_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedEvent() is not DelayEvent)
            return;

        if (!int.TryParse(DelayBox.Text, out var ms) || ms < 1)
        {
            Status("delay must be a whole number ≥ 1");
            return;
        }

        ReplaceEvents(events => events[EventsList.SelectedIndex] = new DelayEvent(ms));
    }

    private void AddDelay_Click(object sender, RoutedEventArgs e) =>
        ReplaceEvents(events => events.Add(new DelayEvent(100)));

    private void AddText_Click(object sender, RoutedEventArgs e)
    {
        if (Selected < 0 || AddTextBox.Text.Length == 0)
            return;

        ReplaceEvents(events => events.Add(new TextEvent(AddTextBox.Text)));
        AddTextBox.Clear();
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

    private void ReplaceEvents(Action<List<MacroEvent>> mutate)
    {
        var i = Selected;
        if (i < 0)
            return;

        var events = bindings[i].Macro.Events.ToList();
        mutate(events);
        bindings[i] = bindings[i] with { Macro = bindings[i].Macro with { Events = events } };

        SaveAndRearm();
        var keepEventSelection = EventsList.SelectedIndex;
        RefreshDetail();
        EventsList.SelectedIndex = Math.Min(keepEventSelection, EventsList.Items.Count - 1);
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
        DelayEditor.Visibility = Visibility.Collapsed;

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
        hooks.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        base.OnClosing(e);
    }
}
