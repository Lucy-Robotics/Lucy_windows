using System.Collections.ObjectModel;
using System.Windows.Threading;
using Lucy.Core;
using Lucy.Mvvm;

namespace Lucy.Launcher;

/// <summary>
/// The Windows "Lucy Control Center": owns the package selection, the running sessions and their
/// status. Behaviour follows launcher/apply.py (apply_changes, stop_all_packages) and
/// launcher/state.py (get_pkg_status, StatusPoller) — tmux windows become job-owned processes.
/// All members are used from the UI thread; only readiness probes run in the background.
/// </summary>
public sealed class LauncherEngine : ObservableObject
{
    private static readonly TimeSpan CoreGrace = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan WindowGrace = TimeSpan.FromSeconds(4);

    private readonly Dictionary<string, Session> _pkgSessions = new();
    private readonly HashSet<string> _runningModifiers = new();
    private readonly HashSet<Task> _pendingStops = new();
    private readonly DispatcherTimer _tick;
    private Dictionary<string, string> _env = new();
    private string _pixi = "pixi";
    private int _generation;
    private bool _probeInFlight;
    private DateTime _nextProbe = DateTime.MinValue;
    private DispatcherTimer? _sweepTimer;

    public LauncherEngine(Workspace ws)
    {
        Ws = ws;
        State = new SelectionState(Array.Empty<PackageItem>());
        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _tick.Tick += (_, _) => OnTick();
    }

    public Workspace Ws { get; }
    public SelectionState State { get; private set; }
    public ObservableCollection<Session> Sessions { get; } = new();
    public PortResolver.Result? Ports { get; private set; }
    public IReadOnlyDictionary<string, string> Environment => _env;

    /// <summary>Status-bar messages (TUI bottom line).</summary>
    public event Action<string, bool>? Message;
    public event Action? StatusChanged;

    private bool _busy;
    /// <summary>An Update/Rebuild task is running.</summary>
    public bool IsBusy { get => _busy; private set => Set(ref _busy, value); }

    public bool AnyRunning => _pkgSessions.Values.Any(s => s.IsAlive || s.TreeAlive) || _pendingStops.Count > 0;

    public bool HasUnappliedChanges => State.Packages.Any(p =>
        p.StartedAt == null && p.StopRequestedAt == null && p.Selected != p.IsRunning && p.IsSupported);

    // ---- loading ---------------------------------------------------------

    /// <summary>(Re)load .env, ports and launcher_config.json. Only while nothing runs.</summary>
    public void Load()
    {
        _env = Ws.CreateEnvironment();
        Ports = PortResolver.Resolve(Ws, _env);
        foreach (var kv in Ports.Env) _env[kv.Key] = kv.Value;
        // Natively there is no port mapping: make vite listen where the URL points.
        if (Ports.HostPort != Ports.ContainerPort) _env["VITE_PORT"] = Ports.HostPort.ToString();
        _pixi = Tooling.PixiPath() ?? throw new InvalidOperationException("pixi was not found. Run Update to reinstall it.");

        var dev = Ws.DevMode;
        var configs = LauncherConfigLoader.Load(Ws).Where(c => LauncherConfigLoader.IsVisible(c, dev, Ws, _env)).ToList();
        var reasons = configs.ToDictionary(c => c.Id, WindowsCommandMapper.UnsupportedReason);
        // Anything that depends on an unsupported package is unsupported too (headless → gazebo).
        bool changed;
        do
        {
            changed = false;
            foreach (var c in configs.Where(c => reasons[c.Id] == null))
            {
                var bad = c.Dependencies.FirstOrDefault(d => reasons.TryGetValue(d, out var r) && r != null);
                if (bad == null) continue;
                reasons[c.Id] = $"Needs {configs.First(x => x.Id == bad).Name.Trim('.', ' ')}, which is not supported on Windows";
                changed = true;
            }
        } while (changed);

        var items = configs.Select(c => new PackageItem(c, reasons[c.Id])
        {
            Url = ExpandUrl(c.Url),
            Hint = WindowsCommandMapper.NeedsTerminal(c) && !c.IsModifier ? "opens in its own window" : "",
        }).ToList();
        State = new SelectionState(items);
        State.RestoreSelection(StateFiles.LoadSelection(Ws));
        State.DefaultRobotSelection();
        OnPropertyChanged(nameof(State));
        _tick.Start();
    }

    private string ExpandUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        var e = ShellEnv.Expand(url, _env);
        return e.Contains("${") || e.EndsWith(':') ? "" : e;
    }

    /// <summary>tui.py production mode: Core + Control Panel started at launch when DEV is not true.</summary>
    public async Task AutoStartProductionAsync()
    {
        if (Ws.DevMode) return;
        if (State.Get("core") is { IsSupported: true } core) core.Selected = true;
        if (State.Get("control_panel") is { } lcp) lcp.Selected = true;
        State.DefaultRobotSelection();
        State.RefreshCanEnable();
        await ApplyAsync();
        Message?.Invoke("Starting default services for production mode...", false);
    }

    public string? Toggle(PackageItem pkg)
    {
        var msg = State.Toggle(pkg.Id);
        StatusChanged?.Invoke();
        return msg;
    }

    // ---- apply (launcher/apply.py apply_changes) -------------------------

    public async Task ApplyAsync()
    {
        _generation++;
        var core = State.Get("core");
        var modifiers = State.Packages.Where(p => p.Config.IsModifier).ToList();

        var modifiersChanged = false;
        if (core is { Selected: true })
        {
            var selected = modifiers.Where(m => m.Selected).Select(m => m.Id).ToHashSet();
            modifiersChanged = !selected.SetEquals(_runningModifiers);
        }

        if (modifiersChanged && core is { Selected: true } && _pkgSessions.ContainsKey("core"))
        {
            Message?.Invoke("Restarting core with the new options...", false);
            core.StopRequestedAt = DateTime.Now;
            core.Status = PackageStatus.Stopping;
            await StopSessionAsync(core, isCore: true);
            core.StopRequestedAt = null;
            await SweepOrphansAsync();
            ResetModifiers(modifiers);
            core.IsRunning = false;
            ResetTiming(core);
        }

        foreach (var pkg in State.Packages)
        {
            if (pkg.Selected || !pkg.IsRunning) continue;
            var stopping = true;
            if (pkg.Config.IsCore)
            {
                Track(StopSessionAsync(pkg, isCore: true).ContinueWith(_ => OnUi(ScheduleSweep)));
                ResetModifiers(modifiers);
            }
            else if (pkg.Config.IsInterface || pkg.Config.IsTool)
            {
                Track(StopSessionAsync(pkg, isCore: false));
            }
            else
            {
                if (pkg.Config.LifecycleHooks.ContainsKey("stop"))
                    Log.Info($"lifecycle stop hook of {pkg.Id} is a POSIX command; skipped on Windows");
                stopping = false;
            }
            ResetTiming(pkg);
            if (stopping) pkg.StopRequestedAt = DateTime.Now;
            else pkg.IsRunning = false;
        }

        foreach (var pkg in State.Packages)
        {
            var crashed = pkg.Exited && pkg.ExitCode != 0;
            if (!pkg.Selected || pkg.StopRequestedAt != null || (pkg.IsRunning && !crashed)) continue;
            if (!pkg.IsSupported) { pkg.Selected = false; continue; }

            if (_pkgSessions.TryGetValue(pkg.Id, out var dead) && !dead.TreeAlive)
            {
                dead.DisposeProcess();
                _pkgSessions.Remove(pkg.Id);
            }
            pkg.Exited = false;
            pkg.ExitCode = null;

            try
            {
                if (pkg.Config.IsCore)
                {
                    var selectedMods = modifiers.Where(m => m.Selected).ToList();
                    var plan = WindowsCommandMapper.ForCore(pkg.Config, selectedMods.Select(m => m.Config), _pixi);
                    StartSession(pkg, plan);
                    _runningModifiers.Clear();
                    foreach (var m in selectedMods) _runningModifiers.Add(m.Id);
                    StateFiles.SaveModifiers(Ws, _runningModifiers);
                    MarkStarted(pkg);
                    foreach (var m in selectedMods) { MarkStarted(m); m.IsRunning = true; }
                }
                else if (pkg.Config.IsInterface || pkg.Config.IsTool)
                {
                    StartSession(pkg, WindowsCommandMapper.ForPackage(pkg.Config, _pixi));
                    MarkStarted(pkg);
                }
                pkg.IsRunning = true;
            }
            catch (Exception ex)
            {
                Log.Error($"could not start {pkg.Id}", ex);
                Message?.Invoke($"Could not start {pkg.Name}: {ex.Message}", true);
                pkg.Exited = true;
                pkg.ExitCode = -1;
                pkg.IntendedRunning = true;
            }
        }

        StateFiles.SaveSelection(Ws, State.Packages.Where(p => p.Selected).Select(p => p.Id));
        RequestProbe();
        UpdateStatuses();
    }

    private void Track(Task t)
    {
        _pendingStops.Add(t);
        t.ContinueWith(_ => OnUi(() => { _pendingStops.Remove(t); RequestProbe(); UpdateStatuses(); }));
    }

    private void ResetModifiers(List<PackageItem> modifiers)
    {
        _runningModifiers.Clear();
        StateFiles.SaveModifiers(Ws, Array.Empty<string>());
        foreach (var m in modifiers)
        {
            m.IsRunning = false;
            m.Ready = false;
            ResetTiming(m);
        }
    }

    private static void ResetTiming(PackageItem p)
    {
        p.StartedAt = null;
        p.IntendedRunning = false;
    }

    private static void MarkStarted(PackageItem p)
    {
        p.StartedAt = DateTime.Now;
        p.IntendedRunning = true;
        p.Ready = false;
        p.Exited = false;
        p.ExitCode = null;
    }

    private void StartSession(PackageItem pkg, LaunchPlan plan)
    {
        var session = Sessions.FirstOrDefault(s => s.Id == pkg.Id && s.Process == null)
                      ?? Sessions.FirstOrDefault(s => s.Id == pkg.Id && !s.TreeAlive);
        if (session == null || session.Mode != plan.Mode)
        {
            if (session != null) Sessions.Remove(session);
            session = new Session(pkg.Id, pkg.Name.Trim('.', ' '), plan.Mode);
            session.Exited += OnSessionExited;
            Sessions.Add(session);
        }
        else
        {
            session.DisposeProcess();
        }

        var env = new Dictionary<string, string>(_env, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in plan.ExtraEnv) env[kv.Key] = kv.Value;
        session.Start(new ProcessSpec
        {
            FileName = plan.FileName,
            Arguments = plan.Arguments,
            RawArguments = plan.RawArguments,
            WorkingDirectory = Ws.Root,
            Environment = env,
            Mode = plan.Mode,
            Title = $"Lucy — {pkg.Name.Trim('.', ' ')}",
        }, plan.Display);
        _pkgSessions[pkg.Id] = session;
    }

    private void OnSessionExited(Session session, int code)
    {
        if (session.StopRequested) return; // our own stop; StopSessionAsync cleans up
        var pkg = State.Get(session.Id);
        if (pkg == null || !_pkgSessions.TryGetValue(session.Id, out var current) || !ReferenceEquals(current, session)) return;

        if (pkg.Config.IsTool || code == 0 || (session.IsExternalWindow && code == unchecked((int)0xC000013A)))
        {
            // Finished or its window was closed: no longer running. Tools are unticked, like closing
            // a tmux tool window; others stay ticked so Apply restarts them.
            session.DisposeProcess();
            _pkgSessions.Remove(session.Id);
            pkg.IsRunning = false;
            ResetTiming(pkg);
            if (pkg.Config.IsTool || session.IsExternalWindow) pkg.Selected = false;
            if (pkg.Config.IsCore) ResetModifiers(State.Packages.Where(p => p.Config.IsModifier).ToList());
            State.RefreshCanEnable();
        }
        else
        {
            // remain-on-exit: keep the dead "pane" so the row reads CRASHED until re-applied.
            pkg.Exited = true;
            pkg.ExitCode = code;
            Message?.Invoke($"{pkg.Name.Trim('.', ' ')} exited with code {code} — see its log tab", true);
        }
        UpdateStatuses();
    }

    /// <summary>tmux.py _stop_core / _stop_tmux_window: Ctrl+C, wait, then kill the tree.</summary>
    private async Task StopSessionAsync(PackageItem pkg, bool isCore)
    {
        if (!_pkgSessions.TryGetValue(pkg.Id, out var session)) return;
        if (isCore && session.Process != null)
        {
            // pkill -9 -x rviz2 first: it ignores SIGINT while its GL context is wedged.
            foreach (var p in session.Process.SnapshotTree().Where(p => p.Name.Equals("rviz2", StringComparison.OrdinalIgnoreCase)))
                ProcessInspector.KillTree(p.Id);
        }
        await session.StopAsync(isCore ? CoreGrace : WindowGrace);
        if (_pkgSessions.TryGetValue(pkg.Id, out var current) && ReferenceEquals(current, session))
        {
            session.DisposeProcess();
            _pkgSessions.Remove(pkg.Id);
        }
        pkg.IsRunning = false;
        pkg.Exited = false;
        pkg.ExitCode = null;
    }

    private void ScheduleSweep()
    {
        _sweepTimer?.Stop();
        _sweepTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _sweepTimer.Tick += async (_, _) =>
        {
            _sweepTimer?.Stop();
            await SweepOrphansAsync();
        };
        _sweepTimer.Start();
    }

    private async Task SweepOrphansAsync()
    {
        var owned = _pkgSessions.Values.Concat(Sessions)
            .Where(s => s.Process != null)
            .SelectMany(s => s.Process!.GetTreeProcessIds())
            .ToHashSet();
        var killed = await StackGuard.SweepAsync(Ws, owned);
        if (killed > 0) Log.Info($"orphan sweep removed {killed} process(es)");
    }

    // ---- teardown (apply.py stop_all_packages) ---------------------------

    public async Task StopAllAsync()
    {
        _generation++;
        var stops = new List<Task>();
        foreach (var pkg in State.Packages.Where(p => !p.Config.IsCore && _pkgSessions.ContainsKey(p.Id)))
        {
            pkg.StopRequestedAt = DateTime.Now;
            stops.Add(StopSessionAsync(pkg, isCore: false));
        }
        if (State.Get("core") is { } core && _pkgSessions.ContainsKey("core"))
        {
            core.StopRequestedAt = DateTime.Now;
            stops.Add(StopSessionAsync(core, isCore: true));
        }
        // Update / Rebuild tasks, if any.
        foreach (var s in Sessions.Where(s => s.TreeAlive && !_pkgSessions.ContainsValue(s)).ToList())
            stops.Add(s.StopAsync(WindowGrace));
        UpdateStatuses();
        await Task.WhenAll(stops.Concat(_pendingStops.ToList()));
        ResetModifiers(State.Packages.Where(p => p.Config.IsModifier).ToList());
        await StackGuard.SweepAsync(Ws);
        foreach (var p in State.Packages) { p.StopRequestedAt = null; ResetTiming(p); p.IsRunning = false; }
        UpdateStatuses();
    }

    /// <summary>Final cleanup once everything is down (launcher exit).</summary>
    public void FinishTeardown()
    {
        _tick.Stop();
        StateFiles.Delete(Ws.ModifiersFile);
        StateFiles.Delete(Ws.PidFile);
        StackGuard.PruneRosLogs();
        foreach (var s in Sessions) { s.DisposeProcess(); s.Log.Stop(); }
    }

    /// <summary>Hard kill without waiting (logoff / shutdown).</summary>
    public void KillAll()
    {
        foreach (var s in Sessions) s.Kill();
        StateFiles.Delete(Ws.ModifiersFile);
        StateFiles.Delete(Ws.PidFile);
    }

    // ---- tasks: Update / Rebuild (Lucy.py menu) ----------------------------

    public async Task<bool> RunTaskAsync(string id, string title, IEnumerable<(string file, string[] args, IDictionary<string, string>? env)> steps)
    {
        if (IsBusy) return false;
        IsBusy = true;
        var session = Sessions.FirstOrDefault(s => s.Id == id);
        if (session == null)
        {
            session = new Session(id, title, ConsoleMode.Captured);
            Sessions.Add(session);
        }
        TaskSessionActivated?.Invoke(session);
        try
        {
            foreach (var (file, args, extra) in steps)
            {
                var env = new Dictionary<string, string>(_env, StringComparer.OrdinalIgnoreCase);
                if (extra != null) foreach (var kv in extra) env[kv.Key] = kv.Value;
                var spec = new ProcessSpec { FileName = file, Arguments = args, WorkingDirectory = Ws.Root, Environment = env };
                session.DisposeProcess();
                session.Start(spec, spec.ToString());
                var code = await session.Process!.Completion;
                if (session.StopRequested) return false;
                if (code != 0)
                {
                    session.Log.AppendTimestamped($"{title} failed (exit code {code}).");
                    return false;
                }
            }
            session.Log.AppendTimestamped($"{title} complete.");
            return true;
        }
        catch (Exception ex)
        {
            session.Log.AppendTimestamped($"{title} failed: {ex.Message}");
            Log.Error($"{title} failed", ex);
            return false;
        }
        finally
        {
            session.DisposeProcess();
            IsBusy = false;
        }
    }

    public event Action<Session>? TaskSessionActivated;

    public void RemoveSessionTab(Session s)
    {
        if (s.TreeAlive) return;
        Sessions.Remove(s);
        s.Log.Stop();
    }

    // ---- status (state.py) ---------------------------------------------

    private void OnTick()
    {
        UpdateStatuses();
        var transitions = State.Packages.Any(p => p.StartedAt != null || p.StopRequestedAt != null);
        var settled = State.Packages.Any(p => p.IntendedRunning);
        if (!transitions && !settled) return;
        if (_probeInFlight || DateTime.Now < _nextProbe) return;
        _nextProbe = DateTime.Now + (transitions ? TimeSpan.FromSeconds(0.5) : TimeSpan.FromSeconds(5));
        _ = ProbeAsync();
    }

    public void RequestProbe() => _nextProbe = DateTime.MinValue;

    private sealed record ProbeInput(string Id, PackageConfig Config, ManagedProcess? Tree, bool IsRunning, bool ExternalWindow);
    private sealed record ProbeOutput(string Id, bool Ready, string Stage);

    private async Task ProbeAsync()
    {
        _probeInFlight = true;
        var generation = _generation;
        try
        {
            _pkgSessions.TryGetValue("core", out var coreSession);
            var inputs = State.Packages.Select(p =>
            {
                ManagedProcess? tree = p.Config.IsModifier ? coreSession?.Process
                    : _pkgSessions.TryGetValue(p.Id, out var s) ? s.Process : null;
                var external = _pkgSessions.TryGetValue(p.Id, out var s2) && s2.IsExternalWindow;
                return new ProbeInput(p.Id, p.Config, tree, p.IsRunning && !p.Exited, external);
            }).ToList();
            var env = new Dictionary<string, string>(_env, StringComparer.OrdinalIgnoreCase);
            var root = Ws.Root;
            var outputs = await Task.Run(() => inputs.Select(i => Probe(i, env, root)).ToList());
            if (generation != _generation) return; // describes the world before the last Apply
            foreach (var o in outputs)
            {
                var pkg = State.Get(o.Id);
                if (pkg == null) continue;
                pkg.Ready = o.Ready;
                pkg.StageText = o.Stage;
            }
            UpdateStatuses();
        }
        catch (Exception ex) { Log.Warn($"probe failed: {ex.Message}"); }
        finally { _probeInFlight = false; }
    }

    private static ProbeOutput Probe(ProbeInput i, IDictionary<string, string> env, string root)
    {
        if (!i.IsRunning) return new ProbeOutput(i.Id, false, "");
        if (i.Tree != null && !i.Tree.IsTreeAlive) return new ProbeOutput(i.Id, false, "");
        var ctx = new ProbeContext(env, root, () => i.Tree?.SnapshotTree() ?? new List<ProcessSnapshot>());

        if (!string.IsNullOrEmpty(i.Config.ReadinessCheckWin))
            return new ProbeOutput(i.Id, ReadinessProbe.Evaluate(i.Config.ReadinessCheckWin, ctx), "");
        if (i.Config.ReadinessStages.Count > 0)
        {
            var stages = i.Config.ReadinessStages;
            for (var n = 0; n < stages.Count; n++)
            {
                if (!ReadinessProbe.Evaluate(stages[n].Check, ctx))
                    return new ProbeOutput(i.Id, false, $"{n + 1}/{stages.Count} {stages[n].Label}...");
            }
            return new ProbeOutput(i.Id, true, "");
        }
        if (!string.IsNullOrEmpty(i.Config.ReadinessCheck))
            return new ProbeOutput(i.Id, ReadinessProbe.Evaluate(i.Config.ReadinessCheck, ctx), "");
        return new ProbeOutput(i.Id, true, "");
    }

    /// <summary>state.py get_pkg_status, evaluated for every row.</summary>
    public void UpdateStatuses()
    {
        var now = DateTime.Now;
        var coreDead = _pkgSessions.TryGetValue("core", out var cs) && !cs.IsAlive && cs.ExitCode is not null and not 0;
        foreach (var pkg in State.Packages)
        {
            // Running = the session (tmux window) exists, or for modifiers: core runs with it.
            pkg.IsRunning = pkg.Config.IsModifier
                ? _runningModifiers.Contains(pkg.Id)
                : _pkgSessions.ContainsKey(pkg.Id) || (pkg.Exited && pkg.ExitCode == -1);

            var exited = pkg.Config.IsModifier ? coreDead && pkg.IsRunning : pkg.Exited;
            var exitCode = pkg.Config.IsModifier ? cs?.ExitCode : pkg.ExitCode;
            pkg.Status = ComputeStatus(pkg, now, exited, exitCode);
            if (pkg.Status != PackageStatus.Loading) pkg.StageText = "";
        }
        StatusChanged?.Invoke();
    }

    private static PackageStatus ComputeStatus(PackageItem pkg, DateTime now, bool exited, int? exitCode)
    {
        if (pkg.StopRequestedAt is { } stopAt)
        {
            if (!pkg.IsRunning) { pkg.StopRequestedAt = null; return PackageStatus.Stopped; }
            if ((now - stopAt).TotalSeconds < LauncherConstants.StoppingTimeout) return PackageStatus.Stopping;
            pkg.StopRequestedAt = null;
        }
        if (exited)
        {
            pkg.StartedAt = null;
            if (exitCode == 0) { pkg.IntendedRunning = false; return PackageStatus.Stopped; }
            return PackageStatus.Crashed;
        }
        if (pkg.Ready && pkg.IsRunning)
        {
            pkg.StartedAt = null;
            return PackageStatus.Running;
        }
        if (pkg.IntendedRunning)
        {
            if (!pkg.IsRunning) { pkg.StartedAt = null; return PackageStatus.Crashed; }
            pkg.StartedAt ??= now;
            return (now - pkg.StartedAt.Value).TotalSeconds < pkg.Config.ReadinessTimeout
                ? PackageStatus.Loading
                : PackageStatus.Crashed;
        }
        return PackageStatus.Stopped;
    }
}
