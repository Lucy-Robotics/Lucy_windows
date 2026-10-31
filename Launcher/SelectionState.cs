namespace Lucy.Launcher;

/// <summary>
/// Package list + tick logic, ported from launcher/state.py (LauncherState) and
/// launcher/apply.py (restore_selection, default_robot_selection).
/// </summary>
public sealed class SelectionState
{
    public SelectionState(IEnumerable<PackageItem> packages)
    {
        Packages = packages.ToList();
        Map = Packages.ToDictionary(p => p.Id);
        RefreshCanEnable();
    }

    public List<PackageItem> Packages { get; }
    private Dictionary<string, PackageItem> Map { get; }

    public PackageItem? Get(string id) => Map.GetValueOrDefault(id);

    public IEnumerable<PackageItem> Robots => Packages.Where(p => !string.IsNullOrEmpty(p.Config.RequiresPkg));

    /// <summary>Space in the TUI. Returns a warning message, or null.</summary>
    public string? Toggle(string id)
    {
        var pkg = Get(id);
        if (pkg == null) return null;
        if (pkg.StopRequestedAt != null && !pkg.Selected) return "Still stopping…";
        if (!pkg.Selected)
        {
            if (!pkg.IsSupported) return pkg.UnsupportedReason;
            var blocked = UnsupportedDependency(pkg);
            if (blocked != null) return $"{pkg.Name} needs {blocked.Name}, which is not supported on Windows";
            EnableWithDeps(pkg);
        }
        else
        {
            if (!string.IsNullOrEmpty(pkg.Config.RequiresPkg))
            {
                if (Robots.Count(r => r.Selected) <= 1) return "One robot package required";
            }
            DisableWithDependents(pkg);
        }
        RefreshCanEnable();
        return null;
    }

    private PackageItem? UnsupportedDependency(PackageItem pkg, HashSet<string>? seen = null)
    {
        seen ??= new HashSet<string>();
        foreach (var depId in pkg.Config.Dependencies)
        {
            if (!seen.Add(depId)) continue;
            var dep = Get(depId);
            if (dep == null) continue;
            if (!dep.IsSupported) return dep;
            var deeper = UnsupportedDependency(dep, seen);
            if (deeper != null) return deeper;
        }
        return null;
    }

    private void Enable(PackageItem pkg)
    {
        foreach (var cid in pkg.Config.Conflicts)
        {
            var c = Get(cid);
            if (c is { Selected: true }) c.Selected = false;
        }
        pkg.Selected = true;
    }

    private void EnableWithDeps(PackageItem pkg)
    {
        foreach (var depId in pkg.Config.Dependencies)
        {
            var dep = Get(depId);
            if (dep is { Selected: false }) EnableWithDeps(dep);
        }
        Enable(pkg);
    }

    private void DisableWithDependents(PackageItem pkg)
    {
        foreach (var other in Packages)
            if (other.Config.Dependencies.Contains(pkg.Id) && other.Selected)
                DisableWithDependents(other);
        pkg.Selected = false;
    }

    public void RefreshCanEnable()
    {
        foreach (var p in Packages)
            p.CanEnable = p.IsSupported && p.Config.Dependencies.All(d => Get(d)?.Selected ?? false);
    }

    /// <summary>apply.py restore_selection: pre-tick from the last applied selection.</summary>
    public void RestoreSelection(ISet<string>? saved)
    {
        if (saved == null) return;
        var robots = Robots.ToList();
        foreach (var p in Packages)
        {
            if (!string.IsNullOrEmpty(p.Config.RequiresPkg)) continue;
            p.Selected = saved.Contains(p.Id) && p.IsSupported;
        }
        var chosen = robots.FirstOrDefault(r => saved.Contains(r.Id));
        if (chosen != null)
            foreach (var r in robots) r.Selected = ReferenceEquals(r, chosen);
        // A dependency may have been dropped (unsupported on Windows): untick what needed it.
        foreach (var p in Packages.Where(p => p.Selected).ToList())
            if (p.Config.Dependencies.Any(d => Get(d) is { Selected: false }))
                DisableWithDependents(p);
        RefreshCanEnable();
    }

    /// <summary>apply.py default_robot_selection: auto-tick a robot when none is selected.</summary>
    public void DefaultRobotSelection()
    {
        var core = Get("core");
        var robots = Robots.ToList();
        if (robots.Count == 0 || core is not { Selected: true }) return;
        if (robots.Any(r => r.Selected)) return;
        if (robots.Count == 1) robots[0].Selected = true;
        else if (Get("robot_inmoov") is { } inmoov) inmoov.Selected = true;
        RefreshCanEnable();
    }

    /// <summary>tui.py ordering: primary (core + modifiers w/o requires_pkg), robots, interfaces, tools.</summary>
    public IReadOnlyList<(string Section, List<PackageItem> Items)> Sections()
    {
        var primary = Packages.Where(p => (p.Config.IsCore || p.Config.IsModifier) && string.IsNullOrEmpty(p.Config.RequiresPkg)).ToList();
        var robots = Robots.Where(p => p.Config.IsModifier).ToList();
        var interfaces = Packages.Where(p => p.Config.IsInterface).ToList();
        var tools = Packages.Where(p => p.Config.IsTool).ToList();
        foreach (var p in primary) p.IndentLevel = p.Config.Subitem ? 2 : p.Config.IsModifier ? 1 : 0;
        foreach (var p in robots) p.IndentLevel = p.Config.Subitem ? 2 : 1;
        foreach (var p in interfaces.Concat(tools)) p.IndentLevel = p.Config.Subitem ? 2 : 0;
        var result = new List<(string, List<PackageItem>)> { ("Primary Launch Targets", primary) };
        if (robots.Count > 0) result.Add(("Robot", robots));
        result.Add(("Interfaces", interfaces));
        result.Add(("Tools", tools));
        return result;
    }
}
