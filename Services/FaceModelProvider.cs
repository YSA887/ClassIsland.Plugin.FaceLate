using System.Net.Http;
using ClassIsland.Plugin.FaceLate.Models;
using Microsoft.Extensions.Logging;

namespace ClassIsland.Plugin.FaceLate.Services;

/// <summary>
/// 一款人脸识别模型的完整描述。
/// <para>
/// 插件不再把「SFace / ArcFace」硬编码成两个分支，而是维护一张模型清单。
/// 这样新增模型只要往 <see cref="FaceModelCatalog.All"/> 里加一条，
/// 检测、下载、选择、自动推荐这些逻辑全都自动跟上。
/// </para>
/// </summary>
public sealed class FaceRecognizerModel
{
    /// <summary>稳定的内部标识，存进设置里。不要随便改，改了会让老配置失效。</summary>
    public string Id { get; init; } = "";

    /// <summary>界面上显示的名字。</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>存到磁盘上用的文件名。</summary>
    public string FileName { get; init; } = "";

    /// <summary>特征维度（128 / 512），用于诊断。</summary>
    public int FeatureLength { get; init; } = 128;

    /// <summary>文件大概多大（MB），用于界面提示与下载前预告。</summary>
    public double ApproxSizeMb { get; init; }

    /// <summary>一句话说明这个模型的特点，帮用户选。</summary>
    public string Description { get; init; } = "";

    /// <summary>是不是 InsightFace 那一系（预处理要做 (x-127.5)/127.5）。</summary>
    public bool IsInsightFaceStyle { get; init; }

    /// <summary>下载地址，按优先级排（国内镜像在前）。</summary>
    public string[] Urls { get; init; } = Array.Empty<string>();

    /// <summary>判定下载是否成功的体积下限（避免把 Git LFS 指针当成模型）。</summary>
    public long MinSize { get; init; } = 5 * 1024 * 1024;

    /// <summary>自动模式下推荐的优先级，越大越优先。</summary>
    public int AutoPriority { get; init; }

    /// <summary>
    /// 该模型推荐的余弦相似度判定阈值（大于它才认为是同一个人）。
    /// <para>
    /// <b>每款模型的阈值必须各算各的，不能共用一个数字。</b>
    /// 余弦相似度的分布随模型结构、训练集、损失函数而变，把 A 模型的阈值套到 B 模型上
    /// 没有任何依据——InsightFace 官方指南里也明确写着
    /// 「never carry over a threshold across model versions」。
    /// </para>
    /// <para>
    /// 最典型的反例：SFace 的官方推荐值是 <b>0.363</b>，而 InsightFace 的 ArcFace 系
    /// 工作点在 <b>0.4~0.6</b> 这一档。沿用 0.363 会让 ArcFace 过于宽松、更容易串脸。
    /// </para>
    /// </summary>
    public double RecommendedThreshold { get; init; } = 0.5;

    /// <summary>这个推荐阈值的出处，显示在界面上，让用户知道能不能信、要不要再调。</summary>
    public string ThresholdNote { get; init; } = "";

    /// <summary>认这个文件名是不是本模型（用户可能把文件改成别的名字）。</summary>
    public bool MatchesFileName(string path)
    {
        try
        {
            var name = Path.GetFileName(path).ToLowerInvariant();

            // 先看自己的主文件名
            if (name == FileName.ToLowerInvariant())
            {
                return true;
            }

            // 再看别名。ArcFace 系的名字特别乱，单独放宽一点。
            return Id switch
            {
                FaceModelCatalog.ArcFaceId =>
                    name.Contains("w600k") || name.Contains("arcface")
                    || name.Contains("mobilefacenet") || name.Contains("glint")
                    || name.Contains("buffalo"),
                FaceModelCatalog.SFaceId =>
                    name.Contains("sface"),
                _ => false,
            };
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// 插件支持的识别模型清单。
/// </summary>
public static class FaceModelCatalog
{
    /// <summary>SFace（OpenCV Zoo 自带，默认）。</summary>
    public const string SFaceId = "sface";

    /// <summary>ArcFace / MobileFaceNet（InsightFace buffalo_s 里的 w600k_mbf）。</summary>
    public const string ArcFaceId = "arcface_mobilefacenet";

    /// <summary>「自动」——按优先级和文件缺失情况自己挑一个。</summary>
    public const string AutoId = "auto";

    /// <summary>全部可选的识别模型。</summary>
    public static readonly IReadOnlyList<FaceRecognizerModel> All = new List<FaceRecognizerModel>
    {
        new()
        {
            Id = SFaceId,
            DisplayName = "SFace（自带，稳妥）",
            FileName = "face_recognition_sface_2021dec.onnx",
            FeatureLength = 128,
            ApproxSizeMb = 37,
            Description = "OpenCV 官方推荐搭配，随插件包一起发布，不用联网下载。"
                          + "精度够用、速度快，是默认选择。",
            IsInsightFaceStyle = false,
            AutoPriority = 10,
            RecommendedThreshold = 0.363,
            ThresholdNote = "OpenCV 官方给的值，一般不用改",
            MinSize = 5 * 1024 * 1024,
            Urls = new[]
            {
                "https://hf-mirror.com/opencv/face_recognition_sface/resolve/main/face_recognition_sface_2021dec.onnx",
                "https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models/face_recognition_sface/face_recognition_sface_2021dec.onnx",
                "https://huggingface.co/opencv/face_recognition_sface/resolve/main/face_recognition_sface_2021dec.onnx",
            },
        },
        new()
        {
            Id = ArcFaceId,
            DisplayName = "ArcFace / MobileFaceNet（更准）",
            FileName = "w600k_mbf.onnx",
            FeatureLength = 512,
            ApproxSizeMb = 13,
            Description = "InsightFace 的 MobileFaceNet，512 维特征，在侧脸、戴眼镜、光线偏暗时"
                          + "通常比 SFace 更稳。文件更小，但需要单独下载或手动导入。",
            IsInsightFaceStyle = true,
            AutoPriority = 20,
            RecommendedThreshold = 0.50,
            ThresholdNote = "InsightFace 系的常用起点，可按班里情况微调",
            MinSize = 5 * 1024 * 1024,
            Urls = new[]
            {
                "https://hf-mirror.com/deepghs/insightface/resolve/main/buffalo_s/w600k_mbf.onnx",
                "https://huggingface.co/deepghs/insightface/resolve/main/buffalo_s/w600k_mbf.onnx",
            },
        },
    };

    /// <summary>按 Id 找一个模型；找不到返回 null。</summary>
    public static FaceRecognizerModel? Find(string? id)
        => All.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 取某款模型的推荐余弦阈值。认不出的 Id 返回一个中间值兜底。
    /// <para>注意这<b>不是</b>「通用阈值」——阈值必须跟着模型走，见 <see cref="FaceRecognizerModel.RecommendedThreshold"/>。</para>
    /// </summary>
    public static double RecommendedThresholdFor(string? id) => Find(id)?.RecommendedThreshold ?? 0.5;

    /// <summary>按磁盘上的文件名反推是哪个模型；认不出返回 null。</summary>
    public static FaceRecognizerModel? MatchByPath(string? path)
        => string.IsNullOrEmpty(path) ? null : All.FirstOrDefault(x => x.MatchesFileName(path));

    /// <summary>这个模型是不是 InsightFace 那一系（决定预处理）。</summary>
    public static bool IsInsightFaceStyle(string? path)
        => MatchByPath(path)?.IsInsightFaceStyle ?? LooksLikeArcFaceName(path);

    /// <summary>
    /// 兼容用的老判定：按文件名猜是不是 ArcFace 系。
    /// </summary>
    public static bool LooksLikeArcFaceName(string path)
    {
        try
        {
            var name = Path.GetFileName(path).ToLowerInvariant();
            return name.Contains("w600k") || name.Contains("arcface")
                   || name.Contains("mobilefacenet") || name.Contains("glint")
                   || name.Contains("buffalo");
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// 负责定位人脸模型文件。
/// <para>
/// <b>关键点：模型一定优先从插件自己的目录里找。</b>
/// 插件运行时 <c>AppContext.BaseDirectory</c> 指向的是 <b>ClassIsland 主程序</b>的目录，
/// 而不是插件的安装目录，所以早期版本用 <c>AppContext.BaseDirectory</c> 拼路径永远找不到随包发布的模型，
/// 于是每次都被判定成「模型缺失」而去联网下载——在访问不了 GitHub / HuggingFace 的学校网络里就彻底卡死。
/// 正确的基准是 <c>typeof(XXX).Assembly.Location</c>（也就是插件 dll 所在目录）。
/// </para>
/// <para>
/// 默认使用的两个模型都来自 OpenCV Zoo（Apache-2.0，https://github.com/opencv/opencv_zoo）：
/// <list type="bullet">
/// <item>YuNet 人脸检测：face_detection_yunet_2023mar.onnx，约 230 KB</item>
/// <item>SFace 人脸识别：face_recognition_sface_2021dec.onnx，约 37 MB</item>
/// </list>
/// 两者都是纯 CPU 推理的小模型，本插件在 8 GB 机器上余量充足。
/// 另外可选 InsightFace 的 MobileFaceNet（w600k_mbf.onnx，约 13 MB），精度更高。
/// </para>
/// </summary>
public sealed class FaceModelProvider
{
    /// <summary>YuNet 检测模型文件名。</summary>
    public const string DetectorFileName = "face_detection_yunet_2023mar.onnx";

    /// <summary>默认识别模型（SFace）文件名。</summary>
    public const string RecognizerFileName = "face_recognition_sface_2021dec.onnx";

    /// <summary>可选高精度识别模型（ArcFace / MobileFaceNet）文件名。</summary>
    public const string ArcFaceFileName = "w600k_mbf.onnx";

    /// <summary>识别模型的备选文件名，用户从别处拷来的文件叫什么都有可能。</summary>
    private static readonly string[] ArcFaceAliases = { "w600k_mbf.onnx", "arcface_w600k_mbf.onnx", "arcface.onnx", "mobilefacenet.onnx" };

    private static readonly string[] DetectorUrls =
    {
        "https://hf-mirror.com/opencv/face_detection_yunet/resolve/main/face_detection_yunet_2023mar.onnx",
        "https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx",
        "https://huggingface.co/opencv/face_detection_yunet/resolve/main/face_detection_yunet_2023mar.onnx",
    };

    /// <summary>模型的期望最小体积，用来判断下载结果是否是真实模型（而不是 Git LFS 指针文件）。</summary>
    private const long DetectorMinSize = 100 * 1024;

    private readonly FaceLateSettings _settings;
    private readonly ILogger<FaceModelProvider> _logger;

    /// <summary>模型缓存目录：插件配置目录下的 models/。用户导入的模型也放这里。</summary>
    public string ModelDirectory { get; }

    /// <summary>
    /// 插件自己的安装目录（插件 dll 所在目录）。随包发布的模型就在这里面的 Assets/Models 下。
    /// </summary>
    public static string PluginDirectory { get; } = ResolvePluginDirectory();

    public FaceModelProvider(FaceLateSettings settings, ILogger<FaceModelProvider> logger)
    {
        _settings = settings;
        _logger = logger;
        ModelDirectory = Path.Combine(FaceLatePaths.PluginConfigFolder, "models");
    }

    /// <summary>当前设置下应该使用的识别模型文件名。</summary>
    public string CurrentRecognizerFileName => ResolveRequestedModel()?.FileName ?? RecognizerFileName;

    /// <summary>
    /// 设置里「想要」的那个模型。选「自动」时返回 null（表示交给 <see cref="PickAutoModel"/> 决定）。
    /// </summary>
    public FaceRecognizerModel? ResolveRequestedModel()
    {
        var id = _settings.RecognizerModelId;
        if (string.IsNullOrWhiteSpace(id) || id == FaceModelCatalog.AutoId)
        {
            return null;
        }

        return FaceModelCatalog.Find(id);
    }

    /// <summary>
    /// 自动模式：挑一款**磁盘上已经存在**的模型；都不在就挑优先级最高的一款（准备下载）。
    /// </summary>
    public FaceRecognizerModel PickAutoModel()
    {
        // 先看哪些已经在磁盘上（用户导入过、或随包带过、或以前下载过）。
        var available = FaceModelCatalog.All
            .Where(m => FindExisting("", new[] { m.FileName }) != null)
            .ToList();

        if (available.Count > 0)
        {
            // 已存在的里面挑特征维度高的（一般更准）。
            return available.OrderByDescending(m => m.FeatureLength).First();
        }

        // 都没有就挑优先级最高的，后面会按需下载。
        return FaceModelCatalog.All.OrderByDescending(m => m.AutoPriority).First();
    }

    /// <summary>
    /// 按文件名猜这个识别模型是不是 ArcFace 系（InsightFace 的 MobileFaceNet / ResNet 系列）。
    /// <para>
    /// 两种模型的图像预处理不一样，所以判断必须跟着「实际载入的那个文件」走。
    /// 用户可能把文件改成任何名字，因此多做几种匹配。
    /// </para>
    /// </summary>
    public static bool LooksLikeArcFaceName(string path) => FaceModelCatalog.LooksLikeArcFaceName(path);

    /// <summary>
    /// 只读地检查一下模型到底能不能用，**不会下载任何东西**。
    /// <para>
    /// 之前的诊断直接拿 <see cref="CurrentRecognizerFileName"/> 去找文件，
    /// 于是「设置了 ArcFace 但没放文件、实际已经回退到自带 SFace」这种情况会被报成
    /// 「识别模型未找到 w600k_mbf.onnx」——明明能跑，却把人吓一跳。
    /// 这里跟 <see cref="EnsureAsync"/> 用同一套回退规则，报出来的一定是真实可用的状态。
    /// </para>
    /// </summary>
    public ModelStatus CheckStatus()
    {
        var detector = FindExisting(_settings.DetectorModelPath, new[] { DetectorFileName });
        var (recognizer, usedFallback, resolved) = ResolveRecognizerPath();

        return new ModelStatus
        {
            DetectorPath = detector,
            RecognizerPath = recognizer,
            UsedFallback = usedFallback,
            RequestedRecognizerName = CurrentRecognizerFileName,
            ResolvedRecognizerName = resolved?.FileName ?? "",
            ResolvedRecognizerDisplayName = resolved?.DisplayName ?? "",
            ModelDirectory = ModelDirectory,
            PluginDirectory = PluginDirectory,
        };
    }

    /// <summary>
    /// 按「设置 → 自动挑选 → 回退」的顺序定出实际要用的识别模型路径。
    /// </summary>
    private (string? Path, bool UsedFallback, FaceRecognizerModel? Model) ResolveRecognizerPath()
    {
        var requested = ResolveRequestedModel();

        // 自动模式：挑一个磁盘上有的。
        if (requested == null)
        {
            var auto = PickAutoModel();
            var autoPath = FindExisting(_settings.RecognizerModelPath, new[] { auto.FileName });
            if (autoPath != null)
            {
                return (autoPath, false, auto);
            }

            // 自动挑的那款不在，看看别的模型有没有在的。
            foreach (var candidate in FaceModelCatalog.All.OrderByDescending(m => m.FeatureLength))
            {
                var path = FindExisting("", new[] { candidate.FileName });
                if (path != null)
                {
                    return (path, true, candidate);
                }
            }

            return (null, false, auto);
        }

        // 手动指定：先按设置里的路径/文件名找。
        var aliases = requested.Id == FaceModelCatalog.ArcFaceId ? ArcFaceAliases : new[] { requested.FileName };
        var found = FindExisting(_settings.RecognizerModelPath, aliases);
        if (found != null)
        {
            return (found, false, requested);
        }

        // 用户指定的模型不在 → 退回自带 SFace，保证功能可用。
        var sface = FaceModelCatalog.Find(FaceModelCatalog.SFaceId);
        if (sface != null)
        {
            var fallback = FindExisting("", new[] { sface.FileName });
            if (fallback != null)
            {
                return (fallback, true, sface);
            }
        }

        return (null, false, requested);
    }

    /// <summary>
    /// 确保两个模型文件都存在且可用。返回 (检测模型路径, 识别模型路径, 错误信息)。
    /// </summary>
    public async Task<(string Detector, string Recognizer, string Error)> EnsureAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var detector = FindExisting(_settings.DetectorModelPath, new[] { DetectorFileName });
        var (recognizer, usedFallback, resolvedModel) = ResolveRecognizerPath();

        if (usedFallback && resolvedModel != null)
        {
            _logger.LogWarning("FaceLate 未找到指定的识别模型，临时回退到 {Model}", resolvedModel.DisplayName);
            progress?.Report($"没有找到指定的识别模型，本次先用{resolvedModel.DisplayName}。");
        }

        if (detector == null && _settings.AllowModelDownload)
        {
            progress?.Report("正在下载人脸检测模型（YuNet，约 230 KB）…");
            detector = await DownloadAsync(DetectorFileName, DetectorUrls, DetectorMinSize, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        if (recognizer == null && _settings.AllowModelDownload)
        {
            var model = resolvedModel ?? PickAutoModel();
            progress?.Report($"正在下载识别模型 {model.DisplayName}（约 {model.ApproxSizeMb:F0} MB，仅首次需要）…");
            recognizer = await DownloadAsync(model.FileName, model.Urls, model.MinSize, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        if (detector != null && recognizer != null)
        {
            return (detector, recognizer, "");
        }

        // 失败时把「找过哪些地方」全部列出来，否则用户完全不知道该把模型放哪儿。
        var searched = string.Join("\n", BuildSearchPaths(DetectorFileName)
            .Concat(BuildSearchPaths(CurrentRecognizerFileName))
            .Distinct());
        var missingList = string.Join("、", new[]
        {
            detector == null ? DetectorFileName : null,
            recognizer == null ? CurrentRecognizerFileName : null,
        }.Where(x => x != null));

        var message =
            $"缺少模型文件：{missingList}\n\n" +
            $"插件已经把这些位置都找过了：\n{searched}\n\n" +
            $"最省事的解决办法：\n" +
            $"1. 确认插件包安装完整（Assets\\Models 目录下应该有 2 个 .onnx 文件）；\n" +
            $"2. 或者点上面的「导入模型文件…」按钮，手动选中 .onnx 文件，插件会自动复制到：\n   {ModelDirectory}\n" +
            $"3. 学校网络访问不了 GitHub / HuggingFace，所以「允许联网下载模型」默认是关掉的；" +
            $"如果确实需要联网下载，请在下面打开这个开关。";

        _logger.LogError("FaceLate 模型准备失败：{Message}", message);
        return ("", "", message);
    }

    /// <summary>
    /// 把清单里所有**还没下载**的模型都下下来，供离线使用。
    /// </summary>
    /// <param name="progress">进度回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<(int Downloaded, int AlreadyHad, List<string> Failures)> DownloadAllAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var downloaded = 0;
        var alreadyHad = 0;
        var failures = new List<string>();

        foreach (var model in FaceModelCatalog.All.OrderBy(m => m.ApproxSizeMb))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (FindExisting("", new[] { model.FileName }) != null)
            {
                alreadyHad++;
                continue;
            }

            progress?.Report($"正在下载 {model.DisplayName}（约 {model.ApproxSizeMb:F0} MB）…");
            var path = await DownloadAsync(model.FileName, model.Urls, model.MinSize, progress, cancellationToken)
                .ConfigureAwait(false);

            if (path != null)
            {
                downloaded++;
            }
            else
            {
                failures.Add(model.DisplayName);
            }
        }

        return (downloaded, alreadyHad, failures);
    }

    /// <summary>
    /// 在候选目录里按名称查找一个可用的模型文件。
    /// </summary>
    public string? FindExisting(string configuredPath, IEnumerable<string> fileNames)
    {
        var names = fileNames.ToList();

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            try
            {
                if (File.Exists(configuredPath) && new FileInfo(configuredPath).Length > 1024)
                {
                    return configuredPath;
                }
            }
            catch
            {
                // 忽略非法路径
            }
        }

        foreach (var candidate in names.SelectMany(BuildSearchPaths))
        {
            try
            {
                if (File.Exists(candidate) && new FileInfo(candidate).Length > 1024)
                {
                    return candidate;
                }
            }
            catch
            {
                // 忽略非法路径
            }
        }

        return null;
    }

    /// <summary>
    /// 生成某个文件名的全部候选路径，按优先级从高到低。
    /// </summary>
    public static IEnumerable<string> BuildSearchPaths(string fileName)
    {
        // 1) 插件自己的目录 —— 随包发布的模型就在这里
        yield return Path.Combine(PluginDirectory, "Assets", "Models", fileName);
        yield return Path.Combine(PluginDirectory, "Assets", fileName);
        yield return Path.Combine(PluginDirectory, "models", fileName);
        yield return Path.Combine(PluginDirectory, fileName);

        // 2) 插件配置目录 —— 用户导入的模型放这里
        yield return Path.Combine(FaceLatePaths.PluginConfigFolder, "models", fileName);
        yield return Path.Combine(FaceLatePaths.PluginConfigFolder, fileName);

        // 3) 最后才兜到底：宿主程序目录（正常用不到，留着以防有人把模型扔在 ClassIsland 根目录）
        yield return Path.Combine(AppContext.BaseDirectory, "Assets", "Models", fileName);
        yield return Path.Combine(AppContext.BaseDirectory, fileName);
    }

    /// <summary>
    /// 把用户选中的模型文件复制到插件配置目录，并返回复制后的路径。
    /// </summary>
    /// <param name="sourcePath">用户选择的 .onnx 文件（U 盘、下载目录里的都行）。</param>
    /// <param name="isDetector">true 表示这是检测模型，false 表示识别模型。</param>
    public async Task<(bool Ok, string Message)> ImportAsync(string sourcePath, bool isDetector)
    {
        try
        {
            if (!File.Exists(sourcePath))
            {
                return (false, "找不到这个文件：" + sourcePath);
            }

            var length = new FileInfo(sourcePath).Length;
            if (length < 1024)
            {
                return (false, "这个文件太小了（只有 " + length + " 字节），不像是真正的模型文件。");
            }

            Directory.CreateDirectory(ModelDirectory);

            // 识别模型：先按文件名认出是哪一款，认不出就按体积猜（大的当 SFace，小的当 MobileFaceNet）。
            var detected = isDetector ? null : FaceModelCatalog.MatchByPath(sourcePath);
            if (!isDetector && detected == null)
            {
                detected = length > 20 * 1024 * 1024
                    ? FaceModelCatalog.Find(FaceModelCatalog.SFaceId)
                    : FaceModelCatalog.Find(FaceModelCatalog.ArcFaceId);
            }

            var targetName = isDetector ? DetectorFileName : (detected?.FileName ?? RecognizerFileName);
            var target = Path.Combine(ModelDirectory, targetName);

            // 源文件和目标文件是同一个就不必复制
            if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                await using (var src = File.OpenRead(sourcePath))
                await using (var dst = File.Create(target))
                {
                    await src.CopyToAsync(dst).ConfigureAwait(false);
                }
            }

            if (isDetector)
            {
                _settings.DetectorModelPath = target;
            }
            else
            {
                _settings.RecognizerModelPath = target;

                // 顺手把「手动选择」指到这款模型上，用户导入完就能直接用。
                if (detected != null)
                {
                    _settings.RecognizerModelId = detected.Id;
                }
            }

            _logger.LogInformation("FaceLate 已导入模型 {Source} -> {Target}", sourcePath, target);

            var note = isDetector ? "" : $"（识别为：{detected?.DisplayName ?? "未知型号"}）";
            return (true, $"已导入：{Path.GetFileName(sourcePath)} → {target}{note}");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "FaceLate 导入模型失败");
            return (false, "导入失败：" + e.Message);
        }
    }

    private async Task<string?> DownloadAsync(
        string fileName,
        string[] urls,
        long minSize,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(ModelDirectory);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "FaceLate 无法创建模型目录 {Dir}", ModelDirectory);
            return null;
        }

        var target = Path.Combine(ModelDirectory, fileName);
        var temp = target + ".part";

        using var handler = new HttpClientHandler { AllowAutoRedirect = true };
        // 超时给短一点：学校网络里这些域名通常直接连不上，没必要让用户干等十分钟。
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "ClassIsland-FaceLate/1.0");

        foreach (var url in urls)
        {
            try
            {
                progress?.Report($"正在下载 {fileName} …");
                _logger.LogInformation("FaceLate 尝试从 {Url} 下载模型", url);
                using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                           .ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("FaceLate 下载 {Url} 失败：{Code}", url, response.StatusCode);
                        continue;
                    }

                    await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                    await using (var destination = File.Create(temp))
                    {
                        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                    }
                }

                var length = new FileInfo(temp).Length;
                if (length < minSize)
                {
                    _logger.LogWarning("FaceLate 从 {Url} 下载到的文件只有 {Size} 字节，判定为无效（可能是 Git LFS 指针）", url, length);
                    SafeDelete(temp);
                    continue;
                }

                if (File.Exists(target))
                {
                    SafeDelete(target);
                }

                File.Move(temp, target);
                _logger.LogInformation("FaceLate 模型已就绪：{Path}（{Size} 字节）", target, length);
                return target;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "FaceLate 从 {Url} 下载模型时出错", url);
            }
        }

        SafeDelete(temp);
        return null;
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 忽略
        }
    }

    private static string ResolvePluginDirectory()
    {
        try
        {
            var location = typeof(FaceModelProvider).Assembly.Location;
            if (!string.IsNullOrEmpty(location))
            {
                var dir = Path.GetDirectoryName(location);
                if (!string.IsNullOrEmpty(dir))
                {
                    return dir;
                }
            }
        }
        catch
        {
            // 忽略，退回到宿主目录
        }

        return AppContext.BaseDirectory;
    }
}

/// <summary>
/// 一次模型检查的结果。全部基于「实际能在磁盘上找到什么」，不含任何推断。
/// </summary>
public sealed class ModelStatus
{
    /// <summary>检测模型的实际路径；找不到为 null。</summary>
    public string? DetectorPath { get; init; }

    /// <summary>识别模型的实际路径；找不到为 null。</summary>
    public string? RecognizerPath { get; init; }

    /// <summary>设置里要的模型不在，实际回退到了别的模型。</summary>
    public bool UsedFallback { get; init; }

    /// <summary>设置里原本想要的识别模型文件名。</summary>
    public string RequestedRecognizerName { get; init; } = "";

    /// <summary>实际用上的识别模型文件名。</summary>
    public string ResolvedRecognizerName { get; init; } = "";

    /// <summary>实际用上的识别模型的显示名。</summary>
    public string ResolvedRecognizerDisplayName { get; init; } = "";

    /// <summary>模型目录。</summary>
    public string ModelDirectory { get; init; } = "";

    /// <summary>插件目录。</summary>
    public string PluginDirectory { get; init; } = "";

    /// <summary>两个模型是否都齐了。</summary>
    public bool Ready => DetectorPath != null && RecognizerPath != null;

    /// <summary>识别模型是 ArcFace 系还是 SFace。按实际文件名判断。</summary>
    public bool RecognizerIsArcFace =>
        RecognizerPath != null && FaceModelProvider.LooksLikeArcFaceName(RecognizerPath);

    /// <summary>识别模型的显示名。</summary>
    public string RecognizerDisplayName =>
        RecognizerPath == null
            ? $"缺少 {RequestedRecognizerName}"
            : Path.GetFileName(RecognizerPath) + (RecognizerIsArcFace ? "（ArcFace / MobileFaceNet）" : "（SFace）");

    /// <summary>
    /// 一句话摘要。界面上的状态栏只用这一行，避免刷屏。
    /// </summary>
    public string Summary
    {
        get
        {
            if (!Ready)
            {
                return "人脸模型没找齐，无法识别或录入。";
            }

            return UsedFallback
                ? $"识别模型：{ResolvedRecognizerDisplayName}（想用的那款没找到，已换用这款）"
                : $"识别模型：{ResolvedRecognizerDisplayName}";
        }
    }

    /// <summary>技术细节（展开时才看）。</summary>
    public string Detail =>
        $"插件目录 : {PluginDirectory}\n" +
        $"模型目录 : {ModelDirectory}\n" +
        $"检测模型 : {DetectorPath ?? "（没找到）"}\n" +
        $"识别模型 : {RecognizerPath ?? "（没找到）"}";
}
