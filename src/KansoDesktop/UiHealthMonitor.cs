using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;
namespace KansoDesktop;

internal sealed class UiHealthMonitor : IDisposable
{
    private long _last = Stopwatch.GetTimestamp();
    private long _reported;
    private readonly DispatcherTimer _pulse = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Timer _check;
    internal UiHealthMonitor()
    {
        _pulse.Tick += (_, _) => Interlocked.Exchange(ref _last, Stopwatch.GetTimestamp()); _pulse.Start();
        _check = new Timer(_ =>
        {
            long now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _last), now) > TimeSpan.FromSeconds(8) && Stopwatch.GetElapsedTime(Interlocked.Read(ref _reported), now) > TimeSpan.FromSeconds(30))
            {
                Interlocked.Exchange(ref _reported, now); Diagnostics.Write("UI-Heartbeat verzögert", new TimeoutException("Oberfläche seit mindestens 8 Sekunden ohne Tick. Keine automatische Prozessbeendigung oder Dateioperation."));
            }
        }, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }
    public void Dispose() { _pulse.Stop(); _check.Dispose(); }
}

