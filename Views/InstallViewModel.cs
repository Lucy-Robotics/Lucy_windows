using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Input;
using Lucy.Core;
using Lucy.Mvvm;

namespace Lucy.Views;

public enum InstallMode { Install, Resume }
public enum InstallPhase { Setup, Running, Done, Failed }
public enum StepState { Pending, Running, Done, Failed, Skipped }

public sealed class InstallStep : ObservableObject
{
    public InstallStep(string key, string title, double weight)
    {
        Key = key;
        Title = title;
        Weight = weight;
    }

    public string Key { get; }
    public string Title { get; }
    public double Weight { get; }

    private StepState _state;
    public StepState State { get => _state; set => Set(ref _state, value); }

    private string _detail = "";
    public string Detail { get => _detail; set => Set(ref _detail, value); }
}

/// <summary>
/// Installer: host prerequisites (Git, Python, pixi, MSVC) → clone lucy_ws → lucy_ws/install.py
/// (repos, pixi install, colcon build, yarn) → Start Menu integration. Every child process runs in
/// a job object, so cancelling or closing the window kills git / pixi / colcon / yarn trees.
/// </summary>
public sealed class InstallViewModel : ObservableObject, IShellPage
{
    private readonly ShellViewModel _shell;
    private CancellationTokenSource? _cts;
    private Task? _run;

    public InstallViewModel(ShellViewModel shell, InstallMode mode, string? workspace)
    {
        _shell = shell;
        Mode = mode;
        _installPath = workspace ?? AppPaths.DefaultWorkspace;
        if (workspace != null && File.Exists(Path.Combine(workspace, ".env")))
            _developerMode = DotEnv.GetDevMode(Path.Combine(workspace, ".env"));
        Branches.Add(AppPaths.DefaultBranch);
        _selectedBranch = AppPaths.DefaultBranch;
        ValidatePath();

        BrowseCommand = new RelayCommand(Browse, () => Phase == InstallPhase.Setup);
        InstallCommand = new RelayCommand(Start, () => Phase is InstallPhase.Setup or InstallPhase.Failed && PathError == null);
        CancelCommand = new AsyncCommand(CancelAsync, () => Phase == InstallPhase.Running);
        BackCommand = new RelayCommand(() => Phase = InstallPhase.Setup, () => Phase == InstallPhase.Failed);
        OpenLauncherCommand = new RelayCommand(OpenLauncher, () => Phase == InstallPhase.Done);
        RefreshBranchesCommand = new AsyncCommand(LoadBranchesAsync, () => Phase == InstallPhase.Setup);
        OpenLogsCommand = new RelayCommand(() => Integration.OpenFolder(Core.Log.Directory));
        OpenWebsiteCommand = new RelayCommand(() => Integration.OpenUrl(AppPaths.WebsiteUrl + "/download"));
    }

    public string Subtitle => Mode == InstallMode.Resume ? "RESUME INSTALL" : "INSTALLER";
    public InstallMode Mode { get; }

    public ICommand BrowseCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand OpenLauncherCommand { get; }
    public ICommand RefreshBranchesCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand OpenWebsiteCommand { get; }

    public LogBuffer Log { get; } = new();
    public ObservableCollection<InstallStep> Steps { get; } = new();
    public ObservableCollection<string> Branches { get; } = new();

    private InstallPhase _phase;
    public InstallPhase Phase
    {
        get => _phase;
        set
        {
            if (!Set(ref _phase, value)) return;
            OnPropertyChanged(nameof(IsSetup));
            OnPropertyChanged(nameof(IsProgress));
            OnPropertyChanged(nameof(IsDone));
            OnPropertyChanged(nameof(IsFailed));
            OnPropertyChanged(nameof(IsRunning));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsSetup => Phase == InstallPhase.Setup;
    public bool IsProgress => Phase is InstallPhase.Running or InstallPhase.Done or InstallPhase.Failed;
    public bool IsDone => Phase == InstallPhase.Done;
    public bool IsFailed => Phase == InstallPhase.Failed;
    public bool IsRunning => Phase == InstallPhase.Running;

    private string _installPath;
    public string InstallPath
    {
        get => _installPath;
        set { if (Set(ref _installPath, value)) ValidatePath(); }
    }

    private string? _pathError;
    public string? PathError { get => _pathError; private set { Set(ref _pathError, value); OnPropertyChanged(nameof(HasPathError)); } }
    public bool HasPathError => PathError != null;

    private string _pathNote = "";
    public string PathNote { get => _pathNote; private set => Set(ref _pathNote, value); }

    private bool _developerMode;
    public bool DeveloperMode { get => _developerMode; set => Set(ref _developerMode, value); }

    private bool _showAdvanced;
    public bool ShowAdvanced
    {
        get => _showAdvanced;
        set
        {
            if (Set(ref _showAdvanced, value) && value && Branches.Count <= 1) RefreshBranchesCommand.Execute(null);
        }
    }

    private string _selectedBranch;
    public string SelectedBranch { get => _selectedBranch; set => Set(ref _selectedBranch, value ?? AppPaths.DefaultBranch); }

    private bool _loadingBranches;
    public bool LoadingBranches { get => _loadingBranches; set => Set(ref _loadingBranches, value); }

    private double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }

    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }

    private string _error = "";
    public string Error { get => _error; set => Set(ref _error, value); }

    private bool _launchWhenDone = true;
    public bool LaunchWhenDone { get => _launchWhenDone; set => Set(ref _launchWhenDone, value); }

    // ---- setup page ----------------------------------------------------

    private void ValidatePath()
    {
        var error = Workspace.ValidateLocation(InstallPath);
        var note = "";
        if (error == null)
        {
            var full = Path.GetFullPath(InstallPath);
            var ws = new Workspace(full);
            if (ws.IsCheckout)
                note = ws.IsInstalled
                    ? "An installed Lucy workspace is here: it will be updated and rebuilt."
                    : "A Lucy workspace is here: the installation will resume in it.";
            else if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
                error = "This folder is not empty. Choose an empty folder or an existing Lucy workspace.";
            else
                note = "Lucy will be cloned here (~15 GB once built).";
        }
        PathError = error;
        PathNote = note;
        CommandManager.InvalidateRequerySuggested();
    }

    private void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose where to install Lucy",
            InitialDirectory = Directory.Exists(InstallPath) ? InstallPath : Path.GetPathRoot(Environment.SystemDirectory),
        };
        if (dlg.ShowDialog() == true)
        {
            var chosen = dlg.FolderName;
            // An empty folder is used as-is; otherwise propose a Lucy subfolder.
            var useAsIs = new Workspace(chosen).IsCheckout || !Directory.EnumerateFileSystemEntries(chosen).Any();
            InstallPath = useAsIs ? chosen : Path.Combine(chosen, "Lucy");
        }
    }

    private async Task LoadBranchesAsync()
    {
        LoadingBranches = true;
        try
        {
            var branches = new List<string>();
            var git = ShellEnv.Which("git");
            if (git != null)
            {
                var r = await ProcessRunner.CaptureAsync(git, new[] { "ls-remote", "--heads", AppPaths.RepoUrl }, timeout: TimeSpan.FromSeconds(30));
                foreach (var line in r.Output.Split('\n'))
                {
                    var idx = line.IndexOf("refs/heads/", StringComparison.Ordinal);
                    if (idx >= 0) branches.Add(line[(idx + 11)..].Trim());
                }
            }
            if (branches.Count == 0)
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Lucy-windows");
                var json = await http.GetStringAsync("https://api.github.com/repos/Lucy-Robotics/lucy_ws/branches?per_page=100");
                using var doc = JsonDocument.Parse(json);
                branches.AddRange(doc.RootElement.EnumerateArray().Select(b => b.GetProperty("name").GetString()!).Where(n => n != null));
            }
            var ordered = branches.Distinct().OrderBy(b => b == "master" ? 0 : b == "dev" ? 1 : 2).ThenBy(b => b).ToList();
            if (ordered.Count == 0) return;
            var keep = SelectedBranch;
            Branches.Clear();
            foreach (var b in ordered) Branches.Add(b);
            SelectedBranch = ordered.Contains(keep) ? keep : ordered[0];
        }
        catch (Exception ex)
        {
            Core.Log.Warn($"branch listing failed: {ex.Message}");
        }
        finally { LoadingBranches = false; }
    }

    // ---- run -----------------------------------------------------------

    private void Start()
    {
        if (PathError != null) return;
        Steps.Clear();
        Log.Clear();
        Error = "";
        Progress = 0;
        Phase = InstallPhase.Running;
        _cts = new CancellationTokenSource();
        _run = RunAsync(_cts.Token);
    }

    private InstallStep AddStep(string key, string title, double weight)
    {
        var s = new InstallStep(key, title, weight);
        Steps.Add(s);
        return s;
    }

    private void Begin(InstallStep step, string? status = null)
    {
        step.State = StepState.Running;
        Status = status ?? step.Title + "...";
        Log.AppendTimestamped("== " + step.Title);
        UpdateProgress();
    }

    private void Complete(InstallStep step, StepState state = StepState.Done, string? detail = null)
    {
        step.State = state;
        if (detail != null) step.Detail = detail;
        UpdateProgress();
    }

    private void UpdateProgress()
    {
        var total = Steps.Sum(s => s.Weight);
        if (total <= 0) return;
        var done = Steps.Where(s => s.State is StepState.Done or StepState.Skipped).Sum(s => s.Weight)
                   + Steps.Where(s => s.State == StepState.Running).Sum(s => s.Weight * 0.35);
        Progress = Math.Min(100, done / total * 100);
    }

    private void LogLine(string line) => Log.Append(line);

    private async Task RunAsync(CancellationToken ct)
    {
        var ws = new Workspace(Path.GetFullPath(InstallPath));
        var branch = SelectedBranch;
        var dev = DeveloperMode;

        var sPrereq = AddStep("prereq", "Check prerequisites", 4);
        var sGit = AddStep("git", "Git", 4);
        var sPython = AddStep("python", "Python 3", 4);
        var sPixi = AddStep("pixi", "pixi (package manager)", 4);
        var sMsvc = AddStep("msvc", "MSVC Build Tools (C++ compiler)", 8);
        var sClone = AddStep("clone", "Download the Lucy workspace", 6);
        var sRepos = AddStep("repos", "Fetch source repositories", 8);
        var sEnv = AddStep("env", "Create the pixi environment (ROS 2 Jazzy)", 30);
        var sBuild = AddStep("build", "Build the ROS workspace (colcon)", 24);
        var sPanel = AddStep("panel", "Install control panel dependencies", 6);
        var sFinal = AddStep("final", "Create Start Menu shortcut", 2);

        try
        {
            // 1. Prerequisites
            Begin(sPrereq, "Checking what is already installed...");
            var winget = Tooling.FindWinget();
            var git = await Tooling.FindGitAsync(ct);
            var python = await Tooling.FindPythonAsync(ct);
            var pixi = await Tooling.FindPixiAsync(ct);
            var msvc = await Tooling.FindMsvcAsync(ct);
            foreach (var t in new[] { git, python, pixi, msvc }) LogLine("  " + t.Display);
            Complete(sPrereq);

            var missing = new[] { git, python, pixi, msvc }.Where(t => !t.Found).ToList();
            if (missing.Count > 0)
            {
                var needsWinget = missing.Any(m => m.Name != "pixi");
                if (needsWinget && !winget.Found)
                    throw new InstallError("winget is required to install the missing tools.",
                        "Install \"App Installer\" from the Microsoft Store (it provides winget), then retry.\nMissing: " +
                        string.Join(", ", missing.Select(m => m.Name)));
                var list = string.Join("\n", missing.Select(m => "  • " + m.Display));
                var hasMsvc = missing.Any(m => m.Name.StartsWith("MSVC"));
                if (!Dialogs.Confirm("Install prerequisites",
                        "Lucy needs these tools, which are not installed yet:\n\n" + list,
                        "Install them", "Cancel",
                        detail: hasMsvc ? "The MSVC Build Tools download is about 1.5 GB and Windows will ask for administrator permission." : null))
                    throw new InstallError("Installation cancelled: prerequisites are required.", null);
            }

            await EnsureTool(sGit, git, () => Tooling.InstallGitAsync(LogLine, ct), () => Tooling.FindGitAsync(ct), ct);
            python = await EnsureTool(sPython, python, () => Tooling.InstallPythonAsync(LogLine, ct), () => Tooling.FindPythonAsync(ct), ct);
            await EnsureTool(sPixi, pixi, () => Tooling.InstallPixiAsync(LogLine, ct), () => Tooling.FindPixiAsync(ct), ct, winget: false);
            await EnsureTool(sMsvc, msvc, () => Tooling.InstallMsvcAsync(LogLine, ct), () => Tooling.FindMsvcAsync(ct), ct);

            // 2. Clone lucy_ws
            if (ws.IsCheckout)
            {
                Complete(sClone, StepState.Skipped, "existing workspace");
                LogLine($"Using the existing workspace at {ws.Root}");
            }
            else
            {
                Begin(sClone, $"Cloning lucy_ws ({branch})...");
                Directory.CreateDirectory(ws.Root);
                var gitExe = ShellEnv.Which("git") ?? throw new InstallError("Git is not available.", null);
                var r = await ProcessRunner.RunAsync(gitExe,
                    new[] { "clone", "--progress", "--branch", branch, "--single-branch", AppPaths.RepoUrl, ws.Root },
                    onLine: LogLine, ct: ct);
                if (!r.Ok) throw new InstallError("Cloning lucy_ws failed.", "Check your internet connection and the selected branch, then retry.");
                Complete(sClone);
            }
            DotEnv.SetDevMode(ws.EnvFile, dev);

            // Remember the workspace now, so an interrupted install can resume.
            var settings = AppSettings.Load();
            settings.Workspace = ws.Root;
            settings.Branch = branch;
            settings.Save();

            // 3. lucy_ws/install.py — the same installer Linux uses.
            await RunInstallPy(ws, python, dev, sRepos, sEnv, sBuild, sPanel, ct);
            if (!ws.IsInstalled)
                throw new InstallError("install.py finished but the workspace is not built (install\\setup.bat missing).", "See the log above.");

            // 4. Finalize
            Begin(sFinal);
            try
            {
                var exe = Integration.InstallSelf();
                Integration.CreateStartMenuShortcut(exe, ws.Root);
                Integration.RegisterUninstall(exe, ws.Root);
                Integration.WriteInstallProfile(ws, branch, dev);
            }
            catch (Exception ex)
            {
                Core.Log.Error("finalize failed", ex);
                LogLine($"warning: Start Menu integration failed: {ex.Message}");
            }
            settings.InstalledAt = DateTime.Now;
            settings.Save();
            Complete(sFinal);

            Progress = 100;
            Status = "Lucy is installed.";
            Log.AppendTimestamped("Installation complete.");
            Phase = InstallPhase.Done;
        }
        catch (OperationCanceledException)
        {
            Fail("Installation cancelled.", "Every process it started has been stopped. Run the installer again to resume.");
        }
        catch (InstallError ex)
        {
            Fail(ex.Message, ex.Hint);
        }
        catch (Exception ex)
        {
            Core.Log.Error("install failed", ex);
            Fail("Installation failed: " + ex.Message, "See the log for details.");
        }
    }

    private void Fail(string message, string? hint)
    {
        foreach (var s in Steps.Where(s => s.State == StepState.Running)) s.State = StepState.Failed;
        Error = hint == null ? message : message + "\n" + hint;
        Status = message;
        Log.AppendTimestamped(message);
        Phase = InstallPhase.Failed;
    }

    private async Task<ToolStatus> EnsureTool(InstallStep step, ToolStatus current, Func<Task<RunResult>> install,
        Func<Task<ToolStatus>> detect, CancellationToken ct, bool winget = true)
    {
        if (current.Found)
        {
            Complete(step, StepState.Skipped, current.Version ?? "found");
            return current;
        }
        Begin(step, $"Installing {current.Name}...");
        var r = await install();
        ct.ThrowIfCancellationRequested();
        var after = await detect();
        if (!after.Found)
        {
            var why = winget && !Tooling.WingetSucceeded(r) ? $"winget exited with code {r.ExitCode}." : after.Problem ?? "not found after installation";
            throw new InstallError($"{current.Name} could not be installed ({why})",
                $"Install {current.Name} manually, then retry.");
        }
        LogLine($"  {after.Display} ready");
        Complete(step, StepState.Done, after.Version);
        return after;
    }

    private async Task RunInstallPy(Workspace ws, ToolStatus python, bool dev, InstallStep sRepos, InstallStep sEnv,
        InstallStep sBuild, InstallStep sPanel, CancellationToken ct)
    {
        var current = sRepos;
        Begin(sRepos, "Fetching source repositories...");

        void Advance(InstallStep next, string status)
        {
            if (ReferenceEquals(current, next) || next.State != StepState.Pending) return;
            OnUi(() =>
            {
                Complete(current);
                current = next;
                Begin(next, status);
            });
        }

        void OnLine(string line)
        {
            LogLine(line);
            if (line.StartsWith("Pixi install") || line.StartsWith("No pixi.lock"))
                Advance(sEnv, "Creating the pixi environment — this downloads several GB, it can take a while...");
            else if (line.StartsWith("Building ROS workspace"))
                Advance(sBuild, "Building the ROS workspace...");
            else if (line.StartsWith("Installing control panel dependencies"))
                Advance(sPanel, "Installing control panel dependencies...");
        }

        var args = new List<string> { "install.py" };
        if (dev) args.Add("--developer");
        var env = ws.CreateEnvironment(new Dictionary<string, string>
        {
            ["LUCY_PIXI_AUTO_UPGRADE"] = "1",
            ["LUCY_MSVC_AUTO_INSTALL"] = "1",
        });
        LogLine($"> {python.Path} {string.Join(" ", args)}");
        var r = await ProcessRunner.RunAsync(python.Path!, args, ws.Root, env, OnLine, ct);
        if (!r.Ok)
            throw new InstallError($"install.py failed (exit code {r.ExitCode}).",
                r.Output.Contains("MANUAL ACTION NEEDED") ? "install.py needs a manual action — see the log." : "See the log above; fix the problem and retry.");
        OnUi(() =>
        {
            foreach (var s in new[] { sRepos, sEnv, sBuild, sPanel })
                if (s.State is StepState.Pending or StepState.Running) Complete(s);
        });
    }

    private async Task CancelAsync()
    {
        if (!Dialogs.Confirm("Cancel installation", "Stop the installation? Running downloads and builds will be stopped.", "Stop", "Continue", danger: true))
            return;
        _cts?.Cancel();
        if (_run != null) await _run;
    }

    private void OpenLauncher()
    {
        var settings = AppSettings.Load();
        if (settings.Workspace == null) return;
        _shell.ShowLauncher(new Workspace(settings.Workspace));
    }

    // ---- IShellPage ----------------------------------------------------

    public async Task<bool> RequestCloseAsync(ShellViewModel shell)
    {
        if (Phase != InstallPhase.Running) return true;
        if (!Dialogs.Confirm("Cancel installation", "The installation is still running. Stop it and close Lucy?", "Stop and close", "Keep installing", danger: true))
            return false;
        shell.Overlay = "Stopping the installation...";
        try
        {
            _cts?.Cancel();
            if (_run != null) await _run;
        }
        finally { shell.Overlay = null; }
        return true;
    }

    public void EmergencyShutdown() => _cts?.Cancel();

    private sealed class InstallError : Exception
    {
        public InstallError(string message, string? hint) : base(message) => Hint = hint;
        public string? Hint { get; }
    }
}
