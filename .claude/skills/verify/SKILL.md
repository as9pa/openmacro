---
name: verify
description: Build, launch, and drive the OpenMacro WPF app to verify changes at the UI surface.
---

# Verifying OpenMacro

## Build & launch

```powershell
dotnet build                       # fails if the app is running (locked DLLs) — stop OpenMacro.App first
Start-Process src\OpenMacro.App\bin\Debug\net10.0-windows\OpenMacro.App.exe
```

## Drive the UI

WPF exposes `x:Name` as UI Automation `AutomationId` — drive controls headlessly with the
`UIAutomationClient` assemblies. **Use Windows PowerShell 5.1 (`powershell.exe -File`), not pwsh** —
pwsh can't load the UIA assemblies. Useful ids: `BindingsList`, `ModeBox`, `RepeatBox`,
`TriggerButton`, `ArmToggle`. Patterns: `SelectionItemPattern` (list rows, combo items),
`ExpandCollapsePattern` (combos — sleep ~500 ms after Expand, retry if items are empty),
`ValuePattern` (text boxes). Working example: a past session's `drive-ui.ps1` pattern —
find window by ProcessId, `FindFirst` by AutomationId, select row, then act.

## Gotchas

- **Config is live user data** at `$env:APPDATA\openmacro\bindings.json` — back it up before
  driving the UI (edits save immediately) and restore it (app closed) when done.
- The user may have a real instance running (it holds a global hook + their macros).
  Note the PID, and relaunch the app when finished so their setup comes back.
- If a fullscreen game is running, screen-region screenshots capture the game. Use
  `PrintWindow` with flag `2` (PW_RENDERFULLCONTENT) to capture the occluded window instead.
- Simulated input (SendKeys) is dropped by the engine's hook (`IsEventSimulated` /
  LLKHF_INJECTED), so macro *playback via a trigger key* can't be driven synthetically —
  cover engine semantics with the xUnit tests, verify the UI → config path live.
