using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Plugin.FaceLate.Models;
using ClassIsland.Plugin.FaceLate.Services;

namespace ClassIsland.Plugin.FaceLate.Views.SettingsPages;

/// <summary>
/// 人脸考勤 · 运行与日志：手动跑一次、看结果、看日志。
/// </summary>
[SettingsPageInfo("facelate.attendance.runlog", "运行与日志", "\uE9D2", "\uE9D2")]
[Group(PluginGroupIds.FaceLate)]
public partial class RunLogSettingsPage : SettingsPageBase
{
    private readonly AttendanceService _attendance;
    private readonly ActivityLog _log;

    /// <summary>
    /// 识别服务。暴露出来是为了让 XAML 直接把进度条绑到
    /// <c>Attendance.ProgressPercent</c> / <c>ProgressVisible</c> / <c>StageText</c> 上。
    /// </summary>
    public AttendanceService Attendance => _attendance;

    /// <summary>忙状态：识别进行中时禁用「立即识别一次」，避免重复点击排队。</summary>
    public BusyState Busy { get; } = new();

    public RunLogSettingsPage(AttendanceService attendance, ActivityLog log)
    {
        _attendance = attendance;
        _log = log;

        InitializeComponent();
        DataContext = this;
        LogView.DataContext = _log;

        RefreshStatus();

        _attendance.ResultChanged += OnAttendanceChanged;
        DetachedFromVisualTree += (_, _) => _attendance.ResultChanged -= OnAttendanceChanged;
    }

    private void OnAttendanceChanged(object? sender, EventArgs e) => RefreshStatus();

    /// <summary>
    /// 刷新结果文字。
    /// <para>
    /// 这个方法**会被后台线程调用**：识别跑在 <c>Task.Run</c> 里，中途和结束时都会
    /// 触发 <see cref="AttendanceService.ResultChanged"/>。Avalonia 控件只允许在 UI 线程上改，
    /// 所以统一走 <see cref="UiThread"/>。
    /// </para>
    /// </summary>
    private void RefreshStatus() => UiThread.Run(() =>
    {
        TextRunStatus.Text = _attendance.StatusText;

        var record = _attendance.LastResult;
        if (record == null)
        {
            TextRunDetail.Text = "还没有执行过识别。";
            TextRunLate.Text = "";
            return;
        }

        TextRunDetail.Text =
            $"时间 {record.CaptureTime:MM-dd HH:mm:ss}　" +
            $"判定方式：{(record.JudgeMode == LateJudgeMode.Presence ? "门口抓拍 · 出现判定" : "教室抓拍 · 缺席判定")}　" +
            $"抓拍 {record.PhotoCount} 张　人脸 {record.FaceCount} 张　应到 {record.ExpectedCount} 人　" +
            $"识别到 {record.Recognized.Count} 人　耗时 {record.ElapsedSeconds:F1} 秒";

        if (!record.Success)
        {
            TextRunLate.Text = "失败原因：" + record.Message;
            return;
        }

        var text = record.Late.Count == 0
            ? "迟到名单：无 ✅"
            : $"迟到名单（{record.Late.Count} 人）：{string.Join("、", record.Late.Select(x => x.Name))}";
        if (!string.IsNullOrWhiteSpace(record.Message))
        {
            text += "\n备注：" + record.Message;
        }

        TextRunLate.Text = text;
    });

    private async void ButtonRunNow_OnClick(object? sender, RoutedEventArgs e)
    {
        if (Busy.IsBusy)
        {
            return;
        }

        Busy.IsBusy = true;
        TextRunStatus.Text = "正在执行…";
        _log.Info("手动触发一次识别。");

        try
        {
            await _attendance.RunCheckAsync(true);
        }
        finally
        {
            Busy.IsBusy = false;
            RefreshStatus();
        }
    }

    private void ButtonRefreshStatus_OnClick(object? sender, RoutedEventArgs e) => RefreshStatus();
}
