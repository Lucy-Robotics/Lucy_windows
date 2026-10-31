using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace Lucy.Mvvm;

/// <summary>
/// Thread-safe, size-capped line buffer for log consoles. Lines can be appended from any thread;
/// they are flushed to the bound collection in batches on the UI thread.
/// </summary>
public sealed class LogBuffer
{
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly DispatcherTimer _timer;
    private readonly int _capacity;

    public LogBuffer(int capacity = 5000)
    {
        _capacity = capacity;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Flush();
        _timer.Start();
    }

    public ObservableCollection<string> Lines { get; } = new();

    /// <summary>Raised on the UI thread after new lines were added.</summary>
    public event Action? Appended;

    public void Append(string line) => _pending.Enqueue(line);

    public void AppendTimestamped(string line) => _pending.Enqueue($"[{DateTime.Now:HH:mm:ss}] {line}");

    public void Clear()
    {
        while (_pending.TryDequeue(out _)) { }
        Lines.Clear();
    }

    public string AllText()
    {
        Flush();
        return string.Join(Environment.NewLine, Lines);
    }

    public void Flush()
    {
        if (_pending.IsEmpty) return;
        var added = 0;
        while (added < 2000 && _pending.TryDequeue(out var line))
        {
            Lines.Add(line);
            added++;
        }
        var excess = Lines.Count - _capacity;
        for (var i = 0; i < excess; i++) Lines.RemoveAt(0);
        Appended?.Invoke();
    }

    public void Stop() => _timer.Stop();
}
