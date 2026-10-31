using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Lucy.Core;

/// <summary>Snapshot of one process: id, image name and full command line.</summary>
public sealed record ProcessSnapshot(int Id, string Name, string CommandLine, string ExecutablePath);

/// <summary>Reads command lines of other processes (same user) without WMI.</summary>
public static class ProcessInspector
{
    public static string? GetCommandLine(int pid)
    {
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            Native.NtQueryInformationProcess(h, Native.ProcessCommandLineInformation, IntPtr.Zero, 0, out var needed);
            if (needed <= 0) return null;
            var buf = Marshal.AllocHGlobal(needed);
            try
            {
                if (Native.NtQueryInformationProcess(h, Native.ProcessCommandLineInformation, buf, needed, out _) != 0)
                    return null;
                // UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer; }
                var length = (ushort)Marshal.ReadInt16(buf);
                var strPtr = Marshal.ReadIntPtr(buf, IntPtr.Size);
                return length == 0 || strPtr == IntPtr.Zero ? "" : Marshal.PtrToStringUni(strPtr, length / 2);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return null; }
        finally { Native.CloseHandle(h); }
    }

    public static string? GetExecutablePath(int pid)
    {
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return Native.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally { Native.CloseHandle(h); }
    }

    public static ProcessSnapshot? Snapshot(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return new ProcessSnapshot(pid, p.ProcessName, GetCommandLine(pid) ?? "", GetExecutablePath(pid) ?? "");
        }
        catch { return null; }
    }

    /// <summary>Every process we can read, with its command line.</summary>
    public static List<ProcessSnapshot> SnapshotAll()
    {
        var list = new List<ProcessSnapshot>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id is 0 or 4) continue;
                var cmd = GetCommandLine(p.Id);
                if (cmd is null) continue; // other users / protected
                list.Add(new ProcessSnapshot(p.Id, p.ProcessName, cmd, GetExecutablePath(p.Id) ?? ""));
            }
            catch { /* exited meanwhile */ }
            finally { p.Dispose(); }
        }
        return list;
    }

    public static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    public static void KillTree(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
        }
        catch { /* gone or not ours */ }
    }
}
