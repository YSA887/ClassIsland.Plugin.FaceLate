using ClassIsland.Plugin.FaceLate.Models;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace ClassIsland.Plugin.FaceLate.Services;

/// <summary>
/// 一次导入里单张照片的处理结果。
/// </summary>
public sealed class ImportItemResult
{
    /// <summary>文件名。</summary>
    public string FileName { get; init; } = "";

    /// <summary>归属的同学；没有则为 null。</summary>
    public Student? Student { get; init; }

    /// <summary>实际新增的样本条数。</summary>
    public int Added { get; init; }

    /// <summary>是否处理成功。</summary>
    public bool Ok { get; init; }

    /// <summary>说明。</summary>
    public string Message { get; init; } = "";
}

/// <summary>
/// 一次导入的汇总。
/// </summary>
public sealed class ImportSummary
{
    /// <summary>逐张照片的结果。</summary>
    public List<ImportItemResult> Items { get; } = new();

    /// <summary>因为前置条件不满足而整批失败时的原因。</summary>
    public string FatalError { get; set; } = "";

    /// <summary>是否被用户中止。</summary>
    public bool Cancelled { get; set; }

    /// <summary>成功导入的照片数。</summary>
    public int OkCount => Items.Count(x => x.Ok);

    /// <summary>新增样本总条数。</summary>
    public int AddedCount => Items.Sum(x => x.Added);

    /// <summary>没导入成功的照片数。</summary>
    public int SkippedCount => Items.Count(x => !x.Ok);
}

/// <summary>
/// 智能导入时给用户看的一条候选匹配。
/// </summary>
public sealed class SmartCandidate
{
    /// <summary>候选同学。</summary>
    public Student Student { get; init; } = null!;

    /// <summary>相似度。</summary>
    public double Score { get; init; }
}

/// <summary>
/// 智能导入时给用户看的一张候选脸（照片里检测到的某一张脸）。
/// <para>
/// 一张照片里可能有好几个人（比如合照），以前插件只会默默取最大的一张，
/// 处理错了用户也没法纠正。现在把每张脸都列出来给用户挑。
/// </para>
/// </summary>
public sealed class SmartFaceOption
{
    /// <summary>这张脸在照片里的序号（从 1 开始），与标注预览图上的编号一致。</summary>
    public int Index { get; init; }

    /// <summary>这张脸的缩略图（JPEG）。</summary>
    public byte[]? Thumbnail { get; init; }

    /// <summary>人脸框宽度（原图像素）。</summary>
    public int Width { get; init; }

    /// <summary>人脸框高度（原图像素）。</summary>
    public int Height { get; init; }

    /// <summary>检测置信度。</summary>
    public float Score { get; init; }

    /// <summary>这张脸最像的同学（可能为 null）。</summary>
    public Student? Best { get; init; }

    /// <summary>与最像的同学的相似度。</summary>
    public double BestScore { get; init; }

    /// <summary>界面上显示的一行文字。</summary>
    public string DisplayText => Best == null
        ? $"脸 {Index}　{Width}×{Height}px　没认出像谁"
        : $"脸 {Index}　{Width}×{Height}px　最像「{Best.Name}」({BestScore:P0})";
}

/// <summary>
/// 智能导入时抛给界面的提问：「这张照片是不是 XXX？」
/// </summary>
public sealed class SmartPrompt
{
    /// <summary>文件名。</summary>
    public string FileName { get; init; } = "";

    /// <summary>第几张 / 共几张。</summary>
    public int Index { get; init; }

    /// <summary>总数。</summary>
    public int Total { get; init; }

    /// <summary>人脸缩略图（JPEG），可能为 null。对应当前选中的那张脸。</summary>
    public byte[]? Thumbnail { get; init; }

    /// <summary>这张照片里检测到几张脸。</summary>
    public int FaceCount { get; init; }

    /// <summary>
    /// 这张照片里**每一张**脸的可选项，按面积从大到小排。
    /// <see cref="FaceCount"/> 大于 1 时界面应该把这组选项显示出来让用户挑。
    /// </summary>
    public List<SmartFaceOption> Faces { get; init; } = new();

    /// <summary>
    /// 整张照片带编号方框的预览图，方便用户把「脸 2」对应到画面上具体哪个人。
    /// </summary>
    public byte[]? AnnotatedPreview { get; init; }

    /// <summary>按相似度排序的候选（只包含超过阈值的）。</summary>
    public List<SmartCandidate> Matches { get; init; } = new();

    /// <summary>照脸识别给出的最佳建议。</summary>
    public Student? Best { get; init; }

    /// <summary>最佳相似度。</summary>
    public double BestScore { get; init; }

    /// <summary>文件名本身能对上的同学（如果有）。</summary>
    public Student? FileNameMatch { get; init; }

    /// <summary>文件名对上了多个同学。</summary>
    public bool FileNameAmbiguous { get; init; }

    /// <summary>给用户看的一句话说明。</summary>
    public string Note { get; init; } = "";

    /// <summary>默认应该选中的同学。</summary>
    public Student? Suggested => Best ?? FileNameMatch;

    /// <summary>照片里不止一张脸，界面需要让用户挑一张。</summary>
    public bool NeedsFaceChoice => Faces.Count > 1;
}

/// <summary>
/// 用户对一次「是不是 XXX？」的回答。
/// </summary>
public sealed class SmartDecision
{
    /// <summary>中止整批导入。</summary>
    public bool Cancel { get; init; }

    /// <summary>跳过这一张。</summary>
    public bool Skip { get; init; }

    /// <summary>导入给这位同学。</summary>
    public Student? Target { get; init; }

    /// <summary>用这个名字新建一位同学再导入。</summary>
    public string? NewName { get; init; }

    /// <summary>
    /// 用户挑中的是第几张脸（<see cref="SmartFaceOption.Index"/>，从 1 开始）。
    /// 0 表示用默认的那张（最大的）。
    /// </summary>
    public int FaceIndex { get; init; }

    /// <summary>跳过。</summary>
    public static SmartDecision DoSkip() => new() { Skip = true };

    /// <summary>中止。</summary>
    public static SmartDecision DoCancel() => new() { Cancel = true };

    /// <summary>导入给指定同学。</summary>
    public static SmartDecision For(Student student, int faceIndex = 0) =>
        new() { Target = student, FaceIndex = faceIndex };

    /// <summary>新建同学后再导入。</summary>
    public static SmartDecision CreateAs(string name, int faceIndex = 0) =>
        new() { NewName = name, FaceIndex = faceIndex };
}

/// <summary>
/// 人脸批量录入。
/// <para>
/// 提供两种导入方式，解决「一个个点太慢」的问题：
/// <list type="number">
/// <item><b>按文件名批量导入</b>：文件名叫「806班A.jpg」就自动导给花名册里的 A，不用手点；</item>
/// <item><b>智能导入</b>：文件名叫什么都不用管，插件先认出这张脸最像谁，
/// 然后弹一个「是不是 XXX？」的确认框，点一下「是」就导进去。</item>
/// </list>
/// </para>
/// </summary>
public sealed class EnrollmentService
{
    /// <summary>一次导入允许的最大照片数，防止误选整个硬盘目录。</summary>
    public const int MaxFilesPerImport = 2000;

    private readonly RosterService _roster;
    private readonly FaceEngine _engine;
    private readonly ActivityLog _log;
    private readonly ILogger<EnrollmentService> _logger;

    public EnrollmentService(
        RosterService roster,
        FaceEngine engine,
        ActivityLog log,
        ILogger<EnrollmentService> logger)
    {
        _roster = roster;
        _engine = engine;
        _log = log;
        _logger = logger;
    }

    /// <summary>
    /// 按文件名批量导入：文件名里能对上花名册姓名（例如「806班张三」）的照片直接自动导入。
    /// </summary>
    /// <param name="paths">照片路径。</param>
    /// <param name="progress">进度回调（已完成数, 总数, 当前文件名）。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<ImportSummary> ImportByFileNameAsync(
        IReadOnlyList<string> paths,
        IProgress<(int Done, int Total, string FileName)>? progress = null,
        CancellationToken ct = default)
    {
        var summary = new ImportSummary();
        if (!await EnsureEngineAsync(summary).ConfigureAwait(false))
        {
            return summary;
        }

        var files = paths.Take(MaxFilesPerImport).ToList();
        _log.Info($"开始按文件名批量导入，共 {files.Count} 张照片。");

        var saveDir = FaceLatePaths.EnsureDirectory(FaceLatePaths.FacesFolder);
        var done = 0;

        try
        {
            foreach (var path in files)
            {
                ct.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(path);
                progress?.Report((done, files.Count, fileName));

                var match = _roster.MatchByFileName(fileName);
                if (match.Student == null)
                {
                    var reason = match.IsAmbiguous
                        ? "文件名同时能对上多位同学，请改用「智能导入」逐张确认"
                        : "文件名里没有花名册中的人名";
                    summary.Items.Add(new ImportItemResult { FileName = fileName, Ok = false, Message = reason });
                    _log.Detail($"跳过「{fileName}」：{reason}");
                }
                else
                {
                    var item = await ImportOneAsync(match.Student, path, "batch", saveDir, ct).ConfigureAwait(false);
                    summary.Items.Add(item);
                }

                done++;
            }
        }
        catch (OperationCanceledException)
        {
            summary.Cancelled = true;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "FaceLate 批量导入出错");
            _log.Error("批量导入出错：" + e.Message);
        }
        finally
        {
            // 整批结束后统一落盘一次，避免每张照片都重写整份花名册。
            _roster.RaiseChanged();
            progress?.Report((done, files.Count, ""));
        }

        _log.Success($"批量导入结束：{summary.OkCount} 张成功（新增 {summary.AddedCount} 条样本），{summary.SkippedCount} 张跳过。");
        return summary;
    }

    /// <summary>
    /// 智能导入：每张照片先算出最像谁，再通过 <paramref name="ask"/> 问用户
    /// 「是不是 XXX」，确认后才导入。
    /// </summary>
    /// <param name="paths">照片路径。</param>
    /// <param name="ask">提问回调。实现里通常会把界面切到 UI 线程并等用户点按钮。</param>
    /// <param name="progress">进度回调。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<ImportSummary> SmartImportAsync(
        IReadOnlyList<string> paths,
        Func<SmartPrompt, Task<SmartDecision>> ask,
        IProgress<(int Done, int Total, string FileName)>? progress = null,
        CancellationToken ct = default)
    {
        var summary = new ImportSummary();
        if (!await EnsureEngineAsync(summary).ConfigureAwait(false))
        {
            return summary;
        }

        var files = paths.Take(MaxFilesPerImport).ToList();
        _log.Info($"开始智能导入，共 {files.Count} 张照片。");

        var saveDir = FaceLatePaths.EnsureDirectory(FaceLatePaths.FacesFolder);
        // 阈值跟着当前实际载入的模型走（SFace 0.363 / ArcFace 系 0.4~0.6 是两回事）。
        var threshold = Math.Clamp(_engine.EffectiveThreshold, 0.05f, 0.99f);
        var done = 0;

        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var path = files[i];
                var fileName = Path.GetFileName(path);
                progress?.Report((done, files.Count, fileName));

                using var probe = await Task.Run(() => Probe(path), ct).ConfigureAwait(false);
                if (probe == null || probe.Faces.Count == 0)
                {
                    var reason = probe?.Error is { Length: > 0 } err ? err : "没有检测到足够大的人脸";
                    summary.Items.Add(new ImportItemResult { FileName = fileName, Ok = false, Message = reason });
                    _log.Detail($"跳过「{fileName}」：{reason}");
                    done++;
                    continue;
                }

                var gallery = _roster.BuildGallery();

                // 先按默认目标（最大的那张脸）算一次候选，作为提问框的默认建议。
                var target = probe.Faces[0];

                // 每张脸各自算一遍「最像谁」，这样界面上用户换一张脸时建议能跟着变。
                var faceOptions = new List<SmartFaceOption>();
                for (var fi = 0; fi < probe.Faces.Count; fi++)
                {
                    var face = probe.Faces[fi];
                    Student? faceBest = null;
                    var faceBestScore = 0d;
                    if (face.Feature != null)
                    {
                        var (st, sc) = FaceEngine.FindBest(face.Feature, gallery, threshold);
                        faceBest = st;
                        faceBestScore = sc;
                    }

                    faceOptions.Add(new SmartFaceOption
                    {
                        Index = fi + 1,
                        Thumbnail = probe.Image != null
                            ? FaceEngine.EncodeFaceThumbnail(probe.Image, face)
                            : null,
                        Width = face.Width,
                        Height = face.Height,
                        Score = face.Score,
                        Best = faceBest,
                        BestScore = faceBestScore,
                    });
                }

                // 全局候选列表（整张照片里所有脸认出来的同学，去重后按相似度排）。
                var candidates = faceOptions
                    .Where(x => x.Best != null)
                    .Select(x => new SmartCandidate { Student = x.Best!, Score = x.BestScore })
                    .GroupBy(x => x.Student.Id)
                    .Select(g => g.OrderByDescending(x => x.Score).First())
                    .OrderByDescending(x => x.Score)
                    .ToList();

                var nameMatch = _roster.MatchByFileName(fileName);

                var prompt = new SmartPrompt
                {
                    FileName = fileName,
                    Index = i + 1,
                    Total = files.Count,
                    Thumbnail = probe.Image != null
                        ? FaceEngine.EncodeFaceThumbnail(probe.Image, target)
                        : null,
                    FaceCount = probe.Faces.Count,
                    Faces = faceOptions,
                    AnnotatedPreview = probe.Image != null && probe.Faces.Count > 1
                        ? FaceEngine.EncodeAnnotatedPreview(probe.Image, probe.Faces)
                        : null,
                    Matches = candidates,
                    Best = candidates.FirstOrDefault()?.Student,
                    BestScore = candidates.FirstOrDefault()?.Score ?? 0d,
                    FileNameMatch = nameMatch.Student,
                    FileNameAmbiguous = nameMatch.IsAmbiguous,
                    Note = BuildPromptNote(probe.Faces.Count, candidates.Count, nameMatch),
                };

                var decision = await ask(prompt).ConfigureAwait(false);
                if (decision.Cancel)
                {
                    summary.Cancelled = true;
                    break;
                }

                if (decision.Skip)
                {
                    summary.Items.Add(new ImportItemResult { FileName = fileName, Ok = false, Message = "用户跳过" });
                    done++;
                    continue;
                }

                // 用户可能挑的不是默认那张脸，按 FaceIndex 换过去（1-based）。
                if (decision.FaceIndex > 0 && decision.FaceIndex <= probe.Faces.Count)
                {
                    target = probe.Faces[decision.FaceIndex - 1];
                }

                var owner = decision.Target;
                if (owner == null && !string.IsNullOrWhiteSpace(decision.NewName))
                {
                    owner = _roster.AddStudent(decision.NewName!, _roster.Document.ClassName);
                    _log.Info($"已新建同学「{decision.NewName}」。");
                }

                if (owner == null)
                {
                    summary.Items.Add(new ImportItemResult { FileName = fileName, Ok = false, Message = "没有指定归属同学" });
                    done++;
                    continue;
                }

                if (target.Feature == null)
                {
                    summary.Items.Add(new ImportItemResult { FileName = fileName, Student = owner, Ok = false, Message = "特征提取失败" });
                    done++;
                    continue;
                }

                // 裁图 + 入库存到后台线程，避免卡住界面。
                var added = await Task.Run(() =>
                {
                    var crop = FaceEngine.SaveFaceCrop(probe.Image!, target, Path.Combine(saveDir, owner.Id),
                        $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.jpg");
                    return _roster.AddFaceSample(owner, target.Feature!, "smart", crop, save: false) ? 1 : 0;
                }, ct).ConfigureAwait(false);

                summary.Items.Add(new ImportItemResult
                {
                    FileName = fileName,
                    Student = owner,
                    Added = added,
                    Ok = added > 0,
                    Message = added > 0 ? $"导入给「{owner.Name}」" : "未能写入样本",
                });

                done++;
            }
        }
        catch (OperationCanceledException)
        {
            summary.Cancelled = true;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "FaceLate 智能导入出错");
            _log.Error("智能导入出错：" + e.Message);
        }
        finally
        {
            _roster.RaiseChanged();
            progress?.Report((done, files.Count, ""));
        }

        _log.Success($"智能导入结束：{summary.OkCount} 张成功（新增 {summary.AddedCount} 条样本），{summary.SkippedCount} 张跳过。");
        return summary;
    }

    // —— 内部实现 ——

    private static string BuildPromptNote(int faceCount, int candidateCount, NameMatchResult nameMatch)
    {
        var parts = new List<string>();
        if (faceCount > 1)
        {
            parts.Add($"这张照片里有 {faceCount} 张脸，请在下面选一张");
        }

        if (nameMatch.IsAmbiguous)
        {
            parts.Add("文件名同时对上了多位同学");
        }
        else if (nameMatch.Student != null)
        {
            parts.Add($"文件名提示是「{nameMatch.Student.Name}」");
        }

        if (candidateCount == 0)
        {
            parts.Add("没有认出像谁，请手动指定");
        }

        return parts.Count == 0 ? "" : string.Join("；", parts) + "。";
    }

    private async Task<bool> EnsureEngineAsync(ImportSummary summary)
    {
        if (_engine.IsReady)
        {
            return true;
        }

        var (ok, message) = await _engine.LoadAsync().ConfigureAwait(false);
        if (ok)
        {
            return true;
        }

        summary.FatalError = message;
        _log.Error("导入失败：人脸模型未就绪。");
        _log.Detail(message);
        return false;
    }

    private async Task<ImportItemResult> ImportOneAsync(
        Student student,
        string path,
        string source,
        string saveDir,
        CancellationToken ct)
    {
        var fileName = Path.GetFileName(path);
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var image = FaceEngine.ReadImageFile(path);
                if (image == null)
                {
                    return new ImportItemResult { FileName = fileName, Student = student, Ok = false, Message = "读取不到图片" };
                }

                var faces = _engine.Analyze(image, withFeature: true);
                if (faces.Count == 0)
                {
                    return new ImportItemResult { FileName = fileName, Student = student, Ok = false, Message = "没有检测到足够大的人脸" };
                }

                // 批量导入（按文件名）不弹窗，只能自动挑。取面积最大的一张 ——
                // 证件照场景就是唯一的本人；合照场景里离镜头最近的那张也通常就是本人。
                var target = faces.OrderByDescending(x => x.Area).First();
                if (target.Feature == null)
                {
                    return new ImportItemResult { FileName = fileName, Student = student, Ok = false, Message = "特征提取失败" };
                }

                var crop = FaceEngine.SaveFaceCrop(image, target, Path.Combine(saveDir, student.Id),
                    $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.jpg");
                var added = _roster.AddFaceSample(student, target.Feature, source, crop, save: false) ? 1 : 0;

                return new ImportItemResult
                {
                    FileName = fileName,
                    Student = student,
                    Added = added,
                    Ok = added > 0,
                    Message = faces.Count > 1
                        ? $"导入给「{student.Name}」（照片里有 {faces.Count} 张脸，自动取了最大的一张；要自己挑请改用「智能导入」）"
                        : $"导入给「{student.Name}」",
                };
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "FaceLate 导入 {File} 失败", fileName);
            return new ImportItemResult { FileName = fileName, Student = student, Ok = false, Message = "处理出错：" + e.Message };
        }
    }

    /// <summary>
    /// 把一张照片里的所有人脸都检测 + 提特征出来，按面积从大到小排好。
    /// </summary>
    private ImageProbe Probe(string path)
    {
        var probe = new ImageProbe { Path = path };
        try
        {
            var image = FaceEngine.ReadImageFile(path);
            if (image == null)
            {
                probe.Error = "读取不到图片";
                return probe;
            }

            probe.Image = image;
            var faces = _engine.Analyze(image, withFeature: true);
            probe.Faces = faces.OrderByDescending(x => x.Area).ToList();
            if (probe.Faces.Count == 0)
            {
                probe.Error = "没有检测到足够大的人脸";
            }

            return probe;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "FaceLate 分析 {File} 失败", path);
            probe.Error = "处理出错：" + e.Message;
            return probe;
        }
    }

    /// <summary>一张正在处理中的照片，持有解码后的图像。</summary>
    private sealed class ImageProbe : IDisposable
    {
        public string Path { get; init; } = "";

        public Mat? Image { get; set; }

        public List<DetectedFace> Faces { get; set; } = new();

        public string Error { get; set; } = "";

        public void Dispose() => Image?.Dispose();
    }
}
