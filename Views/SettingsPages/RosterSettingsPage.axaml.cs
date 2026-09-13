using System.Collections.ObjectModel;
using System.Text;
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
/// 人脸考勤 · 花名册：名单维护 + 单个同学的人脸录入。
/// <para>
/// 界面上的操作按钮全部收进两个下拉菜单（「花名册操作」「录入方式」「样本操作」），
/// 不再像以前那样一排四五个按钮挤在一起。
/// </para>
/// </summary>
[SettingsPageInfo("facelate.attendance.roster", "花名册", "\uE716", "\uE716")]
[Group(PluginGroupIds.FaceLate)]
public partial class RosterSettingsPage : SettingsPageBase
{
    private readonly RosterService _roster;
    private readonly AttendanceService _attendance;
    private readonly FaceEngine _engine;
    private readonly ActivityLog _log;
    private readonly FaceLateSettings _settings;

    /// <summary>供 XAML 绑定的学生列表。</summary>
    public ObservableCollection<Student> Students => _roster.Students;

    /// <summary>忙状态。</summary>
    public BusyState Busy { get; } = new();

    public RosterSettingsPage(
        RosterService roster,
        AttendanceService attendance,
        FaceEngine engine,
        ActivityLog log,
        FaceLateSettings settings)
    {
        _roster = roster;
        _attendance = attendance;
        _engine = engine;
        _log = log;
        _settings = settings;

        InitializeComponent();
        DataContext = this;
        LogView.DataContext = _log;

        NumberEnrollCount.Value = 5;
        NumberEnrollCount.FormatString = "0";
        TextClassName.Text = _roster.Document.ClassName;

        PanelEditor.IsEnabled = false;
        TextEditorHint.Text = "还没有选中同学。先在左边添加或选中一位同学。";

        _roster.RosterChanged += OnRosterChanged;
        DetachedFromVisualTree += (_, _) =>
        {
            _roster.RosterChanged -= OnRosterChanged;
            _roster.Document.ClassName = TextClassName.Text ?? "";
            _roster.RaiseChanged();

            // 页面关掉了，别让还在等用户点脸的录入任务一直挂着。
            if (_facePick != null)
            {
                _facePickDismissed = true;
                CompleteFacePick(null);
            }
        };

        RefreshSummary();
        _log.Detail($"花名册里有 {_roster.Students.Count} 位同学，人脸样本合计 {_roster.Students.Sum(x => x.Faces.Count)} 条。");
    }

    private void OnRosterChanged(object? sender, EventArgs e) => UiThread.Run(() =>
    {
        RefreshSummary();
        PanelEditor.IsEnabled = ListBoxStudents.SelectedItem is Student;
    });

    private void RefreshSummary()
    {
        var total = _roster.Students.Count;
        var withFace = _roster.Students.Count(x => x.Faces.Count > 0);
        var samples = _roster.Students.Sum(x => x.Faces.Count);
        TextRosterSummary.Text = $"共 {total} 人，已录入人脸 {withFace} 人，样本合计 {samples} 条。";
    }

    private Student? SelectedStudent => ListBoxStudents.SelectedItem as Student;

    // —— 多脸框选 ——

    /// <summary>等待用户点选人脸的一次提问；null 表示当前没有待回答的提问。</summary>
    private TaskCompletionSource<int?>? _facePick;
    private bool _facePickDismissed;

    /// <summary>
    /// 供浮层列表绑定的可选项。缩略图是 <see cref="Bitmap"/>，在 UI 线程上构造。
    /// </summary>
    public sealed class FacePickOption
    {
        /// <summary>脸的序号（1 开始），与预览图上的编号一致。</summary>
        public int Index { get; init; }

        /// <summary>缩略图。</summary>
        public Bitmap? Thumbnail { get; init; }

        /// <summary>显示文字。</summary>
        public string DisplayText { get; init; } = "";
    }

    /// <summary>
    /// 画面里有多张脸时弹出选脸浮层，等用户点一张。
    /// 返回选中脸的下标（0 开始）；返回 null 表示跳过这一张。
    /// </summary>
    private async Task<int?> AskFaceAsync(AttendanceService.FacePickRequest request)
    {
        if (_facePick != null)
        {
            return null;
        }

        var tcs = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _facePick = tcs;
        _facePickDismissed = false;

        UiThread.Run(() =>
        {
            try
            {
                var options = new List<FacePickOption>();
                for (var i = 0; i < request.Faces.Count; i++)
                {
                    var thumb = i < request.FaceThumbnails.Count ? TryDecode(request.FaceThumbnails[i]) : null;
                    var face = request.Faces[i];
                    options.Add(new FacePickOption
                    {
                        Index = i + 1,
                        Thumbnail = thumb,
                        DisplayText = thumb == null
                            ? $"脸 {i + 1}　{face.Width}×{face.Height}px"
                            : $"脸 {i + 1}　{face.Width}×{face.Height}px　置信度 {face.Score:P0}",
                    });
                }

                ItemsFacePick.ItemsSource = options;
                ImageFacePickPreview.Source = TryDecode(request.AnnotatedPreview);

                TextFacePickTitle.Text = $"给「{request.Student.Name}」录入：画面里有 {request.Faces.Count} 张脸";
                TextFacePickHint.Text = request.FrameCount > 1
                    ? $"这是连拍的第 {request.FrameIndex} / {request.FrameCount} 张。请点一下要录入的那张脸（或「跳过这张」）。"
                    : "请点一下要录入的那张脸（或「跳过这张」）。";

                BorderFacePick.IsVisible = true;
            }
            catch (Exception ex)
            {
                _log.Warn("选脸浮层打开失败：" + ex.Message);
                CompleteFacePick(null);
            }
        });

        return await tcs.Task.ConfigureAwait(false);
    }

    private void ButtonFaceOption_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int index })
        {
            // Tag 是 1 开始的序号，转成 0 开始的下标。
            CompleteFacePick(index - 1);
        }
        else if (sender is Button { Tag: string text } && int.TryParse(text, out var parsed))
        {
            CompleteFacePick(parsed - 1);
        }
    }

    private void ButtonFacePickSkip_OnClick(object? sender, RoutedEventArgs e) => CompleteFacePick(null);

    private void ButtonFacePickCancel_OnClick(object? sender, RoutedEventArgs e)
    {
        _facePickDismissed = true;
        CompleteFacePick(null);
    }

    private void CompleteFacePick(int? result)
    {
        UiThread.Run(() =>
        {
            BorderFacePick.IsVisible = false;
            ImageFacePickPreview.Source = null;
            ItemsFacePick.ItemsSource = null;
        });

        _facePick?.TrySetResult(_facePickDismissed ? null : result);
        _facePick = null;
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

    private void UpdateEditorHint()
    {
        TextEditorHint.Text = SelectedStudent is not { } student
            ? "还没有选中同学。先在左边添加或选中一位同学。"
            : $"正在编辑：{student.Name}（已录入 {student.Faces.Count} 条人脸样本）";
    }

    private void ListBoxStudents_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var student = SelectedStudent;
        PanelEditor.DataContext = student;
        PanelEditor.IsEnabled = student != null;
        UpdateEditorHint();
    }

    private void ButtonAddStudent_OnClick(object? sender, RoutedEventArgs e)
    {
        var name = TextNewName.Text?.Trim() ?? "";
        if (name.Length == 0)
        {
            _log.Warn("请先输入姓名。");
            return;
        }

        if (_roster.Students.Any(x => x.Name == name && x.ClassName == (TextClassName.Text ?? "")))
        {
            _log.Warn($"「{name}」已经在花名册里了。");
            return;
        }

        var student = _roster.AddStudent(name, TextClassName.Text ?? "");
        TextNewName.Text = "";
        ListBoxStudents.SelectedItem = student;
        _log.Success($"已添加「{name}」，接着给它录入人脸吧。");
    }

    private void ButtonSave_OnClick(object? sender, RoutedEventArgs e)
    {
        _roster.Document.ClassName = TextClassName.Text ?? "";
        _roster.RaiseChanged();
        _log.Success("班级名已保存。");
    }

    // —— 录入 ——

    private async void MenuEnrollCamera_OnClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedStudent is not { } student || Busy.IsBusy)
        {
            if (SelectedStudent == null)
            {
                _log.Warn("请先在左边选中一位同学。");
            }

            return;
        }

        var count = (int)(NumberEnrollCount.Value ?? 5);
        Busy.IsBusy = true;
        TextBusyHint.Text = "正在连拍…";
        _log.Info($"开始为「{student.Name}」连拍 {count} 张，请正对摄像头。");

        try
        {
            var (added, message) = await _attendance.EnrollFromCameraAsync(student, count, ShowPreview, AskFaceAsync);
            if (added > 0)
            {
                _log.Success($"{student.Name}：{message}");
            }
            else
            {
                _log.Warn($"{student.Name}：{message}");
            }

            UpdateEditorHint();
        }
        catch (Exception ex)
        {
            _log.Error("摄像头录入失败：" + ex.Message);
        }
        finally
        {
            Busy.IsBusy = false;
            TextBusyHint.Text = "";
        }
    }

    private void ShowPreview(byte[] jpeg) => UiThread.Run(() =>
    {
        try
        {
            using var stream = new MemoryStream(jpeg);
            ImagePreview.Source = new Bitmap(stream);
            BorderPreview.IsVisible = true;
        }
        catch
        {
            // 预览失败不影响录入
        }
    });

    private async void MenuEnrollFile_OnClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedStudent is not { } student || Busy.IsBusy)
        {
            if (SelectedStudent == null)
            {
                _log.Warn("请先在左边选中一位同学。");
            }

            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"为「{student.Name}」选择照片（可多选）",
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

        if (files.Count == 0)
        {
            return;
        }

        Busy.IsBusy = true;
        TextBusyHint.Text = $"正在处理 {files.Count} 张照片…";
        try
        {
            var total = 0;
            foreach (var file in files)
            {
                var path = file.TryGetLocalPath();
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                var (added, message) = await _attendance.EnrollFromImageFileAsync(student, path, AskFaceAsync);
                total += added;
                if (added > 0)
                {
                    _log.Info($"{Path.GetFileName(path)}：{message}");
                }
                else
                {
                    _log.Warn($"{Path.GetFileName(path)}：{message}");
                }
            }

            _log.Success($"「{student.Name}」本次共新增 {total} 条样本，当前共 {student.Faces.Count} 条。");
            UpdateEditorHint();
        }
        finally
        {
            Busy.IsBusy = false;
            TextBusyHint.Text = "";
        }
    }

    // —— 样本操作 ——

    private void MenuRemoveSample_OnClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedStudent is not { } student || ListBoxSamples.SelectedItem is not FaceSample sample)
        {
            _log.Warn("请先在下面的样本列表里选中一条样本。");
            return;
        }

        student.Faces.Remove(sample);
        student.NotifyFacesChanged();
        _roster.RaiseChanged();
        _log.Info($"已删除「{student.Name}」的一条样本，剩余 {student.Faces.Count} 条。");
    }

    private void MenuClearFaces_OnClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedStudent is not { } student)
        {
            _log.Warn("请先在左边选中一位同学。");
            return;
        }

        _roster.ClearFaces(student);
        _log.Info($"已清空「{student.Name}」的全部人脸样本。");
        UpdateEditorHint();
    }

    private void MenuRemoveStudent_OnClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedStudent is not { } student)
        {
            _log.Warn("请先在左边选中一位同学。");
            return;
        }

        var name = student.Name;
        _roster.RemoveStudent(student);
        ListBoxStudents.SelectedItem = null;
        PanelEditor.DataContext = null;
        PanelEditor.IsEnabled = false;
        _log.Info($"已从花名册删除「{name}」。");
        UpdateEditorHint();
    }

    // —— 名单导入导出 ——

    private async void MenuImport_OnClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择花名册文本（每行一位同学，可用 姓名,班级 的格式）",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("文本 / CSV")
                {
                    Patterns = new[] { "*.txt", "*.csv" },
                },
                FilePickerFileTypes.All,
            },
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            var text = await File.ReadAllTextAsync(path, Encoding.UTF8);
            ImportText(text);
        }
        catch (Exception ex)
        {
            _log.Error("读取文件失败：" + ex.Message);
        }
    }

    private async void MenuImportClipboard_OnClick(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
        {
            return;
        }

        try
        {
            var text = await clipboard.GetTextAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                _log.Warn("剪贴板里没有文本。");
                return;
            }

            ImportText(text);
        }
        catch (Exception ex)
        {
            _log.Error("读取剪贴板失败：" + ex.Message);
        }
    }

    private void ImportText(string text)
    {
        var added = _roster.ImportFromText(text, TextClassName.Text ?? "");
        if (added == 0)
        {
            _log.Warn("没有新增任何同学（可能名单里的人都已存在，或文本格式不对）。");
        }
        else
        {
            _log.Success($"已从文本导入 {added} 位同学。记得为每位同学录入人脸。");
        }

        RefreshSummary();
    }

    private async void MenuExport_OnClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return;
        }

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出花名册",
            SuggestedFileName = $"花名册-{DateTime.Now:yyyyMMdd}.csv",
            DefaultExtension = "csv",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("CSV")
                {
                    Patterns = new[] { "*.csv" },
                },
            },
        });

        var path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            // 带 BOM，避免 Excel 打开中文乱码
            await File.WriteAllTextAsync(path, "\uFEFF" + _roster.ExportToCsv(), Encoding.UTF8);
            _log.Success("花名册已导出：" + path);
        }
        catch (Exception ex)
        {
            _log.Error("导出失败：" + ex.Message);
        }
    }

    private void MenuOpenFacesFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        var error = FaceLatePaths.OpenInExplorer(FaceLatePaths.EnsureDirectory(FaceLatePaths.FacesFolder));
        if (error.Length > 0)
        {
            _log.Warn("打不开人脸样本目录：" + error);
        }
    }
}
