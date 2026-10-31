using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Lucy.Core;

/// <summary>Win32 interop used by the process layer.</summary>
internal static class Native
{
    // ---- Job objects -------------------------------------------------------
    public const int JobObjectBasicAccountingInformation = 1;
    public const int JobObjectBasicProcessIdList = 3;
    public const int JobObjectExtendedLimitInformation = 9;
    public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    public const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x00000800;

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetInformationJobObject(SafeFileHandle hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int cbInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool QueryInformationJobObject(SafeFileHandle hJob, int infoClass, IntPtr info, int cbInfo, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool QueryInformationJobObject(SafeFileHandle hJob, int infoClass, out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info, int cbInfo, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AssignProcessToJobObject(SafeFileHandle hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateJobObject(SafeFileHandle hJob, uint exitCode);

    // ---- Processes ---------------------------------------------------------
    public const uint CREATE_SUSPENDED = 0x00000004;
    public const uint CREATE_NEW_CONSOLE = 0x00000010;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    public const uint CREATE_NO_WINDOW = 0x08000000;
    public const int STARTF_USESTDHANDLES = 0x00000100;
    public const int STARTF_USESHOWWINDOW = 0x00000001;
    public const uint INFINITE = 0xFFFFFFFF;
    public const uint STILL_ACTIVE = 259;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcess(
        string? lpApplicationName, System.Text.StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles,
        uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateProcess(IntPtr hProcess, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SECURITY_ATTRIBUTES sa, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetHandleInformation(IntPtr hObject, int mask, int flags);
    public const int HANDLE_FLAG_INHERIT = 1;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFile(string name, uint access, uint share, ref SECURITY_ATTRIBUTES sa, uint disposition, uint flags, IntPtr template);
    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_SHARE_READ_WRITE = 0x3;
    public const uint OPEN_EXISTING = 3;

    // ---- Console control (graceful Ctrl+C delivery) -------------------------
    public const uint CTRL_C_EVENT = 0;
    public const uint CTRL_BREAK_EVENT = 1;

    public delegate bool ConsoleCtrlDelegate(uint ctrlType);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate? handler, bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    // ---- Process inspection -----------------------------------------------
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_TERMINATE = 0x0001;
    public const int ProcessCommandLineInformation = 60;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(IntPtr hProcess, int infoClass, IntPtr info, int infoLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, System.Text.StringBuilder exeName, ref int size);

    // ---- Windows -----------------------------------------------------------
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    public const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);
}
