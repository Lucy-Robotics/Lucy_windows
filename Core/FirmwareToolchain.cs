using System.Net.Http;

namespace Lucy.Core;

/// <summary>
/// Workaround for lucy_ws scripts/firmware_setup.py on Windows: it downloads rustup-init as
/// "lucy-rustup-init.exe", but rustup picks its mode from its own file name, so it runs as a proxy
/// and fails ("unknown proxy name: 'lucy-rustup-init'"). Lucy runs install.py with
/// LUCY_SKIP_FIRMWARE_SETUP=1, installs rustup itself (correctly named, same location and options),
/// then runs `pixi run firmware-setup`, which finds rustup and does the rest. Once upstream is fixed
/// the rustup step is simply skipped (already installed).
/// </summary>
public static class FirmwareToolchain
{
    public const string RustupInitUrl = "https://win.rustup.rs/x86_64";
    public const string SkipEnv = "LUCY_SKIP_FIRMWARE_SETUP";
    public const string HelperArgument = "--firmware-rustup";

    public static bool IsUsedBy(Workspace ws) => File.Exists(Path.Combine(ws.Root, "scripts", "firmware_setup.py"));

    public static string EnvPrefix(Workspace ws) => Path.Combine(ws.Root, ".pixi", "envs", "default");
    public static string CargoHome(Workspace ws) => Path.Combine(EnvPrefix(ws), "cargo");
    public static string RustupHome(Workspace ws) => Path.Combine(EnvPrefix(ws), "rustup");
    public static bool HasRustup(Workspace ws) => File.Exists(Path.Combine(CargoHome(ws), "bin", "rustup.exe"));

    /// <summary>Same as firmware_setup.py install_rustup, with the installer named rustup-init.exe.</summary>
    public static async Task<bool> InstallRustupAsync(Workspace ws, Action<string> log, CancellationToken ct = default)
    {
        if (HasRustup(ws))
        {
            log("rustup already installed in the pixi environment.");
            return true;
        }
        if (!Directory.Exists(EnvPrefix(ws)))
        {
            log($"error: the pixi environment {EnvPrefix(ws)} does not exist yet.");
            return false;
        }
        var tmp = Directory.CreateTempSubdirectory("lucy-rustup").FullName;
        var init = Path.Combine(tmp, "rustup-init.exe"); // the name matters: rustup reads argv[0]
        try
        {
            log($"Downloading {RustupInitUrl} ...");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Lucy-windows");
                await using var src = await http.GetStreamAsync(RustupInitUrl, ct);
                await using var dst = File.Create(init);
                await src.CopyToAsync(dst, ct);
            }
            Directory.CreateDirectory(CargoHome(ws));
            Directory.CreateDirectory(RustupHome(ws));
            var env = ws.CreateEnvironment(new Dictionary<string, string>
            {
                ["CARGO_HOME"] = CargoHome(ws),
                ["RUSTUP_HOME"] = RustupHome(ws),
            });
            log("Installing rustup into the pixi environment (stable, minimal profile)...");
            var r = await ProcessRunner.RunAsync(init,
                new[] { "-y", "--default-toolchain", "stable", "--profile", "minimal", "--no-modify-path" },
                ws.Root, env, log, ct);
            if (!r.Ok || !HasRustup(ws))
            {
                log($"error: rustup-init failed (exit code {r.ExitCode}).");
                return false;
            }
            log("rustup installed.");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error("rustup install failed", ex);
            log("error: " + ex.Message);
            return false;
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* best effort */ }
        }
    }

    /// <summary>Body of <c>Lucy.exe --firmware-rustup WORKSPACE</c> (launcher Update step).</summary>
    public static int RunHelper(string workspace)
    {
        var ok = InstallRustupAsync(new Workspace(workspace), line => Console.Out.WriteLine(line)).GetAwaiter().GetResult();
        Console.Out.Flush();
        return ok ? 0 : 1;
    }
}
