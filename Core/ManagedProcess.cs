using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Lucy.Core;

public enum ConsoleMode
{
    /// <summary>Hidden console, stdout+stderr captured line by line, stdin = NUL.</summary>
    Captured,
    /// <summary>Visible console window of its own (interactive tools); nothing captured.</summary>
    NewWindow,
}

public sealed class ProcessSpec
{
    public required string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
    /// <summary>Verbatim argument string (e.g. for cmd.exe /s /c "..."); overrides <see cref="Arguments"/>.</summary>
    public string? RawArguments { get; init; }
    public string? WorkingDirectory { get; init; }
    /// <summary>Full environment for the child. Null = <see cref="ShellEnv.Create"/>.</summary>
    public IDictionary<string, string>? Environment { get; init; }
    public ConsoleMode Mode { get; init; } = ConsoleMode.Captured;
    public string? Title { get; init; }

    public override string ToString() =>
        RawArguments != null ? $"{CommandLine.Quote(FileName)} {RawArguments}" : CommandLine.Join(new[] { FileName }.Concat(Arguments));
}

/// <summary>
/// A child process tree owned by a Win32 Job Object.
///
/// The process is created suspended and put in its job before its first instruction, so every
/// descendant (pixi → python → ros2 launch → nodes, yarn → node → vite, …) is in the job too.
/// The job is created with KILL_ON_JOB_CLOSE: if Lucy.exe dies for any reason — crash, Task
/// Manager, logoff — Windows closes the handle and kills the whole tree. Nothing can be orphaned.
/// </summary>
public sealed class ManagedProcess : IDisposable
{
    private static readonly object CreateLock = new();
    private static readonly Regex Ansi = new(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])", RegexOptions.Compiled);


    static ManagedProcess()
    {
        // "Ignore Ctrl+C" is inherited by children. If whoever started Lucy set it (a new process
        // group, a test host...), children could never be stopped gracefully: clear it once.
        Native.SetConsoleCtrlHandler(null, false);
    }

    private readonly SafeFileHandle _job;
    private readonly IntPtr _hProcess;
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _pump = Task.CompletedTask;
    private RegisteredWaitHandle? _waitRegistration;
    private bool _disposed;

    public int Id { get; }
    public ProcessSpec Spec { get; }
    public DateTime StartTime { get; } = DateTime.Now;
    public bool HasExited => _exit.Task.IsCompleted;
    public int? ExitCode => _exit.Task.IsCompleted ? _exit.Task.Result : null;
    public Task<int> Completion => _exit.Task;

    public event Action<string>? Output;
    public event Action<int>? Exited;

    private ManagedProcess(ProcessSpec spec, SafeFileHandle job, Native.PROCESS_INFORMATION pi)
    {
        Spec = spec;
        _job = job;
        _hProcess = pi.hProcess;
        Id = pi.dwProcessId;
    }

    public static ManagedProcess Start(ProcessSpec spec, Action<string>? onOutput = null)
    {
        var env = spec.Environment ?? ShellEnv.Create();
        var exe = ShellEnv.Which(spec.FileName, env)
                  ?? throw new FileNotFoundException($"'{spec.FileName}' was not found on PATH.");

        string commandLine;
        string application;
        var ext = Path.GetExtension(exe).ToLowerInvariant();
        if (spec.RawArguments != null)
        {
            application = exe;
            commandLine = $"{CommandLine.Quote(exe)} {spec.RawArguments}";
        }
        else if (ext is ".cmd" or ".bat")
        {
            application = Path.Combine(System.Environment.SystemDirectory, "cmd.exe");
            commandLine = $"\"{application}\" /d /s /c \"{CommandLine.Join(new[] { exe }.Concat(spec.Arguments))}\"";
        }
        else
        {
            application = exe;
            commandLine = CommandLine.Join(new[] { exe }.Concat(spec.Arguments));
        }

        var job = CreateKillOnCloseJob();
        var envBlock = BuildEnvironmentBlock(env);
        IntPtr readOut = IntPtr.Zero, writeOut = IntPtr.Zero, nulIn = IntPtr.Zero;
        Native.PROCESS_INFORMATION pi;

        // One creation at a time: inheritable pipe ends must not leak into a sibling child,
        // or that sibling would keep our pipe open and the reader would never see EOF.
        lock (CreateLock)
        {
            try
            {
                var si = new Native.STARTUPINFO { cb = Marshal.SizeOf<Native.STARTUPINFO>(), lpTitle = spec.Title };
                var flags = Native.CREATE_SUSPENDED | Native.CREATE_UNICODE_ENVIRONMENT;
                var inherit = false;

                if (spec.Mode == ConsoleMode.Captured)
                {
                    var sa = new Native.SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<Native.SECURITY_ATTRIBUTES>(), bInheritHandle = true };
                    if (!Native.CreatePipe(out readOut, out writeOut, ref sa, 0))
                        throw new System.ComponentModel.Win32Exception();
                    Native.SetHandleInformation(readOut, Native.HANDLE_FLAG_INHERIT, 0);
                    nulIn = Native.CreateFile("NUL", Native.GENERIC_READ, Native.FILE_SHARE_READ_WRITE, ref sa, Native.OPEN_EXISTING, 0, IntPtr.Zero);
                    si.dwFlags = Native.STARTF_USESTDHANDLES;
                    si.hStdInput = nulIn;
                    si.hStdOutput = writeOut;
                    si.hStdError = writeOut;
                    flags |= Native.CREATE_NO_WINDOW;
                    inherit = true;
                }
                else
                {
                    flags |= Native.CREATE_NEW_CONSOLE;
                }

                var cwd = string.IsNullOrEmpty(spec.WorkingDirectory) ? null : spec.WorkingDirectory;
                if (!Native.CreateProcess(application, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, inherit,
                        flags, envBlock, cwd, ref si, out pi))
                {
                    var err = new System.ComponentModel.Win32Exception();
                    if (readOut != IntPtr.Zero) Native.CloseHandle(readOut);
                    job.Dispose();
                    throw new InvalidOperationException($"Could not start {spec.FileName}: {err.Message}", err);
                }
            }
            finally
            {
                if (writeOut != IntPtr.Zero) Native.CloseHandle(writeOut);
                if (nulIn != IntPtr.Zero && nulIn != new IntPtr(-1)) Native.CloseHandle(nulIn);
                Marshal.FreeHGlobal(envBlock);
            }
        }

        if (!Native.AssignProcessToJobObject(job, pi.hProcess))
            Log.Warn($"AssignProcessToJobObject failed for pid {pi.dwProcessId}: {Marshal.GetLastWin32Error()}");
        Native.ResumeThread(pi.hThread);
        Native.CloseHandle(pi.hThread);

        var mp = new ManagedProcess(spec, job, pi);
        if (onOutput != null) mp.Output += onOutput;
        Log.Info($"started pid {mp.Id}: {commandLine} (cwd {spec.WorkingDirectory})");

        if (readOut != IntPtr.Zero)
        {
            var stream = new FileStream(new SafeFileHandle(readOut, ownsHandle: true), FileAccess.Read, 4096, isAsync: false);
            mp._pump = Task.Factory.StartNew(() => mp.PumpOutput(stream), TaskCreationOptions.LongRunning);
        }
        mp.WatchExit();
        return mp;
    }

    private static SafeFileHandle CreateKillOnCloseJob()
    {
        var job = Native.CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new System.ComponentModel.Win32Exception();
        var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!Native.SetInformationJobObject(job, Native.JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf(info)))
            Log.Warn($"SetInformationJobObject failed: {Marshal.GetLastWin32Error()}");
        return job;
    }

    private static IntPtr BuildEnvironmentBlock(IDictionary<string, string> env)
    {
        var sb = new StringBuilder();
        foreach (var kv in env.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (kv.Key.Length == 0 || kv.Key.Contains('=')) continue;
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        }
        sb.Append('\0');
        return Marshal.StringToHGlobalUni(sb.ToString());
    }

    private void PumpOutput(FileStream stream)
    {
        try
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
            var buf = new char[4096];
            var splitter = new LineSplitter(Emit);
            int n;
            while ((n = reader.Read(buf, 0, buf.Length)) > 0)
                splitter.Feed(buf.AsSpan(0, n));
            splitter.Complete();
        }
        catch (Exception ex) { Log.Warn($"output pump for pid {Id} stopped: {ex.Message}"); }
    }

    private void Emit(string line)
    {
        var text = Ansi.Replace(line, "");
        try { Output?.Invoke(text); } catch (Exception ex) { Log.Warn($"output handler threw: {ex.Message}"); }
    }

    private void WatchExit()
    {
        var wait = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(_hProcess, ownsHandle: false) };
        _waitRegistration = ThreadPool.RegisterWaitForSingleObject(wait, (_, _) => OnRootExited(), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    private async void OnRootExited()
    {
        Native.GetExitCodeProcess(_hProcess, out var code);
        // The session is its root process. Anything it left behind goes with it.
        if (IsTreeAlive) Native.TerminateJobObject(_job, 1);
        try { await _pump.WaitAsync(TimeSpan.FromSeconds(3)); } catch { /* pipe held open */ }
        var exitCode = unchecked((int)code);
        Log.Info($"pid {Id} exited with {exitCode}");
        _exit.TrySetResult(exitCode);
        try { Exited?.Invoke(exitCode); } catch (Exception ex) { Log.Warn($"exit handler threw: {ex.Message}"); }
    }

    /// <summary>True while any process of the tree is alive (the root may already be gone).</summary>
    public bool IsTreeAlive
    {
        get
        {
            if (_disposed) return false;
            return Native.QueryInformationJobObject(_job, Native.JobObjectBasicAccountingInformation, out var info,
                       Marshal.SizeOf<Native.JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), out _)
                   && info.ActiveProcesses > 0;
        }
    }

    /// <summary>Ids of every live process in the tree.</summary>
    public int[] GetTreeProcessIds()
    {
        if (_disposed) return Array.Empty<int>();
        const int max = 2048;
        var size = 8 + IntPtr.Size * max;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (!Native.QueryInformationJobObject(_job, Native.JobObjectBasicProcessIdList, buf, size, out _))
                return Array.Empty<int>();
            var count = Marshal.ReadInt32(buf, 4);
            var ids = new int[count];
            for (var i = 0; i < count; i++)
                ids[i] = (int)Marshal.ReadIntPtr(buf, 8 + i * IntPtr.Size).ToInt64();
            return ids;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>Command lines of the tree's processes (for pgrep-style readiness checks).</summary>
    public List<ProcessSnapshot> SnapshotTree()
    {
        var list = new List<ProcessSnapshot>();
        foreach (var pid in GetTreeProcessIds())
        {
            var s = ProcessInspector.Snapshot(pid);
            if (s != null) list.Add(s);
        }
        return list;
    }

    /// <summary>
    /// Deliver Ctrl+C to every process on the tree's console — the Windows equivalent of
    /// `tmux send-keys C-c`, letting ros2 launch / vite shut down cleanly.
    ///
    /// Sending requires attaching to the target's console, and everything attached receives the
    /// event. A short-lived helper (<c>Lucy.exe --ctrl-c PID</c>) does the attaching, so Lucy
    /// itself never shares a console with the stack.
    /// </summary>
    public bool SendCtrlC()
    {
        if (!IsTreeAlive) return false;
        var helper = CtrlCHelper;
        if (helper == null)
        {
            Log.Warn("no Ctrl+C helper available; the tree will be terminated");
            return false;
        }
        foreach (var pid in new[] { Id }.Concat(GetTreeProcessIds()).Distinct().Take(4))
        {
            if (!ProcessInspector.IsAlive(pid)) continue;
            try
            {
                using var h = Start(new ProcessSpec
                {
                    FileName = helper,
                    Arguments = new[] { CtrlCArgument, pid.ToString() },
                    Environment = ShellEnv.Create(),
                });
                if (!h.Completion.Wait(TimeSpan.FromSeconds(10))) continue;
                if (h.ExitCode == 0)
                {
                    Log.Info($"Ctrl+C sent to the console of pid {pid}");
                    return true;
                }
                Log.Warn($"Ctrl+C helper for pid {pid} exited with {h.ExitCode}");
            }
            catch (Exception ex)
            {
                Log.Warn($"Ctrl+C helper failed: {ex.Message}");
                return false;
            }
        }
        return false;
    }

    public const string CtrlCArgument = "--ctrl-c";

    /// <summary>Lucy.exe (the published single file, or the apphost next to Lucy.dll).</summary>
    public static string? CtrlCHelper
    {
        get
        {
            var beside = Path.Combine(AppContext.BaseDirectory, "Lucy.exe");
            if (File.Exists(beside)) return beside;
            var self = System.Environment.ProcessPath;
            return self != null && Path.GetFileName(self).Equals("Lucy.exe", StringComparison.OrdinalIgnoreCase) ? self : null;
        }
    }

    /// <summary>Body of the helper process: attach to PID's console and raise Ctrl+C on it.</summary>
    public static int RunCtrlCHelper(int pid)
    {
        Native.FreeConsole();
        if (!Native.AttachConsole(pid)) return 2;
        // This helper must survive the event it raises.
        Native.SetConsoleCtrlHandler(null, true);
        return Native.GenerateConsoleCtrlEvent(Native.CTRL_C_EVENT, 0) ? 0 : 3;
    }

    /// <summary>Kill the whole tree immediately.</summary>
    public void Kill()
    {
        if (_disposed) return;
        if (IsTreeAlive)
        {
            Log.Info($"terminating job of pid {Id}");
            Native.TerminateJobObject(_job, 1);
        }
    }

    /// <summary>Ctrl+C, wait for the tree to go away, then terminate the job.</summary>
    public async Task StopAsync(TimeSpan grace, CancellationToken ct = default)
    {
        if (!IsTreeAlive) return;
        if (grace > TimeSpan.Zero && SendCtrlC())
        {
            var deadline = DateTime.UtcNow + grace;
            while (IsTreeAlive && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                await Task.Delay(250, CancellationToken.None);
        }
        Kill();
        var settle = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (IsTreeAlive && DateTime.UtcNow < settle)
            await Task.Delay(100, CancellationToken.None);
    }

    public Task<int> WaitForExitAsync(CancellationToken ct = default) => _exit.Task.WaitAsync(ct);

    public void Dispose()
    {
        if (_disposed) return;
        Kill();
        _disposed = true;
        _waitRegistration?.Unregister(null);
        _job.Dispose();
        // _hProcess is kept open until exit is observed; close it after.
        _exit.Task.ContinueWith(_ => Native.CloseHandle(_hProcess));
        if (!_exit.Task.IsCompleted) _exit.TrySetResult(-1);
    }
}
