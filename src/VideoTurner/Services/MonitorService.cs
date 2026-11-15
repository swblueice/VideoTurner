using VideoTurner.Interop;
using VideoTurner.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VideoTurner.Services;

public class MonitorService
{
    private List<MonitorInfo>? _cachedMonitors;
    private DateTime _lastRefresh = DateTime.MinValue;
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromSeconds(5);

    public List<MonitorInfo> GetMonitors(bool forceRefresh = false)
    {
        if (!forceRefresh && _cachedMonitors != null && DateTime.UtcNow - _lastRefresh < CacheExpiry)
            return _cachedMonitors;

        var monitors = new List<MonitorInfo>();
        int index = 1;

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref NativeMethods.RECT lprcMonitor, IntPtr dwData) =>
        {
            var info = new NativeMethods.MONITORINFOEX();
            info.cbSize = Marshal.SizeOf(info);

            if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
            {
                var deviceName = info.szDevice?.TrimEnd('\0') ?? $"DISPLAY{index}";
                var friendlyName = GetMonitorFriendlyName(deviceName) ?? $"显示器 {index}";
                monitors.Add(new MonitorInfo
                {
                    Handle = hMonitor,
                    DeviceName = deviceName,
                    FriendlyName = friendlyName,
                    Left = info.rcMonitor.Left,
                    Top = info.rcMonitor.Top,
                    Width = info.rcMonitor.Right - info.rcMonitor.Left,
                    Height = info.rcMonitor.Bottom - info.rcMonitor.Top,
                    IsPrimary = (info.dwFlags & 1) != 0
                });
                index++;
            }
            return true;
        }, IntPtr.Zero);

        _cachedMonitors = monitors;
        _lastRefresh = DateTime.UtcNow;

        // 用 CCD（DisplayConfig）补上型号名 / 设备路径 / 容器 ID：
        // EnumDisplayDevices 在很多机器上只给「Generic PnP Monitor」，
        // 而 CCD 给出的是显示器型号（EDID 里的名字，也就是 Windows 显示设置里显示的那个）。
        EnrichWithDisplayConfig(monitors);

        return monitors;
    }

    /// <summary>
    /// 给每台显示器补上 CCD 才能提供的信息。任何一步拿不到就保持原样，
    /// 让上层退回「型号未知、需要手动选择」的状态，而不是猜一个。
    /// </summary>
    private static void EnrichWithDisplayConfig(List<MonitorInfo> monitors)
    {
        var targets = DisplayConfig.GetActiveTargets();
        if (targets.Count == 0) return;

        // 同一台显示器可能有多个 target（复制/克隆模式），取第一个即可。
        var byGdiName = new Dictionary<string, DisplayTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            if (!string.IsNullOrEmpty(target.GdiDeviceName))
                byGdiName.TryAdd(target.GdiDeviceName, target);
        }

        foreach (var monitor in monitors)
        {
            if (!byGdiName.TryGetValue(monitor.DeviceName, out var target))
                continue;

            if (!string.IsNullOrWhiteSpace(target.MonitorFriendlyName))
                monitor.FriendlyName = target.MonitorFriendlyName;

            monitor.DevicePath = target.MonitorDevicePath;
            monitor.MayCarryAudio = target.MayCarryAudio;

            // 容器 ID：和 HDMI/DP 音频端点比对，用来把「哪台显示器」和「哪个音频端点」配成一对
            monitor.ContainerId =
                DeviceContainerService.GetMonitorContainerId(target.MonitorDevicePath)
                ?? string.Empty;
        }
    }

    private static string? GetMonitorFriendlyName(string deviceName)
    {
        try
        {
            var dd = new NativeMethods.DISPLAY_DEVICE();
            dd.cb = Marshal.SizeOf(dd);

            // Query the monitor attached to this display adapter
            if (NativeMethods.EnumDisplayDevices(deviceName, 0, ref dd, NativeMethods.EDD_GET_DEVICE_INTERFACE_NAME))
            {
                var name = dd.DeviceString?.Trim();
                if (!string.IsNullOrEmpty(name) && name != "Generic PnP Monitor")
                    return name;
            }
        }
        catch { }

        return null;
    }

    public void InvalidateCache()
    {
        _cachedMonitors = null;
    }

    public MonitorInfo? GetMonitorForProcess(uint processId)
    {
        // Try the exact PID first, then walk up the parent process tree.
        // Handles Electron apps (Discord, Slack, VS Code) where the audio
        // session runs in a child renderer process without a visible window.
        var pidsToTry = GetProcessAndAncestors(processId);

        IntPtr hwnd = IntPtr.Zero;
        foreach (var pid in pidsToTry)
        {
            hwnd = FindMainWindowForProcess(pid);
            if (hwnd != IntPtr.Zero)
                break;
        }

        if (hwnd == IntPtr.Zero) return null;

        var monitors = GetMonitors();
        if (monitors.Count == 0) return null;

        // PRIMARY METHOD: Use the window's actual position to determine which
        // monitor it's on. This works correctly regardless of DPI scaling.
        var windowMonitor = FindMonitorByWindowPosition(hwnd, monitors);
        if (windowMonitor != null) return windowMonitor;

        // FALLBACK: Use MonitorFromWindow API + device name matching
        var hMonitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (hMonitor != IntPtr.Zero)
        {
            // Try handle match
            var match = monitors.FirstOrDefault(m => m.Handle == hMonitor);
            if (match != null) return match;

            // Try device name match
            var info = new NativeMethods.MONITORINFOEX();
            info.cbSize = Marshal.SizeOf(info);
            if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
            {
                var deviceName = info.szDevice?.TrimEnd('\0');
                match = monitors.FirstOrDefault(m => m.DeviceName == deviceName);
                if (match != null) return match;
            }
        }

        // Last resort: primary monitor
        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.First();
    }

    private MonitorInfo? FindMonitorByWindowPosition(IntPtr hwnd, List<MonitorInfo> monitors)
    {
        // Try DWM extended frame bounds first (gives physical/unscaled coordinates)
        int centerX, centerY;
        int hr = NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
            out NativeMethods.RECT dwmRect, Marshal.SizeOf<NativeMethods.RECT>());

        if (hr == 0)
        {
            centerX = (dwmRect.Left + dwmRect.Right) / 2;
            centerY = (dwmRect.Top + dwmRect.Bottom) / 2;
        }
        else
        {
            // Fall back to GetWindowRect
            if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT windowRect))
                return null;
            centerX = (windowRect.Left + windowRect.Right) / 2;
            centerY = (windowRect.Top + windowRect.Bottom) / 2;
        }

        // Find which monitor contains the window center point
        foreach (var monitor in monitors)
        {
            if (centerX >= monitor.Left && centerX < monitor.Left + monitor.Width &&
                centerY >= monitor.Top && centerY < monitor.Top + monitor.Height)
            {
                return monitor;
            }
        }

        // If no exact match (window might be between monitors), find closest
        MonitorInfo? closest = null;
        double closestDist = double.MaxValue;
        foreach (var monitor in monitors)
        {
            double monCenterX = monitor.Left + monitor.Width / 2.0;
            double monCenterY = monitor.Top + monitor.Height / 2.0;
            double dist = Math.Pow(centerX - monCenterX, 2) + Math.Pow(centerY - monCenterY, 2);
            if (dist < closestDist)
            {
                closestDist = dist;
                closest = monitor;
            }
        }

        return closest;
    }

    private static List<uint> GetProcessAndAncestors(uint processId)
    {
        var pids = new List<uint> { processId };
        var seen = new HashSet<uint> { processId };
        var currentPid = processId;

        // 12 层足够覆盖「启动器 → 主进程 → 渲染/音频子进程」这类多层结构
        // （模拟器、国产游戏客户端常见），循环引用由 seen 兜住。
        for (int i = 0; i < 12; i++)
        {
            try
            {
                var parentPid = GetParentProcessId(currentPid);
                if (parentPid == 0 || !seen.Add(parentPid))
                    break;
                pids.Add(parentPid);
                currentPid = parentPid;
            }
            catch
            {
                break;
            }
        }

        return pids;
    }

    /// <summary>
    /// 排查用：把一个进程的关键信息拼成一行（路径、是否以管理员运行、祖先链）。
    ///
    /// <para>用途：当路由引擎「找不到该进程的可见窗口」时把这一行写进日志和首页状态，
    /// 立刻能分辨是「这是某个客户端的无窗口子进程（模拟器、游戏的音频进程）」
    /// 还是「该进程提权了，未提权的我们无权给它设设备」。</para>
    /// </summary>
    public static string DescribeProcess(uint processId)
    {
        var parts = new List<string>();

        try
        {
            var process = Process.GetProcessById((int)processId);
            try
            {
                parts.Add($"路径={process.MainModule?.FileName ?? "(读不到)"}");
            }
            catch
            {
                parts.Add("路径=读不到（权限不足或已退出）");
            }
        }
        catch
        {
            parts.Add("进程已退出");
        }

        var elevated = IsProcessElevated(processId);
        parts.Add($"提权={(elevated.HasValue ? (elevated.Value ? "是（管理员）" : "否") : "未知")}");

        var ancestors = GetProcessAndAncestors(processId).Skip(1).ToList();
        if (ancestors.Count > 0)
        {
            var names = ancestors.Select(pid =>
            {
                try { return $"{Process.GetProcessById((int)pid).ProcessName}({pid})"; }
                catch { return $"({pid})"; }
            });
            parts.Add("上级进程=" + string.Join(" ← ", names));
        }
        else
        {
            parts.Add("上级进程=无");
        }

        return string.Join("；", parts);
    }

    /// <summary>目标进程是否以管理员权限运行；读不到返回 null。</summary>
    public static bool? IsProcessElevated(uint processId)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero) return null;

        try
        {
            if (!NativeMethods.OpenProcessToken(handle, NativeMethods.TOKEN_QUERY, out var token) || token == IntPtr.Zero)
                return null;

            try
            {
                return NativeMethods.GetTokenInformation(token, NativeMethods.TokenElevation,
                           out var elevated, sizeof(uint), out _)
                    ? elevated != 0
                    : null;
            }
            finally
            {
                NativeMethods.CloseHandle(token);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static uint GetParentProcessId(uint processId)
    {
        var handle = NativeMethods.CreateToolhelp32Snapshot(0x00000002, 0);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            return 0;

        try
        {
            var entry = new NativeMethods.PROCESSENTRY32();
            entry.dwSize = (uint)Marshal.SizeOf(entry);

            if (!NativeMethods.Process32First(handle, ref entry))
                return 0;

            do
            {
                if (entry.th32ProcessID == processId)
                    return entry.th32ParentProcessID;
            } while (NativeMethods.Process32Next(handle, ref entry));
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }

        return 0;
    }

    private IntPtr FindMainWindowForProcess(uint targetPid)
    {
        IntPtr bestWindow = IntPtr.Zero;
        int bestArea = 0;

        NativeMethods.EnumWindows((IntPtr hWnd, IntPtr lParam) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd))
                return true;

            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid != targetPid)
                return true;

            if (NativeMethods.GetWindowTextLength(hWnd) == 0)
                return true;

            NativeMethods.GetWindowRect(hWnd, out NativeMethods.RECT rect);
            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;

            if (w < 50 || h < 50)
                return true;

            int area = w * h;
            if (area > bestArea)
            {
                bestArea = area;
                bestWindow = hWnd;
            }
            return true;
        }, IntPtr.Zero);

        return bestWindow;
    }
}
