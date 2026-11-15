using System.IO;
using System.Text;

namespace VideoTurner.Services;

/// <summary>
/// 路由决策的「黑匣子」。
///
/// <para>为什么需要它：<see cref="Interop.AudioPolicyConfigClient"/> 的调用失败只会抛异常，
/// 而路由引擎在后台线程里跑、异常一律被吞掉 —— 于是「拖了窗口没反应」时，
/// 用户和开发者都完全看不出到底是「没匹配到显示器」「没有映射」还是
/// 「Set 调用被系统拒绝（HRESULT）」。有了这个日志，一次复现就能定位。</para>
///
/// <para>日志写到 <c>%APPDATA%\VideoTurner\routing.log</c>，超过 <see cref="MaxBytes"/>
/// 自动轮转一份 <c>routing.1.log</c>，避免无限增长。写入失败全部静默忽略 ——
/// 记日志本身绝不能影响路由。</para>
/// </summary>
public static class RoutingLog
{
    private static readonly object Gate = new();

    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoTurner");

    public static string LogPath => Path.Combine(LogDir, "routing.log");

    private const long MaxBytes = 1024 * 1024;

    /// <summary>最近一条「值得用户知道」的状态（成功或失败），供界面显示。</summary>
    public static string LastStatus { get; private set; } = string.Empty;

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message) => Write("ERROR", message);

    /// <summary>同时写日志并更新界面状态行。</summary>
    public static void Status(string message)
    {
        LastStatus = message;
        Write("STAT ", message);
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDir);

                var file = new FileInfo(LogPath);
                if (file.Exists && file.Length > MaxBytes)
                {
                    var rotated = Path.Combine(LogDir, "routing.1.log");
                    try { File.Delete(rotated); } catch { }
                    try { File.Move(LogPath, rotated); } catch { }
                }

                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // 记日志失败不是错误
        }
    }
}
