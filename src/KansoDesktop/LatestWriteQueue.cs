using System;
using System.Threading.Tasks;

namespace KansoDesktop;
// One writer, at most one waiting payload. A newer snapshot replaces obsolete queued work.
public sealed class LatestWriteQueue
{
    private readonly object _gate = new();
    private readonly Action<string> _write;
    private (long Version, string Payload)? _pending;
    private Task _worker = Task.CompletedTask;
    private bool _running;
    private long _requested, _written;
    public event Action<Exception>? Failed;
    public LatestWriteQueue(Action<string> write) => _write = write;
    public long Written { get { lock (_gate) return _written; } }
    public int PendingCount { get { lock (_gate) return _pending.HasValue ? 1 : 0; } }
    public void Request(string payload)
    {
        lock (_gate)
        {
            _pending = (++_requested, payload);
            if (_running) return;
            _running = true; _worker = Task.Run(Drain);
            _ = _worker.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }
    public Task Completion { get { lock (_gate) return _worker; } }
    private void Drain()
    {
        while (true)
        {
            (long Version, string Payload) current;
            lock (_gate) { if (!_pending.HasValue) { _running = false; return; } current = _pending.Value; _pending = null; }
            try { _write(current.Payload); lock (_gate) _written = current.Version; }
            catch (Exception ex)
            {
                lock (_gate) { _pending ??= current; _running = false; }
                try { Failed?.Invoke(ex); } catch { }
                throw;
            }
        }
    }
}

