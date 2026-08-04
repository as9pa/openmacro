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
