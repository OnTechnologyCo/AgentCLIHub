using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentCLIHub;

/// <summary>동시에 열린 새 세션을 구분하기 위한 읽기 전용 파일 사용 프로세스 조회.</summary>
public static class NativeFileOwners
{
    [StructLayout(LayoutKind.Sequential)]
    private struct UniqueProcess { public uint Pid; public System.Runtime.InteropServices.ComTypes.FILETIME Started; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Owner
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Service;
        public uint Type, Status, Session;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Pid;
        public nuint Heap;
        public uint Module, Threads, Parent;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe;
    }
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, uint flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint count, string[] paths,
        uint apps, IntPtr processes, uint services, IntPtr names);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] Owner[]? owners, ref uint reason);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);

    // RmShutdown / RmRestart는 사용하지 않는다. 등록·조회·세션 해제만 한다.
    public static bool IsOpenBy(string path, ISet<int> processes)
    {
        if (processes.Count == 0 || RmStartSession(out var session, 0, new StringBuilder(33)) != 0) return false;
        try
        {
            if (RmRegisterResources(session, 1, [path], 0, IntPtr.Zero, 0, IntPtr.Zero) != 0) return false;
            uint count = 0, reason = 0;
            var result = RmGetList(session, out var needed, ref count, null, ref reason);
            for (var retry = 0; result == 234 && retry < 3 && needed <= 1024; retry++)
            {
                count = needed;
                var owners = new Owner[count];
                result = RmGetList(session, out needed, ref count, owners, ref reason);
                if (result == 0)
                    for (var i = 0; i < count; i++)
                        if (processes.Contains((int)owners[i].Process.Pid)) return true;
            }
            return false;
        }
        catch { return false; }
        finally { RmEndSession(session); }
    }

    public static HashSet<int> ProcessTree(int root, DateTime startedUtc)
    {
        var result = new HashSet<int>();
        try
        {
            using var process = Process.GetProcessById(root);
            if (process.HasExited || process.StartTime.ToUniversalTime() < startedUtc.AddSeconds(-5)) return result;
            result.Add(root);
            using var snapshot = CreateToolhelp32Snapshot(2, 0);
            if (snapshot.IsInvalid) return result;
            var entries = new List<(int Pid, int Parent)>();
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (Process32FirstW(snapshot, ref entry))
                do { entries.Add(((int)entry.Pid, (int)entry.Parent)); } while (Process32NextW(snapshot, ref entry));
            bool changed;
            do
            {
                changed = false;
                foreach (var (pid, parent) in entries)
                    if (result.Contains(parent)) changed |= result.Add(pid);
            } while (changed);
        }
        catch { }
        return result;
    }
}
