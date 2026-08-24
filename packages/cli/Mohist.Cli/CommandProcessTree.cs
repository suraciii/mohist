using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Mohist.Cli;

internal static class CommandProcessTree
{
    public const string EnvironmentVariable = "MOHIST_COMMAND_TREE_ID";

    public static List<Process> CaptureDescendants(int rootProcessId)
    {
        var snapshot = CaptureSnapshot();
        var descendants = FindDescendantIds(
            rootProcessId,
            snapshot
                .Where(entry => entry.ParentProcessId.HasValue)
                .Select(entry => (entry.ProcessId, entry.ParentProcessId!.Value)));
        return SelectProcesses(snapshot, descendants);
    }

    public static List<Process> CaptureRemainingDescendants(int rootProcessId, string processTreeId)
    {
        if (OperatingSystem.IsWindows()) return CaptureDescendants(rootProcessId);
        if (!OperatingSystem.IsLinux()) return [];

        var snapshot = CaptureSnapshot();
        var processIds = snapshot
            .Where(entry => entry.ProcessId != rootProcessId && HasProcessTreeMarker(entry.ProcessId, processTreeId))
            .Select(entry => entry.ProcessId)
            .ToHashSet();
        return SelectProcesses(snapshot, processIds);
    }

    internal static HashSet<int> FindDescendantIds(
        int rootProcessId,
        IEnumerable<(int ProcessId, int ParentProcessId)> relationships)
    {
        var children = relationships.ToLookup(entry => entry.ParentProcessId, entry => entry.ProcessId);
        var descendants = new HashSet<int>();
        var pending = new Queue<int>();
        pending.Enqueue(rootProcessId);
        while (pending.TryDequeue(out var parent))
        {
            foreach (var child in children[parent])
            {
                if (!descendants.Add(child)) continue;
                pending.Enqueue(child);
            }
        }
        return descendants;
    }

    private static List<ProcessEntry> CaptureSnapshot()
    {
        var entries = new List<ProcessEntry>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var processId = process.Id;
                var parentProcessId = ReadParentProcessId(process, processId);
                entries.Add(new ProcessEntry(process, processId, parentProcessId));
            }
            catch
            {
                process.Dispose();
            }
        }
        return entries;
    }

    private static int? ReadParentProcessId(Process process, int processId)
    {
        if (OperatingSystem.IsLinux()) return ReadLinuxParentProcessId(processId);
        if (OperatingSystem.IsWindows()) return ReadWindowsParentProcessId(process);
        return null;
    }

    private static int ReadLinuxParentProcessId(int processId)
    {
        var stat = File.ReadAllText($"/proc/{processId}/stat");
        var commandEnd = stat.LastIndexOf(')');
        if (commandEnd < 0 || commandEnd + 2 >= stat.Length)
            throw new InvalidDataException($"Invalid process stat for PID {processId}.");
        var fields = stat[(commandEnd + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2
            || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parentProcessId))
            throw new InvalidDataException($"Invalid process identity for PID {processId}.");
        return parentProcessId;
    }

    private static bool HasProcessTreeMarker(int processId, string processTreeId)
    {
        try
        {
            // Update and service-manager commands do not daemonize or scrub their
            // inherited environment, so the marker survives Linux reparenting.
            var environment = Encoding.UTF8.GetString(File.ReadAllBytes($"/proc/{processId}/environ"));
            var expected = $"{EnvironmentVariable}={processTreeId}";
            return environment.Split('\0', StringSplitOptions.RemoveEmptyEntries).Contains(expected, StringComparer.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static int ReadWindowsParentProcessId(Process process)
    {
        var status = NtQueryInformationProcess(
            process.Handle,
            0,
            out var information,
            Marshal.SizeOf<ProcessBasicInformation>(),
            out _);
        if (status != 0)
            throw new InvalidOperationException($"Process parent query failed with NTSTATUS 0x{status:x8}.");
        return information.InheritedFromUniqueProcessId.ToInt32();
    }

    private static List<Process> SelectProcesses(List<ProcessEntry> snapshot, HashSet<int> processIds)
    {
        var selected = new List<Process>();
        foreach (var entry in snapshot)
        {
            if (processIds.Contains(entry.ProcessId))
            {
                try
                {
                    _ = entry.Process.SafeHandle;
                    selected.Add(entry.Process);
                }
                catch
                {
                    entry.Process.Dispose();
                }
            }
            else
                entry.Process.Dispose();
        }
        return selected;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        out ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    private sealed record ProcessEntry(
        Process Process,
        int ProcessId,
        int? ParentProcessId);
}
