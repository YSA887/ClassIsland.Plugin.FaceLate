using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassIsland.Plugin.FaceLate.Services;

namespace ClassIsland.Plugin.FaceLate.Controls;

/// <summary>
/// 日志面板控件。
/// <para>
/// 拿日志的方式很直接：调用方把 <see cref="Control.DataContext"/> 设成
/// <see cref="ActivityLog"/> 实例即可（见各设置页构造函数）。
/// </para>
/// </summary>
public partial class ActivityLogView : UserControl
{
    public ActivityLogView()
    {
        InitializeComponent();
    }

    private ActivityLog? Log => DataContext as ActivityLog;

    private async void ButtonCopy_OnClick(object? sender, RoutedEventArgs e)
    {
        var log = Log;
        if (log == null)
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
        {
            return;
        }

        var text = log.Dump();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await clipboard.SetTextAsync(text);
        log.Info("日志已复制到剪贴板。");
    }

    private void ButtonClear_OnClick(object? sender, RoutedEventArgs e) => Log?.Clear();
}
