using Lucy.Mvvm;

namespace Lucy.Launcher;

public enum PackageStatus { Stopped, Loading, Running, Stopping, Crashed }

/// <summary>A launcher row: config + selection + live status (launcher/package.py Package).</summary>
public sealed class PackageItem : ObservableObject
{
    public PackageItem(PackageConfig config, string? unsupportedReason = null)
    {
        Config = config;
        UnsupportedReason = unsupportedReason;
        _selected = config.DefaultOn && unsupportedReason == null;
    }

    public PackageConfig Config { get; }
    public string Id => Config.Id;
    public string Name => Config.Name;
    public string Description => Config.Description;

    /// <summary>Why this package cannot run on Windows (null = supported).</summary>
    public string? UnsupportedReason { get; }
    public bool IsSupported => UnsupportedReason == null;

    /// <summary>Indentation level as drawn by tui.py: subitems 2, modifiers/robots 1.</summary>
    public int IndentLevel { get; set; }

    private bool _selected;
    public bool Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) OnPropertyChanged(nameof(IsDirty)); }
    }

    private bool _canEnable = true;
    /// <summary>All dependencies are ticked (tui.py dims the row otherwise).</summary>
    public bool CanEnable { get => _canEnable; set => Set(ref _canEnable, value); }

    // ---- runtime ----------------------------------------------------------
    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        set { if (Set(ref _isRunning, value)) OnPropertyChanged(nameof(IsDirty)); }
    }

    public bool Ready { get; set; }
    public bool Exited { get; set; }
    public int? ExitCode { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? StopRequestedAt { get; set; }
    public bool IntendedRunning { get; set; }

    private PackageStatus _status;
    public PackageStatus Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(ShowUrl));
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public string StatusText => Status.ToString().ToUpperInvariant();

    private string _stageText = "";
    /// <summary>"6/8 Building robot model..." while loading.</summary>
    public string StageText { get => _stageText; set => Set(ref _stageText, value); }

    private string _url = "";
    public string Url
    {
        get => _url;
        set { if (Set(ref _url, value)) OnPropertyChanged(nameof(ShowUrl)); }
    }

    public bool ShowUrl => Status == PackageStatus.Running && Url.Length > 0;

    private string _hint = "";
    public string Hint { get => _hint; set => Set(ref _hint, value); }

    /// <summary>Selection differs from what is running (tui.py "Unapplied changes").</summary>
    public bool IsDirty => StartedAt == null && StopRequestedAt == null && Selected != IsRunning;
}
