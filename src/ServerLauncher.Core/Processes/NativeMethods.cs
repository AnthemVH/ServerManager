using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ServerLauncher.Core.Processes;

/// <summary>
/// P/Invoke surface for Windows job objects. Job objects are how we guarantee that
/// stopping a server kills the whole process tree: a .bat launched through cmd.exe
/// spawns the real server (java.exe, etc.) as a child, and killing only the handle
/// we hold would leave that child orphaned.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NativeMethods
{
    internal const int JobObjectBasicProcessIdList = 3;
    internal const int JobObjectExtendedLimitInformation = 9;

    /// <summary>Kill every process in the job when the last handle to it closes.</summary>
    internal const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    internal const int ERROR_MORE_DATA = 234;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeJobHandle CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetInformationJobObject(
        SafeJobHandle hJob, int infoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryInformationJobObject(
        SafeJobHandle hJob, int infoClass, IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength, out uint lpReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AssignProcessToJobObject(SafeJobHandle hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateJobObject(SafeJobHandle hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
    internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
