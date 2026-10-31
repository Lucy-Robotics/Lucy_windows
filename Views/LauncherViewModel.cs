using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Lucy.Core;
using Lucy.Launcher;
using Lucy.Mvvm;

namespace Lucy.Views;

public sealed record PackageSection(string Title, IReadOnlyList<PackageItem> Items);

/// <summary>
/// Replaces Lucy.py's menu (Launch / Update / Rebuild / Developer Mode / Exit) and the curses
/// Control Center (launcher/tui.py) with one window, reading the same config and state files.
/// </summary>
public sealed class LauncherViewModel : ObservableObject, IShellPage
{
    private readonly ShellViewModel _shell;
    private readonly DispatcherTimer _messageTimer;

    public LauncherViewModel(ShellViewModel shell, Workspace ws)
    {
        _shell = shell;
        Workspace = ws;
        Engine = new LauncherEngine(ws);
        Engine.Message += (msg, warn) => ShowMessage(msg, warn);
        Engine.StatusChanged += () => OnPropertyChanged(nameof(HasUnappliedChanges));
        Engine.TaskSessionActivated += s => SelectedSession = s;
        Engine.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LauncherEngine.IsBusy)) { OnPropertyChanged(nameof(IsBusy)); CommandManager.InvalidateRequerySuggested(); }
        };
        _devMode = ws.DevMode;

        _messageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _messageTimer.Tick += (_, _) => { _messageTimer.Stop(); Message = ""; };

        ToggleCommand = new RelayCommand(p => Toggle(p as PackageItem), _ => !IsBusy);
        ApplyCommand = new AsyncCommand(ApplyAsync, () => Loaded && !IsBusy);
        StopAllCommand = new AsyncCommand(StopAllAsync, () => Loaded && Engine.AnyRunning);
        UpdateCommand = new AsyncCommand(UpdateAsync, () => !IsBusy);
        RebuildCommand = new AsyncCommand(RebuildAsync, () => !IsBusy);
        StopSessionCommand = new AsyncCommand(p => StopSessionAsync(p as Session), p => p is Session { IsAlive: true });
        CloseTabCommand = new RelayCommand(p => { if (p is Session s) Engine.RemoveSessionTab(s); }, p => p is Session s && !s.TreeAlive);
        OpenUrlCommand = new RelayCommand(p => { if (p is string u && u.Length > 0) Integration.OpenUrl(u); });
        OpenWorkspaceCommand = new RelayCommand(() => Integration.OpenFolder(Workspace.Root));
        OpenLogsCommand = new RelayCommand(() => Integration.OpenFolder(Log.Directory));
        ExitCommand = new RelayCommand(() => Application.Current.MainWindow?.Close());
        UninstallCommand = new AsyncCommand(UninstallAsync, () => !IsBusy);

        Application.Current.Dispatcher.BeginInvoke(new Action(async () => await InitializeAsync()), DispatcherPriority.Loaded);
    }

    public string Subtitle => "CONTROL CENTER";
    public Workspace Workspace { get; }
    public LauncherEngine Engine { get; }
    public ObservableCollection<PackageSection> Sections { get; } = new();
    public ObservableCollection<Session> Sessions => Engine.Sessions;

    public ICommand ToggleCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand StopAllCommand { get; }
    public ICommand UpdateCommand { get; }
    public ICommand RebuildCommand { get; }
    public ICommand StopSessionCommand { get; }
    public ICommand CloseTabCommand { get; }
    public ICommand OpenUrlCommand { get; }
    public ICommand OpenWorkspaceCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand UninstallCommand { get; }

    public bool IsBusy => Engine.IsBusy;
    public bool HasUnappliedChanges => Loaded && Engine.HasUnappliedChanges;

    private bool _loaded;
    public bool Loaded { get => _loaded; private set { Set(ref _loaded, value); OnPropertyChanged(nameof(HasUnappliedChanges)); } }

    private string? _loadError;
    public string? LoadError { get => _loadError; private set { Set(ref _loadError, value); OnPropertyChanged(nameof(HasLoadError)); } }
    public bool HasLoadError => LoadError != null;

    private Session? _selectedSession;
    public Session? SelectedSession { get => _selectedSession; set => Set(ref _selectedSession, value); }

    private string _message = "";
    public string Message { get => _message; private set { Set(ref _message, value); OnPropertyChanged(nameof(HasMessage)); } }
    public bool HasMessage => Message.Length > 0;

    private bool _messageIsWarning;
    public bool MessageIsWarning { get => _messageIsWarning; private set => Set(ref _messageIsWarning, value); }

    private bool _devMode;
    /// <summary>Lucy.py "Developer Mode": DEV=true|false in the workspace .env.</summary>
    public bool DevMode
    {
        get => _devMode;
        set
        {
            if (_devMode == value) return;
            if (Engine.AnyRunning || IsBusy)
            {
                ShowMessage("Stop all processes before switching developer mode.", true);
                OnPropertyChanged();
                return;
            }
            DotEnv.SetDevMode(Workspace.EnvFile, value);
            _devMode = value;
            OnPropertyChanged();
            ShowMessage(value ? "Developer mode on: nothing starts automatically." : "Developer mode off: Core + Control Panel start at launch.", false);
            Reload();
        }
    }

    public string WorkspacePath => Workspace.Root;
    public string ControlPanelUrl => Engine.State.Get("control_panel")?.Url ?? "";

    // ---- startup -------------------------------------------------------

    private async Task InitializeAsync()
    {
        // preflight.py guard_single_stack
        var other = StackGuard.LiveLauncherPid(Workspace);
        if (other != null)
        {
            Dialogs.Error("Lucy is already running",
                $"Another launcher (pid {other}) is managing this workspace. Close it first.");
            (Application.Current.MainWindow as MainWindow)?.ForceClose();
            return;
        }
        var orphans = await Task.Run(() => StackGuard.FindOrphans(Workspace));
        if (orphans.Count > 0)
        {
            var stop = Dialogs.Confirm("Lucy stack already running",
                $"{orphans.Count} Lucy process(es) from an earlier session are still running in this workspace. Stop them and continue?",
                "Stop and continue", "Exit",
                detail: string.Join("\n", orphans.Take(6).Select(o => $"{o.Id}  {o.Name}")));
            if (!stop)
            {
                (Application.Current.MainWindow as MainWindow)?.ForceClose();
                return;
            }
            _shell.Overlay = "Stopping the previous Lucy stack...";
            try { await StackGuard.SweepAsync(Workspace); }
            finally { _shell.Overlay = null; }
        }
        StackGuard.WritePidFile(Workspace);

        if (!Reload()) return;
        await Engine.AutoStartProductionAsync();
        SelectedSession ??= Sessions.FirstOrDefault();
    }

    private bool Reload()
    {
        try
        {
            Engine.Load();
            Sections.Clear();
            foreach (var (title, items) in Engine.State.Sections())
                Sections.Add(new PackageSection(title, items));
            LoadError = null;
            Loaded = true;
            OnPropertyChanged(nameof(ControlPanelUrl));
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("launcher load failed", ex);
            LoadError = ex.Message;
            Loaded = false;
            return false;
        }
    }

    // ---- actions -------------------------------------------------------

    private void Toggle(PackageItem? pkg)
    {
        if (pkg == null) return;
        var msg = Engine.Toggle(pkg);
        if (msg != null) ShowMessage(msg, true);
        OnPropertyChanged(nameof(HasUnappliedChanges));
    }

    private async Task ApplyAsync()
    {
        await Engine.ApplyAsync();
        ShowMessage("Configuration Applied!", false);
        SelectedSession ??= Sessions.FirstOrDefault();
        OnPropertyChanged(nameof(HasUnappliedChanges));
    }

    private async Task StopAllAsync()
    {
        foreach (var p in Engine.State.Packages) if (p.Selected && !p.Config.IsModifier) p.Selected = false;
        foreach (var p in Engine.State.Packages.Where(p => p.Config.IsModifier && string.IsNullOrEmpty(p.Config.RequiresPkg))) p.Selected = false;
        Engine.State.RefreshCanEnable();
        await Engine.ApplyAsync();
        ShowMessage("Stopping everything...", false);
    }

    private async Task StopSessionAsync(Session? s)
    {
        if (s == null) return;
        var pkg = Engine.State.Get(s.Id);
        if (pkg != null)
        {
            if (pkg.Selected) Engine.Toggle(pkg);
            if (pkg.Selected) pkg.Selected = false; // robots: allow stopping core anyway
            await Engine.ApplyAsync();
        }
        else
        {
            await s.StopAsync(TimeSpan.FromSeconds(4));
        }
    }

    private async Task<bool> EnsureStoppedForTask(string what)
    {
        if (!Engine.AnyRunning) return true;
        if (!Dialogs.Confirm(what, $"{what} needs the Lucy stack to be stopped. Stop all processes now?", "Stop and continue", "Cancel"))
            return false;
        _shell.Overlay = "Stopping all processes...";
        try { await Engine.StopAllAsync(); }
        finally { _shell.Overlay = null; }
        return true;
    }

    /// <summary>Lucy.py "Update": git pull lucy_ws (when it is a clone), then install.py.</summary>
    private async Task UpdateAsync()
    {
        if (!await EnsureStoppedForTask("Update")) return;
        var python = await Tooling.FindPythonAsync();
        if (!python.Found)
        {
            Dialogs.Error("Python not found", "Python 3 is needed to run install.py. Reinstall it, or run the installer again (Lucy.exe --install).");
            return;
        }
        var steps = new List<(string, string[], IDictionary<string, string>?)>();
        var git = ShellEnv.Which("git");
        if (git != null && Directory.Exists(Path.Combine(Workspace.Root, ".git")))
            steps.Add((git, new[] { "-C", Workspace.Root, "pull", "--ff-only" }, null));
        steps.Add((python.Path!, new[] { "install.py" }, new Dictionary<string, string>
        {
            ["LUCY_PIXI_AUTO_UPGRADE"] = "1",
            ["LUCY_MSVC_AUTO_INSTALL"] = "1",
        }));
        var ok = await Engine.RunTaskAsync("update", "Update", steps);
        ShowMessage(ok ? "Update complete." : "Update failed — see the Update tab.", !ok);
        if (ok) Reload();
    }

    /// <summary>Lucy.py "Rebuild": pixi run build, then pixi run panel-install.</summary>
    private async Task RebuildAsync()
    {
        if (!await EnsureStoppedForTask("Rebuild")) return;
        var pixi = Tooling.PixiPath();
        if (pixi == null)
        {
            Dialogs.Error("pixi not found", "Run Update to reinstall pixi.");
            return;
        }
        var ok = await Engine.RunTaskAsync("rebuild", "Rebuild", new (string, string[], IDictionary<string, string>?)[]
        {
            (pixi, new[] { "run", "build" }, null),
            (pixi, new[] { "run", "panel-install" }, null),
        });
        ShowMessage(ok ? "Rebuild complete." : "Rebuild failed — see the Rebuild tab.", !ok);
        if (ok) Reload();
    }

    private void ShowMessage(string msg, bool warning)
    {
        Message = msg;
        MessageIsWarning = warning;
        _messageTimer.Stop();
        _messageTimer.Interval = TimeSpan.FromSeconds(warning ? 6 : 3);
        _messageTimer.Start();
    }

    // ---- IShellPage (Q/X/Esc: "Stop all processes and exit? (y/n)") ------

    public async Task<bool> RequestCloseAsync(ShellViewModel shell)
    {
        if (Engine.AnyRunning || IsBusy)
        {
            if (!Dialogs.Confirm("Exit Lucy", "Stop all processes and exit?", "Stop all & exit", "Cancel", danger: true))
                return false;
            shell.Overlay = "Stopping all processes...";
            try { await Engine.StopAllAsync(); }
            finally { shell.Overlay = null; }
        }
        Engine.FinishTeardown();
        return true;
    }

    public void EmergencyShutdown() => Engine.KillAll();

    /// <summary>Stop everything, remove Lucy (optionally the workspace), then exit.</summary>
    private async Task UninstallAsync()
    {
        var window = Application.Current.MainWindow as MainWindow;
        var removed = await Uninstaller.RunAsync(Workspace.Root, async () =>
        {
            if (Engine.AnyRunning || IsBusy)
            {
                if (window != null) window.Shell.Overlay = "Stopping all processes...";
                try { await Engine.StopAllAsync(); }
                finally { if (window != null) window.Shell.Overlay = null; }
            }
            Engine.FinishTeardown();
        });
        if (removed) window?.ForceClose();
    }
}
