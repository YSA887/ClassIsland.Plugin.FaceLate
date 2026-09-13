using System.Diagnostics;

namespace ClassIsland.Plugin.FaceLate.Services;

/// <summary>
/// 插件用到的一堆路径。
/// <para>
/// 这些目录都在 <b>ClassIsland 的插件配置目录</b>下（不是插件安装目录，也不是宿主程序目录），
/// 这样重装插件时数据不会丢。具体位置由宿主通过 <see cref="PluginConfigFolder"/> 告诉我们。
/// </para>
/// </summary>
public static class FaceLatePaths
{
    private static string _pluginConfigFolder = "";

    /// <summary>
    /// 插件的配置根目录，由宿主在初始化时注入（<c>Plugin.cs</c> 里设置）。
    /// 拿不到时退化到临时目录，保证不会因为路径为空而崩。
    /// </summary>
    public static string PluginConfigFolder
    {
        get
        {
            if (!string.IsNullOrEmpty(_pluginConfigFolder))
            {
                return _pluginConfigFolder;
            }

            // 还没注入时先用一个临时目录兜着，别让上层拿到空字符串去拼路径。
            var fallback = Path.Combine(Path.GetTempPath(), "ClassIsland-FaceLate");
            try
            {
                Directory.CreateDirectory(fallback);
            }
            catch
            {
                // 连临时目录都建不了就只能返回原值了
            }

            return fallback;
        }
        set => _pluginConfigFolder = value ?? "";
    }

    /// <summary>抓拍存档目录：每次执行建一个时间戳子目录。</summary>
    public static string SnapshotsFolder => Path.Combine(PluginConfigFolder, "snapshots");

    /// <summary>人脸样本目录：按学生 Id 分子目录存放人脸小图。</summary>
    public static string FacesFolder => Path.Combine(PluginConfigFolder, "faces");

    /// <summary>
    /// 确保若干个目录存在，返回第一个（主要的那个）。
    /// </summary>
    public static string EnsureDirectory(params string[] paths)
    {
        var first = "";
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (first.Length == 0)
            {
                first = path;
            }

            try
            {
                Directory.CreateDirectory(path);
            }
            catch
            {
                // 建不了就算了，调用方写入时会再报错
            }
        }

        return first;
    }

    /// <summary>
    /// 在资源管理器里打开一个目录。成功返回空字符串，失败返回原因。
    /// </summary>
    public static string OpenInExplorer(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "路径为空。";
            }

            if (!Directory.Exists(path))
            {
                return "目录不存在：" + path;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                Verb = "open",
            });

            return "";
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }
}
