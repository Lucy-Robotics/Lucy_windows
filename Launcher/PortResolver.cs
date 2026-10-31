using System.Net;
using System.Net.NetworkInformation;
using Lucy.Core;

namespace Lucy.Launcher;

/// <summary>Port of launch_lucy.sh's control-panel port/scheme resolution (LUCY_LCP_*).</summary>
public static class PortResolver
{
    public sealed record Result(int HostPort, int ContainerPort, string Scheme)
    {
        public IReadOnlyDictionary<string, string> Env => new Dictionary<string, string>
        {
            ["LUCY_LCP_PUBLISHED_HOST_PORT"] = HostPort.ToString(),
            ["LUCY_LCP_CONTAINER_PORT"] = ContainerPort.ToString(),
            ["LUCY_LCP_SCHEME"] = Scheme,
        };
    }

    public static Result Resolve(Workspace ws, IDictionary<string, string> env, Func<int, bool>? inUse = null)
    {
        inUse ??= IsPortInUse;
        var panelEnv = DotEnv.Read(Path.Combine(ws.ControlPanelDir, ".env"));

        var container = 4004;
        if (env.TryGetValue("PORT_CONTROL_PANEL_CONTAINER", out var c) && int.TryParse(c, out var ci)) container = ci;
        else if (panelEnv.TryGetValue("VITE_PORT", out var vp) && int.TryParse(vp, out var vpi)) container = vpi;

        var start = env.TryGetValue("PORT_CONTROL_PANEL", out var pc) && int.TryParse(pc, out var pci) ? pci : container;
        var host = start;
        for (var p = start; p <= start + 50; p++)
        {
            if (!inUse(p)) { host = p; break; }
        }

        string scheme;
        if (env.TryGetValue("LUCY_LCP_SCHEME", out var s) && s.Length > 0) scheme = s;
        else scheme = panelEnv.TryGetValue("VITE_HTTPS", out var https) && https.Equals("true", StringComparison.OrdinalIgnoreCase) ? "https" : "http";

        return new Result(host, container, scheme);
    }

    public static bool IsPortInUse(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);
        }
        catch { return ReadinessProbe.IsPortOpen(port, 150); }
    }
}
