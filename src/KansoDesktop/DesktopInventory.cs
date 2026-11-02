using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KansoDesktop;

public sealed record DesktopItem(string Path, string Name, bool Folder, bool Missing = false);
public static class DesktopInventory
{
    public static List<DesktopItem> Read(string[] roots, string[] assigned)
    {
        var items = new Dictionary<string, DesktopItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(root))
                {
                    try
                    {
                        var attr = File.GetAttributes(path);
                        if ((attr & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        items[path] = new(path, Path.GetFileName(path), (attr & FileAttributes.Directory) != 0);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        foreach (var path in assigned) if (!items.ContainsKey(path))
        {
            try { var attr = File.GetAttributes(path); items[path] = new(path, Path.GetFileName(path), (attr & FileAttributes.Directory) != 0); }
            catch (IOException) { items[path] = new(path, Path.GetFileName(path), false, true); }
            catch (UnauthorizedAccessException) { items[path] = new(path, Path.GetFileName(path), false, true); }
        }
        return items.Values.OrderByDescending(i => i.Folder).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}

// A timed-out Windows/cloud call cannot be aborted safely. Keep its slot occupied until it exits.
public sealed class SingleFlight<T>
{
    private int _busy;
    public bool Busy => Volatile.Read(ref _busy) != 0;
    public async Task<T?> RunAsync(Func<T> work, TimeSpan timeout, CancellationToken cancellation = default)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return default;
        var task = Task.Run(() => { try { return work(); } finally { Interlocked.Exchange(ref _busy, 0); } });
        _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await task.WaitAsync(timeout, cancellation);
    }
}

