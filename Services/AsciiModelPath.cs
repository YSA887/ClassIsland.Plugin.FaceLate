using System.IO;
using Microsoft.Extensions.Logging;

namespace ClassIsland.Plugin.FaceLate.Services;

/// <summary>
/// 把模型文件搬到「纯 ASCII 路径」下再交给 OpenCV。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要这个：</b>OpenCV 的 C++ 实现是用窄字符（ANSI）路径去开文件的，
/// 而 <see cref="OpenCvSharp.FaceDetectorYN.Create"/> 只会把路径原样递进去。
/// 于是只要模型路径里含非 ASCII 字符（典型的就是<b>中文目录名</b>），
/// 它就会抛 <c>OpenCVException: Can't read ONNX file</c>。
/// </para>
/// <para>
/// 这个坑非常隐蔽：ClassIsland 装在 <c>D:\Desktop\ClassIsland_...</c> 这类纯英文目录下时一切正常，
/// 一旦装在 <c>D:\教学软件\ClassIsland</c> 或者 Windows 用户名是中文
/// （<c>C:\Users\张老师\...</c>，插件配置目录跟着用户名走）就会稳定失败。
/// 而失败又被上层 <c>catch</c> 转成了「未检测到人脸」，看起来像是照片或模型的问题，
/// 完全联想不到「装在哪个目录」上。
/// </para>
/// <para>
/// 图片读写那边早就绕开了这个坑（用 <c>File.ReadAllBytes</c> + <c>ImDecode</c>、
/// 用 <c>WriteAllBytes</c> 而不是 <c>ImWrite</c>），但模型加载漏了 —— 这里补上。
/// </para>
/// <para>
/// 检测模型只有 230 KB 左右，复制一次的代价可以忽略；识别模型不需要走这里
/// （<c>CvDnn.ReadNetFromOnnx</c> 有字节数组重载，直接读进内存即可）。
/// </para>
/// </remarks>
internal static class AsciiModelPath
{
    /// <summary>ASCII 缓存目录名。</summary>
    private const string CacheFolderName = "ClassIsland-FaceLate-Models";

    /// <summary>选定的 ASCII 缓存目录，选定后不再变。</summary>
    private static string? _cacheDir;

    /// <summary>判断路径是否只含 ASCII 字符（即能不能直接交给 OpenCV）。</summary>
    public static bool IsAscii(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return true;
        }

        foreach (var c in path)
        {
            if (c > 127)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 返回一个可以直接交给 OpenCV 的路径。
    /// <para>
    /// 原路径本身就是纯 ASCII 时<b>原样返回</b>（不复制、零开销）；
    /// 含非 ASCII 字符时把文件复制到一个纯 ASCII 目录，返回副本路径。
    /// </para>
    /// </summary>
    /// <param name="sourcePath">原始模型路径。</param>
    /// <param name="asciiFileName">复制到缓存目录后使用的文件名（必须是纯 ASCII）。</param>
    /// <param name="logger">可选的日志。</param>
    /// <returns>可用路径；实在找不到可写的 ASCII 目录时返回 <c>null</c>。</returns>
    public static string? Resolve(string sourcePath, string asciiFileName, ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(sourcePath) || IsAscii(sourcePath))
        {
            return sourcePath;
        }

        try
        {
            var dir = EnsureCacheDir(logger);
            if (dir == null)
            {
                logger?.LogError(
                    "FaceLate 模型路径含非 ASCII 字符（{Path}），但找不到可写的纯 ASCII 目录来放副本。",
                    sourcePath);
                return null;
            }

            var dest = Path.Combine(dir, asciiFileName);

            // 大小一致就认为已经是同一份，省掉一次复制（每次检测都调用，这里要便宜）。
            var sameSize = false;
            try
            {
                sameSize = File.Exists(dest)
                           && new FileInfo(dest).Length == new FileInfo(sourcePath).Length;
            }
            catch
            {
                sameSize = false;
            }

            if (!sameSize)
            {
                File.Copy(sourcePath, dest, true);
                logger?.LogInformation("FaceLate 模型路径含非 ASCII 字符，已复制副本到：{Dest}", dest);
            }

            return dest;
        }
        catch (Exception e)
        {
            logger?.LogError(e, "FaceLate 无法把模型复制到 ASCII 路径：{Path}", sourcePath);
            return null;
        }
    }

    /// <summary>
    /// 挑一个可写的纯 ASCII 目录。按「与用户名无关 → 与安装路径无关」的顺序试。
    /// </summary>
    private static string? EnsureCacheDir(ILogger? logger)
    {
        if (_cacheDir != null)
        {
            return _cacheDir;
        }

        foreach (var candidate in Candidates())
        {
            if (!IsAscii(candidate))
            {
                // 用户名是中文时 %TEMP% 也会带中文，直接跳过。
                continue;
            }

            try
            {
                Directory.CreateDirectory(candidate);

                // 光能建目录不代表能写文件（可能有组策略/杀软拦着），真写一个试一下。
                var probe = Path.Combine(candidate, ".write-test");
                File.WriteAllBytes(probe, Array.Empty<byte>());
                File.Delete(probe);

                _cacheDir = candidate;
                logger?.LogInformation("FaceLate 选定 ASCII 模型缓存目录：{Dir}", candidate);
                return candidate;
            }
            catch (Exception e)
            {
                logger?.LogWarning(e, "FaceLate 候选 ASCII 目录不可用：{Dir}", candidate);
            }
        }

        return null;
    }

    /// <summary>
    /// 候选目录，按优先级排列。
    /// </summary>
    private static List<string> Candidates()
    {
        var list = new List<string>();

        // 1) C:\ProgramData —— 固定盘符固定名字，跟用户名、跟安装路径都无关，最稳。
        try
        {
            var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrEmpty(common))
            {
                list.Add(Path.Combine(common, CacheFolderName));
            }
        }
        catch
        {
            // 拿不到就跳过
        }

        // 2) 系统临时目录 —— 用户名是英文时通常也可写。
        try
        {
            list.Add(Path.Combine(Path.GetTempPath(), CacheFolderName));
        }
        catch
        {
            // 忽略
        }

        // 3) Windows\Temp —— 前面都不行时兜底。
        try
        {
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(win))
            {
                list.Add(Path.Combine(win, "Temp", CacheFolderName));
            }
        }
        catch
        {
            // 忽略
        }

        return list;
    }
}
