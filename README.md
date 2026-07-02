# wootcro

An open-source, SteelSeries-style **macro app for any keyboard** — record, edit, and replay macros — built as a native Windows app you own. Ships in two flavors: a **Core** build, and a **Wooting-integrated** build that adds analog features on a Wooting 60HE.

> **Status:** research complete, ready to build. No code yet — Phase 1 is the next step.
> A visual version of this plan lives in [`docs/plan.html`](docs/plan.html).

---

## What we're building

Keyboard software rarely does proper macros well (the Wooting's own Wootomation is basic, and keyboard firmware generally has no macro engine). So this is a **host-side** desktop app that does what SteelSeries Engine / DS4Windows do — a polished GUI to record, edit, and fire macros — for any keyboard, with optional Wooting analog features layered on top.

## Decisions (locked)

| Decision | Why |
|---|---|
| **Native Windows desktop app** | Open it when you want macros (not an always-on service); while open it can minimize to the tray to arm/disarm, and closing it leaves nothing running. Not a browser tool, not onboard. |
| **C# / .NET + WPF** | Windows-native; key hooks, input sending, and the Wooting SDK are all easy interop; real polished GUI. |
| **Host-side software injection** | Same method SteelSeries & DS4Windows use under the hood. Confirmed fine — it was always software. |
| **Not stored on the keyboard** | The 60HE firmware has no macro engine (only DKS / Mod Tap / Toggle Key / remaps / 4 profiles), so macros must run in the app. |
| **Features:** record → editable timeline w/ delays → **Once / While-held / Toggle** | The DS4Windows macro model. |
| **Analog depth features** | **Phase 2, optional module** — e.g. one key firing different actions at different actuation depths, depth-threshold macro triggers, or mapping press depth to a continuous output. The thing no normal keyboard can do. |
| **One codebase → two releases** | Ships as two installers — **Core** (any keyboard) and **Wooting-integrated** — built from the *same* codebase, not a fork. The analog module only activates when a Wooting is present, so non-Wooting users pay ~zero for it. A fork would mean maintaining two diverging copies (every fix applied twice); build flavors from one source is lighter *and* easier. |
| **Graceful stop by default** | On stop, finish the current cycle cleanly (with an option to stop immediately). Either way, **never leave a key held down** — always release on exit. |
| **Clean, minimal UI** | Modern but restrained — uncluttered, never overloaded (Blur-AutoClicker is too dense). No AI-UI tells (gradients, glass, rounded-neon cards, emoji). Progressive disclosure: advanced options tucked away until needed. |

## How it works — three systems

1. **Capture** — while a macro is enabled, the app watches only for its *armed* trigger key(s) and decides whether to fire (see **Safety & trust** below). *(To bind a macro to a key AND stop it typing normally, it must swallow the original press.)*
2. **Execute** — replay the recorded events with correct timing: keystrokes, text, mouse, delays, launch apps. *(This is the "software injection" part.)*
3. **Interface** — a visual keyboard to assign binds + an editable timeline, plus JSON storage and a tray toggle. *(The biggest chunk of work.)*

## Safety & trust (a core design principle)

A macro tool has to watch the keyboard — the same thing a keylogger does. So we design it to be *obviously* not one, and to stay auditable:

- **No hook unless a macro is enabled.** The global hook is installed only while at least one macro is armed, and removed the moment you toggle everything off. Idle app = nothing watching.
- **Allowlist, never stored.** While active, the hook checks each event against the small set of *armed keys* (those with an enabled binding) and returns immediately for anything else. Keystrokes are never buffered, logged, or written to disk.
- **Prefer the narrowest API.** For a macro that just fires on a key press, Win32 `RegisterHotKey` makes the OS notify you for that one key only — the app never sees anything else. The low-level hook is the fallback for while-held, key-up, suppression, and analog.
- **Open source = auditable.** Anyone can verify the above.

> **Technical note:** a Windows low-level keyboard hook (`WH_KEYBOARD_LL`) is invoked for *every* key by OS design — you can't ask Windows to call you for only one key. The trust guarantee comes from *what the callback does* (filter to armed keys, never record) and *when it's installed* (only while a macro is enabled), not from the hook selectively not seeing keys. `RegisterHotKey` is the one API that literally fires for just the registered key.

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

## Timing & precision

Macros need a **1 ms minimum delay** and intervals that stay accurate over time. Windows makes this hard: the default timer granularity is ~15.6 ms, and naive `sleep(interval)` loops accumulate drift because every sleep overshoots slightly and the errors pile up.

The fix (the pattern used in [Blur-AutoClicker](https://github.com/Blur009/Blur-AutoClicker)'s engine, `worker.rs`) is an **absolute-deadline, self-correcting scheduler**:

1. **Raise the timer resolution to 1 ms while a macro runs.** `timeBeginPeriod(1)` on start, `timeEndPeriod(1)` on stop (wrapped in an `IDisposable` guard). Only while firing — it's a system-wide setting. *(Blur uses the native `NtSetTimerResolution`; `timeBeginPeriod` is the documented equivalent.)*
2. **Schedule against a fixed grid, not relative sleeps.** Keep a `nextFireTime` (a `Stopwatch` timestamp). Each cycle advance it by `nextFireTime += interval` — relative to the *previous target*, never to "now" — then wait only `nextFireTime - now`. If one cycle overshoots, the next automatically waits less to catch up, so the *average* interval stays exactly on target. This **is** the "how far off was it → correct the next one" mechanism; anchoring to absolute deadlines does it for free.
3. **Hybrid sleep + spin for the last millisecond.** Coarse-sleep the bulk of the wait, then busy-spin (`Stopwatch` + `Thread.SpinWait`) the final ~1 ms for tight accuracy. *(Trade-off: spinning costs CPU — gate it behind a "high precision" toggle.)*
4. **Clamp every delay to `max(1, value)` ms** in the data model.
5. **Wait interruptibly** (small ticks, checking the stop flag) so toggling a macro off stays responsive.

> `System.Threading.PeriodicTimer` also self-compensates and is fine for coarse macros, but its resolution is bound by the system timer — the `Stopwatch` + spin loop is what reliably hits 1 ms.
>
> **Note:** this timing machinery is entirely OS/CPU-level — it has nothing to do with the Wooting, and works identically on any keyboard.

## Performance & footprint

Target: lightweight, low CPU/RAM, and **no stutter** (it'll run during games where jitter matters).

- **Idle ≈ free — or just closed.** It's a normal app, not a background service: open it when you want macros, close it when you don't and nothing lingers. While open, the hook is gated off until a macro is armed (see Safety & trust).
- **Built for rapid, lag-free triggering.** Firing a single-use macro several times a second must have *zero* felt lag, so the engine thread stays alive and waiting (a trigger wakes it instantly) and the hot code path is **pre-warmed** (ReadyToRun / a warm-up pass at launch) — so even the *first* fire after opening isn't slow from cold JIT. Re-triggering while a macro is still running follows a defined policy (restart / ignore / queue).
- **Engine on its own thread.** The timing loop never touches the UI thread; run it at raised priority so the scheduler doesn't starve it.
- **No allocations in the hot loop.** The #1 cause of random stutters in .NET is the garbage collector — so the timing/hook path pre-allocates and avoids per-cycle garbage (no LINQ/closures/boxing in the loop).
- **Keep the hook callback tiny.** A slow low-level keyboard hook adds *system-wide* input lag and can be silently dropped by Windows. The callback only checks the armed-key set and signals the engine — real work happens off the callback.
- **Spin only when asked.** The sub-ms spin-wait burns a core, so it's behind the high-precision toggle and runs only during an active high-precision macro.
- **Honest note:** any host-side tool adds a little latency vs. the raw hardware. A tight engine keeps it minimal and, more importantly, *consistent* — low jitter matters more than low absolute latency in games.

## Analog features (Phase 2, optional module)

Only possible on a Wooting (via the Analog SDK), all opt-in and off by default — an optional module, **not a fork**:

- **Multi-point actuation** *(the main idea)* — one key fires different actions at different press depths: a light press does X, a full press does Y. Like the keyboard's onboard DKS, but host-side so it can trigger full **macros**, not just single keys.
- **Depth-threshold trigger** — fire a macro when a key crosses a chosen depth (e.g. 50%).
- **Analog → continuous output** — map how far a key is pressed to a smooth value: mouse-move speed, scroll, a gamepad axis, volume. This analog-to-analog mapping is the genuinely unique one.
- **Hold-past-depth gating** — a while-held macro repeats only while the key is pressed beyond a set depth.

*Cost note:* reading analog means polling the SDK on a loop, which uses CPU — another reason it's a dormant, optional module unless you're using it.

## UI direction

**Clean, modern, and minimal — never overloaded.** Not a retro look, but not busy either: [Blur-AutoClicker](https://github.com/Blur009/Blur-AutoClicker) is a good example of *too much* (panels, sub-panels, dozens of options on screen at once). The opposite of that:

- One clear primary view; advanced settings behind a tab or expander (progressive disclosure).
- Generous whitespace; only a few controls visible at any moment.
- A neutral, flat palette; a system UI font; simple square-ish controls.
- **None** of the "AI-generated UI" tells — no purple gradients, glassmorphism/frosted glass, oversized rounded neon cards, emoji-as-labels, or a giant centered hero.

Restraint here also serves the lightweight goal: fewer, simpler controls render faster and lighter.

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
