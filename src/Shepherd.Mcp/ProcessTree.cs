using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Shepherd.Mcp;

/// <summary>Parent-process lookup (Windows toolhelp snapshot, Linux /proc).</summary>
internal static partial class ProcessTree
{
    public static (int Pid, string Name)? Parent(int pid)
    {
        int ppid;
        try
        {
            ppid = OperatingSystem.IsWindows() ? WindowsParent(pid) : LinuxParent(pid);
        }
        catch
        {
            return null;
        }
        if (ppid <= 0) return null;
        try
        {
            using var p = Process.GetProcessById(ppid);
            return (ppid, p.ProcessName);
        }
        catch
        {
            return (ppid, "?");
        }
    }

    private static int LinuxParent(int pid)
    {
        var stat = File.ReadAllText($"/proc/{pid}/stat");
        var rest = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        return int.Parse(rest[1], System.Globalization.CultureInfo.InvariantCulture);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

#pragma warning disable SYSLIB1054 // ByValTStr struct marshalling needs classic DllImport
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr h, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr h, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
#pragma warning restore SYSLIB1054

    private static int WindowsParent(int pid)
    {
        var h = CreateToolhelp32Snapshot(0x2, 0);
        if (h == IntPtr.Zero || h == new IntPtr(-1)) return 0;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(h, ref e)) return 0;
            do
            {
                if (e.th32ProcessID == (uint)pid) return (int)e.th32ParentProcessID;
            } while (Process32NextW(h, ref e));
            return 0;
        }
        finally
        {
            CloseHandle(h);
        }
    }
}
