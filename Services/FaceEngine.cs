using ClassIsland.Plugin.FaceLate.Models;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace ClassIsland.Plugin.FaceLate.Services;

/// <summary>
/// 一张被检测到的人脸。
/// </summary>
public sealed class DetectedFace
{
    /// <summary>
    /// YuNet 原始输出：x, y, w, h, 5 组关键点 (x,y)，最后一位是置信度，共 15 个浮点数。
    /// </summary>
    public float[] Box { get; } = new float[15];

    /// <summary>
    /// 128 维特征向量（调用特征提取后才有值）。
    /// </summary>
    public float[]? Feature { get; set; }

    /// <summary>人脸框左上角 X。</summary>
    public int X => (int)MathF.Round(Box[0]);

    /// <summary>人脸框左上角 Y。</summary>
    public int Y => (int)MathF.Round(Box[1]);

    /// <summary>人脸框宽度。</summary>
    public int Width => (int)MathF.Round(Box[2]);

    /// <summary>人脸框高度。</summary>
    public int Height => (int)MathF.Round(Box[3]);

    /// <summary>检测置信度。</summary>
    public float Score => Box[14];

    /// <summary>人脸框面积，用于挑选「离摄像头最近的人」。</summary>
    public int Area => Math.Max(0, Width) * Math.Max(0, Height);
}

/// <summary>
/// 人脸库里的一条记录：某个学生的一条人脸样本。
/// </summary>
public sealed class GalleryEntry
{
    /// <summary>样本所属学生。</summary>
    public Student Student { get; init; } = null!;

    /// <summary>特征向量。</summary>
    public float[] Feature { get; init; } = Array.Empty<float>();
}

/// <summary>
/// 人脸引擎：YuNet 负责人脸检测（含 5 点关键点），识别模型负责提取特征向量
/// （自带 SFace 128 维，可选 ArcFace / MobileFaceNet 512 维）。
/// 两者都通过 OpenCvSharp / OpenCV DNN 在本机 CPU 上推理，无网络请求、无云端调用。
/// <para>
/// 对齐流程按 5 点关键点做 Umeyama 相似变换，把脸摆正到 112×112
/// （SFace 侧严格照搬 OpenCV 官方 <c>cv::FaceRecognizerSF</c> 的实现
/// <c>modules/objdetect/src/face_recognize.cpp</c>）。
/// 预处理随模型而定：SFace 是 <c>blobFromImage(scale=1)</c>，
/// InsightFace 系是 <c>blobFromImage(scale=1/127.5, mean=127.5)</c>。
/// </para>
/// <para>
/// <b>判定阈值不是全局的</b>：每款模型用自己的推荐值（见
/// <see cref="FaceRecognizerModel.RecommendedThreshold"/>），运行时取
/// <see cref="EffectiveThreshold"/>。
/// </para>
/// <para>
/// 说明：OpenCvSharp 4.13 只暴露了 <c>FaceDetectorYN</c>，没有 <c>FaceRecognizerSF</c>
/// （后者是 OpenCV 4.14 才封装进 OpenCvSharp 的）。所以这里用 <c>CvDnn</c> 直接跑模型，
/// 并自己实现对齐，效果与官方实现一致。
/// </para>
/// </summary>
public sealed class FaceEngine : IDisposable
{
    /// <summary>
    /// 兜底阈值，只在「认不出当前载入的是哪款模型」时才会用到。
    /// <para>
    /// 0.363 是 <b>SFace</b> 的官方推荐值（余弦），<b>它不是通用阈值</b>。
    /// 每款模型都应该用自己的推荐值，见 <see cref="FaceModelCatalog"/> 里各自的
    /// <c>RecommendedThreshold</c>；运行时请一律使用 <see cref="EffectiveThreshold"/>，
    /// 不要直接引用这个常量。
    /// </para>
    /// </summary>
    public const float DefaultThreshold = 0.363f;

    /// <summary>
    /// 当前<b>实际载入</b>的识别模型 Id（<see cref="LoadAsync"/> 之后才有意义）。
    /// <para>注意是「实际载入」而不是「设置里选的」——设置里选的那款文件不在时，
    /// 插件会回退到自带的 SFace，这里反映的是回退之后的真实情况。</para>
    /// </summary>
    public string EffectiveModelId { get; private set; } = FaceModelCatalog.SFaceId;

    /// <summary>
    /// 当前应当使用的余弦相似度判定阈值。
    /// <para>
    /// <b>阈值跟着模型走</b>：不同模型的相似度分布不同，官方推荐值也不同
    /// （SFace 0.363；InsightFace 的 ArcFace 系在 0.4~0.6 这一档）。
    /// 所以这里每次都向设置要「当前模型那一档」的值，而不是拿一个写死的数字到处用。
    /// </para>
    /// </summary>
    public float EffectiveThreshold
    {
        get
        {
            var model = FaceModelCatalog.Find(EffectiveModelId);
            var id = model?.Id ?? FaceModelCatalog.SFaceId;
            return _settings.ThresholdForModel(id, (float)(model?.RecommendedThreshold ?? DefaultThreshold));
        }
    }

    /// <summary>当前模型自己的推荐阈值（供设置页显示与「恢复推荐值」）。</summary>
    public float RecommendedThreshold
        => (float)(FaceModelCatalog.Find(EffectiveModelId)?.RecommendedThreshold ?? DefaultThreshold);

    /// <summary>当前模型的显示名，供界面提示用。</summary>
    public string EffectiveModelName
        => FaceModelCatalog.Find(EffectiveModelId)?.DisplayName ?? EffectiveModelId;

    /// <summary>当前模型推荐阈值的出处说明。</summary>
    public string EffectiveThresholdNote
        => FaceModelCatalog.Find(EffectiveModelId)?.ThresholdNote ?? "";

    /// <summary>
    /// 人脸检测置信度阈值。教室场景人多脸小，取 0.6 换一点召回：
    /// 后排同学的脸本来置信度就偏低，阈值太高会直接漏掉；误检出来的假脸
    /// 会因为「最小人脸宽度」和「多张照片投票」被过滤掉。
    /// </summary>
    private const float DetectScoreThreshold = 0.6f;

    /// <summary>
    /// 默认忽略小于这个像素宽度的脸（单位：原图像素）。设置页的「最小人脸像素」可以覆盖它。
    /// </summary>
    public const int MinFaceWidth = 60;

    /// <summary>
    /// 检测器的工作分辨率。所有输入都会被**等比例**缩放到不超过这个尺寸再检测。
    /// <para>
    /// 用固定的工作尺寸而不是「跟着抓拍分辨率走」，有两个好处：
    /// 一是同一批照片的检测尺度一致，检测框大小可比、最小人脸宽度的语义稳定；
    /// 二是摄像头换成别的分辨率时不用重建检测器。
    /// </para>
    /// <para>
    /// 取 640×480 是因为 YuNet 在这个尺度上速度和精度的平衡最好，
    /// 而且对教室后排小脸的召回已经够用（小脸还会被最小人脸宽度过滤掉）。
    /// </para>
    /// </summary>
    private const int DetectorWorkWidth = 640;

    /// <summary>检测器的工作高度，见 <see cref="DetectorWorkWidth"/>。</summary>
    private const int DetectorWorkHeight = 480;

    /// <summary>SFace 对齐后的输入尺寸。</summary>
    private const int AlignSize = 112;

    /// <summary>
    /// SFace 对齐参考点，取自 OpenCV face_recognize.cpp。
    /// </summary>
    private static readonly double[,] AlignDst =
    {
        { 38.2946, 51.6963 },
        { 73.5318, 51.5014 },
        { 56.0252, 71.7366 },
        { 41.5493, 92.3655 },
        { 70.7299, 92.2041 },
    };

    private static readonly double[] AlignDstMean = { 56.0262, 71.9008 };

    private readonly FaceLateSettings _settings;
    private readonly FaceModelProvider _models;
    private readonly ILogger<FaceEngine> _logger;
    private readonly object _sync = new();

    private FaceDetectorYN? _detector;
    private Size _detectorInputSize;
    private Net? _recognizer;
    private string _detectorPath = "";
    private string _recognizerPath = "";

    /// <summary>
    /// 检测器缓存：键是输入尺寸，值是按该尺寸创建的检测器。
    /// <see cref="FaceDetectorYN"/> 一个实例只能处理一个固定尺寸，所以按尺寸缓存复用。
    /// 访问必须在 <see cref="_sync"/> 锁内。
    /// </summary>
    private readonly Dictionary<(int Width, int Height), FaceDetectorYN> _detectorPool = new();

    /// <summary>
    /// 当前载入的识别模型是不是 ArcFace 系（InsightFace / MobileFaceNet）。
    /// 两种模型的预处理不一样，必须跟着「实际载入的文件」走，而不是跟着设置走。
    /// </summary>
    private bool _recognizerIsArcFace;

    /// <summary>当前识别模型输出的特征维度（SFace 128，ArcFace MobileFaceNet 512）。</summary>
    private int _featureLength = 128;

    public FaceEngine(FaceLateSettings settings, FaceModelProvider models, ILogger<FaceEngine> logger)
    {
        _settings = settings;
        _models = models;
        _logger = logger;
    }

    /// <summary>模型是否已经加载完毕。</summary>
    public bool IsReady
    {
        get
        {
            lock (_sync)
            {
                return _recognizer != null
                       && !string.IsNullOrEmpty(_detectorPath)
                       && !string.IsNullOrEmpty(_recognizerPath);
            }
        }
    }

    /// <summary>当前使用的检测模型路径，用于在设置页展示。</summary>
    public string DetectorPath
    {
        get
        {
            lock (_sync)
            {
                return _detectorPath;
            }
        }
    }

    /// <summary>当前使用的识别模型路径，用于在设置页展示。</summary>
    public string RecognizerPath
    {
        get
        {
            lock (_sync)
            {
                return _recognizerPath;
            }
        }
    }

    /// <summary>
    /// 载入（或重新载入）模型。检测器会按当前设置的分辨率在首次使用时创建。
    /// </summary>
    public async Task<(bool Ok, string Message)> LoadAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var (detectorPath, recognizerPath, error) = await _models.EnsureAsync(progress, cancellationToken);
        if (!string.IsNullOrEmpty(error))
        {
            return (false, error);
        }

        try
        {
            // ReadNetFromOnnx 是**同步阻塞**的，37 MB 的 SFace 载入要好几秒。
            // 设置页的「载入模型」按钮是直接在 UI 线程上调这里的，
            // 不挪到后台线程就会让整个界面卡住（就是之前「点一下卡死」的原因之一）。
            await Task.Run(() =>
            {
                lock (_sync)
                {
                    if (_recognizer == null || _recognizerPath != recognizerPath)
                    {
                        _recognizer?.Dispose();
                        _recognizer = null;

                        var loaded = CvDnn.ReadNetFromOnnx(recognizerPath);
                        if (loaded == null || loaded.Empty())
                        {
                            loaded?.Dispose();
                            throw new InvalidDataException($"人脸识别模型无法解析，文件可能不完整：{recognizerPath}");
                        }

                        _recognizer = loaded;
                        _recognizerPath = recognizerPath;
                        // 预处理方式跟着**实际载入的文件**走，而不是跟着设置走 ——
                        // 设置里可能选了 A 但实际回退用了 B。认不出型号时按文件名兜底猜。
                        _recognizerIsArcFace = FaceModelCatalog.IsInsightFaceStyle(recognizerPath);
                        // 比对阈值同样跟着实际模型走（SFace 0.363 / ArcFace 系 0.4~0.6 是两回事）。
                        EffectiveModelId = FaceModelCatalog.MatchByPath(recognizerPath)?.Id
                                           ?? FaceModelCatalog.SFaceId;
                        _logger.LogInformation("FaceLate 识别模型已载入：{Path}（{Kind}）",
                            recognizerPath, _recognizerIsArcFace ? "InsightFace 系（512 维，预处理 /127.5）" : "OpenCV 系（128 维，预处理 scale=1）");
                    }

                    if (_detectorPath != detectorPath)
                    {
                        // 换了检测模型，按旧模型的尺寸缓存全部作废。
                        _detector?.Dispose();
                        _detector = null;

                        foreach (var stale in _detectorPool.Values)
                        {
                            stale?.Dispose();
                        }
                        _detectorPool.Clear();

                        _detectorPath = detectorPath;
                    }
                }
            }).ConfigureAwait(false);
        }
        catch (DllNotFoundException e)
        {
            _logger.LogError(e, "FaceLate 找不到 OpenCV 原生库");
            return (false, "找不到 OpenCV 原生库（OpenCvSharpExtern.dll）。请确认插件目录下存在该文件，或重新安装插件。");
        }
        catch (InvalidDataException e)
        {
            _logger.LogError(e, "FaceLate 识别模型文件损坏：{Path}", recognizerPath);
            return (false, e.Message);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "FaceLate 加载模型失败");
            return (false, $"加载人脸模型失败：{e.Message}");
        }

        // 把「这次到底用的是哪款模型、阈值取多少、为什么取这个数」写进日志，
        // 省得以后又出现「阈值不对」却查不出用的是哪套参数。
        _logger.LogInformation("FaceLate 比对阈值 {Threshold:F3}（模型 {Model}，推荐值 {Recommended:F3}）",
            EffectiveThreshold, EffectiveModelId, RecommendedThreshold);

        return (true, "模型已就绪");
    }

    /// <summary>
    /// 在图片中检测人脸，并（可选）提取特征向量。
    /// <para>
    /// <b>为什么必须自己缩放：</b><see cref="FaceDetectorYN.Detect"/> 有个很硬的约束 ——
    /// 输入图的尺寸必须**严格等于**创建检测器时给定的尺寸，否则直接抛
    /// <c>OpenCVException: Size does not match</c>。也就是说它<b>不会</b>帮你把任意
    /// 尺寸的图缩放到工作尺寸。
    /// </para>
    /// <para>
    /// 所以这里做两件事：先把原图**等比例**缩放到一个合适的工作尺寸（长宽比不变，
    /// 不拉伸、不补黑边），然后按这个尺寸取检测器去检测，最后把检测框和 5 个关键点
    /// 一起乘回比例、还原到原图坐标。
    /// </para>
    /// <para>
    /// 之前的问题是：检测器按「设置里的抓拍分辨率」创建，输入却是原图（比如
    /// 1440×1920 的竖版证件照、或者摄像头实际协商出来的 1280×720），尺寸对不上，
    /// 于是每次检测都抛异常，界面就报「未检测到人脸」—— 跟模型好坏、脸正不正都没关系。
    /// </para>
    /// </summary>
    /// <param name="bgr">待检测的 BGR 图像（不会被修改）。</param>
    /// <param name="withFeature">是否同时提取特征向量。</param>
    /// <param name="minFaceWidth">
    /// 最小人脸宽度，单位是**原图像素**。默认取 <see cref="MinFaceWidth"/>。
    /// </param>
    public List<DetectedFace> Analyze(Mat bgr, bool withFeature = true, int minFaceWidth = MinFaceWidth)
    {
        var result = new List<DetectedFace>();
        if (bgr.Empty())
        {
            return result;
        }

        lock (_sync)
        {
            if (string.IsNullOrEmpty(_detectorPath) || _recognizer == null)
            {
                return result;
            }

            // 等比例缩放后的工作尺寸（长边贴合上限，长宽比不变）。
            var target = ComputeDetectionSize(bgr.Width, bgr.Height);

            if (!EnsureDetectorLocked(target))
            {
                return result;
            }

            // scale = 原图 / 工作图。检测完把框和关键点乘回去就还原到原图坐标了。
            var scaleX = (double)bgr.Width / target.Width;
            var scaleY = (double)bgr.Height / target.Height;

            Mat? resized = null;
            Mat input = bgr;
            try
            {
                if (target.Width != bgr.Width || target.Height != bgr.Height)
                {
                    resized = new Mat();
                    // 等比缩放，不改变长宽比 —— 脸不会被压扁。
                    // 缩小用 Area 质量最好，放大用 Linear 更平滑，这里按比例选。
                    var interp = target.Width < bgr.Width
                        ? InterpolationFlags.Area
                        : InterpolationFlags.Linear;
                    Cv2.Resize(bgr, resized, target, 0, 0, interp);
                    input = resized;
                }

                using var faces = new Mat();
                _detector!.Detect(input, faces);

                if (faces.Empty() || faces.Rows <= 0)
                {
                    return result;
                }

                for (var i = 0; i < faces.Rows; i++)
                {
                    var face = new DetectedFace();
                    for (var j = 0; j < 15; j++)
                    {
                        face.Box[j] = faces.At<float>(i, j);
                    }

                    if (face.Score < DetectScoreThreshold)
                    {
                        continue;
                    }

                    // 把检测框和 5 个关键点一起还原到原图坐标。
                    // 关键点不能漏，否则 ExtractFeature 的对齐会整体错位，
                    // 提出来的特征跟别人对不上（相似度乱飘）。
                    RescaleToOriginal(face, scaleX, scaleY);

                    // 最小人脸宽度按原图像素算，用户看到的「最小人脸像素」
                    // 跟设置页里的数字才是一致的。
                    if (face.Width < minFaceWidth)
                    {
                        continue;
                    }

                    if (withFeature)
                    {
                        face.Feature = ExtractFeature(bgr, face);
                        if (face.Feature == null)
                        {
                            // 单张脸对齐/推理失败就跳过它，不影响同张照片里的其它人脸。
                            continue;
                        }
                    }

                    result.Add(face);
                }
            }
            finally
            {
                resized?.Dispose();
            }

            return result;
        }
    }

    /// <summary>
    /// 计算等比例缩放后的检测工作尺寸（保持长宽比，不补黑边）。
    /// <para>
    /// 上限取 640×480：YuNet 在这个尺度上速度与精度平衡最好，教室后排的小脸
    /// 到这个尺度也还检得出来（更小的脸本来就会被「最小人脸宽度」过滤）。
    /// 比上限还小的图直接原样使用，不做无意义的放大。
    /// </para>
    /// </summary>
    private static Size ComputeDetectionSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return new Size(DetectorWorkWidth, DetectorWorkHeight);
        }

        var ratio = Math.Min((double)DetectorWorkWidth / width, (double)DetectorWorkHeight / height);

        // 图片本身就比工作尺寸小，原样使用（放大只会更慢更糊，还可能引入伪影）。
        if (ratio >= 1.0)
        {
            return new Size(width, height);
        }

        // 至少留 1 像素，避免极端长宽比算出 0。
        var w = Math.Max(1, (int)Math.Round(width * ratio));
        var h = Math.Max(1, (int)Math.Round(height * ratio));
        return new Size(w, h);
    }

    /// <summary>
    /// 把检测结果（框 + 5 个关键点）从检测坐标还原到原图坐标。
    /// </summary>
    private static void RescaleToOriginal(DetectedFace face, double scaleX, double scaleY)
    {
        face.Box[0] = (float)(face.Box[0] * scaleX);
        face.Box[1] = (float)(face.Box[1] * scaleY);
        face.Box[2] = (float)(face.Box[2] * scaleX);
        face.Box[3] = (float)(face.Box[3] * scaleY);

        // 第 4~13 列：5 组关键点的 (x, y)，x 乘 scaleX、y 乘 scaleY。
        for (var k = 0; k < 5; k++)
        {
            face.Box[4 + k * 2] = (float)(face.Box[4 + k * 2] * scaleX);
            face.Box[5 + k * 2] = (float)(face.Box[5 + k * 2] * scaleY);
        }
        // 第 14 列是置信度，不参与缩放。
    }

    /// <summary>
    /// 取出（必要时创建）指定输入尺寸的检测器。
    /// <para>
    /// <see cref="FaceDetectorYN"/> 的一个实例只能处理**一个固定尺寸**的输入，尺寸不符
    /// 就直接抛异常。不同来源的图尺寸不一样（竖版证件照 960×1280、摄像头 1280×720、
    /// 合影 640×360……），所以这里按尺寸做一层缓存：同一个尺寸复用同一个实例，
    /// 换了尺寸尽量从缓存里换，缓存里没有才新建。
    /// </para>
    /// </summary>
    private bool EnsureDetectorLocked(Size size)
    {
        if (string.IsNullOrEmpty(_detectorPath))
        {
            return false;
        }

        if (size.Width <= 0 || size.Height <= 0)
        {
            return false;
        }

        if (_detector != null && _detectorInputSize == size)
        {
            return true;
        }

        // 缓存里正好有这个尺寸的检测器，直接换上去用。
        if (_detectorPool.TryGetValue((size.Width, size.Height), out var cached)
            && cached != null)
        {
            _detector = cached;
            _detectorInputSize = size;
            return true;
        }

        try
        {
            var created = FaceDetectorYN.Create(_detectorPath, "", size,
                DetectScoreThreshold, 0.3f, 5000);

            // 换新的之前把当前这个按原尺寸收回缓存，下次遇到就能直接复用。
            if (_detector != null && _detectorInputSize.Width > 0)
            {
                _detectorPool[(_detectorInputSize.Width, _detectorInputSize.Height)] = _detector;
            }
            else
            {
                _detector?.Dispose();
            }

            _detector = created;
            _detectorInputSize = size;

            // 缓存别无限涨：常见的也就两三种尺寸，超过 6 个就清掉最早的那些。
            if (_detectorPool.Count > 6)
            {
                foreach (var key in _detectorPool.Keys.ToList())
                {
                    if (key == (_detectorInputSize.Width, _detectorInputSize.Height))
                    {
                        continue;
                    }

                    _detectorPool[key]?.Dispose();
                    _detectorPool.Remove(key);
                }
            }

            return true;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "FaceLate 创建人脸检测器失败（{Size}）", size);
            return false;
        }
    }

    /// <summary>
    /// 把人脸摆正到 112×112 并提取特征向量。流程与 OpenCV <c>FaceRecognizerSF</c> 一致。
    /// </summary>
    /// <param name="bgr">原图。</param>
    /// <param name="face">检测结果。<b>关键点必须已经是原图坐标</b>（<see cref="RescaleToOriginal"/> 还原过）。</param>
    private float[]? ExtractFeature(Mat bgr, DetectedFace face)
    {
        try
        {
            // YuNet 输出第 4~13 列是 5 个关键点（右眼、左眼、鼻尖、右嘴角、左嘴角）
            var landmarks = new double[10];
            for (var k = 0; k < 10; k++)
            {
                landmarks[k] = face.Box[4 + k];
            }

            using var transform = GetSimilarityTransformMatrix(landmarks);
            using var aligned = new Mat();
            Cv2.WarpAffine(bgr, aligned, transform, new Size(AlignSize, AlignSize),
                InterpolationFlags.Linear, BorderTypes.Constant, new Scalar(0, 0, 0));
            if (aligned.Empty())
            {
                return null;
            }

            // 两个模型的预处理不一样，必须跟「实际载入的那个文件」走：
            //  · SFace（OpenCV Zoo）：不做 1/255 缩放、不减均值，只做 RGB 通道交换，与 OpenCV 官方实现一致；
            //  · ArcFace（InsightFace / MobileFaceNet）：(像素 - 127.5) / 127.5，同样是 RGB。
            // 预处理对不上的话，相似度分布会整体跑偏，设置的阈值就完全失效了。
            using var blob = _recognizerIsArcFace
                ? CvDnn.BlobFromImage(aligned, 1.0 / 127.5, new Size(AlignSize, AlignSize),
                    new Scalar(127.5, 127.5, 127.5), true, false)
                : CvDnn.BlobFromImage(aligned, 1.0, new Size(AlignSize, AlignSize),
                    new Scalar(0, 0, 0), true, false);

            _recognizer!.SetInput(blob, "");
            using var output = _recognizer.Forward("");
            if (output.Empty() || output.Total() < 64)
            {
                return null;
            }

            Mat? reshaped = null;
            var feature = output;
            if (output.Rows != 1)
            {
                reshaped = output.Reshape(1, 1);
                feature = reshaped;
            }

            try
            {
                // 特征维度由模型决定：SFace 是 128，ArcFace MobileFaceNet 是 512，所以不能写死。
                var length = feature.Cols;
                var vector = new float[length];
                for (var j = 0; j < length; j++)
                {
                    vector[j] = feature.At<float>(0, j);
                }

                _featureLength = length;
                return vector;
            }
            finally
            {
                reshaped?.Dispose();
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "FaceLate 提取人脸特征失败，已跳过该脸");
            return null;
        }
    }

    /// <summary>
    /// 计算 5 点关键点到标准正脸的相似变换矩阵。
    /// <para>
    /// 移植自 OpenCV <c>face_recognize.cpp</c> 的 <c>getSimilarityTransformMatrix</c>（Umeyama 算法）。
    /// 参考点用的是 38.29/51.69 这一套 —— SFace 与 ArcFace 用的是同一套 5 点模板，
    /// 所以两种模型可以共用这段对齐代码。
    /// </para>
    /// </summary>
    private static Mat GetSimilarityTransformMatrix(double[] src)
    {
        double avgX = 0, avgY = 0;
        for (var i = 0; i < 5; i++)
        {
            avgX += src[i * 2];
            avgY += src[i * 2 + 1];
        }

        double[] srcMean = { avgX / 5.0, avgY / 5.0 };

        var srcDemean = new double[5, 2];
        var dstDemean = new double[5, 2];
        for (var j = 0; j < 5; j++)
        {
            srcDemean[j, 0] = src[j * 2] - srcMean[0];
            srcDemean[j, 1] = src[j * 2 + 1] - srcMean[1];
            dstDemean[j, 0] = AlignDst[j, 0] - AlignDstMean[0];
            dstDemean[j, 1] = AlignDst[j, 1] - AlignDstMean[1];
        }

        double a00 = 0, a01 = 0, a10 = 0, a11 = 0;
        for (var i = 0; i < 5; i++)
        {
            a00 += dstDemean[i, 0] * srcDemean[i, 0];
            a01 += dstDemean[i, 0] * srcDemean[i, 1];
            a10 += dstDemean[i, 1] * srcDemean[i, 0];
            a11 += dstDemean[i, 1] * srcDemean[i, 1];
        }

        a00 /= 5;
        a01 /= 5;
        a10 /= 5;
        a11 /= 5;

        var d = new[] { 1.0, 1.0 };
        if (a00 * a11 - a01 * a10 < 0)
        {
            d[1] = -1;
        }

        using var a = new Mat(2, 2, MatType.CV_64FC1);
        a.Set(0, 0, a00);
        a.Set(0, 1, a01);
        a.Set(1, 0, a10);
        a.Set(1, 1, a11);

        using var s = new Mat();
        using var u = new Mat();
        using var vt = new Mat();
        Cv2.SVDecomp(a, s, u, vt, SVD.Flags.None);

        var s0 = s.At<double>(0, 0);
        var s1 = s.At<double>(1, 0);

        var u00 = u.At<double>(0, 0);
        var u01 = u.At<double>(0, 1);
        var u10 = u.At<double>(1, 0);
        var u11 = u.At<double>(1, 1);
        var v00 = vt.At<double>(0, 0);
        var v01 = vt.At<double>(0, 1);
        var v10 = vt.At<double>(1, 0);
        var v11 = vt.At<double>(1, 1);

        var detU = u00 * u11 - u01 * u10;
        var detVt = v00 * v11 - v01 * v10;

        var smax = Math.Max(s0, s1);
        var tol = smax * 2 * float.MinValue;
        var rank = (s0 > tol ? 1 : 0) + (s1 > tol ? 1 : 0);

        double t00, t01, t10, t11;
        if (rank == 1 && detU * detVt > 0)
        {
            // U * Vt
            t00 = u00 * v00 + u01 * v10;
            t01 = u00 * v01 + u01 * v11;
            t10 = u10 * v00 + u11 * v10;
            t11 = u10 * v01 + u11 * v11;
        }
        else if (rank == 1)
        {
            // U * diag(d0, -1) * Vt
            t00 = u00 * d[0] * v00 - u01 * v10;
            t01 = u00 * d[0] * v01 - u01 * v11;
            t10 = u10 * d[0] * v00 - u11 * v10;
            t11 = u10 * d[0] * v01 - u11 * v11;
        }
        else
        {
            // U * diag(d0, d1) * Vt
            t00 = u00 * d[0] * v00 + u01 * d[1] * v10;
            t01 = u00 * d[0] * v01 + u01 * d[1] * v11;
            t10 = u10 * d[0] * v00 + u11 * d[1] * v10;
            t11 = u10 * d[0] * v01 + u11 * d[1] * v11;
        }

        double var1 = 0, var2 = 0;
        for (var i = 0; i < 5; i++)
        {
            var1 += srcDemean[i, 0] * srcDemean[i, 0];
            var2 += srcDemean[i, 1] * srcDemean[i, 1];
        }

        var1 /= 5;
        var2 /= 5;

        var denom = var1 + var2;
        var scale = denom <= double.Epsilon ? 1.0 : (s0 * d[0] + s1 * d[1]) / denom;

        var ts0 = t00 * srcMean[0] + t01 * srcMean[1];
        var ts1 = t10 * srcMean[0] + t11 * srcMean[1];

        var result = new Mat(2, 3, MatType.CV_64FC1);
        result.Set(0, 0, t00 * scale);
        result.Set(0, 1, t01 * scale);
        result.Set(0, 2, AlignDstMean[0] - scale * ts0);
        result.Set(1, 0, t10 * scale);
        result.Set(1, 1, t11 * scale);
        result.Set(1, 2, AlignDstMean[1] - scale * ts1);
        return result;
    }

    /// <summary>
    /// 余弦相似度。与 OpenCV <c>FaceRecognizerSF::match(FR_COSINE)</c> 的算法一致（先 L2 归一化再点积）。
    /// </summary>
    public static double Similarity(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length)
        {
            return 0d;
        }

        double dot = 0d, normA = 0d, normB = 0d;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA <= double.Epsilon || normB <= double.Epsilon)
        {
            return 0d;
        }

        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    /// <summary>
    /// 在人脸库中查找与查询特征最相似的学生。
    /// </summary>
    public static (Student? Student, double Score) FindBest(
        float[] query,
        IReadOnlyList<GalleryEntry> gallery,
        double threshold)
    {
        Student? best = null;
        var bestScore = double.MinValue;

        foreach (var entry in gallery)
        {
            var score = Similarity(query, entry.Feature);
            if (score > bestScore)
            {
                bestScore = score;
                best = entry.Student;
            }
        }

        if (best == null || bestScore < threshold)
        {
            return (null, bestScore == double.MinValue ? 0d : bestScore);
        }

        return (best, bestScore);
    }

    /// <summary>
    /// 把 BGR 图像编码为 JPEG 字节，用于界面预览或落盘。
    /// </summary>
    public static byte[]? EncodeJpeg(Mat bgr, int quality = 90)
    {
        try
        {
            Cv2.ImEncode(".jpg", bgr, out var buffer,
                new[] { new ImageEncodingParam(ImwriteFlags.JpegQuality, quality) });
            return buffer;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 裁出人脸附近的一小块并编码成 JPEG，用作「是不是这位同学？」确认框里的缩略图。
    /// </summary>
    public static byte[]? EncodeFaceThumbnail(Mat bgr, DetectedFace face, int maxSize = 220)
    {
        try
        {
            var pad = (int)(face.Width * 0.5);
            var x = Math.Max(0, face.X - pad);
            var y = Math.Max(0, face.Y - pad);
            var w = Math.Min(bgr.Width - x, face.Width + pad * 2);
            var h = Math.Min(bgr.Height - y, face.Height + pad * 2);
            if (w <= 0 || h <= 0)
            {
                return null;
            }

            using var crop = new Mat(bgr, new Rect(x, y, w, h));
            var scale = Math.Min(1.0, (double)maxSize / Math.Max(w, h));
            if (scale >= 1.0)
            {
                return EncodeJpeg(crop, 85);
            }

            using var resized = new Mat();
            Cv2.Resize(crop, resized, new Size((int)(w * scale), (int)(h * scale)), 0, 0, InterpolationFlags.Area);
            return EncodeJpeg(resized, 85);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把整张图缩到指定大小，并在每张脸上画编号方框，用于让用户「框选一张脸」。
    /// 编号从 1 开始，与 <c>faces</c> 列表的下标一一对应（下标 0 → 编号 1）。
    /// </summary>
    /// <param name="bgr">原图。</param>
    /// <param name="faces">要标注的人脸，顺序即编号顺序。</param>
    /// <param name="maxSize">输出图的长边上限。</param>
    /// <param name="highlightIndex">要特别高亮的那张脸（-1 表示不高亮）。</param>
    public static byte[]? EncodeAnnotatedPreview(
        Mat bgr,
        IReadOnlyList<DetectedFace> faces,
        int maxSize = 480,
        int highlightIndex = -1)
    {
        if (bgr.Empty())
        {
            return null;
        }

        try
        {
            var scale = Math.Min(1.0, (double)maxSize / Math.Max(bgr.Width, bgr.Height));
            var tw = Math.Max(1, (int)(bgr.Width * scale));
            var th = Math.Max(1, (int)(bgr.Height * scale));

            using var canvas = new Mat();
            Cv2.Resize(bgr, canvas, new Size(tw, th), 0, 0,
                scale < 1.0 ? InterpolationFlags.Area : InterpolationFlags.Linear);

            for (var i = 0; i < faces.Count; i++)
            {
                var f = faces[i];
                var x1 = (int)(f.X * scale);
                var y1 = (int)(f.Y * scale);
                var x2 = (int)((f.X + f.Width) * scale);
                var y2 = (int)((f.Y + f.Height) * scale);

                // 全部裁剪到画布范围，避免画到外面去。
                x1 = Math.Clamp(x1, 0, tw - 1);
                y1 = Math.Clamp(y1, 0, th - 1);
                x2 = Math.Clamp(x2, 0, tw - 1);
                y2 = Math.Clamp(y2, 0, th - 1);
                if (x2 <= x1 || y2 <= y1)
                {
                    continue;
                }

                var selected = i == highlightIndex;
                // BGR：选中是亮橙色 (0,140,255)，未选中是青绿色 (200,230,70)。
                var color = selected
                    ? new Scalar(0, 140, 255)
                    : new Scalar(200, 230, 70);
                var thickness = selected ? 3 : 2;

                Cv2.Rectangle(canvas, new Rect(x1, y1, x2 - x1, y2 - y1), color, thickness);

                // 编号：画在框的左上角，带一层深色底衬保证在亮背景上也看得清。
                var label = (i + 1).ToString();
                var org = new Point(x1, Math.Max(14, y1 - 6));
                Cv2.PutText(canvas, label, org, HersheyFonts.HersheyDuplex, 0.6, new Scalar(20, 20, 20), 3);
                Cv2.PutText(canvas, label, org, HersheyFonts.HersheyDuplex, 0.6, color, 1);
            }

            return EncodeJpeg(canvas, 85);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从文件读取图片（自动处理中文路径）。
    /// </summary>
    public static Mat? ReadImageFile(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var mat = Cv2.ImDecode(bytes, ImreadModes.Color);
            return mat.Empty() ? null : mat;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 保存一张人脸小图用于人工复核。
    /// </summary>
    public static string? SaveFaceCrop(Mat bgr, DetectedFace face, string directory, string fileName)
    {
        try
        {
            var pad = (int)(face.Width * 0.25);
            var x = Math.Max(0, face.X - pad);
            var y = Math.Max(0, face.Y - pad);
            var w = Math.Min(bgr.Width - x, face.Width + pad * 2);
            var h = Math.Min(bgr.Height - y, face.Height + pad * 2);
            if (w <= 0 || h <= 0)
            {
                return null;
            }

            FaceLatePaths.EnsureDirectory(directory);
            var path = Path.Combine(directory, fileName);
            using var crop = new Mat(bgr, new Rect(x, y, w, h));
            var bytes = EncodeJpeg(crop, 92);
            if (bytes == null)
            {
                return null;
            }

            // 用 WriteAllBytes 而不是 Cv2.ImWrite，避免中文/空格路径在 Windows 上写不出去。
            File.WriteAllBytes(path, bytes);
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            _detector?.Dispose();
            _detector = null;

            foreach (var detector in _detectorPool.Values)
            {
                detector?.Dispose();
            }
            _detectorPool.Clear();

            _recognizer?.Dispose();
            _recognizer = null;
            _detectorPath = "";
            _recognizerPath = "";
        }
    }
}
