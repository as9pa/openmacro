using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Hardcodet.Wpf.TaskbarNotification;
using OpenMacro.Engine;
using SharpHook.Data;
// System.Windows.Input has its own MouseButton; macro steps use SharpHook's.
using MouseButton = SharpHook.Data.MouseButton;

namespace OpenMacro.App;

public partial class MainWindow : Window
{
    private readonly HookService hooks = new();
    private readonly List<Binding> bindings;
    private readonly Dictionary<KeyCode, Button> keyButtons = [];

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
            commit: (sources, to) =>
            {
                // A reorder renumbers the rows a pending Undo record points
                // at, so the offer goes with the old order.
                CancelUndoOffer();

                // The list already shows the final order; commit it to the
                // model: pull the dragged steps out (sources are pre-drag
                // indices, ascending) and reinsert them as one block.
                ReplaceEvents(events =>
                {
                    var block = sources.Select(i => events[i]).ToList();
                    for (var i = sources.Length - 1; i >= 0; i--)
                        events.RemoveAt(sources[i]);
                    events.InsertRange(to, block);
                });
                SelectEventRange(to, sources.Length);
            },
            cancel: sources =>
            {
                CancelUndoOffer();

                // Rebuild to restore the model's order and clear ghosting.
                RefreshDetail();
                foreach (var i in sources)
                    if (i < EventsList.Items.Count)
                        EventsList.SelectedItems.Add(EventsList.Items[i]);
            }
        );

        bindingsReorder = new ListReorder(
            BindingsList,
            // A press on the checkbox is a toggle and a press on the row's
            // "Set keybind" is a click; neither is a grab.
            blocksDrag: Rows.IsWithin<ButtonBase>,
            // Single-select list: the block is always exactly one row.
            commit: (sources, to) =>
            {
                CancelUndoOffer();

                var moved = bindings[sources[0]];
                bindings.RemoveAt(sources[0]);
                bindings.Insert(to, moved);
                SaveAndRearm();
                RefreshBindingsList(to);
                RefreshDetail();
            },
            cancel: sources =>
            {
                CancelUndoOffer();
                RefreshBindingsList(sources.Length > 0 ? sources[0] : -1);
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

        settings = SettingsStore.Load();
        (armHotkeyKey, armHotkeyModifiers) = ParseHotkey(settings);
        UpdateHotkeyButton();

        // A confirmation's 3 s are up: fade it out, then bring the base line
        // back in its place. The base itself never fades away.
        statusFade.Tick += (_, _) =>
        {
            statusFade.Stop();
            FadeToBaseStatus();
        };

        // The Undo offer's 8 s are up: same ending as any other way it
        // stops being offered.
        undoExpiry.Tick += (_, _) => CancelUndoOffer();

        RefreshBindingsList(bindings.Count > 0 ? 0 : -1);
        RefreshDetail();
        RefreshBaseStatus(); // the bar opens on the base line, never blank
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

    // Set when Enable is refused (nothing to arm) so the resulting Unchecked
    // reports "no macros enabled" instead of falling back to the base line.
    private bool decliningEnable;

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

        if (decliningEnable)
        {
            decliningEnable = false;
            Status("no macros enabled", sticky: true);
        }
        else
        {
            RefreshBaseStatus(); // the base line already reads Off
        }
    }

    // Only enabled bindings with a real trigger; first binding wins a
    // duplicate keybind (the UI enforces one enabled holder per trigger, but a
    // hand-edited config can still enable two).
    private Binding[] Armable() =>
        bindings
            .Where(b => b.Enabled && b.HasTrigger)
            .DistinctBy(b => (b.Trigger, b.MouseTrigger))
            .ToArray();

    /// <summary>
    /// Index of the enabled binding (other than <paramref name="except"/>)
    /// holding this trigger — a key or a mouse button — or -1. Macros may share
    /// a keybind, but only one holder can be enabled at a time — every path
    /// that enables a binding checks this and makes you uncheck the current
    /// holder first. A trigger-less binding (VcUndefined + null) never counts.
    /// </summary>
    private int EnabledHolderOf(KeyCode trigger, MouseButton? mouseTrigger, int except)
    {
        if (trigger == KeyCode.VcUndefined && mouseTrigger is null)
            return -1;

        for (var i = 0; i < bindings.Count; i++)
            if (
                i != except
                && bindings[i].Enabled
                && bindings[i].Trigger == trigger
                && bindings[i].MouseTrigger == mouseTrigger
            )
                return i;

        return -1;
    }

    private async Task RearmAsync()
    {
        var armable = Armable();

        // Nothing to arm: refuse the enable instead of arming an empty hook.
        if (armable.Length == 0)
        {
            decliningEnable = true;
            ArmToggle.IsChecked = false; // fires Unchecked → disarm + status
            return;
        }

        await hooks.ArmAsync(armable);
        RefreshBaseStatus(); // the base line counts what just went live
    }

    private void SaveAndRearm()
    {
        ConfigStore.Save(bindings);

        // Re-arming recomputes the base itself once the hook is live, or lands
        // the "no macros enabled" refusal; either way it owns the bar from here.
        if (ArmToggle.IsChecked == true)
        {
            _ = RearmAsync();
            return;
        }

        // Nothing to re-arm, but the edit is still a user action: the base
        // line takes the bar back from a stale sticky error.
        RefreshBaseStatus();
    }

    // ---- new macro ----

    private void AddMacro_Click(object sender, RoutedEventArgs e)
    {
        CancelUndoOffer(); // a new row changes the shape the offer recorded

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
        StartNameEdit();
        Status("new macro");
    }

    // ---- capture overlay ----

    // True while the "press any key" overlay is up. Dismissal can race
    // (scrim click vs. the hook callback), so both paths check-and-clear it.
    private bool captureOverlayUp;

    /// <summary>One-shot key capture fronted by the full-window overlay: the
    /// shell blurs and an empty keycap breathes in the accent until the
    /// captured key stamps it. Esc reaches <paramref name="onKey"/> like any
    /// other key — each flow decides what it means. Clicking the scrim
    /// cancels without a key. <paramref name="onKey"/> runs on the UI thread.
    /// </summary>
    private void CaptureKeyWithOverlay(string prompt, string hint, Action<KeyCode> onKey)
    {
        CapturePrompt.Text = prompt;
        CaptureHint.Text = hint;
        ShowCaptureOverlay();
        hooks.CaptureNextKey(key =>
            Dispatcher.Invoke(() =>
            {
                DismissCaptureOverlay(key == KeyCode.VcEscape ? null : KeyName(key));
                onKey(key);
            })
        );
    }

    /// <summary>The "Add input" counterpart of <see cref="CaptureKeyWithOverlay"/>:
    /// mouse buttons and wheel scrolls count as much as keys do. A click lands
    /// here as the captured input (the hook suppresses it before WPF could see
    /// it), so the scrim's click-to-cancel only applies to key-only captures.</summary>
    private void CaptureInputWithOverlay(string prompt, string hint, Action<CapturedInput> onInput)
    {
        CapturePrompt.Text = prompt;
        CaptureHint.Text = hint;
        ShowCaptureOverlay();
        hooks.CaptureNextInput(input =>
            Dispatcher.Invoke(() =>
            {
                DismissCaptureOverlay(
                    input switch
                    {
                        CapturedInput.Key { Code: KeyCode.VcEscape } => null,
                        CapturedInput.Key k => KeyName(k.Code),
                        CapturedInput.Mouse m => MouseName(m.Button),
                        CapturedInput.Scroll s => ScrollName(s.Direction),
                        _ => null,
                    }
                );
                onInput(input);
            })
        );
    }

    /// <summary>The trigger-capture counterpart of <see cref="CaptureKeyWithOverlay"/>:
    /// a key or a mouse button (not left click, not a scroll — the hook filters
    /// those out) sets the keybind. Esc reaches <paramref name="onInput"/> as a
    /// key like any other, and the flow reads it as "cancel". Clicking the
    /// scrim (left click) cancels too. <paramref name="onInput"/> runs on the
    /// UI thread.</summary>
    private void CaptureTriggerWithOverlay(
        string prompt,
        string hint,
        Action<CapturedInput> onInput
    )
    {
        CapturePrompt.Text = prompt;
        CaptureHint.Text = hint;
        ShowCaptureOverlay();
        hooks.CaptureNextTrigger(input =>
            Dispatcher.Invoke(() =>
            {
                DismissCaptureOverlay(
                    input switch
                    {
                        CapturedInput.Key { Code: KeyCode.VcEscape } => null,
                        CapturedInput.Key k => KeyName(k.Code),
                        CapturedInput.Mouse m => MouseName(m.Button),
                        _ => null,
                    }
                );
                onInput(input);
            })
        );
    }

    private void CaptureOverlay_Click(object sender, MouseButtonEventArgs e)
    {
        hotkeyCapturing = false;
        hooks.CancelCapture();
        DismissCaptureOverlay(null);
        Status("cancelled");
    }

    private void ShowCaptureOverlay()
    {
        captureOverlayUp = true;
        CaptureKeyName.Text = "";

        var blur = new BlurEffect { Radius = 0 };
        Shell.Effect = blur;
        blur.BeginAnimation(
            BlurEffect.RadiusProperty,
            new DoubleAnimation(10, TimeSpan.FromMilliseconds(180))
        );
        CaptureOverlay.Visibility = Visibility.Visible;
        CaptureOverlay.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(150))
        );

        // The waiting LED: the empty keycap's border breathes toward the
        // accent.
        CaptureKeycapStroke.BeginAnimation(SolidColorBrush.ColorProperty, null);
        CaptureKeycapStroke.Color = ThemeManager.Color("Surface3");
        CaptureKeycapStroke.BeginAnimation(
            SolidColorBrush.ColorProperty,
            new ColorAnimation(
                ThemeManager.Color("Accent"),
                TimeSpan.FromMilliseconds(1100)
            )
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            }
        );
    }

    /// <summary>Fades the overlay out. With a <paramref name="keyName"/>, the
    /// name stamps into the keycap and the border lights solid for a beat
    /// first; null (Esc or scrim click) skips straight to the fade.</summary>
    private async void DismissCaptureOverlay(string? keyName)
    {
        if (!captureOverlayUp)
            return;
        captureOverlayUp = false;

        if (keyName is not null)
        {
            CaptureKeyName.Text = keyName;
            CaptureKeycapStroke.BeginAnimation(SolidColorBrush.ColorProperty, null);
            CaptureKeycapStroke.Color = ThemeManager.Color("Accent");
            await Task.Delay(320);
            // A capture begun during the beat owns the overlay again.
            if (captureOverlayUp)
                return;
        }

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) =>
        {
            if (captureOverlayUp)
                return;
            CaptureOverlay.Visibility = Visibility.Collapsed;
            Shell.Effect = null;
        };
        CaptureOverlay.BeginAnimation(OpacityProperty, fade);
        Shell.Effect?.BeginAnimation(
            BlurEffect.RadiusProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(180))
        );
    }

    // ---- trigger capture ----

    private void TriggerButton_Click(object sender, RoutedEventArgs e) => BeginTriggerCapture();

    private void BeginTriggerCapture()
    {
        if (Selected < 0)
            return;

        Status("press a key or mouse button · Esc cancels");
        CaptureTriggerWithOverlay(
            "press a key or mouse button to set the keybind",
            "Esc cancels · left click is reserved",
            input =>
            {
                switch (input)
                {
                    case CapturedInput.Key k:
                        TriggerCaptured(k.Code);
                        break;

                    case CapturedInput.Mouse m:
                        MouseTriggerCaptured(m.Button);
                        break;

                    // Scrolls never arrive here (the hook ignores them).
                }
            }
        );
    }

    private void TriggerCaptured(KeyCode key)
    {
        var i = Selected;
        if (i < 0)
            return;

        // Esc is reserved as "never mind", so it can never be a trigger
        // itself. The binding keeps the keybind it already had; unassigning
        // is the menu's "Clear keybind" (see ClearSelectedKeybind).
        if (key == KeyCode.VcEscape)
        {
            Status("cancelled");
            return;
        }

        // Sharing a keybind is fine, but if another holder is enabled this
        // one starts unchecked — enabling it means unchecking that one first.
        // A key trigger clears any mouse trigger the binding had.
        var holder = EnabledHolderOf(key, null, i);
        bindings[i] = bindings[i] with { Trigger = key, MouseTrigger = null, Enabled = holder < 0 };
        if (holder >= 0)
            conflictRow = i; // the refusal marks the row's chip, as elsewhere
        SaveAndRearm();
        // Detail first: the mark lives for exactly one list rebuild, and the
        // header's chip has to read it before that rebuild clears it.
        RefreshDetail();
        RefreshBindingsList(i);
        if (holder < 0)
            Status($"keybind set · {KeyName(key)}");
        else
            Status($"keybind set · {TriggerTakenBy(i, holder)}", sticky: true);
    }

    private void MouseTriggerCaptured(MouseButton button)
    {
        var i = Selected;
        if (i < 0)
            return;

        // A mouse trigger clears the key trigger; the shared-keybind rule is
        // the same as keys, just keyed by the button.
        var holder = EnabledHolderOf(KeyCode.VcUndefined, button, i);
        bindings[i] = bindings[i] with
        {
            Trigger = KeyCode.VcUndefined,
            MouseTrigger = button,
            Enabled = holder < 0,
        };
        if (holder >= 0)
            conflictRow = i; // same refusal, same mark
        SaveAndRearm();
        RefreshDetail(); // before the rebuild clears the mark, as above
        RefreshBindingsList(i);
        if (holder < 0)
            Status($"keybind set · {MouseName(button)}");
        else
            Status($"keybind set · {TriggerTakenBy(i, holder)}", sticky: true);
    }

    // ---- binding edits ----

    private void BindingsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Mid-drag selection changes track the placeholder, not the user —
        // the detail panel must keep showing the grabbed binding until the
        // drop commits the model.
        if (!refreshing && bindingsReorder is not { IsReordering: true })
        {
            RefreshDetail();
            RefreshBaseStatus(); // a new selection clears a stale conflict
        }
    }

    // ---- rename (the name heading edits in place on click) ----

    private void NameText_Click(object sender, MouseButtonEventArgs e) => StartNameEdit();

    private void StartNameEdit()
    {
        if (Selected < 0)
            return;

        NameEditBox.Text = bindings[Selected].Macro.Name;
        NameText.Visibility = Visibility.Collapsed;
        NameEditBox.Visibility = Visibility.Visible;
        NameEditBox.Focus();
        NameEditBox.SelectAll();
    }

    private void NameEdit_LostFocus(object sender, RoutedEventArgs e) => FinishNameEdit(true);

    private void NameEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            FinishNameEdit(true);
        else if (e.Key == Key.Escape)
            FinishNameEdit(false);
    }

    private void FinishNameEdit(bool apply)
    {
        // Visibility doubles as the re-entry guard: committing on Enter
        // collapses the box, which fires LostFocus right after.
        if (NameEditBox.Visibility != Visibility.Visible)
            return;

        NameEditBox.Visibility = Visibility.Collapsed;
        NameText.Visibility = Visibility.Visible;

        var i = Selected;
        var name = NameEditBox.Text.Trim();
        if (!apply || i < 0 || name.Length == 0 || name == bindings[i].Macro.Name)
            return;

        bindings[i] = bindings[i] with { Macro = bindings[i].Macro with { Name = name } };
        SaveAndRearm();
        RefreshBindingsList(i);
        RefreshDetail();
    }

    private void BindingsList_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // A click on empty space (below the rows) deselects everything and
        // returns to the opening page.
        if (Rows.IndexUnderMouse(BindingsList, e.GetPosition(BindingsList)) < 0)
            BindingsList.SelectedIndex = -1;
    }

    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var i = Selected;
        if (refreshing || i < 0 || ModeBox.SelectedIndex < 0)
            return;

        bindings[i] = bindings[i] with { Mode = (PlaybackMode)ModeBox.SelectedIndex };
        SyncRepeatBox(bindings[i]);
        SaveAndRearm();
        RefreshBindingsList(i);
    }

    /// <summary>The count box rides along with the mode picker: visible (and
    /// filled in) only while the binding is in Repeat mode.</summary>
    private void SyncRepeatBox(Binding b)
    {
        RepeatBox.Visibility =
            b.Mode == PlaybackMode.Repeat ? Visibility.Visible : Visibility.Collapsed;
        RepeatBox.Text = Math.Max(1, b.RepeatCount).ToString();
    }

    private void RepeatBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            CommitRepeatCount();
    }

    private void RepeatBox_LostFocus(object sender, RoutedEventArgs e) => CommitRepeatCount();

    private void CommitRepeatCount()
    {
        var i = Selected;
        if (refreshing || i < 0)
            return;

        // Garbage or out-of-range input snaps back to the saved value
        // instead of guessing.
        if (!int.TryParse(RepeatBox.Text, out var count) || count < 1 || count > 100_000)
        {
            RepeatBox.Text = Math.Max(1, bindings[i].RepeatCount).ToString();
            FlashRepeatBox();
            return;
        }

        if (count == bindings[i].RepeatCount)
            return;

        bindings[i] = bindings[i] with { RepeatCount = count };
        SaveAndRearm();
        RefreshBindingsList(i);
    }

    // The Tag the TextBox template's last trigger watches for: while it is
    // set, the box's chrome takes the control's own BorderBrush, so the flash
    // below shows over the hover and focus borders (see Theme.xaml).
    private const string RejectedTag = "Rejected";

    // One brush and one token for the flash: a second rejection inside the
    // 600 ms restarts the settle, and only the newest one puts the resting
    // border back.
    private readonly SolidColorBrush repeatFlash = new();
    private int repeatFlashToken;

    /// <summary>The refusal, said without a message: the box's border lights
    /// Red and settles back over 600 ms — onto the focus accent while the box
    /// still has focus (the Enter path), onto the resting hairline once it
    /// does not (the blur path), so the flash never ends in a jump. The
    /// resting value goes back as a resource reference, so a theme swap still
    /// repaints it.</summary>
    private void FlashRepeatBox()
    {
        var token = ++repeatFlashToken;

        repeatFlash.BeginAnimation(SolidColorBrush.ColorProperty, null); // stop a flash in flight
        repeatFlash.Color = ThemeManager.Color("Red");
        RepeatBox.BorderBrush = repeatFlash;
        RepeatBox.Tag = RejectedTag;

        var settle = new ColorAnimation(
            ThemeManager.Color(RepeatBox.IsKeyboardFocusWithin ? "Accent" : "Hairline"),
            TimeSpan.FromMilliseconds(600)
        );
        settle.Completed += (_, _) =>
        {
            if (token != repeatFlashToken)
                return; // a newer flash owns the border

            RepeatBox.ClearValue(TagProperty);
            RepeatBox.SetResourceReference(BorderBrushProperty, "Hairline");
        };
        repeatFlash.BeginAnimation(SolidColorBrush.ColorProperty, settle);
    }

    // Row whose enable was just refused: its trigger reads Red until the list
    // is next rebuilt. Task 5 moves the mark onto the row's key chip; the flag
    // is what stays put.
    private int conflictRow = -1;

    private void EnabledChanged(object sender, RoutedEventArgs e)
    {
        if (refreshing || sender is not CheckBox { Tag: int i } check)
            return;

        var enable = check.IsChecked == true;

        // Shared keybind: only one holder may be enabled — bounce the check
        // back off, mark the row, and name the macro already holding it.
        var holder = enable
            ? EnabledHolderOf(bindings[i].Trigger, bindings[i].MouseTrigger, i)
            : -1;
        if (holder >= 0)
        {
            refreshing = true; // reverting the box is not a user edit
            check.IsChecked = false;
            refreshing = false;
            conflictRow = i;
            RefreshBindingsList(Selected); // stamps the mark on the refused row
            Status(TriggerTakenBy(i, holder), sticky: true);
            return;
        }

        bindings[i] = bindings[i] with { Enabled = enable };
        SaveAndRearm();
    }

    /// <summary>The shared-keybind refusal, in the one wording every path
    /// that refuses an enable uses: the trigger, then the macro already
    /// holding it.</summary>
    private string TriggerTakenBy(int refused, int holder) =>
        $"{TriggerLabel(bindings[refused])} is already used by {bindings[holder].Macro.Name}";

    private void BindingsList_RightClick(object sender, MouseButtonEventArgs e)
    {
        var i = Rows.IndexUnderMouse(BindingsList, e.GetPosition(BindingsList));
        if (i < 0)
        {
            BindingsList.ContextMenu = null;
            return;
        }

        BindingsList.SelectedIndex = i;
        BindingsList.ContextMenu = BuildBindingMenu(i);
    }

    /// <summary>The one macro menu, opened by a right-click on a sidebar row
    /// and by the detail header's overflow button. Every item acts on the
    /// selected binding, so both callers select <paramref name="index"/>
    /// first.</summary>
    private ContextMenu BuildBindingMenu(int index)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItemFor("Run now", RunSelectedBindingOnce));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor("Rename", StartNameEdit));
        menu.Items.Add(MenuItemFor("Change keybind…", BeginTriggerCapture));

        // Where unassigning lives now that Esc cancels a capture instead.
        var clear = MenuItemFor("Clear keybind", ClearSelectedKeybind);
        clear.IsEnabled = bindings[index].HasTrigger;
        AutomationProperties.SetAutomationId(clear, "ClearKeybindItem");
        menu.Items.Add(clear);

        menu.Items.Add(BuildAppFilterMenu());
        menu.Items.Add(MenuItemFor("Duplicate", DuplicateSelectedBinding));
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
        menu.Items.Add(new Separator());

        // Last, alone, and in the alarm ink: the only item that destroys
        // anything.
        var delete = MenuItemFor("Delete", DeleteSelectedBinding);
        delete.SetResourceReference(ForegroundProperty, "Red");
        menu.Items.Add(delete);
        return menu;
    }

    /// <summary>The detail header's overflow button: the same macro menu the
    /// sidebar row opens, hung under the dots.</summary>
    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        var i = Selected;
        if (i < 0)
            return;

        var menu = BuildBindingMenu(i);
        menu.Placement = PlacementMode.Bottom;
        menu.PlacementTarget = MoreButton;
        MoreButton.ContextMenu = menu; // the menu's parent, for the theme's sake
        menu.IsOpen = true;
    }

    /// <summary>"Clear keybind": the binding gives up its keybind, and with it
    /// any chance of firing, so it is force-disabled at the same time.</summary>
    private void ClearSelectedKeybind()
    {
        var i = Selected;
        if (i < 0)
            return;

        bindings[i] = bindings[i] with
        {
            Trigger = KeyCode.VcUndefined,
            MouseTrigger = null,
            Enabled = false,
        };
        SaveAndRearm();
        RefreshBindingsList(i);
        RefreshDetail();
        Status("keybind cleared");
    }

    /// <summary>"Only in app": limits the selected binding to firing while one
    /// app has focus. Lists apps that currently have a window; elsewhere the
    /// trigger key types normally.</summary>
    private MenuItem BuildAppFilterMenu()
    {
        var current = bindings[Selected].AppFilter;
        var root = new MenuItem { Header = "Only in app" };

        var anywhere = new MenuItem
        {
            Header = "Anywhere",
            IsCheckable = true,
            IsChecked = current is null,
        };
        anywhere.Click += (_, _) => SetAppFilter(null);
        root.Items.Add(anywhere);
        root.Items.Add(new Separator());

        var listed = false;
        foreach (var (name, title) in RunningApps())
        {
            var item = new MenuItem
            {
                Header = name,
                IsCheckable = true,
                IsChecked = string.Equals(current, name, StringComparison.OrdinalIgnoreCase),
                ToolTip = title,
            };
            item.Click += (_, _) => SetAppFilter(name);
            root.Items.Add(item);
            listed = string.Equals(current, name, StringComparison.OrdinalIgnoreCase) || listed;
        }

        // The filtered app isn't running right now: still show (and keep) it.
        if (current is not null && !listed)
        {
            var item = new MenuItem
            {
                Header = current,
                IsCheckable = true,
                IsChecked = true,
                ToolTip = "not running",
            };
            item.Click += (_, _) => SetAppFilter(current);
            root.Items.Add(item);
        }

        return root;
    }

    private static (string Name, string Title)[] RunningApps()
    {
        var apps = new List<(string Name, string Title)>();
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            // Some processes refuse these queries or exit mid-enumeration —
            // they just aren't candidates.
            try
            {
                if (
                    process.Id != Environment.ProcessId
                    && process.MainWindowHandle != 0
                    && process.MainWindowTitle.Length > 0
                )
                    apps.Add((process.ProcessName, process.MainWindowTitle));
            }
            catch (Exception e)
                when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // skip it
            }
            finally
            {
                process.Dispose();
            }
        }

        return apps.DistinctBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Icons resolved once per process name; misses are retried on the next
    // refresh (the app may have started since).
    private static readonly Dictionary<string, ImageSource> appIconCache = new(
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>The filtered app's icon: from a currently running instance's
    /// executable when there is one (the limited query even works on
    /// anti-cheat-protected games), otherwise the copy a past session left in
    /// the on-disk cache. Null only when neither has it.</summary>
    private static ImageSource? GetAppIcon(string processName)
    {
        if (appIconCache.TryGetValue(processName, out var cached))
            return cached;

        var processes = System.Diagnostics.Process.GetProcessesByName(processName);
        try
        {
            foreach (var process in processes)
            {
                if (ForegroundApp.ExecutablePath(process.Id) is not { Length: > 0 } path)
                    continue;

                using var extracted = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (extracted is null)
                    continue;

                // 32 px rather than the 14 the row shows: the same bitmap is
                // what goes to the on-disk cache, and WPF scales it down.
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    extracted.Handle,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(32, 32)
                );
                source.Freeze(); // usable from any thread, no live resource behind it
                AppIconCache.Save(processName, source);
                appIconCache[processName] = source;
                return source;
            }
        }
        catch (Exception e)
            when (e
                    is System.ComponentModel.Win32Exception
                        or InvalidOperationException
                        or System.IO.FileNotFoundException
                        // ExtractAssociatedIcon refuses UNC paths
                        or ArgumentException
            )
        {
            // nothing readable while it runs, so try the cache too
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }

        // No live instance to read the icon from: the copy a past session
        // cached stands in, memoized for this session like a live hit.
        if (AppIconCache.Load(processName) is not { } stored)
            return null;

        appIconCache[processName] = stored;
        return stored;
    }

    private void SetAppFilter(string? app)
    {
        var i = Selected;
        if (i < 0)
            return;

        bindings[i] = bindings[i] with { AppFilter = app };
        SaveAndRearm();
        RefreshBindingsList(i);

        // The header's App box follows the submenu too. Only the box, not the
        // whole detail panel: a filter change is no reason to rebuild the
        // timeline underneath it.
        refreshing = true;
        SyncAppBox(bindings[i]);
        refreshing = false;

        Status(
            app is null
                ? $"{bindings[i].Macro.Name} fires anywhere"
                : $"{bindings[i].Macro.Name} fires only in {app}"
        );
    }

    /// <summary>One row of the header's App box: the filter it writes (null is
    /// "Anywhere"), the text it shows, and the app's icon when there is
    /// one.</summary>
    private sealed record AppChoice(string? App, string Label, ImageSource? Icon);

    private static readonly AppChoice anywhere = new(null, "Anywhere", null);

    /// <summary>What the closed box has to read before it is ever opened:
    /// "Anywhere" and, when this binding is filtered, the app it is filtered
    /// to. Enumerating every running process waits for the drop-down (see
    /// <see cref="AppBox_DropDownOpened"/>).</summary>
    private void SyncAppBox(Binding b)
    {
        AppBox.Items.Clear();
        AppBox.Items.Add(anywhere);
        if (b.AppFilter is { } app)
            AppBox.Items.Add(new AppChoice(app, app, GetAppIcon(app)));
        AppBox.SelectedIndex = b.AppFilter is null ? 0 : 1;
    }

    /// <summary>The full list, built when the drop-down opens: "Anywhere",
    /// every app with a window right now, and the filter itself when it names
    /// an app that isn't running. The selection rides through the
    /// rebuild.</summary>
    private void AppBox_DropDownOpened(object sender, EventArgs e)
    {
        var i = Selected;
        if (i < 0)
            return;

        var current = bindings[i].AppFilter;
        var choices = new List<AppChoice> { anywhere };
        var listed = false;
        foreach (var (name, _) in RunningApps())
        {
            choices.Add(new AppChoice(name, name, GetAppIcon(name)));
            listed = string.Equals(current, name, StringComparison.OrdinalIgnoreCase) || listed;
        }

        // Not running right now: still listed, so the filter is visible and
        // stays selected. The icon can still come back from the disk cache.
        if (current is not null && !listed)
            choices.Add(new AppChoice(current, $"{current} (not running)", GetAppIcon(current)));

        refreshing = true; // rebuilding the list is not a user edit
        AppBox.Items.Clear();
        foreach (var choice in choices)
            AppBox.Items.Add(choice);
        AppBox.SelectedItem = choices.First(c =>
            string.Equals(c.App, current, StringComparison.OrdinalIgnoreCase)
        );
        refreshing = false;
    }

    private void AppBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (refreshing || Selected < 0 || AppBox.SelectedItem is not AppChoice choice)
            return;

        SetAppFilter(choice.App);
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
        // toggle a third time; "Set keybind" owns its own clicks too.
        if (Rows.IsWithin<ButtonBase>(e.OriginalSource))
            return;

        var i = Rows.IndexUnderMouse(BindingsList, e.GetPosition(BindingsList));
        if (i < 0)
            return;

        var enabling = !bindings[i].Enabled;
        var holder = enabling
            ? EnabledHolderOf(bindings[i].Trigger, bindings[i].MouseTrigger, i)
            : -1;
        if (holder >= 0)
        {
            // Same refusal as the checkbox path, so the same mark and the
            // same message.
            conflictRow = i;
            RefreshBindingsList(Selected);
            Status(TriggerTakenBy(i, holder), sticky: true);
            return;
        }

        bindings[i] = bindings[i] with { Enabled = enabling };
        SaveAndRearm();
        RefreshBindingsList(i);
        Status(
            bindings[i].Enabled
                ? $"{bindings[i].Macro.Name} enabled"
                : $"{bindings[i].Macro.Name} disabled"
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
        Status($"running {binding.Macro.Name}");
        await hooks.RunMacroOnceAsync(binding.Macro);
        Status("done");
    }

    private void DuplicateSelectedBinding()
    {
        var i = Selected;
        if (i < 0)
            return;

        CancelUndoOffer(); // the copy pushes every row below it down one

        // The copy keeps the keybind (sharing is allowed) but starts
        // unchecked — only one holder of a key may be enabled at a time.
        var copy = bindings[i] with
        {
            Enabled = false,
            Macro = bindings[i].Macro with { Name = $"{bindings[i].Macro.Name} (copy)" },
        };

        bindings.Insert(i + 1, copy);
        ConfigStore.Save(bindings);
        RefreshBindingsList(i + 1);
        RefreshDetail();
        Status("duplicated");
    }

    private void DeleteSelectedBinding()
    {
        var i = Selected;
        if (i < 0)
            return;

        var deleted = bindings[i];
        bindings.RemoveAt(i);
        SaveAndRearm();
        RefreshBindingsList(Math.Min(i, bindings.Count - 1));
        RefreshDetail();
        OfferUndo(new Deletion(i, deleted, null), $"Deleted “{deleted.Macro.Name}”");
    }

    private static MenuItem MenuItemFor(string header, Action action, string? toolTip = null)
    {
        var item = new MenuItem { Header = header, ToolTip = toolTip };
        item.Click += (_, _) => action();
        return item;
    }

    // ---- undo (one delete deep) ----

    /// <summary>What the last delete took out: the whole
    /// <see cref="Deletion.Binding"/>, which sat at
    /// <see cref="Deletion.Index"/>, or — when <see cref="Deletion.Steps"/> is
    /// set — those steps lifted out of that binding, each paired with the row
    /// it sat on (ascending). The steps case keeps the binding itself rather
    /// than trusting its index: a reorder or an edit can move it before Undo
    /// runs, and the steps must go back into the macro they came out of.</summary>
    private sealed record Deletion(
        int Index,
        Binding Binding,
        List<(int At, MacroEvent Step)>? Steps
    );

    private Deletion? pendingUndo;

    // Longer than a confirmation on purpose: 8 s is time enough to read the
    // bar and reach for the link. The record lives exactly this long, whether
    // or not a later message takes the link off the bar first.
    private readonly System.Windows.Threading.DispatcherTimer undoExpiry = new()
    {
        Interval = TimeSpan.FromSeconds(8),
    };

    // The message the offer was written under, so the expiry only hands the
    // bar back if the offer is still what the bar is showing.
    private int undoStatusToken;

    /// <summary>Puts "<paramref name="message"/> · Undo" over the base line
    /// and holds <paramref name="deletion"/> for the next 8 s. The message
    /// rides the confirmation tier, so the re-arm the delete just kicked off
    /// waits underneath it instead of taking the bar straight back.</summary>
    private void OfferUndo(Deletion deletion, string message)
    {
        SetStatusText(message, sticky: false);
        statusOverlaid = true;
        statusFade.Stop(); // this message runs on the 8 s clock, not the 3 s one

        var undo = new Hyperlink(new Run("Undo")) { Style = (Style)FindResource("StatusLink") };
        AutomationProperties.SetAutomationId(undo, "UndoLink");
        AutomationProperties.SetName(undo, "Undo");
        undo.Click += (_, _) => RestoreUndo();
        StatusText.Inlines.Add(new Run(" · "));
        StatusText.Inlines.Add(undo);

        pendingUndo = deletion;
        undoStatusToken = statusToken;
        undoExpiry.Stop(); // restart the 8 s clock for this delete
        undoExpiry.Start();
    }

    /// <summary>Forgets the pending delete: nothing left to put back.</summary>
    private void DropUndo()
    {
        undoExpiry.Stop();
        pendingUndo = null;
    }

    /// <summary>Withdraws the offer: forgets the delete and, if the offer is
    /// still what the bar is showing, fades the message back to the base line.
    /// The 8 s expiry ends this way, and so does anything that renumbers the
    /// rows the record points at.</summary>
    private void CancelUndoOffer()
    {
        if (pendingUndo is null)
            return;

        var showing = statusToken == undoStatusToken;
        DropUndo();
        if (showing)
            FadeToBaseStatus();
    }

    /// <summary>Puts the last delete back where it came from and re-selects
    /// it. Steps go in ascending index order, so a non-contiguous selection
    /// lands on the rows it came from rather than bunched together.</summary>
    private void RestoreUndo()
    {
        if (pendingUndo is not { } undo)
            return;

        DropUndo();
        statusOverlaid = false; // the offer is spent; the base takes the bar

        if (undo.Steps is not { } steps)
        {
            var at = Math.Min(undo.Index, bindings.Count);
            bindings.Insert(at, undo.Binding);
            SaveAndRearm();
            RefreshBindingsList(at);
            RefreshDetail();
        }
        else if (bindings.IndexOf(undo.Binding) is var macro && macro >= 0)
        {
            // The macro is found by value, never by the index the steps came
            // out of: a reorder or an edit can have put a different macro
            // there, and the steps would land in its timeline. Gone means
            // gone, and re-selecting them only means anything while the macro
            // they came from is the one on show.
            if (Selected != macro)
            {
                RefreshBindingsList(macro);
                RefreshDetail();
            }

            ReplaceEventsAt(
                macro,
                events =>
                {
                    foreach (var (at, step) in steps)
                        events.Insert(Math.Min(at, events.Count), step);
                }
            );
            SelectEvents(steps.Select(step => step.At));
        }

        RefreshBaseStatus();
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
                Status("milliseconds · type inf for infinite");
                BeginInlineEdit(
                    d.Infinite ? "inf" : d.Milliseconds.ToString(),
                    text =>
                    {
                        var t = text.Trim();
                        if (
                            t is "∞"
                            || t.Equals("inf", StringComparison.OrdinalIgnoreCase)
                            || t.Equals("infinite", StringComparison.OrdinalIgnoreCase)
                        )
                            return new DelayEvent(d.Milliseconds, infinite: true);
                        return int.TryParse(t, out var ms) && ms >= 1 ? new DelayEvent(ms) : null;
                    }
                );
                break;

            case ScrollEvent s:
                BeginInlineEdit(
                    s.Clicks.ToString(),
                    text =>
                        int.TryParse(text, out var clicks) && clicks >= 1
                            ? new ScrollEvent(s.Direction, clicks)
                            : null
                );
                break;

            case TextEvent t:
                BeginInlineEdit(t.Text, text => text.Length > 0 ? new TextEvent(text) : null);
                break;

            case Engine.KeyDownEvent
            or Engine.KeyUpEvent:
                var isDown = SelectedEvent() is Engine.KeyDownEvent;
                Status("press a key · Esc cancels");
                CaptureKeyWithOverlay(
                    "press a key to replace this step",
                    "Esc cancels",
                    key =>
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
                        Status("step updated");
                    }
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
            // Sits where the value was; the vertical margins absorb the box's
            // own border+padding so the row height doesn't jump. No negative
            // left margin — it would cover the opening quote of a text step —
            // and a small right margin keeps the closing quote/unit clear.
            MinWidth = Math.Max(36, valueText.ActualWidth + 14),
            Padding = new Thickness(3, 0, 3, 0),
            Margin = new Thickness(0, -3, 3, -3),
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

        // WPF raises double-click on the second mouse-DOWN; the second UP can
        // then land outside this box (short values make it narrow) and yank
        // focus the instant it opens. Treat focus loss in the first moments
        // as that stray up-click and take focus back instead of committing.
        var openedAt = Environment.TickCount64;
        box.LostFocus += (_, _) =>
        {
            if (Environment.TickCount64 - openedAt < 300)
                box.Focus();
            else
                Finish(true);
        };

        box.SelectAll();
        box.Focus();
    }

    private void EventsList_RightClick(object sender, MouseButtonEventArgs e)
    {
        var at = Rows.IndexUnderMouse(EventsList, e.GetPosition(EventsList));

        // Empty space is for inserting; rows are for editing.
        if (at < 0)
        {
            EventsList.ContextMenu = BuildStepMenu("Insert", atEnd: true);
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
                case ScrollEvent s:
                    var flipped =
                        s.Direction == ScrollDirection.Up
                            ? ScrollDirection.Down
                            : ScrollDirection.Up;
                    menu.Items.Add(
                        MenuItemFor(
                            $"Make {ScrollName(flipped)}",
                            () => ReplaceSelectedStep(new ScrollEvent(flipped, s.Clicks))
                        )
                    );
                    break;
                case DelayEvent d:
                    menu.Items.Add(
                        d.Infinite
                            ? MenuItemFor(
                                "Make timed",
                                () => ReplaceSelectedStep(new DelayEvent(d.Milliseconds))
                            )
                            : MenuItemFor(
                                "Make infinite",
                                () =>
                                    ReplaceSelectedStep(
                                        new DelayEvent(d.Milliseconds, infinite: true)
                                    ),
                                "Waits until the macro is stopped · keybind released (While held) or pressed again (Toggle)"
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

    /// <summary>The one step menu, shared by the Add button ("Add …", inserts
    /// after the selected step) and the empty-space right-click ("Insert …",
    /// appends at the end).</summary>
    private ContextMenu BuildStepMenu(string verb, bool atEnd)
    {
        var menu = new ContextMenu();
        menu.Items.Add(
            MenuItemFor(
                $"{verb} input",
                () => InsertInputViaCapture(atEnd),
                "Captures the next key, mouse button, or scroll"
            )
        );
        menu.Items.Add(
            MenuItemFor($"{verb} delay", () => InsertThenEdit(new DelayEvent(100), atEnd))
        );
        menu.Items.Add(
            MenuItemFor($"{verb} text", () => InsertThenEdit(new TextEvent("text"), atEnd))
        );
        menu.Items.Add(
            MenuItemFor(
                $"{verb} wait for keybind release",
                () => Insert(atEnd, new WaitForReleaseEvent())
            )
        );
        menu.Items.Add(new Separator());

        var mouse = new MenuItem { Header = $"{verb} mouse" };
        mouse.Items.Add(MenuItemFor("Left click", () => InsertClick(MouseButton.Button1, atEnd)));
        mouse.Items.Add(MenuItemFor("Right click", () => InsertClick(MouseButton.Button2, atEnd)));
        mouse.Items.Add(
            MenuItemFor("Middle click (wheel)", () => InsertClick(MouseButton.Button3, atEnd))
        );
        mouse.Items.Add(
            MenuItemFor("Mouse 4 (back)", () => InsertClick(MouseButton.Button4, atEnd))
        );
        mouse.Items.Add(
            MenuItemFor("Mouse 5 (forward)", () => InsertClick(MouseButton.Button5, atEnd))
        );
        mouse.Items.Add(new Separator());
        mouse.Items.Add(
            MenuItemFor("Scroll up", () => Insert(atEnd, new ScrollEvent(ScrollDirection.Up)))
        );
        mouse.Items.Add(
            MenuItemFor("Scroll down", () => Insert(atEnd, new ScrollEvent(ScrollDirection.Down)))
        );
        menu.Items.Add(mouse);
        return menu;
    }

    /// <summary>Appends a step and immediately opens its value for editing.</summary>
    private void InsertThenEdit(MacroEvent step, bool atEnd)
    {
        if (atEnd)
            EventsList.SelectedIndex = -1; // empty-space insert goes to the end
        InsertEvents(step);
        // The new row's container doesn't exist until after layout.
        Dispatcher.InvokeAsync(
            BeginEditSelectedStep,
            System.Windows.Threading.DispatcherPriority.Background
        );
    }

    /// <summary>Inserts after the selected step, or at the end when
    /// <paramref name="atEnd"/> (the empty-space insert path).</summary>
    private void Insert(bool atEnd, params MacroEvent[] steps)
    {
        if (atEnd)
            EventsList.SelectedIndex = -1;
        InsertEvents(steps);
    }

    private void InsertClick(MouseButton button, bool atEnd) =>
        Insert(atEnd, new MouseDownEvent(button), new DelayEvent(30), new MouseUpEvent(button));

    private void ReplaceSelectedStep(MacroEvent step) =>
        ReplaceEvents(events => events[EventsList.SelectedIndex] = step);

    private void SelectEventRange(int start, int length)
    {
        if (length <= 1)
        {
            EventsList.SelectedIndex = start;
            return;
        }

        EventsList.SelectedItems.Clear();
        for (var i = start; i < start + length && i < EventsList.Items.Count; i++)
            EventsList.SelectedItems.Add(EventsList.Items[i]);
    }

    /// <summary>Selects the steps at <paramref name="indices"/>, whether or
    /// not they sit next to each other. Indices past the end are skipped.</summary>
    private void SelectEvents(IEnumerable<int> indices)
    {
        EventsList.SelectedItems.Clear();
        foreach (var i in indices)
            if (i >= 0 && i < EventsList.Items.Count)
                EventsList.SelectedItems.Add(EventsList.Items[i]);
    }

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
            e.Key is Key.Delete or Key.Back
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
        var macro = Selected;
        if (selected.Count == 0 || macro < 0)
            return;

        // Read the steps before the removal, each with the row it is on: the
        // pairs are what Undo puts back.
        var steps = bindings[macro].Macro.Events;
        var lifted = selected.Select(at => (At: at, Step: steps[at])).ToList();

        ReplaceEvents(events =>
        {
            for (var j = selected.Count - 1; j >= 0; j--)
                events.RemoveAt(selected[j]);
        });
        EventsList.SelectedIndex = Math.Min(selected[0], EventsList.Items.Count - 1);

        // The binding as it stands after the removal: Undo looks it up by
        // value, so the sidebar may be in a different order by then.
        OfferUndo(
            new Deletion(macro, bindings[macro], lifted),
            selected.Count == 1 ? "Deleted 1 step" : $"Deleted {selected.Count} steps"
        );
    }

    private void AddStep_Click(object sender, RoutedEventArgs e)
    {
        if (Selected < 0)
            return;

        var menu = BuildStepMenu("Add", atEnd: false);
        menu.PlacementTarget = AddStepButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void InsertInputViaCapture(bool atEnd)
    {
        if (Selected < 0)
            return;

        Status("press a key, mouse button, or scroll · Esc cancels");
        CaptureInputWithOverlay(
            "press any input to add",
            "keys, mouse buttons, and scrolling all count · Esc cancels",
            input =>
            {
                switch (input)
                {
                    case CapturedInput.Key { Code: KeyCode.VcEscape }:
                        Status("cancelled");
                        break;

                    case CapturedInput.Key k:
                        Insert(
                            atEnd,
                            new KeyDownEvent(k.Code),
                            new DelayEvent(30),
                            new KeyUpEvent(k.Code)
                        );
                        Status($"added {KeyName(k.Code)}");
                        break;

                    case CapturedInput.Mouse m:
                        Insert(
                            atEnd,
                            new MouseDownEvent(m.Button),
                            new DelayEvent(30),
                            new MouseUpEvent(m.Button)
                        );
                        Status($"added {MouseName(m.Button)}");
                        break;

                    case CapturedInput.Scroll s:
                        Insert(atEnd, new ScrollEvent(s.Direction));
                        Status($"added {ScrollName(s.Direction)}");
                        break;
                }
            }
        );
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
            // The click leaves keyboard focus on this button, and Space/Enter
            // activate a focused button — recording a Space would press Stop.
            Keyboard.ClearFocus();
            AppendRecordButton.Content = "Stop";
            AppendRecordButton.SetResourceReference(ForegroundProperty, "Red");
            AppendRecordButton.SetResourceReference(BorderBrushProperty, "Red");
            RefreshBaseStatus(); // the base line reads Recording from here
            return;
        }

        var recorded = hooks.StopRecording("steps");
        var target = recordTargetIndex;
        recordTargetIndex = -1;
        AppendRecordButton.Content = "Record";
        AppendRecordButton.ClearValue(ForegroundProperty);
        AppendRecordButton.ClearValue(BorderBrushProperty);

        if (recorded.Events.Count == 0)
        {
            Status("nothing recorded");
            return;
        }

        ReplaceEventsAt(target, events => events.AddRange(recorded.Events));
        Status($"added {recorded.Events.Count} steps");
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

            // The label block takes what the checkbox and the keybind leave.
            var labels = new StackPanel { Margin = new Thickness(8, 2, 0, 2) };

            // App-filtered macros carry the app's icon next to the name; when
            // the icon can't be resolved (app not running), the subtitle
            // spells the filter out instead.
            var icon = b.AppFilter is null ? null : GetAppIcon(b.AppFilter);

            // Two columns, not a stack: both shrink-wrap, so the icon still
            // sits 6 px after the name, and the name carries a MaxWidth of the
            // label block's width less the room the icon reserves — a stack
            // measures its children unbounded, and TextTrimming can only trim
            // against a width. Short name: the cap never binds. Long name: the
            // cap stops it and the ellipsis lands before the icon.
            var nameRow = new Grid();
            nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock
            {
                Text = b.Macro.Name,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            name.SetBinding(
                MaxWidthProperty,
                new System.Windows.Data.Binding(nameof(ActualWidth))
                {
                    Source = labels,
                    Converter = new SubtractConverter(),
                    // The icon's 14 px and the 6 px that sets it off the name.
                    ConverterParameter = icon is null ? 0d : 20d,
                }
            );
            // A disabled macro reads one ink step back, name and line under it.
            name.SetResourceReference(TextBlock.ForegroundProperty, b.Enabled ? "Text" : "Subtext");
            nameRow.Children.Add(name);
            if (icon is not null)
            {
                var badge = new Image
                {
                    Source = icon,
                    Width = 14,
                    Height = 14,
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = $"only in {b.AppFilter}",
                };
                Grid.SetColumn(badge, 1);
                nameRow.Children.Add(badge);
            }

            labels.Children.Add(nameRow);

            // The keybind has moved to the chip, so the line under the name
            // carries the mode — and the filtered app when no icon could.
            var subtitle = new TextBlock
            {
                Text =
                    ModeLabel(b)
                    + (b.AppFilter is null || icon is not null ? "" : $" · {b.AppFilter}"),
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            subtitle.SetResourceReference(
                TextBlock.ForegroundProperty,
                b.Enabled ? "Subtext" : "Overlay0"
            );
            labels.Children.Add(subtitle);

            var row = new DockPanel { Margin = new Thickness(4, 2, 4, 2) };
            DockPanel.SetDock(check, Dock.Left);
            row.Children.Add(check);
            var keybind = KeybindColumn(b, i);
            DockPanel.SetDock(keybind, Dock.Right);
            row.Children.Add(keybind);
            row.Children.Add(labels); // last child: fills what the two leave

            BindingsList.Items.Add(new ListBoxItem { Content = row, MinHeight = 48 });
        }

        BindingsList.SelectedIndex = select;

        // The header's chip wears whatever the selected row's chip wears, a
        // refusal mark included, and loses it on the same rebuild the row
        // does. Every path that refuses an enable ends up here, so this is
        // the one place the two chips have to agree.
        if (Selected >= 0)
            SyncTriggerChip(bindings[Selected], Selected);

        conflictRow = -1; // the mark lives for exactly one rebuild
        refreshing = false;
        RefreshKeyboard();
    }

    /// <summary>The right edge of a sidebar row: the keybind as a key chip —
    /// in Red when this row's enable was just refused (see
    /// <see cref="conflictRow"/>) — or, with no keybind set, a chromeless
    /// button wearing the unset chip that starts capture for that row. The
    /// chip styles have hit testing off, so a click on a chip lands on the
    /// row and a click on the button lands on the button.</summary>
    private FrameworkElement KeybindColumn(Binding b, int i)
    {
        if (!b.HasTrigger)
        {
            var set = new Button
            {
                Style = (Style)FindResource("GhostButton"),
                Content = new ContentControl
                {
                    Content = "Set keybind",
                    Style = (Style)FindResource("KeyChipUnset"),
                },
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Click, then press a key · Esc cancels",
                Tag = i,
            };

            // Built in code, so there is no x:Name to become the automation
            // id: the id names the control, the name names the row it is in.
            AutomationProperties.SetAutomationId(set, "SetKeybind");
            AutomationProperties.SetName(set, $"Set keybind for {b.Macro.Name}");
            set.Click += SetKeybind_Click;
            return set;
        }

        return new ContentControl
        {
            Content = TriggerLabel(b),
            Style = (Style)FindResource(i == conflictRow ? "KeyChipDanger" : "KeyChip"),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>A row's "Set keybind": the row becomes the selection — capture
    /// writes into the selected binding — and then the same flow the detail
    /// panel's keybind button runs takes over.</summary>
    private void SetKeybind_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int i })
            return;

        BindingsList.SelectedIndex = i;
        BeginTriggerCapture();
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
            if (bound is null)
                button.ClearValue(BackgroundProperty);
            else
                button.SetResourceReference(BackgroundProperty, "KeyboardBound");
            button.FontWeight = bound is null ? FontWeights.Normal : FontWeights.SemiBold;
            button.ToolTip = bound is null ? null : $"{bound.Macro.Name} · {ModeLabel(bound)}";
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
        NameText.Text = b.Macro.Name;
        NameEditBox.Visibility = Visibility.Collapsed;
        NameText.Visibility = Visibility.Visible;
        SyncTriggerChip(b, i);
        ModeBox.SelectedIndex = (int)b.Mode;
        SyncRepeatBox(b);
        SyncAppBox(b);

        EventsList.Items.Clear();
        foreach (var macroEvent in b.Macro.Events)
            EventsList.Items.Add(new ListBoxItem { Content = BuildStepRow(macroEvent) });

        refreshing = false;
    }

    /// <summary>The header's keybind, as the button's whole face: the key on a
    /// big keycap, the dashed outline when nothing is bound, or Red when this
    /// binding's keybind was just refused. The same three faces the sidebar
    /// row wears (see <see cref="KeybindColumn"/>), all three at the header's
    /// size, so the row never changes height under them. Style and text only:
    /// cheap enough for the refusal paths that must not rebuild the detail
    /// panel.</summary>
    private void SyncTriggerChip(Binding b, int i)
    {
        TriggerChip.Content = b.HasTrigger ? TriggerLabel(b) : "Set keybind";
        TriggerChip.Style = (Style)FindResource(
            !b.HasTrigger ? "KeyChipBigUnset"
            : i == conflictRow ? "KeyChipBigDanger"
            : "KeyChipBig"
        );
    }

    // Marks the editable part of a step row (see BeginInlineEdit).
    private const string ValueTag = "value";

    /// <summary>A muted TextBlock. The foreground is a resource reference, not
    /// a copied brush, so a theme change repaints it.</summary>
    private static TextBlock MutedText(string text)
    {
        var block = new TextBlock { Text = text };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Subtext");
        return block;
    }

    /// <summary>A timeline row: muted verb in a fixed column, then the value
    /// (tagged so inline edit can swap just that part) with muted quotes/units
    /// around it.</summary>
    private Grid BuildStepRow(MacroEvent macroEvent)
    {
        var (verb, prefix, value, suffix) = DescribeParts(macroEvent);

        var parts = new StackPanel { Orientation = Orientation.Horizontal };
        if (prefix.Length > 0)
            parts.Children.Add(MutedText(prefix));
        parts.Children.Add(new TextBlock { Text = value, Tag = ValueTag });
        if (suffix.Length > 0)
            parts.Children.Add(MutedText(suffix));

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var verbText = MutedText(verb);
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
            ScrollEvent s => (
                "scroll",
                s.Direction == ScrollDirection.Up ? "up × " : "down × ",
                s.Clicks.ToString(),
                ""
            ),
            DelayEvent { Infinite: true } => ("wait", "", "∞", ""),
            DelayEvent d => ("wait", "", d.Milliseconds.ToString(), " ms"),
            TextEvent t => ("type", "“", t.Text, "”"),
            WaitForReleaseEvent => ("wait", "", "until keybind released", ""),
            _ => ("?", "", e.ToString() ?? "", ""),
        };

    private static string ScrollName(ScrollDirection direction) =>
        direction == ScrollDirection.Up ? "scroll up" : "scroll down";

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

    // The bar reads in two tiers. The base line always names the true state
    // and never fades; messages sit on top of it — confirmations for a few
    // seconds, errors and conflicts until the next thing the user does.
    private readonly System.Windows.Threading.DispatcherTimer statusFade = new()
    {
        Interval = TimeSpan.FromSeconds(3),
    };

    // True while a confirmation owns the bar; the base line waits underneath.
    private bool statusOverlaid;

    // Bumped by every message, so a fade that finishes after a newer message
    // arrived knows the bar is no longer its to restore.
    private int statusToken;

    /// <summary>The bar's resting text: what the app is doing right now. The
    /// live count is the set <see cref="RearmAsync"/> arms.</summary>
    private string BaseStatus
    {
        get
        {
            if (hooks.IsRecording)
                return "Recording";
            if (ArmToggle.IsChecked != true)
                return "Off";

            var live = Armable().Length;
            return $"On · {live} {(live == 1 ? "macro" : "macros")} live";
        }
    }

    /// <summary>Puts a message over the base line. Confirmations fade back to
    /// it after 3 s; <paramref name="sticky"/> ones — errors and conflicts —
    /// stay in Red until the next message or the next base recompute.</summary>
    private void Status(string message, bool sticky = false)
    {
        SetStatusText(message, sticky);
        statusOverlaid = !sticky;

        statusFade.Stop(); // restart the 3 s clock for this message
        if (!sticky)
            statusFade.Start();
    }

    /// <summary>Fades the message on the bar out and brings the base line
    /// back in its place. The base itself never fades away.</summary>
    private void FadeToBaseStatus()
    {
        var token = statusToken;
        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(400));
        fadeOut.Completed += (_, _) =>
        {
            if (token != statusToken)
                return; // a newer message took the bar mid-fade

            statusOverlaid = false;
            SetStatusText(BaseStatus, sticky: false);
            StatusText.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400))
            );
        };
        StatusText.BeginAnimation(OpacityProperty, fadeOut);
    }

    /// <summary>Recomputes the base line: call it wherever arm state, the live
    /// count, or recording changes. It replaces a sticky message (the state
    /// moved on, so the error is stale) but waits under a confirmation until
    /// that confirmation's 3 s are up.</summary>
    private void RefreshBaseStatus()
    {
        if (statusOverlaid)
        {
            // The dot and rule still follow the state right away; the text
            // lands on the new base when the confirmation fades.
            UpdateLiveIndicators();
            return;
        }

        statusFade.Stop();
        SetStatusText(BaseStatus, sticky: false);
    }

    private void SetStatusText(string text, bool sticky)
    {
        statusToken++;
        StatusText.BeginAnimation(OpacityProperty, null); // cancel a fade in flight
        StatusText.Opacity = 1;
        StatusText.Text = text; // takes an Undo link down with the old message
        StatusText.SetResourceReference(ForegroundProperty, sticky ? "Red" : "Subtext");
        UpdateLiveIndicators();
    }

    /// <summary>The theme's "LED": the rule under the top bar and the status
    /// dot go amber while armed, red while recording, off when idle. Every
    /// state change routes through <see cref="Status"/> or
    /// <see cref="RefreshBaseStatus"/>, so this stays true.</summary>
    private void UpdateLiveIndicators()
    {
        var (dot, rule) =
            hooks.IsRecording ? ("Red", "Red")
            : ArmToggle.IsChecked == true ? ("Accent", "Accent")
            : ("Overlay0", null);

        StatusDot.SetResourceReference(Shape.FillProperty, dot);
        if (rule is null)
            LiveRule.Fill = Brushes.Transparent;
        else
            LiveRule.SetResourceReference(Shape.FillProperty, rule);
    }

    // ---- global Enable hotkey ----
    // RegisterHotKey, not the global hook — the plan's "prefer the narrowest
    // API" rule. The OS notifies us for exactly this one key, so it works
    // while disarmed and from the tray without anything watching the
    // keyboard. Trade-off: the chosen key is swallowed system-wide the whole
    // time the app runs, so bare letter keys make poor choices — F-keys or a
    // modifier combo are the sane picks.

    private AppSettings settings = new();
    private Key armHotkeyKey = Key.None;
    private ModifierKeys armHotkeyModifiers = ModifierKeys.None;
    private bool hotkeyCapturing;
    private nint windowHandle;

    private const int ArmHotkeyId = 1;
    private const int WmHotkey = 0x0312;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hwnd, int id);

    private static (Key, ModifierKeys) ParseHotkey(AppSettings s)
    {
        Enum.TryParse(s.ArmHotkeyKey, out Key key);
        Enum.TryParse(s.ArmHotkeyModifiers, out ModifierKeys modifiers);
        return (key, modifiers);
    }

    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        // The hotkey is captured at the WPF level (the window has focus right
        // now), not via the global hook — no KeyCode→virtual-key mapping, and
        // no hook while disarmed.
        CapturePrompt.Text = "press a key to toggle Enable globally";
        CaptureHint.Text = "modifiers count (e.g. Ctrl+F6) · Esc or a click cancels";
        hotkeyCapturing = true;
        ShowCaptureOverlay();
        Status("press a key for the Enable hotkey · Esc cancels");
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!hotkeyCapturing)
        {
            // Ctrl+Z takes the last delete back. Guarded like the Delete keys:
            // inside a text box the shortcut belongs to the editor.
            if (
                e.Key == Key.Z
                && Keyboard.Modifiers == ModifierKeys.Control
                && pendingUndo is not null
                && Keyboard.FocusedElement is not TextBox
            )
            {
                RestoreUndo();
                e.Handled = true;
            }

            return;
        }

        // Alt-combinations arrive as Key.System with the real key tucked away.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (
            key
            is Key.LeftCtrl
                or Key.RightCtrl
                or Key.LeftShift
                or Key.RightShift
                or Key.LeftAlt
                or Key.RightAlt
                or Key.LWin
                or Key.RWin
        )
            return; // modifiers ride along with the next real key

        e.Handled = true;
        hotkeyCapturing = false;

        // The hotkey that was registered stays registered; unassigning is the
        // button's own "Clear hotkey" (see ClearHotkey_Click).
        if (key == Key.Escape)
        {
            DismissCaptureOverlay(null);
            Status("cancelled");
            return;
        }

        DismissCaptureOverlay(HotkeyLabel(key, Keyboard.Modifiers));
        ApplyArmHotkey(key, Keyboard.Modifiers);
    }

    /// <summary>"Clear hotkey", off the button's right-click: Enable loses its
    /// global key and can only be flipped from the window or the tray.</summary>
    private void ClearHotkey_Click(object sender, RoutedEventArgs e)
    {
        ApplyArmHotkey(Key.None, ModifierKeys.None);
        Status("hotkey cleared");
    }

    private void ApplyArmHotkey(Key key, ModifierKeys modifiers)
    {
        if (windowHandle != 0)
            UnregisterHotKey(windowHandle, ArmHotkeyId);

        armHotkeyKey = key;
        armHotkeyModifiers = modifiers;

        if (key != Key.None)
        {
            if (TryRegisterArmHotkey())
            {
                Status($"Enable hotkey set · {HotkeyLabel(key, modifiers)}");
            }
            else
            {
                Status(
                    $"couldn't register {HotkeyLabel(key, modifiers)} · another app may own it",
                    sticky: true
                );
                armHotkeyKey = Key.None;
                armHotkeyModifiers = ModifierKeys.None;
            }
        }

        settings = settings with
        {
            ArmHotkeyKey = armHotkeyKey == Key.None ? null : armHotkeyKey.ToString(),
            ArmHotkeyModifiers =
                armHotkeyModifiers == ModifierKeys.None ? null : armHotkeyModifiers.ToString(),
        };
        SettingsStore.Save(settings);
        UpdateHotkeyButton();
    }

    private bool TryRegisterArmHotkey() =>
        windowHandle != 0
        && armHotkeyKey != Key.None
        && RegisterHotKey(
            windowHandle,
            ArmHotkeyId,
            ToNativeModifiers(armHotkeyModifiers),
            (uint)KeyInterop.VirtualKeyFromKey(armHotkeyKey)
        );

    // MOD_NOREPEAT is always on: holding the hotkey shouldn't strobe Enable.
    private static uint ToNativeModifiers(ModifierKeys modifiers) =>
        0x4000u // MOD_NOREPEAT
        | (modifiers.HasFlag(ModifierKeys.Alt) ? 0x1u : 0)
        | (modifiers.HasFlag(ModifierKeys.Control) ? 0x2u : 0)
        | (modifiers.HasFlag(ModifierKeys.Shift) ? 0x4u : 0)
        | (modifiers.HasFlag(ModifierKeys.Windows) ? 0x8u : 0);

    private static string HotkeyLabel(Key key, ModifierKeys modifiers)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control))
            parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt))
            parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift))
            parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows))
            parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    // The button's content is the key chip: the key name on a keycap, or
    // the dashed empty outline when no hotkey is stored.
    private void UpdateHotkeyButton()
    {
        var hasHotkey = armHotkeyKey != Key.None;
        HotkeyChip.Content = hasHotkey ? HotkeyLabel(armHotkeyKey, armHotkeyModifiers) : "set";
        HotkeyChip.Style = (Style)FindResource(hasHotkey ? "KeyChip" : "KeyChipUnset");
        ClearHotkeyItem.IsEnabled = hasHotkey; // nothing to clear without one
    }

    private nint HotkeyWndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam == ArmHotkeyId)
        {
            ArmToggle.IsChecked = ArmToggle.IsChecked != true;
            handled = true;
        }

        return 0;
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

        var hwnd = new WindowInteropHelper(this).Handle;

        // The HWND exists now: install the WM_HOTKEY listener and claim any
        // saved Enable hotkey.
        windowHandle = hwnd;
        HwndSource.FromHwnd(hwnd)!.AddHook(HotkeyWndProc);
        if (armHotkeyKey != Key.None && !TryRegisterArmHotkey())
        {
            Status(
                $"couldn't register {HotkeyLabel(armHotkeyKey, armHotkeyModifiers)} · another app may own it",
                sticky: true
            );
            armHotkeyKey = Key.None;
            armHotkeyModifiers = ModifierKeys.None;
            UpdateHotkeyButton();
        }

        // Ask DWM for a dark caption and paint it to match the top bar; both
        // are best-effort (older Windows just keeps the default title bar).
        var dark = 1;
        _ = DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        var background = ThemeManager.Color("Base");
        // COLORREF (0x00BBGGRR).
        var caption = background.R | (background.G << 8) | (background.B << 16);
        _ = DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR
    }

    private static string TriggerLabel(Binding b) =>
        b.MouseTrigger is { } button ? Capitalize(MouseName(button))
        : b.Trigger == KeyCode.VcUndefined ? "Not set"
        : KeyName(b.Trigger);

    private static string KeyName(KeyCode key) =>
        key.ToString().StartsWith("Vc") ? key.ToString()[2..] : key.ToString();

    // Mouse names come lowercase ("mouse 4"); the keybind label sentence-cases
    // them so they sit beside key names like "F8".
    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string ModeLabel(Binding b) =>
        b.Mode switch
        {
            PlaybackMode.Once => "once",
            PlaybackMode.WhileHeld => "while held",
            PlaybackMode.Toggle => "toggle",
            PlaybackMode.Repeat => $"×{Math.Max(1, b.RepeatCount)}",
            _ => b.Mode.ToString(),
        };

    protected override void OnClosing(CancelEventArgs e)
    {
        // Uninstall the hook and hard-stop playback (releases held keys).
        // Blocking briefly here is fine — the window is closing.
        if (windowHandle != 0)
            UnregisterHotKey(windowHandle, ArmHotkeyId);
        tray?.Dispose();
        hooks.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        base.OnClosing(e);
    }
}
