# wootcro

An open-source, SteelSeries-style **macro app for the Wooting 60HE** — record, edit, and replay macros — built as a native Windows app you own.

> **Status:** research complete, ready to build. No code yet — Phase 1 is the next step.
> A visual version of this plan lives in [`docs/plan.html`](docs/plan.html).

---

## What we're building

The 60HE's software can't do proper macros (Wootomation is basic, and the keyboard firmware has no macro engine). So this is a **host-side** desktop app that does what SteelSeries Engine / DS4Windows do — a polished GUI to record, edit, and fire macros — aimed squarely at the Wooting.

## Decisions (locked)

| Decision | Why |
|---|---|
| **Native Windows desktop app** (tray app) | Always running so you can toggle macros on/off on demand. Not a browser tool, not onboard. |
| **C# / .NET + WPF** | Windows-native; key hooks, input sending, and the Wooting SDK are all easy interop; real polished GUI. |
| **Host-side software injection** | Same method SteelSeries & DS4Windows use under the hood. Confirmed fine — it was always software. |
| **Not stored on the keyboard** | The 60HE firmware has no macro engine (only DKS / Mod Tap / Toggle Key / remaps / 4 profiles), so macros must run in the app. |
| **Features:** record → editable timeline w/ delays → **Once / While-held / Toggle** | The DS4Windows macro model. |
| **Analog depth triggers** | **Phase 2, optional bonus** — fire by how far a key is pressed. The one thing SteelSeries can't do. |

## How it works — three systems

1. **Capture** — a background global hook watches every key and decides if it's a macro trigger. *(To bind a macro to a key AND stop it typing normally, the hook must swallow the original press.)*
2. **Execute** — replay the recorded events with correct timing: keystrokes, text, mouse, delays, launch apps. *(This is the "software injection" part.)*
3. **Interface** — a visual keyboard to assign binds + an editable timeline, plus JSON storage and a tray toggle. *(The biggest chunk of work.)*

## Data model

```
Macro       = ordered list of MacroEvent

MacroEvent  ─┬─ KeyDown(key)        // press
             ├─ KeyUp(key)          // release
             ├─ Delay(ms)           // editable wait
             ├─ Text("gg ez")       // type a string
             ├─ LaunchApp(path)
             └─ MouseClick(button)

Binding     = Trigger + Macro + PlaybackMode
              Trigger      = a key   // Phase 2: key + depth threshold
              PlaybackMode = Once | WhileHeld | Toggle

Profile     = list of Bindings + optional "match this app" rule
Config      = list of Profiles      // → saved to %AppData% as JSON
```

## Toolbox

| Job | Use | Note |
|---|---|---|
| Capture keys | `SharpHook` | Global hook; can suppress a key on Windows. Also runs on macOS, so you can prototype the engine now. |
| Play input | `H.InputSimulator` | Sends keystrokes, text, mouse (Win32 `SendInput` underneath). SharpHook's simulator also works. |
| Interface | `WPF + XAML` | Visual keyboard grid + macro timeline editor. Start with code-behind; learn MVVM later. |
| Save configs | `System.Text.Json` | Built in. Serialize the Config tree to JSON in `%AppData%`. |
| Tray / background | `Hardcodet.NotifyIcon.Wpf` | Lives in the system tray; the master on/off toggle. |
| Analog (Phase 2) | Wooting Analog SDK via P/Invoke | Read key depth 0.0–1.0; run a threshold state machine. |

## Roadmap

1. **MVP engine** — one key → one fixed keystroke sequence, firing reliably. *(global hooks, input synthesis, events)*
2. **Playback modes** — Once / repeat while held / toggle. *(state machines, timers, async)*
3. **Recorder** — capture live events with timing → JSON, then edit them. *(serialization, data modeling)*
4. **The GUI** *(the long pole)* — WPF visual keyboard + editable timeline. *(XAML, data binding, MVVM)*
5. **Per-app profiles** — detect foreground app, swap macro set. *(Win32 window queries)*
6. **Analog triggers** *(Phase 2 — beats SteelSeries)* — depth thresholds via the SDK. *(P/Invoke, polling, hysteresis)*

## Phase 1 — what the first step looks like

A console app: press Caps Lock, get text typed. Runs on macOS too (grant Accessibility) for prototyping; the exact logic moves into WPF later.

```csharp
using SharpHook;
using SharpHook.Native;

var sim  = new EventSimulator();
var hook = new SimpleGlobalHook();

hook.KeyPressed += (s, e) =>
{
    if (e.Data.KeyCode == KeyCode.VcCapsLock)
    {
        e.SuppressEvent = true;              // swallow the real key (Windows)
        sim.SimulateTextEntry("gg ez");      // fire the macro
    }
};

hook.Run();   // listen for global key events
```

*(Shape, not gospel — check SharpHook's current API for exact names.)*

## Before you build

- **The keyboard can't do this itself.** 60HE onboard features (DKS, Mod Tap, Toggle Key, remaps, 4 profiles) are fixed depth-rules, not recorded/timed macros.
- **SteelSeries & DS4Windows are software too** — same injection path as AutoHotkey; the difference you liked is the GUI.
- **Anti-cheat can flag injected input** — check the ToS of any game you'd use it with.
- **Prototype on Mac, ship on Windows** — the engine (SharpHook) runs on macOS; WPF is Windows-only.

## Resources

- [Wooting Analog SDK](https://github.com/WootingKb/wooting-analog-sdk)
- [SharpHook — hooks + simulation](https://github.com/TolikPylypchuk/SharpHook)
- [H.InputSimulator](https://github.com/HavenDV/H.InputSimulator)
- [WPF documentation](https://learn.microsoft.com/dotnet/desktop/wpf/)
- [Wootomation — the bar to clear](https://github.com/WootingKb/wooting-macros)
- [Dynamic Keystroke, explained](https://help.wooting.io/article/99-how-to-use-dks)
