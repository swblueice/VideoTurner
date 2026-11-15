# 列出某个进程（及其上级进程链）的所有顶层窗口，用来判断
# 「路由引擎为什么把它的窗口认成在另一台显示器上」。
#
# 用法： & tools\dump-windows.ps1 -Name notepad
#        & tools\dump-windows.ps1 -TargetPid 41288
#
# 说明：全部自己调 Win32，不依赖 WMI/CIM（本机沙箱下 WMI 查询会被拒）。

param(
    [uint32]$TargetPid,
    [string]$Name
)

$ErrorActionPreference = 'Continue'

if (-not $TargetPid -and $Name) {
    $p = Get-Process -Name $Name -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $p) { "找不到名为 $Name 的进程"; exit 1 }
    $TargetPid = [uint32]$p.Id
}
if (-not $TargetPid) { "请给出 -Name 或 -TargetPid"; exit 1 }

$src = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class WinDump
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);

    public static uint ParentOf(uint pid)
    {
        IntPtr snap = CreateToolhelp32Snapshot(0x2, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return 0;
        try
        {
            var e = new PROCESSENTRY32();
            e.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
            if (!Process32First(snap, ref e)) return 0;
            do { if (e.th32ProcessID == pid) return e.th32ParentProcessID; } while (Process32Next(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return 0;
    }

    public static uint[] Chain(uint pid)
    {
        var list = new List<uint>();
        var cur = pid;
        for (int i = 0; i < 12; i++)
        {
            if (list.Contains(cur)) break;
            list.Add(cur);
            var parent = ParentOf(cur);
            if (parent == 0) break;
            cur = parent;
        }
        return list.ToArray();
    }

    public static List<string> Windows(uint[] pids)
    {
        var result = new List<string>();
        EnumWindows((hWnd, _) =>
        {
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            if (Array.IndexOf(pids, pid) < 0) return true;

            int len = GetWindowTextLength(hWnd);
            var sb = new StringBuilder(len + 2);
            if (len > 0) GetWindowText(hWnd, sb, sb.Capacity);

            RECT r;
            GetWindowRect(hWnd, out r);

            var mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            var hmon = MonitorFromWindow(hWnd, 2);
            string mon = "?";
            if (GetMonitorInfo(hmon, ref mi))
                mon = (mi.dwFlags & 1) != 0
                    ? "主屏"
                    : string.Format("({0},{1}) {2}x{3}", mi.rcMonitor.Left, mi.rcMonitor.Top,
                                    mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top);

            result.Add(string.Format(
                "  pid={0} 可见={1} 最小化={2} 矩形={3}x{4}@({5},{6}) MonitorFromWindow={7} 标题「{8}」",
                pid, IsWindowVisible(hWnd), IsIconic(hWnd),
                r.Right - r.Left, r.Bottom - r.Top, r.Left, r.Top, mon, sb.ToString()));
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
'@

Add-Type -TypeDefinition $src -ErrorAction Stop

$chain = [WinDump]::Chain($TargetPid)
$names = $chain | ForEach-Object {
    $n = (Get-Process -Id $_ -ErrorAction SilentlyContinue).ProcessName
    if (-not $n) { $n = '?' }
    "$n($_)"
}
"进程链: " + ($names -join ' ← ')
""
"顶层窗口:"
$rows = [WinDump]::Windows($chain)
if ($rows.Count -eq 0) { "  （没有顶层窗口 —— 这会让引擎判定为『找不到窗口』）" } else { $rows | ForEach-Object { $_ } }
""
"当前显示器（GDI 名 → 矩形）:"
Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.Screen]::AllScreens | ForEach-Object {
    "  $($_.DeviceName)  $($_.Bounds)  主屏=$($_.Primary)"
}
