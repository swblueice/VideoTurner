using System.Reflection;

namespace VideoTurner.Services;

/// <summary>
/// 关于页需要的一点点程序自身信息。原本这部分逻辑住在上游的
/// <c>UpdateService</c> 里（它还会去查 GitHub Release）；自有版本不做联网更新检查，
/// 只保留「版本号 / 版权行」这两项从程序集元数据读取的能力，
/// 这样版本号永远跟实际构建一致，不会和 csproj 里的字面量脱节。
/// </summary>
public static class AppInfoService
{
    /// <summary>
    /// 取自 <c>[AssemblyInformationalVersion]</c>（MSBuild 由 csproj 的 &lt;Version&gt; 生成），
    /// 拿不到时退回文件版本，再退回 "unknown"。
    /// </summary>
    public static string CurrentVersion => GetInformationalVersion();

    /// <summary>
    /// 取自 <c>[AssemblyCopyright]</c>。备注：本项目包含 MIT 许可的第三方代码，
    /// 版权行里同时写明原作者的声明（见 csproj 的 &lt;Copyright&gt;）。
    /// </summary>
    public static string Copyright => GetAssemblyCopyright();

    private static string GetInformationalVersion()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                // SourceLink 会追加 "+<commit sha>"，界面上不需要
                var plus = informational.IndexOf('+');
                return plus > 0 ? informational[..plus] : informational;
            }

            return assembly.GetName().Version?.ToString(3) ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string GetAssemblyCopyright()
    {
        try
        {
            return Assembly.GetExecutingAssembly()
                       .GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
                   ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
