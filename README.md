# openmacro

light macro software highly customizable with useful features
has true 1ms delays

## 1ms delays

each delay waits until an absolute deadline instead of sleeping for a duration, so nothing drifts

```csharp
using var _ = TimerResolution.Request(1);        // timeBeginPeriod(1) while firing

var next = Stopwatch.GetTimestamp();
foreach (var step in macro)
{
    next += step.DelayMs * Stopwatch.Frequency / 1000;   // advance the target, never "now"
    while (Stopwatch.GetTimestamp() < next)
        Thread.SpinWait(1);                              // spin the last stretch
    sink.Send(step);
}
```

## performance

the engine runs on its own thread and allocates nothing per cycle, so it stays quiet in the background and never stutters mid-game

# download

requires windows x64.

| [releases](https://github.com/as9pa/openmacro/releases) | size | needs .NET? |
|---|---|---|
| `openmacro-v0.1.0-win-x64.exe` | ~72 mb | **no** — built in runtime |
| `openmacro-v0.1.0-win-x64-dotnet.zip` | ~510 kb | **yes** — [.NET 10 desktop runtime](https://dotnet.microsoft.com/en-us/download) |

if you dont have dotnet download the `.exe`

if you have dotnet download the `.zip`

macros are stored in `%APPDATA%\openmacro\bindings.json`

windows may warn on first run since the exe is unsigned — "more info" → "run anyway"
