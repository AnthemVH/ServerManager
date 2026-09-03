using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ServerLauncher.Core.Processes;

/// <summary>
/// Safe handle for a Windows job object. Closing the handle is what triggers
/// KILL_ON_JOB_CLOSE, so even an unexpected crash of this app takes the
/// supervised server processes down with it rather than orphaning them.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeJobHandle() : base(ownsHandle: true) { }

    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}
