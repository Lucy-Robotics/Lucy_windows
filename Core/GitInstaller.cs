using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Lucy.Core;

/// <summary>
/// Installs Git for the current user, without admin rights (like pixi and Python):
///   1. winget Git.Git --scope user (Git for Windows in %LOCALAPPDATA%\Programs\Git), when winget exists;
///   2. otherwise, or if that fails, the official portable MinGit from the git-for-windows GitHub
///      release, checked against its SHA-256 and extracted to %LOCALAPPDATA%\Programs\MinGit.
/// </summary>
public static class GitInstaller
{
    public const string ReleaseApi = "https://api.github.com/repos/git-for-windows/git/releases/latest";

    public static string MinGitDir => Path.Combine(ShellEnv.LocalAppData, "Programs", "MinGit");
    public static string MinGitCmd => Path.Combine(MinGitDir, "cmd");

    public static async Task<RunResult> InstallAsync(Action<string> log, CancellationToken ct)
    {
        if (Tooling.FindWinget().Found)
        {
            var r = await Tooling.Winget(new[] { "install", "--id", "Git.Git", "--scope", "user" }, log, ct);
            if (Tooling.WingetSucceeded(r) && (await Tooling.FindGitAsync(ct)).Found) return r;
            log($"winget could not install Git (exit code {r.ExitCode}); falling back to the portable MinGit.");
        }
        else
        {
            log("winget is not available; installing the portable MinGit.");
        }
        return await InstallMinGitAsync(log, ct);
    }

    public static Task<RunResult> InstallMinGitAsync(Action<string> log, CancellationToken ct) =>
        InstallMinGitAsync(log, ct, MinGitDir, updateUserPath: true);

    /// <param name="targetDir">Extraction folder (tests use a temp folder).</param>
    /// <param name="updateUserPath">Add targetDir\cmd to HKCU PATH.</param>
    internal static async Task<RunResult> InstallMinGitAsync(Action<string> log, CancellationToken ct, string targetDir, bool updateUserPath)
    {
        var cmdDir = Path.Combine(targetDir, "cmd");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Lucy-windows");

            log("Looking up the latest Git for Windows release...");
            var json = await http.GetStringAsync(ReleaseApi, ct);
            var asset = PickAsset(json) ?? throw new InvalidOperationException("no 64-bit MinGit zip in the latest Git for Windows release");
            log($"Downloading {asset.Name} ({asset.Size / 1024 / 1024} MB)...");

            var zip = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-{asset.Name}");
            await DownloadAsync(http, asset.Url, zip, log, ct);

            if (asset.Sha256 != null)
            {
                var actual = await Sha256Async(zip, ct);
                if (!actual.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(zip);
                    throw new InvalidOperationException($"checksum mismatch for {asset.Name} (expected {asset.Sha256}, got {actual})");
                }
                log("SHA-256 verified.");
            }
            else log("warning: the release publishes no SHA-256 for this file; relying on HTTPS only.");

            log($"Extracting to {targetDir}...");
            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true); // only ever ours
            ZipFile.ExtractToDirectory(zip, targetDir);
            File.Delete(zip);
            if (!File.Exists(Path.Combine(cmdDir, "git.exe")))
                throw new InvalidOperationException("git.exe missing from the extracted archive");

            if (updateUserPath)
            {
                AddToUserPath(cmdDir);
                log($"Added {cmdDir} to your user PATH.");
            }
            return new RunResult(0, "MinGit installed");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error("MinGit install failed", ex);
            log("error: " + ex.Message);
            return new RunResult(1, ex.Message);
        }
    }

    public sealed record Asset(string Name, string Url, long Size, string? Sha256);

    /// <summary>The plain 64-bit MinGit zip (not busybox, not 32-bit/arm64) and its SHA-256.</summary>
    internal static Asset? PickAsset(string releaseJson)
    {
        using var doc = JsonDocument.Parse(releaseJson);
        var root = doc.RootElement;
        var body = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (!Regex.IsMatch(name, @"^MinGit-[\d.]+(\.windows\.\d+)?-64-bit\.zip$")) continue;
            // Newer GitHub releases expose "digest": "sha256:..."; git-for-windows also lists hashes in the notes.
            string? sha = null;
            if (a.TryGetProperty("digest", out var d) && d.GetString() is string dg && dg.StartsWith("sha256:"))
                sha = dg["sha256:".Length..];
            sha ??= Regex.Match(body, Regex.Escape(name) + @"\s*\|\s*([0-9a-fA-F]{64})") is { Success: true } m ? m.Groups[1].Value : null;
            return new Asset(name, a.GetProperty("browser_download_url").GetString()!, a.GetProperty("size").GetInt64(), sha);
        }
        return null;
    }

    private static async Task DownloadAsync(HttpClient http, string url, string path, Action<string> log, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(path);
        var buffer = new byte[81920];
        long done = 0, lastReport = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            done += n;
            if (total > 0 && done - lastReport > total / 5)
            {
                lastReport = done;
                log($"  {done * 100 / total}%");
            }
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var f = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(f, ct));
    }

    /// <summary>Append to HKCU\Environment\Path (kept REG_EXPAND_SZ) and notify running programs.</summary>
    internal static void AddToUserPath(string dir)
    {
        using var key = Registry.CurrentUser.CreateSubKey("Environment");
        var current = key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Any(p => Environment.ExpandEnvironmentVariables(p).TrimEnd('\\').Equals(dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            return;
        parts.Add(dir);
        key.SetValue("Path", string.Join(";", parts), RegistryValueKind.ExpandString);
        SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "Environment", 0x0002, 3000, out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}
