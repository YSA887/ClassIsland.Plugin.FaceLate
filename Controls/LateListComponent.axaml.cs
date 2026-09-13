using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Attributes;
using ClassIsland.Plugin.FaceLate.Models;
using ClassIsland.Plugin.FaceLate.Services;
using ClassIsland.Shared.Enums;

namespace ClassIsland.Plugin.FaceLate.Controls;

/// <summary>
/// 主界面组件：显示本次早读考勤识别出的迟到同学。
/// </summary>
[ComponentInfo("7402F420-8082-4770-8509-7BF72202CA02", "早读迟到名单", "\uE8D4",
    "显示最近一次人脸考勤识别出的迟到同学；没有迟到时自动隐藏。")]
public partial class LateListComponent : ComponentBase<LateListComponentSettings>
{
    private readonly AttendanceService _attendance;
    private readonly ILessonsService _lessonsService;

    private bool _subscribed;
    private bool _settingsHooked;

    public LateListComponent(AttendanceService attendance, ILessonsService lessonsService)
    {
        _attendance = attendance;
        _lessonsService = lessonsService;
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe();
        Refresh();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Unsubscribe();
        base.OnDetachedFromVisualTree(e);
    }

    private void Subscribe()
    {
        if (_subscribed)
        {
            return;
        }

        _attendance.ResultChanged += OnSomethingChanged;
        _lessonsService.OnClass += OnSomethingChanged;
        _lessonsService.OnBreakingTime += OnSomethingChanged;
        _lessonsService.OnAfterSchool += OnSomethingChanged;
        _lessonsService.CurrentTimeStateChanged += OnSomethingChanged;
        _lessonsService.PostMainTimerTicked += OnSomethingChanged;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        _attendance.ResultChanged -= OnSomethingChanged;
        _lessonsService.OnClass -= OnSomethingChanged;
        _lessonsService.OnBreakingTime -= OnSomethingChanged;
        _lessonsService.OnAfterSchool -= OnSomethingChanged;
        _lessonsService.CurrentTimeStateChanged -= OnSomethingChanged;
        _lessonsService.PostMainTimerTicked -= OnSomethingChanged;
        _subscribed = false;
    }

    private void OnSomethingChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(Refresh);
    }

    private void HookSettings()
    {
        if (_settingsHooked || Settings == null)
        {
            return;
        }

        Settings.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
        _settingsHooked = true;
    }

    private void Refresh()
    {
        HookSettings();
        if (Settings == null)
        {
            return;
        }

        var record = _attendance.LastResult;

        TitleText.Text = string.IsNullOrWhiteSpace(Settings.Title) ? "早读迟到" : Settings.Title;

        // 名单
        var names = "";
        var lateCount = 0;
        if (record is { Success: true } && record.Late.Count > 0)
        {
            lateCount = record.Late.Count;
            var max = Settings.MaxDisplayNames;
            if (max > 0 && lateCount > max)
            {
                names = string.Join("、", record.Late.Take(max).Select(x => x.Name)) + $" 等 {lateCount} 人";
            }
            else
            {
                names = string.Join("、", record.Late.Select(x => x.Name));
            }
        }

        NamesText.Text = names;
        NamesText.IsVisible = names.Length > 0;

        CountBadge.IsVisible = lateCount > 0;
        CountText.Text = $"{lateCount} 人";

        // 状态行
        if (Settings.ShowStatusLine)
        {
            StatusText.Text = _attendance.StatusText;
            StatusText.IsVisible = true;
        }
        else
        {
            StatusText.IsVisible = false;
        }

        // 时间行
        if (Settings.ShowDetectTime && record is { Success: true })
        {
            TimeText.Text =
                $"识别时间 {record.CaptureTime:HH:mm:ss} · 抓拍 {record.PhotoCount} 张 · 检测到 {record.FaceCount} 张脸 · 耗时 {record.ElapsedSeconds:F1} 秒";
            TimeText.IsVisible = true;
        }
        else
        {
            TimeText.IsVisible = false;
        }

        // 可见性
        var visible = true;
        if (Settings.HideWhenEmpty && lateCount == 0)
        {
            visible = false;
        }

        var state = _lessonsService.CurrentState;
        if (Settings.HideWhenOnClass && state == TimeState.OnClass)
        {
            visible = false;
        }

        if (Settings.ShowOnlyOnBreak && state != TimeState.Breaking)
        {
            visible = false;
        }

        RootCard.IsVisible = visible;
    }
}
