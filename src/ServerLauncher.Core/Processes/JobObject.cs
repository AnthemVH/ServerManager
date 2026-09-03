using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ServerLauncher.Core.Processes;

/// <summary>
/// A Windows job object holding one server's entire process tree.
///
/// Every process launched inside the job — and every descendant it spawns — is
/// tracked automatically by the OS. That gives us two things a plain
/// <see cref="System.Diagnostics.Process"/> handle cannot:
///   * <see cref="Terminate"/> kills the whole tree atomically, with no orphans.
///   * <see cref="GetProcessIds"/> enumerates the live tree for resource sampling
///     without walking parent/child relationships ourselves.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class JobObject : IDisposable
{
    private readonly SafeJobHandle _handle;
    private bool _disposed;

    public JobObject(string? name = null)
    {
        _handle = NativeMethods.CreateJobObject(IntPtr.Zero, name);
        if (_handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create job object.");
        }

        ConfigureKillOnClose();
    }

    /// <summary>
    /// Sets KILL_ON_JOB_CLOSE so the OS tears the tree down if this process dies
    /// without a clean shutdown. This is our safety net against orphaned servers.
    /// </summary>
    private void ConfigureKillOnClose()
    {
        var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            }
        };

        var length = Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, ptr, fDeleteOld: false);
            if (!NativeMethods.SetInformationJobObject(
                    _handle, NativeMethods.JobObjectExtendedLimitInformation, ptr, (uint)length))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Failed to configure job object kill-on-close.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>
    /// Adds a process to the job. Children it spawns afterwards join automatically.
    /// </summary>
    /// <remarks>
    /// There is an unavoidable race here: .NET cannot start a process suspended, so a
    /// process that forks a child within the first few microseconds could escape the
    /// job. Interpreters like cmd.exe and powershell.exe take far longer than that to
    /// initialise, so in practice the tree is always captured.
    /// </remarks>
    public void Assign(System.Diagnostics.Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!NativeMethods.AssignProcessToJobObject(_handle, process.Handle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"Failed to assign process {process.Id} to job object.");
        }
    }

    /// <summary>Kills every process in the job immediately.</summary>
    public void Terminate(uint exitCode = 1)
    {
        if (_disposed || _handle.IsInvalid)
        {
            return;
        }

        if (!NativeMethods.TerminateJobObject(_handle, exitCode))
        {
            var error = Marshal.GetLastWin32Error();
            // The job may already be empty and torn down; that is a success for us.
            if (error is not 0 and not 5)
            {
                throw new Win32Exception(error, "Failed to terminate job object.");
            }
        }
    }

    /// <summary>
    /// Returns the process IDs currently alive in the job. Used for resource sampling
    /// across the whole server tree, not just the launcher script.
    /// </summary>
    public IReadOnlyList<int> GetProcessIds()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // The struct is variable length: a two-field header followed by an inline array.
        // Start with room for 64 processes and grow if the OS reports more.
        var capacity = 64;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var headerSize = sizeof(uint) * 2;
            var size = headerSize + (IntPtr.Size * capacity);
            var buffer = Marshal.AllocHGlobal(size);

            try
            {
                if (!NativeMethods.QueryInformationJobObject(
                        _handle, NativeMethods.JobObjectBasicProcessIdList,
                        buffer, (uint)size, out _))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == NativeMethods.ERROR_MORE_DATA)
                    {
                        capacity *= 4;
                        continue;
                    }

                    return Array.Empty<int>();
                }

                var assigned = (uint)Marshal.ReadInt32(buffer);
                var returned = (uint)Marshal.ReadInt32(buffer, sizeof(uint));

                // Guard against a tree that grew between sizing and reading.
                if (returned < assigned)
                {
                    capacity = (int)assigned * 2;
                    continue;
                }

                var ids = new List<int>((int)returned);
                for (var i = 0; i < returned; i++)
                {
                    var offset = headerSize + (IntPtr.Size * i);
                    var id = IntPtr.Size == 8
                        ? Marshal.ReadInt64(buffer, offset)
                        : Marshal.ReadInt32(buffer, offset);
                    ids.Add((int)id);
                }

                return ids;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return Array.Empty<int>();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
    }
}
