using System.Text;

namespace Lucy.Core;

public sealed record RunResult(int ExitCode, string Output)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>Run-to-completion helpers on top of <see cref="ManagedProcess"/>.</summary>
public static class ProcessRunner
{
    /// <summary>
    /// Run a command, streaming each line to <paramref name="onLine"/>. Cancelling kills the whole
    /// process tree (job object) and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task<RunResult> RunAsync(
        string file, IEnumerable<string> args, string? cwd = null,
        IDictionary<string, string>? env = null, Action<string>? onLine = null,
        CancellationToken ct = default, Action<ManagedProcess>? onStarted = null)
    {
        ct.ThrowIfCancellationRequested();
        var output = new StringBuilder();
        var spec = new ProcessSpec
        {
            FileName = file,
            Arguments = args.ToList(),
            WorkingDirectory = cwd,
            Environment = env,
        };
        using var proc = ManagedProcess.Start(spec, line =>
        {
            lock (output) output.AppendLine(line);
            onLine?.Invoke(line);
        });
        onStarted?.Invoke(proc);
        using var reg = ct.Register(() => proc.Kill());
        var code = await proc.Completion.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        lock (output) return new RunResult(code, output.ToString());
    }

    /// <summary>Run quietly and capture output; never throws for a missing executable.</summary>
    public static async Task<RunResult> CaptureAsync(
        string file, IEnumerable<string> args, string? cwd = null,
        IDictionary<string, string>? env = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
        try
        {
            return await RunAsync(file, args, cwd, env, null, cts.Token).ConfigureAwait(false);
        }
        catch (FileNotFoundException) { return new RunResult(-1, ""); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new RunResult(-2, "timeout"); }
        catch (InvalidOperationException ex) { return new RunResult(-1, ex.Message); }
    }
}
