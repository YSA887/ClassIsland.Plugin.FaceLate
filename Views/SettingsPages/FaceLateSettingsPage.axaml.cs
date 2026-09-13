using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Plugin.FaceLate.Models;
using ClassIsland.Plugin.FaceLate.Services;
using Microsoft.Extensions.Logging;

namespace ClassIsland.Plugin.FaceLate.Views.SettingsPages;

/// <summary>
/// 人脸考勤 · 主设置：启用、判定方式、按星期的时间计划、摄像头与抓拍、存档。
/// </summary>
[SettingsPageInfo("facelate.attendance.settings", "主设置", "\uE713", "\uE712")]
[Group(PluginGroupIds.FaceLate)]
public partial class FaceLateSettingsPage : SettingsPageBase
{
    private readonly AttendanceService _attendance;
    private readonly FaceModelProvider _models;
    private readonly RosterService _roster;
    private readonly ActivityLog _log;
    private readonly ILogger<FaceLateSettingsPage> _logger;
    private bool _initializing = true;

    /// <summary>插件设置，供 XAML 绑定。</summary>
    public FaceLateSettings Settings { get; }

    // —— NumericUpDown 的包装属性。
    // NumericUpDown.Value 是 decimal?，包装属性也用 decimal?，
    // 这样用户清空输入框时不会把 null 写回非空属性、也就不会触发绑定异常。

    public decimal? CameraIndexValue
    {
        get => Settings.CameraIndex;
        set => SetIfHasValue(value, v => Settings.CameraIndex = (int)v);
    }

    public decimal? CaptureWidthValue
    {
        get => Settings.CaptureWidth;
        set => SetIfHasValue(value, v => Settings.CaptureWidth = (int)v);
    }

    public decimal? CaptureHeightValue
    {
        get => Settings.CaptureHeight;
        set => SetIfHasValue(value, v => Settings.CaptureHeight = (int)v);
    }

    public decimal? ShotCountValue
    {
        get => Settings.ShotCount;
        set => SetIfHasValue(value, v => Settings.ShotCount = (int)v);
    }

    public decimal? ShotIntervalValue
    {
        get => Settings.ShotIntervalMs;
        set => SetIfHasValue(value, v => Settings.ShotIntervalMs = (int)v);
    }

    public decimal? WarmUpValue
    {
        get => Settings.WarmUpSeconds;
        set => SetIfHasValue(value, v => Settings.WarmUpSeconds = (int)v);
    }

    public decimal? GraceMinutes
    {
        get => Settings.GraceMinutes;
        set => SetIfHasValue(value, v => Settings.GraceMinutes = (int)v);
    }

    public decimal? SnapshotKeepDaysValue
    {
        get => Settings.SnapshotKeepDays;
        set => SetIfHasValue(value, v => Settings.SnapshotKeepDays = (int)v);
    }

    private static void SetIfHasValue(decimal? raw, Action<decimal> setter)
    {
        if (raw.HasValue)
        {
            setter(raw.Value);
        }
    }

    /// <summary>
    /// 顶边栏切换：同一时刻只显示一组设置。
    /// <para>
    /// 这里按 <c>sender</c> 判定，而不是去读每个 RadioButton 的 IsChecked ——
    /// 同组单选按钮在切换时，新按钮的 Checked 可能先于旧按钮被取消勾选触发，
    /// 那时旧按钮还是 IsChecked == true，会让上一页留在画面上（两页叠在一起）。
    /// </para>
    /// </summary>
    private void NavTab_OnChecked(object? sender, RoutedEventArgs e)
    {
        // 构造函数里 InitializeComponent 就会触发一次 Checked，此时各页字段还没赋值。
        if (PageBasic == null)
        {
            return;
        }

        var tab = sender as RadioButton;
        PageBasic.IsVisible = ReferenceEquals(tab, TabBasic);
        PageSchedule.IsVisible = ReferenceEquals(tab, TabSchedule);
        PageCamera.IsVisible = ReferenceEquals(tab, TabCamera);
        PageArchive.IsVisible = ReferenceEquals(tab, TabArchive);
    }

    public FaceLateSettingsPage(
        FaceLateSettings settings,
        AttendanceService attendance,
        FaceModelProvider models,
        RosterService roster,
        ActivityLog log,
        ILogger<FaceLateSettingsPage> logger)
    {
        Settings = settings;
        _attendance = attendance;
        _models = models;
        _roster = roster;
        _log = log;
        _logger = logger;

        // 保证时间计划一定是周一~周日 7 行（旧配置会在这里被自动迁移）。
        Settings.NormalizeSchedule();

        InitializeComponent();
        DataContext = this;
        LogView.DataContext = _log;

        ComboBoxJudgeMode.SelectedIndex = Settings.JudgeMode == LateJudgeMode.Presence ? 1 : 0;
        RefreshBanner();
        ReportModelStateOnce();
        _initializing = false;

        Settings.PropertyChanged += OnSettingsChanged;
        _roster.RosterChanged += OnRosterChanged;
        DetachedFromVisualTree += (_, _) =>
        {
            Settings.PropertyChanged -= OnSettingsChanged;
            _roster.RosterChanged -= OnRosterChanged;
        };
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // NumericUpDown 默认格式会显示成 0.00，这里统一改成整数/最多三位小数。
        foreach (var spinner in this.GetVisualDescendants().OfType<NumericUpDown>())
        {
            spinner.FormatString = "0.###";
        }
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => UiThread.Run(RefreshBanner);

    private void OnRosterChanged(object? sender, EventArgs e) => UiThread.Run(RefreshBanner);

    /// <summary>
    /// 刷新顶边栏右侧的状态，以及「时间计划」里的下次执行提示。
    /// </summary>
    private void RefreshBanner()
    {
        var status = _models.CheckStatus();
        var total = _roster.Students.Count;
        var withFace = _roster.Students.Count(x => x.Faces.Count > 0);

        TextBanner.Text = status.Ready
            ? $"模型就绪 · 已录入 {withFace}/{total} 人"
            : "模型未就绪，到「识别与模型」页处理";

        TextNextRun.Text = LateCheckScheduler.DescribeNextRun(Settings, DateTime.Now);
        TextPlan.Text = LateCheckScheduler.DescribePlan(Settings);
    }

    /// <summary>
    /// 进页面时把模型状态写进日志：正常就一行成功，异常就一行警告，
    /// 完整路径全部塞进「细节」，默认不显示。
    /// </summary>
    private void ReportModelStateOnce()
    {
        var status = _models.CheckStatus();
        if (status.Ready)
        {
            _log.Success(status.Summary);
        }
        else
        {
            _log.Warn(status.Summary + "到「识别与模型」页可以导入模型文件。");
        }

        _log.Detail(status.Detail);
        _log.Detail("时间计划：" + LateCheckScheduler.DescribePlan(Settings));
    }

    private void ComboBoxJudgeMode_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        Settings.JudgeMode = ComboBoxJudgeMode.SelectedIndex == 1 ? LateJudgeMode.Presence : LateJudgeMode.Absence;
    }

    // —— 时间计划的快捷操作 ——

    private void MenuEnableWeekdays_OnClick(object? sender, RoutedEventArgs e)
        => SetEnabledForDays(new[]
        {
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday,
        }, true);

    private void MenuEnableWeekend_OnClick(object? sender, RoutedEventArgs e)
        => SetEnabledForDays(new[] { DayOfWeek.Saturday, DayOfWeek.Sunday }, true);

    private void MenuEnableAll_OnClick(object? sender, RoutedEventArgs e)
        => SetEnabledForDays(Enum.GetValues<DayOfWeek>(), true);

    private void MenuDisableAll_OnClick(object? sender, RoutedEventArgs e)
        => SetEnabledForDays(Enum.GetValues<DayOfWeek>(), false);

    private void SetEnabledForDays(IEnumerable<DayOfWeek> days, bool enabled)
    {
        var set = days.ToHashSet();
        foreach (var item in Settings.WeekSchedule.Where(x => set.Contains(x.Day)))
        {
            item.Enabled = enabled;
        }

        RefreshBanner();
    }

    /// <summary>
    /// 把周一那一行的时间套用到其余各天。平时和周末如果时间一样，就不用一行行敲。
    /// </summary>
    private void MenuCopyMondayTime_OnClick(object? sender, RoutedEventArgs e)
    {
        var monday = Settings.WeekSchedule.FirstOrDefault(x => x.Day == DayOfWeek.Monday);
        if (monday == null || string.IsNullOrWhiteSpace(monday.Times))
        {
            _log.Warn("周一那一行还没有填时间，没法套用。");
            return;
        }

        foreach (var item in Settings.WeekSchedule.Where(x => x.Day != DayOfWeek.Monday))
        {
            item.Times = monday.Times;
        }

        _log.Info($"已把周一的时间（{monday.Times}）套用到其余各天。");
        RefreshBanner();
    }

    /// <summary>把当前时刻追加到某一行，方便先设个「一分钟后就跑」来试效果。</summary>
    private void ButtonAppendNow_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DayScheduleItem item })
        {
            return;
        }

        var now = DateTime.Now.ToString("HH:mm:ss");
        item.Times = string.IsNullOrWhiteSpace(item.Times) ? now : item.Times.TrimEnd(',', '，', ' ') + "," + now;
        item.Enabled = true;
        _log.Info($"{item.DayName}：已追加 {now}。");
        RefreshBanner();
    }

    private void ButtonClearDay_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DayScheduleItem item })
        {
            item.Times = "";
            RefreshBanner();
        }
    }

    // —— 摄像头 ——

    private async void MenuTestCamera_OnClick(object? sender, RoutedEventArgs e)
    {
        TextCameraStatus.Text = "正在打开摄像头…";
        BorderPreview.IsVisible = false;

        var (jpeg, message) = await _attendance.TestCameraAsync();
        TextCameraStatus.Text = message;

        if (jpeg == null)
        {
            _log.Warn(message);
            return;
        }

        try
        {
            using var stream = new MemoryStream(jpeg);
            ImagePreview.Source = new Bitmap(stream);
            BorderPreview.IsVisible = true;
            _log.Success("摄像头测试通过：" + message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FaceLate 显示预览图失败");
        }
    }

    private void MenuOpenSnapshots_OnClick(object? sender, RoutedEventArgs e)
    {
        var error = FaceLatePaths.OpenInExplorer(FaceLatePaths.EnsureDirectory(FaceLatePaths.SnapshotsFolder));
        if (error.Length > 0)
        {
            _log.Warn("打不开抓拍存档目录：" + error);
        }
    }
}
