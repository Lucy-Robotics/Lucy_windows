using Lucy.Core;
using Lucy.Mvvm;

namespace Lucy.Views;

/// <summary>A page hosted by the main window (installer or launcher).</summary>
public interface IShellPage
{
    string Subtitle { get; }
    /// <summary>Called when the window is asked to close. Clean up and return true to let it close.</summary>
    Task<bool> RequestCloseAsync(ShellViewModel shell);
    /// <summary>Logoff / shutdown: no time for graceful stops.</summary>
    void EmergencyShutdown();
}

public sealed class ShellViewModel : ObservableObject
{
    private IShellPage? _page;
    public IShellPage? Page
    {
        get => _page;
        private set { if (Set(ref _page, value)) OnPropertyChanged(nameof(Subtitle)); }
    }

    public string Subtitle => Page?.Subtitle ?? "";
    public string Version => "v" + Integration.Version;

    private string? _overlay;
    /// <summary>Blocking overlay message (e.g. "Stopping all processes..."), null = hidden.</summary>
    public string? Overlay
    {
        get => _overlay;
        set { if (Set(ref _overlay, value)) OnPropertyChanged(nameof(OverlayVisible)); }
    }
    public bool OverlayVisible => _overlay != null;

    public void ShowInstaller(InstallMode mode = InstallMode.Install, string? workspace = null)
    {
        Page = new InstallViewModel(this, mode, workspace);
    }

    public void ShowLauncher(Workspace ws)
    {
        Page = new LauncherViewModel(this, ws);
    }

    public void NotifySubtitle() => OnPropertyChanged(nameof(Subtitle));
}
