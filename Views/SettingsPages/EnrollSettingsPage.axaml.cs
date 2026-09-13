using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Plugin.FaceLate.Models;
using ClassIsland.Plugin.FaceLate.Services;

namespace ClassIsland.Plugin.FaceLate.Views.SettingsPages;

/// <summary>
/// 智能导入确认面板的可绑定状态。
/// <para>
/// 页面本身继承自 <c>UserControl</c>，发不出 CLR 属性的变更通知，
/// 所以把面板要用的东西集中到这个小对象里，XAML 绑 <c>Prompt.XXX</c>。
/// </para>
/// </summary>
public sealed class SmartImportState : ObservableObjectBase
{
    private bool _visible;
    private string _title = "";
    private string _note = "";
    private string _progressText = "";
    private string _newNameHint = "";
    private Bitmap? _thumbnail;
    private Student? _selectedCandidate;
    private FaceChoice? _selectedFace;

    /// <summary>面板是否可见。</summary>
    public bool Visible
    {
        get => _visible;
        set => Set(ref _visible, value);
    }

    /// <summary>「这张照片是 XXX 吗？」</summary>
    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    /// <summary>补充说明（相似度、文件名提示等）。</summary>
    public string Note
    {
        get => _note;
        set => Set(ref _note, value);
    }

    /// <summary>「第 3 / 12 张：xxx.jpg」</summary>
    public string ProgressText
    {
        get => _progressText;
        set => Set(ref _progressText, value);
    }

    /// <summary>新建同学时会用的名字提示。</summary>
    public string NewNameHint
    {
        get => _newNameHint;
        set => Set(ref _newNameHint, value);
    }

    /// <summary>人脸缩略图。</summary>
    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        set => Set(ref _thumbnail, value);
    }

    /// <summary>整张照片带编号方框的预览图。</summary>
    public Bitmap? AnnotatedPreview { get; set; }

    /// <summary>这张照片里检测到的所有脸，让用户挑一张。</summary>
    public ObservableCollection<FaceChoice> Faces { get; } = new();

    /// <summary>照片里不止一张脸时，界面上才需要显示选脸区。</summary>
    public bool NeedsFaceChoice => Faces.Count > 1;

    /// <summary>当前选中的那张脸；换脸时会顺带更新归属同学的建议。</summary>
    public FaceChoice? SelectedFace
    {
        get => _selectedFace;
        set => Set(ref _selectedFace, value);
    }

    /// <summary>可选的归属同学（命中的排在前面）。</summary>
    public ObservableCollection<Student> Candidates { get; } = new();

    /// <summary>当前选中的归属同学。</summary>
    public Student? SelectedCandidate
    {
        get => _selectedCandidate;
        set => Set(ref _selectedCandidate, value);
    }

    /// <summary>建议的新名字。</summary>
    public string SuggestedNewName { get; set; } = "";

    /// <summary>
    /// 一张可选的脸（界面用）。除了缩略图，还带着「这张脸最像谁」的建议。
    /// </summary>
    public sealed class FaceChoice
    {
        /// <summary>序号（1 开始），与预览图上的编号一致。</summary>
        public int Index { get; init; }

        /// <summary>缩略图。</summary>
        public Bitmap? Thumbnail { get; init; }

        /// <summary>显示文字。</summary>
        public string DisplayText { get; init; } = "";

        /// <summary>这张脸最像的同学。</summary>
        public Student? Best { get; init; }

        /// <summary>相似度。</summary>
        public double BestScore { get; init; }
    }
}

/// <summary>
/// 人脸考勤 · 批量导入：按文件名自动匹配 + 逐张确认的智能导入。
/// </summary>
[SettingsPageInfo("facelate.attendance.enroll", "批量导入", "\uE8B7", "\uE8B7")]
[Group(PluginGroupIds.FaceLate)]
public partial class EnrollSettingsPage : SettingsPageBase
{
    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".jfif" };

    private readonly EnrollmentService _enrollment;
    private readonly RosterService _roster;
    private readonly ActivityLog _log;

    private TaskCompletionSource<SmartDecision>? _pending;
    private CancellationTokenSource? _importCts;

    /// <summary>当前正在回答的那次提问，用于「换脸 → 换建议」。</summary>
    private SmartPrompt? _activePrompt;

    /// <summary>忙状态。</summary>
    public BusyState Busy { get; } = new();

    /// <summary>智能导入确认面板状态。</summary>
    public SmartImportState Prompt { get; } = new();

    public EnrollSettingsPage(EnrollmentService enrollment, RosterService roster, ActivityLog log)
    {
        _enrollment = enrollment;
        _roster = roster;
        _log = log;

        InitializeComponent();
        DataContext = this;
        LogView.DataContext = _log;

        // 用户换一张脸时，顺便把「归给谁」的建议也换过去。
        Prompt.PropertyChanged += OnPromptPropertyChanged;

        DetachedFromVisualTree += (_, _) =>
        {
            // 页面被关掉时把还没回答的提问直接取消，避免导入流程一直挂着。
            _importCts?.Cancel();
            _pending?.TrySetResult(SmartDecision.DoCancel());
            _pending = null;
        };
    }

    private void OnPromptPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SmartImportState.SelectedFace) || _activePrompt is not { } prompt)
        {
            return;
        }

        var best = Prompt.SelectedFace?.Best
                   ?? prompt.FileNameMatch
                   ?? prompt.Matches.FirstOrDefault()?.Student;

        if (best != null && Prompt.Candidates.Contains(best))
        {
            Prompt.SelectedCandidate = best;
        }

        RefreshTitle(prompt);
    }

    // ===================== 批量导入（按文件名） =====================

    private async void ButtonBatchPickFiles_OnClick(object? sender, RoutedEventArgs e)
        => await RunBatchAsync(await PickFilesAsync("选择要导入的照片（可多选）"));

    private async void ButtonBatchPickFolder_OnClick(object? sender, RoutedEventArgs e)
        => await RunBatchAsync(await PickFolderImagesAsync("选择装着照片的文件夹"));

    private async Task RunBatchAsync(List<string> paths)
    {
        if (paths.Count == 0 || Busy.IsBusy)
        {
            return;
        }

        BorderBatchResult.IsVisible = false;
        Busy.IsBusy = true;
        _importCts = new CancellationTokenSource();

        var progress = new Progress<(int Done, int Total, string FileName)>(p =>
            TextBatchProgress.Text = string.IsNullOrEmpty(p.FileName)
                ? ""
                : $"正在处理 {p.Done + 1}/{p.Total}：{p.FileName}");

        try
        {
            var summary = await _enrollment.ImportByFileNameAsync(paths, progress, _importCts.Token);
            ShowBatchResult(summary);
        }
        finally
        {
            Busy.IsBusy = false;
            _importCts?.Dispose();
            _importCts = null;
            TextBatchProgress.Text = "";
        }
    }

    private void ShowBatchResult(ImportSummary summary)
    {
        BorderBatchResult.IsVisible = true;

        if (summary.FatalError.Length > 0)
        {
            TextBatchSummary.Text = "导入没有开始：" + summary.FatalError;
            TextBatchDetail.Text = "";
            return;
        }

        TextBatchSummary.Text = $"完成：{summary.OkCount} 张成功（新增 {summary.AddedCount} 条样本），"
                                + $"{summary.SkippedCount} 张没导入。"
                                + (summary.Cancelled ? "（已被中止）" : "");

        // 界面只列前若干条没导入的原因，全部明细都在日志的「显示详细信息」里。
        var failures = summary.Items.Where(x => !x.Ok).ToList();
        TextBatchDetail.Text = failures.Count == 0
            ? "全部照片都归位了。"
            : "没导入的：" + string.Join("\n", failures.Take(12).Select(x => $"{x.FileName} —— {x.Message}"))
              + (failures.Count > 12 ? $"\n…… 还有 {failures.Count - 12} 张，明细见日志。" : "");

        foreach (var item in summary.Items)
        {
            if (item.Ok)
            {
                _log.Detail($"✔ {item.FileName} → {item.Student?.Name}（+{item.Added}）");
            }
            else
            {
                _log.Detail($"✘ {item.FileName}：{item.Message}");
            }
        }
    }

    // ===================== 智能导入 =====================

    private async void ButtonSmartPickFiles_OnClick(object? sender, RoutedEventArgs e)
        => await RunSmartAsync(await PickFilesAsync("选择要智能导入的照片（可多选）"));

    private async void ButtonSmartPickFolder_OnClick(object? sender, RoutedEventArgs e)
        => await RunSmartAsync(await PickFolderImagesAsync("选择装着照片的文件夹"));

    private async Task RunSmartAsync(List<string> paths)
    {
        if (paths.Count == 0 || Busy.IsBusy)
        {
            return;
        }

        if (_roster.Students.Count == 0)
        {
            _log.Warn("花名册还是空的，先在「花名册」页添加同学，或用「批量导入」按文件名导入。");
            return;
        }

        BorderSmartResult.IsVisible = false;
        Busy.IsBusy = true;
        _importCts = new CancellationTokenSource();

        var progress = new Progress<(int Done, int Total, string FileName)>(p =>
        {
            if (!string.IsNullOrEmpty(p.FileName) && !Prompt.Visible)
            {
                TextBatchProgress.Text = $"正在分析 {p.Done + 1}/{p.Total}…";
            }
        });

        try
        {
            var summary = await _enrollment.SmartImportAsync(paths, AskAsync, progress, _importCts.Token);
            ShowSmartResult(summary);
        }
        finally
        {
            Prompt.Visible = false;
            Busy.IsBusy = false;
            _importCts?.Dispose();
            _importCts = null;
            TextBatchProgress.Text = "";
        }
    }

    /// <summary>
    /// 把一次提问显示到面板上，并返回一个「等用户点按钮」的任务。
    /// <para>
    /// 注意这里**不能**阻塞 UI 线程：我们只是把界面状态改好（切回 UI 线程做），
    /// 然后返回一个未完成的 Task，让导入流程在那里 await。
    /// UI 线程随后是空闲的，可以正常渲染并响应按钮点击。
    /// </para>
    /// </summary>
    private Task<SmartDecision> AskAsync(SmartPrompt prompt)
    {
        var tcs = new TaskCompletionSource<SmartDecision>(TaskCreationOptions.RunContinuationsAsynchronously);

        UiThread.Run(() =>
        {
            try
            {
                Prompt.ProgressText = $"第 {prompt.Index} / {prompt.Total} 张　·　{prompt.FileName}"
                                      + (prompt.FaceCount > 1 ? $"（照片里有 {prompt.FaceCount} 张脸）" : "");

                // 画面里有多张脸时，把每张脸都列出来让用户挑。
                Prompt.Faces.Clear();
                foreach (var face in prompt.Faces)
                {
                    Prompt.Faces.Add(new SmartImportState.FaceChoice
                    {
                        Index = face.Index,
                        Thumbnail = TryDecode(face.Thumbnail),
                        DisplayText = face.DisplayText,
                        Best = face.Best,
                        BestScore = face.BestScore,
                    });
                }

                Prompt.SelectedFace = Prompt.Faces.FirstOrDefault();
                Prompt.AnnotatedPreview = TryDecode(prompt.AnnotatedPreview);
                Prompt.Thumbnail = TryDecode(prompt.Thumbnail);

                RefreshTitle(prompt);

                // 候选顺序：识别命中的在前，其余花名册同学在后，方便「改为其他同学」。
                Prompt.Candidates.Clear();
                foreach (var candidate in prompt.Matches)
                {
                    if (!Prompt.Candidates.Contains(candidate.Student))
                    {
                        Prompt.Candidates.Add(candidate.Student);
                    }
                }

                foreach (var student in _roster.Students)
                {
                    if (!Prompt.Candidates.Contains(student))
                    {
                        Prompt.Candidates.Add(student);
                    }
                }

                Prompt.SelectedCandidate = prompt.Suggested ?? Prompt.Candidates.FirstOrDefault();
                Prompt.SuggestedNewName = prompt.FileNameMatch?.Name
                                          ?? Path.GetFileNameWithoutExtension(prompt.FileName);
                Prompt.NewNameHint = string.IsNullOrWhiteSpace(Prompt.SuggestedNewName)
                    ? "「新建同学并导入」会用文件名当姓名，之后可以在「花名册」页改名。"
                    : $"「新建同学并导入」会新建一位叫「{Prompt.SuggestedNewName}」的同学。";

                // 换脸时把「归给谁」的建议一起换掉，用户就不用自己重选了。
                // 这个事件在构造函数里挂一次（见 OnSelectedFaceChanged），
                // 不能在这里挂，否则每问一张都会多挂一个处理器。
                _activePrompt = prompt;

                Prompt.Visible = true;
            }
            catch (Exception ex)
            {
                _log.Warn("确认面板显示失败：" + ex.Message);
                tcs.TrySetResult(SmartDecision.DoSkip());
            }
        });

        _pending = tcs;
        return tcs.Task;
    }

    /// <summary>根据当前选中的脸刷新标题与说明文字。</summary>
    private void RefreshTitle(SmartPrompt prompt)
    {
        var face = Prompt.SelectedFace;

        Prompt.Title = prompt.Faces.Count > 1
            ? $"给「{face?.Best?.Name ?? "谁"}」导入这张照片里的第 {face?.Index ?? 1} 张脸吗？"
            : face?.Best != null
                ? $"这张照片是「{face.Best.Name}」吗？"
                : prompt.Best != null
                    ? $"这张照片是「{prompt.Best.Name}」吗？"
                    : "没能认出这张照片是谁";

        var parts = new List<string>();
        if (face?.Best != null)
        {
            parts.Add($"这张脸最像「{face.Best.Name}」，相似度 {face.BestScore:F3}");
        }
        else if (prompt.Best != null)
        {
            parts.Add($"最像「{prompt.Best.Name}」，相似度 {prompt.BestScore:F3}");
        }

        if (prompt.Faces.Count > 1)
        {
            parts.Add($"照片里有 {prompt.Faces.Count} 张脸，点下面任意一张可以换人");
        }

        if (prompt.Note.Length > 0)
        {
            parts.Add(prompt.Note);
        }

        if (face?.Best == null && prompt.Best == null && prompt.FileNameMatch == null)
        {
            parts.Add("请从下面的下拉框里选一位同学，或点「新建同学并导入」");
        }

        Prompt.Note = string.Join("；", parts);
    }

    private static Bitmap? TryDecode(byte[]? jpeg)
    {
        if (jpeg == null || jpeg.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(jpeg);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    private void ButtonAccept_OnClick(object? sender, RoutedEventArgs e)
    {
        if (Prompt.SelectedCandidate is not { } student)
        {
            _log.Warn("请先在下拉框里选一位同学。");
            return;
        }

        Complete(SmartDecision.For(student, Prompt.SelectedFace?.Index ?? 0));
    }

    private void ButtonSkip_OnClick(object? sender, RoutedEventArgs e) => Complete(SmartDecision.DoSkip());

    /// <summary>用户在缩略图列表里点了某一张脸，把它设为当前选中项。</summary>
    private void ButtonPickFace_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SmartImportState.FaceChoice choice })
        {
            Prompt.SelectedFace = choice;
        }
    }

    private void ButtonCreateNew_OnClick(object? sender, RoutedEventArgs e)
    {
        var name = (Prompt.SuggestedNewName ?? "").Trim();
        if (name.Length == 0)
        {
            _log.Warn("文件名里没有可用的姓名，请从下拉框里选一位同学。");
            return;
        }

        Complete(SmartDecision.CreateAs(name, Prompt.SelectedFace?.Index ?? 0));
    }

    private void ButtonAbort_OnClick(object? sender, RoutedEventArgs e) => Complete(SmartDecision.DoCancel());

    private void ButtonCancelImport_OnClick(object? sender, RoutedEventArgs e)
    {
        // 中止正在跑的导入：取消令牌会让循环退出；如果此刻正等着用户回答，
        // 就把那个问题直接答成「中止」。
        _importCts?.Cancel();
        Complete(SmartDecision.DoCancel());
    }

    private void Complete(SmartDecision decision)
    {
        Prompt.Visible = false;
        _activePrompt = null;
        var pending = _pending;
        _pending = null;
        pending?.TrySetResult(decision);
    }

    private void ShowSmartResult(ImportSummary summary)
    {
        BorderSmartResult.IsVisible = true;

        if (summary.FatalError.Length > 0)
        {
            TextSmartSummary.Text = "导入没有开始：" + summary.FatalError;
            TextSmartDetail.Text = "";
            return;
        }

        TextSmartSummary.Text = $"完成：{summary.OkCount} 张导入（新增 {summary.AddedCount} 条样本），"
                                + $"{summary.SkippedCount} 张跳过。"
                                + (summary.Cancelled ? "（已被中止）" : "");

        var imported = summary.Items.Where(x => x.Ok).ToList();
        TextSmartDetail.Text = imported.Count == 0
            ? "这次没有导入任何照片。"
            : "已导入：" + string.Join("、", imported.Take(20).Select(x => $"{x.FileName}→{x.Student?.Name}"))
              + (imported.Count > 20 ? $" …等 {imported.Count} 张" : "");

        foreach (var item in summary.Items)
        {
            _log.Detail(item.Ok
                ? $"✔ {item.FileName} → {item.Student?.Name}（+{item.Added}）"
                : $"✘ {item.FileName}：{item.Message}");
        }
    }

    // ===================== 选文件 / 选文件夹 =====================

    private async Task<List<string>> PickFilesAsync(string title)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return new List<string>();
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("图片")
                {
                    Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.bmp", "*.webp" },
                },
                FilePickerFileTypes.All,
            },
        });

        return files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Cast<string>()
            .ToList();
    }

    /// <summary>
    /// 选一个文件夹，把它（含最多 3 层子文件夹）里的图片都收出来。
    /// 「一人一个文件夹」的组织方式很常见，所以顺手递归一下。
    /// </summary>
    private async Task<List<string>> PickFolderImagesAsync(string title)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return new List<string>();
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });

        var folder = folders.FirstOrDefault();
        if (folder == null)
        {
            return new List<string>();
        }

        var sink = new List<string>();
        await CollectImagesAsync(folder, sink, 0);

        if (sink.Count == 0)
        {
            _log.Warn($"「{folder.Name}」里没有找到图片（支持 jpg / jpeg / png / bmp / webp）。");
        }

        return sink.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task CollectImagesAsync(IStorageFolder folder, List<string> sink, int depth)
    {
        if (depth > 3)
        {
            return;
        }

        try
        {
            // Avalonia 的 GetItemsAsync 返回 IAsyncEnumerable，要用 await foreach 消费。
            await foreach (var item in folder.GetItemsAsync())
            {
                switch (item)
                {
                    case IStorageFolder sub:
                        await CollectImagesAsync(sub, sink, depth + 1);
                        break;
                    case IStorageFile file:
                    {
                        var path = file.TryGetLocalPath();
                        if (!string.IsNullOrEmpty(path) && IsImage(path))
                        {
                            sink.Add(path);
                        }

                        break;
                    }
                }
            }
        }
        catch
        {
            // 没权限的目录直接跳过，不影响其它目录
        }
    }

    private static bool IsImage(string path)
    {
        var ext = Path.GetExtension(path);
        return ImageExtensions.Any(x => string.Equals(x, ext, StringComparison.OrdinalIgnoreCase));
    }
}
