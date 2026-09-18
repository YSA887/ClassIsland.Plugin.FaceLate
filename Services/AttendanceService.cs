using System.Diagnostics;
using ClassIsland.Plugin.FaceLate.Models;
using ClassIsland.Shared.Helpers;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace ClassIsland.Plugin.FaceLate.Services;

/// <summary>
/// 考勤执行状态。
/// </summary>
public enum AttendanceState
{
    /// <summary>尚未执行。</summary>
    Idle,

    /// <summary>正在抓拍 / 识别。</summary>
    Running,

    /// <summary>已完成。</summary>
    Completed,

    /// <summary>执行失败。</summary>
    Failed,
}

/// <summary>
/// 考勤核心服务：负责「抓拍 → 检测 → 特征提取 → 与花名册比对 → 得出迟到名单」的完整流程。
/// <para>
/// 一次完整流程的耗时构成（默认设置，4 张照片）：
/// 打开摄像头 ≈ 1 s + 预热 1 s + 抓拍 4×0.4 s ≈ 1.6 s + 推理 ≈ 1~4 s，合计约 5~8 秒，
/// 远小于 2 分钟的上限设置（<see cref="FaceLateSettings.MaxDurationSeconds"/>）。
/// </para>
/// </summary>
public sealed class AttendanceService : ObservableObjectBase
{
    /// <summary>
    /// 相对阈值的最小人数保护：即使只抓到 1 张照片，也至少要命中 1 次。
    /// </summary>
    private const int MinHitsFloor = 1;

    private readonly FaceLateSettings _settings;
    private readonly RosterService _roster;
    private readonly FaceEngine _engine;
    private readonly CameraCapture _camera;
    private readonly ActivityLog _log;
    private readonly ILogger<AttendanceService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private AttendanceState _state = AttendanceState.Idle;
    private string _statusText = "尚未执行考勤";
    private string _stageText = "空闲";
    private double _progressPercent;
    private bool _progressVisible;
    private LateRecord? _lastResult;

    public AttendanceService(
        FaceLateSettings settings,
        RosterService roster,
        FaceEngine engine,
        CameraCapture camera,
        ActivityLog log,
        ILogger<AttendanceService> logger)
    {
        _settings = settings;
        _roster = roster;
        _engine = engine;
        _camera = camera;
        _log = log;
        _logger = logger;
    }

    /// <summary>当前状态。</summary>
    public AttendanceState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                Raise();
            }
        }
    }

    /// <summary>状态描述文本，可直接显示在主界面组件上。</summary>
    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (Set(ref _statusText, value))
            {
                Raise();
            }
        }
    }

    /// <summary>当前正在做的事，例如「正在抓拍（2/4）…」，直接显示在进度条上方。</summary>
    public string StageText
    {
        get => _stageText;
        private set => Set(ref _stageText, value);
    }

    /// <summary>当前进度百分比（0~100），给界面上的进度条用。</summary>
    public double ProgressPercent
    {
        get => _progressPercent;
        private set => Set(ref _progressPercent, value);
    }

    /// <summary>进度条是否应该显示（只在识别进行中有意义）。</summary>
    public bool ProgressVisible
    {
        get => _progressVisible;
        private set => Set(ref _progressVisible, value);
    }

    /// <summary>最近一次考勤结果。</summary>
    public LateRecord? LastResult
    {
        get => _lastResult;
        private set
        {
            if (Set(ref _lastResult, value))
            {
                OnPropertyChanged(nameof(LastResultSummary));
                Raise();
            }
        }
    }

    /// <summary>最近一次结果的文字摘要。</summary>
    public string LastResultSummary
    {
        get
        {
            if (LastResult == null)
            {
                return "尚未执行考勤";
            }

            var time = LastResult.CaptureTime.ToString("MM-dd HH:mm:ss");
            if (!LastResult.Success)
            {
                return $"{time} 识别失败：{LastResult.Message}";
            }

            return LastResult.Late.Count == 0
                ? $"{time} 识别完成，没有迟到 ✅（识别到 {LastResult.Recognized.Count} 人）"
                : $"{time} 识别完成，迟到 {LastResult.Late.Count} 人：{LastResult.LateNamesText}";
        }
    }

    /// <summary>状态或结果变化时触发。始终在 UI 线程上抛出。</summary>
    public event EventHandler? ResultChanged;

    private void Raise() => UiThread.Run(() => ResultChanged?.Invoke(this, EventArgs.Empty));

    /// <summary>
    /// 所有属性变化通知都切回 UI 线程。
    /// <para>
    /// 识别流程跑在后台线程上。属性赋值本身没问题——字段更新是同步的，调用方 <c>await</c> 之后能立刻读到最新值；
    /// 但 PropertyChanged 会被 Avalonia 的绑定和界面代码消费，跨线程就会抛
    /// <c>InvalidOperationException: Call from invalid thread</c>，所以这里统一改到 UI 线程再抛。
    /// </para>
    /// </summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        UiThread.Run(() => base.OnPropertyChanged(e));
    }

    /// <summary>
    /// 更新进度：一次设置状态文本、阶段说明、进度百分比并通知界面。
    /// </summary>
    /// <param name="status">一句话状态，会显示在主界面组件上。</param>
    /// <param name="stage">阶段说明，显示在进度条上方。</param>
    /// <param name="percent">进度百分比 0~100；传负数表示不显示进度条（流程已结束）。</param>
    private void ReportProgress(string status, string stage, double percent)
    {
        _statusText = status;
        _stageText = stage;
        _progressVisible = percent >= 0;
        _progressPercent = percent < 0 ? 0 : Math.Clamp(percent, 0, 100);

        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StageText));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressVisible));
        Raise();
    }

    /// <summary>
    /// 执行一次完整的考勤。
    /// </summary>
    /// <param name="manual">是否由用户手动触发（手动触发不受时间/星期限制）。</param>
    /// <param name="external">外部取消令牌。</param>
    public async Task<LateRecord> RunCheckAsync(bool manual, CancellationToken external = default)
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            StatusText = "已有一次识别正在进行中，已跳过本次";
            return LastResult ?? new LateRecord { Success = false, Message = "已有一次识别正在进行中。" };
        }

        try
        {
            State = AttendanceState.Running;
            ReportProgress("正在抓拍并识别…", "准备开始…", 2);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(external);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, _settings.MaxDurationSeconds)));

            LateRecord record;
            try
            {
                record = await Task.Run(() => RunCoreAsync(manual, cts.Token), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                record = new LateRecord
                {
                    CaptureTime = DateTime.Now,
                    Success = false,
                    Message = $"识别超过 {_settings.MaxDurationSeconds} 秒仍未完成，已中止。可以适当减少抓拍张数或降低分辨率。",
                };
            }
            catch (Exception e)
            {
                _logger.LogError(e, "FaceLate 执行考勤时发生未处理异常");
                record = new LateRecord
                {
                    CaptureTime = DateTime.Now,
                    Success = false,
                    Message = $"识别出错：{e.Message}",
                };
            }

            LastResult = record;
            State = record.Success ? AttendanceState.Completed : AttendanceState.Failed;
            ReportProgress(
                record.Success
                    ? (record.Late.Count == 0
                        ? $"识别完成，未发现迟到（{record.ElapsedSeconds:F1} 秒）"
                        : $"识别完成，迟到 {record.Late.Count} 人（{record.ElapsedSeconds:F1} 秒）")
                    : $"识别失败：{record.Message}",
                record.Success ? "已完成" : "已中止",
                -1);

            SaveLastResult(record);
            CleanupSnapshots();

            // 日志只留一条结论 + 一条细节，不再把每一步都抖出来。
            if (record.Success)
            {
                _log.Success(record.Late.Count == 0
                    ? $"考勤完成，没有迟到（识别到 {record.Recognized.Count} 人，耗时 {record.ElapsedSeconds:F1} 秒）。"
                    : $"考勤完成，迟到 {record.Late.Count} 人：{record.LateNamesText}（耗时 {record.ElapsedSeconds:F1} 秒）。");
            }
            else
            {
                _log.Error("考勤失败：" + record.Message);
            }

            _log.Detail($"抓拍 {record.PhotoCount} 张 / 检测到人脸 {record.FaceCount} 张 / 应到 {record.ExpectedCount} 人 / 识别到 {record.Recognized.Count} 人");

            _logger.LogInformation("FaceLate 考勤结束：成功={Success} 照片={Photos} 人脸={Faces} 识别={Recognized} 迟到={Late} 耗时={Elapsed:F2}s",
                record.Success, record.PhotoCount, record.FaceCount, record.Recognized.Count, record.Late.Count,
                record.ElapsedSeconds);

            Raise();
            return record;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<LateRecord> RunCoreAsync(bool manual, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var record = new LateRecord { CaptureTime = DateTime.Now, JudgeMode = _settings.JudgeMode };

        // 1. 模型
        if (!_engine.IsReady)
        {
            ReportProgress("正在载入人脸模型…", "载入人脸模型…", 5);
            var (ok, message) = await _engine.LoadAsync(null, ct).ConfigureAwait(false);
            if (!ok)
            {
                record.Success = false;
                record.Message = message;
                record.ElapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                return record;
            }
        }

        // 2. 花名册
        ReportProgress("正在读取花名册…", "读取花名册…", 10);
        var gallery = _roster.BuildGallery();
        var activeStudents = _roster.GetActiveStudents();
        var enrollable = activeStudents.Where(x => x.Faces.Count > 0).ToList();
        record.ExpectedCount = enrollable.Count;

        if (gallery.Count == 0)
        {
            record.Success = false;
            // 分清楚「根本没录」和「录了但维度对不上」——后者多半是中途换了识别模型。
            var sampleCount = activeStudents.Sum(x => x.Faces.Count);
            record.Message = sampleCount == 0
                ? "花名册里还没有任何人脸样本。请先到「人脸考勤 · 花名册」录入，或在「批量导入」里导入照片。"
                : $"花名册里有 {sampleCount} 条样本，但都不是当前识别模型（{_engine.RecognizerPath}）的特征维度，无法比对。"
                  + "通常是因为中途换过识别模型（SFace ↔ ArcFace），请用当前模型重新录入或重新导入照片。";
            record.ElapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            return record;
        }

        // 3. 摄像头
        ReportProgress("正在打开摄像头…", "正在打开摄像头…", 15);
        if (!_camera.Open(_settings.CameraIndex, _settings.CaptureWidth, _settings.CaptureHeight))
        {
            record.Success = false;
            record.Message = $"打不开摄像头（序号 {_settings.CameraIndex}）。请检查设备是否被占用，或在设置里更换摄像头序号。";
            record.ElapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            return record;
        }

        try
        {
            ReportProgress("正在等摄像头自动曝光稳定…", "预热摄像头…", 20);
            await _camera.WarmUpAsync(_settings.WarmUpSeconds, ct).ConfigureAwait(false);

            // 4. 连拍
            var shotCount = Math.Clamp(_settings.ShotCount, 1, 20);
            ReportProgress($"正在抓拍（共 {shotCount} 张）…", $"正在抓拍 0/{shotCount}", 22);
            var snapshotDir = _settings.KeepSnapshots
                ? FaceLatePaths.EnsureDirectory(FaceLatePaths.SnapshotsFolder,
                    record.CaptureTime.ToString("yyyyMMdd-HHmmss"))
                : "";

            var frames = await _camera.CaptureSeriesAsync(
                shotCount,
                Math.Clamp(_settings.ShotIntervalMs, 0, 5000),
                (frame, index) =>
                {
                    if (!string.IsNullOrEmpty(snapshotDir))
                    {
                        SaveJpeg(frame, Path.Combine(snapshotDir, $"shot{index:00}.jpg"));
                    }

                    ReportProgress(
                        $"正在抓拍（{index}/{shotCount}）…",
                        $"正在抓拍 {index}/{shotCount}",
                        22 + 28.0 * index / Math.Max(1, shotCount));
                },
                ct).ConfigureAwait(false);

            record.PhotoCount = frames.Count;
            if (frames.Count == 0)
            {
                record.Success = false;
                record.Message = "没有从摄像头读到任何画面，请检查摄像头是否可用。";
                record.ElapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                return record;
            }

            // 5. 检测 + 特征提取 + 逐帧比对
            // 阈值跟着**当前实际载入的模型**走：SFace 是 0.363，ArcFace 系是 0.4~0.6 那一档，
            // 不能拿设置里某个写死的数字到处用（这正是之前 ArcFace 用错阈值的原因）。
            var threshold = Math.Clamp(_engine.EffectiveThreshold, 0.05f, 0.99f);
            var hits = new Dictionary<Student, LateEntry>();
            var scoreBoard = new Dictionary<Student, double>();

            for (var frameIndex = 0; frameIndex < frames.Count; frameIndex++)
            {
                ct.ThrowIfCancellationRequested();
                var frame = frames[frameIndex];
                ReportProgress(
                    $"正在比对人脸（{frameIndex + 1}/{frames.Count}）…",
                    $"分析第 {frameIndex + 1}/{frames.Count} 张照片",
                    50 + 40.0 * frameIndex / Math.Max(1, frames.Count));
                using (frame)
                {
                    var faces = _engine.Analyze(frame, withFeature: true, minFaceWidth: _settings.MinFaceWidth);
                    record.FaceCount += faces.Count;

                    // 同一张照片里，同一位同学只记一次命中
                    var matchedInThisFrame = new HashSet<Student>();
                    foreach (var face in faces)
                    {
                        if (face.Feature == null)
                        {
                            continue;
                        }

                        var (student, score) = FaceEngine.FindBest(face.Feature, gallery, threshold);
                        if (student == null)
                        {
                            continue;
                        }

                        if (!scoreBoard.TryGetValue(student, out var best) || score > best)
                        {
                            scoreBoard[student] = score;
                        }

                        if (!matchedInThisFrame.Add(student))
                        {
                            continue;
                        }

                        if (!hits.TryGetValue(student, out var entry))
                        {
                            entry = new LateEntry
                            {
                                Name = student.Name,
                                ClassName = student.ClassName,
                                Reason = "抓拍命中",
                            };
                            hits[student] = entry;
                        }

                        entry.Hits++;
                    }
                }
            }

            // 6. 汇总：命中次数达到 MinHits 才算「被识别到」
            ReportProgress("正在统计结果…", "统计迟到名单…", 95);
            foreach (var (student, entry) in hits)
            {
                entry.Score = scoreBoard.TryGetValue(student, out var s) ? s : 0d;
            }

            var minHits = Math.Clamp(_settings.MinHits, MinHitsFloor, Math.Max(MinHitsFloor, record.PhotoCount));
            var recognized = hits
                .Where(x => x.Value.Hits >= minHits)
                .OrderByDescending(x => x.Value.Hits)
                .Select(x => x.Key)
                .ToList();
            var recognizedIds = recognized.Select(x => x.Id).ToHashSet();

            record.Recognized = recognized
                .Select(x => new LateEntry
                {
                    Name = x.Name,
                    ClassName = x.ClassName,
                    Reason = "抓拍命中",
                    Score = scoreBoard.TryGetValue(x, out var s) ? s : 0d,
                    Hits = hits[x].Hits,
                })
                .ToList();

            if (_settings.JudgeMode == LateJudgeMode.Absence)
            {
                // 教室抓拍：应在场却没被认出来的人 = 迟到
                var notSeen = enrollable.Where(x => !recognizedIds.Contains(x.Id)).ToList();
                record.Late = notSeen.Select(x => new LateEntry
                {
                    Name = x.Name,
                    ClassName = x.ClassName,
                    Reason = "早读抓拍未检测到",
                    Hits = 0,
                }).ToList();

                var noSample = activeStudents.Where(x => x.Faces.Count == 0).Select(x => x.Name).ToList();
                if (noSample.Count > 0)
                {
                    record.Message = $"有 {noSample.Count} 位同学尚未录入人脸，未参与判定：{string.Join("、", noSample)}";
                }
            }
            else
            {
                // 门口抓拍：被认出来的人 = 迟到
                record.Late = record.Recognized.Select(x => new LateEntry
                {
                    Name = x.Name,
                    ClassName = x.ClassName,
                    Reason = "门口抓拍出现",
                    Score = x.Score,
                    Hits = x.Hits,
                }).ToList();
            }

            record.Success = true;
            record.ElapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            return record;
        }
        finally
        {
            _camera.Close();
        }
    }

    /// <summary>
    /// 用摄像头为某位同学录入人脸样本。可反复调用，样本会不断累积，识别越来越稳。
    /// </summary>
    /// <param name="student">目标同学。</param>
    /// <param name="shotCount">连拍张数。</param>
    /// <param name="onPreview">每抓到一张照片时的回调，参数为该帧的 JPEG 字节，用于界面预览。</param>
    /// <param name="pickFace">
    /// 画面里有多张脸时，用来问用户「要哪一张」的回调。参数是这一帧的选脸请求，
    /// 返回用户选中的脸在 <c>Faces</c> 列表里的下标（0 开始）；返回 null 表示跳过这一帧。
    /// 传 null 时自动取最大的一张脸。
    /// </param>
    /// <param name="ct">取消令牌。</param>
    public async Task<(int Added, string Message)> EnrollFromCameraAsync(
        Student student,
        int shotCount,
        Action<byte[]>? onPreview = null,
        Func<FacePickRequest, Task<int?>>? pickFace = null,
        CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            return (0, "已有一次摄像头任务在进行中，请稍后再试。");
        }

        try
        {
            if (!_engine.IsReady)
            {
                var (ok, message) = await _engine.LoadAsync(null, ct).ConfigureAwait(false);
                if (!ok)
                {
                    return (0, message);
                }
            }

            if (!_camera.Open(_settings.CameraIndex, _settings.CaptureWidth, _settings.CaptureHeight))
            {
                return (0, $"打不开摄像头（序号 {_settings.CameraIndex}）。");
            }

            try
            {
                await _camera.WarmUpAsync(_settings.WarmUpSeconds, ct).ConfigureAwait(false);

                var frames = await _camera.CaptureSeriesAsync(
                    Math.Clamp(shotCount, 1, 20),
                    Math.Clamp(_settings.ShotIntervalMs, 100, 3000),
                    null,
                    ct).ConfigureAwait(false);

                var added = 0;
                var noFace = 0;
                var skippedByUser = 0;
                var dir = Path.Combine(FaceLatePaths.FacesFolder, student.Id);

                for (var frameIndex = 0; frameIndex < frames.Count; frameIndex++)
                {
                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    using (var frame = frames[frameIndex])
                    {
                        if (onPreview != null)
                        {
                            var preview = FaceEngine.EncodeJpeg(frame, 80);
                            if (preview != null)
                            {
                                onPreview(preview);
                            }
                        }

                        var faces = _engine.Analyze(frame, withFeature: true);
                        if (faces.Count == 0)
                        {
                            noFace++;
                            continue;
                        }

                        DetectedFace? target;

                        if (faces.Count == 1 || pickFace == null)
                        {
                            // 只有一张脸（或用不上让用户挑），直接取最大的。
                            target = faces.OrderByDescending(x => x.Area).FirstOrDefault();
                        }
                        else
                        {
                            // 画面里有多张脸，让用户挑一张。
                            var request = new FacePickRequest
                            {
                                Student = student,
                                FrameIndex = frameIndex + 1,
                                FrameCount = frames.Count,
                                Faces = faces.OrderByDescending(x => x.Area).ToList(),
                                AnnotatedPreview = FaceEngine.EncodeAnnotatedPreview(
                                    frame, faces.OrderByDescending(x => x.Area).ToList()),
                                FaceThumbnails = faces.OrderByDescending(x => x.Area)
                                    .Select(f => FaceEngine.EncodeFaceThumbnail(frame, f))
                                    .ToList(),
                            };

                            var chosen = await pickFace(request).ConfigureAwait(false);
                            if (chosen == null)
                            {
                                // 用户跳过这一帧。
                                skippedByUser++;
                                continue;
                            }

                            target = chosen >= 0 && chosen < request.Faces.Count
                                ? request.Faces[chosen.Value]
                                : null;
                        }

                        if (target?.Feature == null)
                        {
                            noFace++;
                            continue;
                        }

                        var crop = FaceEngine.SaveFaceCrop(frame, target,
                            dir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{frameIndex}.jpg");
                        if (_roster.AddFaceSample(student, target.Feature, "camera", crop))
                        {
                            added++;
                        }
                    }
                }

                var message = noFace == 0 && skippedByUser == 0
                    ? $"成功录入 {added} 条样本。"
                    : $"成功录入 {added} 条样本，有 {noFace} 张照片没找到人脸" +
                      (skippedByUser > 0 ? $"，{skippedByUser} 张你选择了跳过" : "") +
                      "（请正对摄像头、保证光线充足）。";
                return (added, message);
            }
            finally
            {
                _camera.Close();
            }
        }
        catch (OperationCanceledException)
        {
            return (0, "已取消。");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "FaceLate 录入人脸时出错");
            return (0, $"录入失败：{e.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 从一张已有的图片文件为某位同学录入人脸样本。适合用学生证件照录入。
    /// <para>
    /// 如果图片里有多张脸（比如合照），会通过 <paramref name="pickFace"/> 让用户挑一张，
    /// 而不是像以前那样把所有人的脸都录进去。
    /// </para>
    /// </summary>
    /// <param name="student">目标同学。</param>
    /// <param name="path">图片路径。</param>
    /// <param name="pickFace">
    /// 多张脸时用来问用户「要哪一张」的回调。返回选中的下标（0 开始），
    /// 返回 null 表示用户放弃这张图。传 null 时自动取最大的一张脸。
    /// </param>
    public async Task<(int Added, string Message)> EnrollFromImageFileAsync(
        Student student,
        string path,
        Func<FacePickRequest, Task<int?>>? pickFace = null)
    {
        var added = 0;
        string message;

        try
        {
            if (!_engine.IsReady)
            {
                return (0, "人脸模型尚未载入，请先到「设置」页点一下「载入 / 重新载入模型」。");
            }

            using var image = FaceEngine.ReadImageFile(path);
            if (image == null)
            {
                return (0, "读取不到图片，或图片格式不支持。");
            }

            var faces = await Task.Run(() => _engine.Analyze(image, withFeature: true)).ConfigureAwait(false);
            if (faces.Count == 0)
            {
                return (0, _engine.NoFaceMessage(
                    "这张图片里没有检测到人脸（人脸太小或太模糊时会被忽略）。" +
                    "现在会自动按原图比例缩放检测，如果还检不到，请确认照片里的人脸是否清晰、占比是否够大。"));
            }

            var ordered = faces.OrderByDescending(x => x.Area).ToList();
            DetectedFace? target;

            if (ordered.Count == 1 || pickFace == null)
            {
                target = ordered[0];
            }
            else
            {
                var request = new FacePickRequest
                {
                    Student = student,
                    FrameIndex = 1,
                    FrameCount = 1,
                    Faces = ordered,
                    AnnotatedPreview = FaceEngine.EncodeAnnotatedPreview(image, ordered),
                    FaceThumbnails = ordered.Select(f => FaceEngine.EncodeFaceThumbnail(image, f)).ToList(),
                };

                var chosen = await pickFace(request).ConfigureAwait(false);
                if (chosen == null)
                {
                    return (0, "已取消这张照片的录入。");
                }

                target = chosen >= 0 && chosen < ordered.Count ? ordered[chosen.Value] : null;
            }

            if (target?.Feature == null)
            {
                return (0, "没能从选中的那张脸提取出特征，请换一张更清晰的照片。");
            }

            var dir = Path.Combine(FaceLatePaths.FacesFolder, student.Id);
            var crop = FaceEngine.SaveFaceCrop(image, target, dir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.jpg");
            if (_roster.AddFaceSample(student, target.Feature, "file", crop))
            {
                added = 1;
            }

            message = ordered.Count > 1
                ? $"从图片中录入 {added} 条样本（图片里共 {ordered.Count} 张脸，用了你选的那一张）。"
                : $"从图片中录入 {added} 条样本。";
        }
        catch (Exception e)
        {
            _logger.LogError(e, "FaceLate 从图片录入人脸时出错");
            return (0, $"录入失败：{e.Message}");
        }

        return (added, message);
    }

    /// <summary>
    /// 一次「这张脸要哪一张？」的提问。
    /// </summary>
    public sealed class FacePickRequest
    {
        /// <summary>正在录入的同学。</summary>
        public Student Student { get; init; } = null!;

        /// <summary>当前是第几张（连拍时用，从 1 开始）。</summary>
        public int FrameIndex { get; init; }

        /// <summary>总共几张。</summary>
        public int FrameCount { get; init; }

        /// <summary>画面里的所有脸，按面积从大到小排。</summary>
        public List<DetectedFace> Faces { get; init; } = new();

        /// <summary>整张画面带编号方框的预览图。</summary>
        public byte[]? AnnotatedPreview { get; init; }

        /// <summary>每张脸的缩略图，与 <see cref="Faces"/> 一一对应。</summary>
        public List<byte[]?> FaceThumbnails { get; init; } = new();
    }

    /// <summary>
    /// 摄像头自检：打开摄像头抓一帧，返回 JPEG 预览与描述文本。
    /// </summary>
    public async Task<(byte[]? Jpeg, string Message)> TestCameraAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            return (null, "已有一次摄像头任务在进行中，请稍后再试。");
        }

        try
        {
            if (!_camera.Open(_settings.CameraIndex, _settings.CaptureWidth, _settings.CaptureHeight))
            {
                return (null, $"打不开摄像头（序号 {_settings.CameraIndex}）。请确认没有被其它程序占用，或换个序号试试。");
            }

            try
            {
                await _camera.WarmUpAsync(Math.Min(_settings.WarmUpSeconds, 2), ct).ConfigureAwait(false);
                using var frame = _camera.Grab();
                if (frame == null)
                {
                    return (null, "摄像头已打开，但读不到画面。");
                }

                var jpeg = FaceEngine.EncodeJpeg(frame, 85);
                var message = $"摄像头正常：接口 {_camera.CurrentBackend}，分辨率 {frame.Width}×{frame.Height}。";
                return (jpeg, message);
            }
            finally
            {
                _camera.Close();
            }
        }
        catch (OperationCanceledException)
        {
            return (null, "已取消。");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "FaceLate 摄像头自检失败");
            return (null, $"摄像头自检失败：{e.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void SaveLastResult(LateRecord record)
    {
        try
        {
            var path = Path.Combine(FaceLatePaths.PluginConfigFolder, "last-result.json");
            ConfigureFileHelper.SaveConfig(path, record);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "FaceLate 保存最近结果失败");
        }
    }

    private static void SaveJpeg(Mat frame, string path)
    {
        try
        {
            var bytes = FaceEngine.EncodeJpeg(frame, 88);
            if (bytes != null)
            {
                File.WriteAllBytes(path, bytes);
            }
        }
        catch
        {
            // 抓拍照片只是存档用途，失败不影响识别
        }
    }

    private void CleanupSnapshots()
    {
        try
        {
            var days = Math.Max(1, _settings.SnapshotKeepDays);
            if (!Directory.Exists(FaceLatePaths.SnapshotsFolder))
            {
                return;
            }

            var deadline = DateTime.Now.AddDays(-days);
            foreach (var dir in Directory.GetDirectories(FaceLatePaths.SnapshotsFolder))
            {
                if (Directory.GetCreationTime(dir) < deadline)
                {
                    Directory.Delete(dir, true);
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "FaceLate 清理过期抓拍失败");
        }
    }
}
