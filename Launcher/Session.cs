using Lucy.Core;
using Lucy.Mvvm;

namespace Lucy.Launcher;

/// <summary>One running command (the Windows stand-in for a tmux window).</summary>
public sealed class Session : ObservableObject
{
    public Session(string id, string title, ConsoleMode mode)
    {
        Id = id;
        Title = title;
        Mode = mode;
    }

    public string Id { get; }
    public string Title { get; }
    public ConsoleMode Mode { get; }
    public LogBuffer Log { get; } = new();
    public ManagedProcess? Process { get; private set; }

    private string _command = "";
    public string Command { get => _command; private set => Set(ref _command, value); }

    private bool _isAlive;
    public bool IsAlive { get => _isAlive; set { if (Set(ref _isAlive, value)) OnPropertyChanged(nameof(StateText)); } }

    private int? _exitCode;
    public int? ExitCode { get => _exitCode; set { if (Set(ref _exitCode, value)) OnPropertyChanged(nameof(StateText)); } }

    public bool StopRequested { get; set; }

    public string StateText => IsAlive ? "RUNNING" : ExitCode is { } c ? $"EXITED ({c})" : "STOPPED";

    public bool IsExternalWindow => Mode == ConsoleMode.NewWindow;

    /// <summary>Raised on the UI thread when the root process exits.</summary>
    public event Action<Session, int>? Exited;

    public void Start(ProcessSpec spec, string display)
    {
        Command = display;
        StopRequested = false;
        ExitCode = null;
        Log.AppendTimestamped($"> {display}");
        if (spec.Mode == ConsoleMode.NewWindow)
            Log.Append("  (running in its own console window)");
        var proc = ManagedProcess.Start(spec, line => Log.Append(line));
        Process = proc;
        IsAlive = true;
        proc.Exited += code => OnUi(() =>
        {
            if (!ReferenceEquals(Process, proc)) return;
            IsAlive = false;
            ExitCode = code;
            Log.AppendTimestamped(code == 0 ? "--- process finished ---" : $"--- process exited with code {code} ---");
            Exited?.Invoke(this, code);
        });
    }

    public async Task StopAsync(TimeSpan grace)
    {
        var p = Process;
        if (p == null) return;
        StopRequested = true;
        if (p.IsTreeAlive) Log.AppendTimestamped("--- stopping (Ctrl+C) ---");
        await p.StopAsync(grace);
    }

    public void Kill() => Process?.Kill();

    public bool TreeAlive => Process?.IsTreeAlive ?? false;

    public void DisposeProcess()
    {
        Process?.Dispose();
        Process = null;
        IsAlive = false;
    }
}
