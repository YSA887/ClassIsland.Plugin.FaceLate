using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Media;
using ClassIsland.Plugin.FaceLate.Models;

namespace ClassIsland.Plugin.FaceLate.Services;

/// <summary>
/// 日志级别。
/// <para>
/// 刻意分成 5 级而不是 3 级：<see cref="Detail"/> 是「平时根本不用看」的技术细节
/// （模型路径、查找过的目录、单张照片的中间结果），默认折叠在「显示详细信息」开关后面，
/// 这样界面就不会被一堆路径刷屏，但排查问题时又一条都不少。
/// </para>
/// </summary>
public enum LogLevel
{
    /// <summary>技术细节，默认隐藏。</summary>
    Detail,

    /// <summary>普通进度信息。</summary>
    Info,

    /// <summary>成功。</summary>
    Success,

    /// <summary>需要注意，但不影响继续使用。</summary>
    Warn,

    /// <summary>失败。</summary>
    Error,
}

/// <summary>
/// 一条日志。
/// </summary>
public sealed class LogItem : ObservableObjectBase
{
    private bool _isVisible = true;

    /// <summary>发生时间。</summary>
    public DateTime Time { get; init; } = DateTime.Now;

    /// <summary>级别。</summary>
    public LogLevel Level { get; init; }

    /// <summary>正文。</summary>
    public string Text { get; init; } = "";

    /// <summary>时间文本，界面上做得很淡，不抢视线。</summary>
    public string TimeText => Time.ToString("HH:mm:ss");

    /// <summary>是否技术细节行。</summary>
    public bool IsDetail => Level == LogLevel.Detail;

    /// <summary>是否显示（由 <see cref="ActivityLog.ShowDetails"/> 控制）。</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => Set(ref _isVisible, value);
    }

    /// <summary>级别标记。普通信息不加标记，减少视觉噪音。</summary>
    public string Glyph => Level switch
    {
        LogLevel.Success => "\u2713",
        LogLevel.Warn => "!",
        LogLevel.Error => "\u2715",
        LogLevel.Detail => "\u00b7",
        _ => "",
    };

    /// <summary>
    /// 级别标记的颜色。
    /// <para>
    /// 全部取中间调的亮色，浅色和深色主题下都能看清；正文颜色不写死，
    /// 交给主题的 <c>Foreground</c> 继承，避免深色主题下变成黑字黑底。
    /// </para>
    /// </summary>
    public IBrush GlyphBrush => Level switch
    {
        LogLevel.Success => new SolidColorBrush(Color.Parse("#4CAF50")),
        LogLevel.Warn => new SolidColorBrush(Color.Parse("#E0A030")),
        LogLevel.Error => new SolidColorBrush(Color.Parse("#E5484D")),
        _ => new SolidColorBrush(Color.Parse("#9A9AA0")),
    };

    /// <summary>正文透明度：细节行压暗，不抢视线。</summary>
    public double TextOpacity => Level switch
    {
        LogLevel.Detail => 0.62,
        _ => 1.0,
    };
}

/// <summary>
/// 全局运行日志。所有页面对话共用同一份，界面只在需要时展开。
/// <para>
/// 之前每个页面各自维护一个 <c>TextBlock</c> 日志，进页面就把模型路径、查找目录
/// 等一大堆技术细节全铺出来，既繁琐又挡视野。现在改成：
/// 日志集中在这里、默认折叠、细节行默认隐藏、超过上限自动淘汰最旧的。
/// </para>
/// </summary>
public sealed class ActivityLog : ObservableObjectBase
{
    /// <summary>最多保留多少条。</summary>
    private const int MaxItems = 300;

    private bool _showDetails;
    private int _errorCount;
    private int _warnCount;

    /// <summary>全部日志（新旧顺序）。</summary>
    public ObservableCollection<LogItem> Items { get; } = new();

    /// <summary>
    /// 是否显示技术细节行。默认 **关闭**，这就是「日志不再刷屏」的关键开关。
    /// </summary>
    public bool ShowDetails
    {
        get => _showDetails;
        set
        {
            if (Set(ref _showDetails, value))
            {
                RefreshVisibility();
            }
        }
    }

    /// <summary>出现过多少次警告 / 错误，用于在折叠状态下给一个提示角标。</summary>
    public int ErrorCount
    {
        get => _errorCount;
        private set => Set(ref _errorCount, value);
    }

    /// <summary>警告次数。</summary>
    public int WarnCount
    {
        get => _warnCount;
        private set => Set(ref _warnCount, value);
    }

    /// <summary>折叠状态的标题，例如「运行日志 · 2 条警告」。</summary>
    public string HeaderText
    {
        get
        {
            if (ErrorCount > 0)
            {
                return $"运行日志（{ErrorCount} 条错误）";
            }

            return WarnCount > 0 ? $"运行日志（{WarnCount} 条提示）" : "运行日志";
        }
    }

    /// <summary>写一条普通信息。</summary>
    public void Info(string text) => Add(LogLevel.Info, text);

    /// <summary>写一条成功信息。</summary>
    public void Success(string text) => Add(LogLevel.Success, text);

    /// <summary>写一条警告。</summary>
    public void Warn(string text) => Add(LogLevel.Warn, text);

    /// <summary>写一条错误。</summary>
    public void Error(string text) => Add(LogLevel.Error, text);

    /// <summary>
    /// 写一条技术细节。默认不会显示在界面上，只有打开「显示详细信息」才看得到。
    /// </summary>
    public void Detail(string text) => Add(LogLevel.Detail, text);

    /// <summary>清空日志。</summary>
    public void Clear() => UiThread.Run(() =>
    {
        Items.Clear();
        ErrorCount = 0;
        WarnCount = 0;
        OnPropertyChanged(nameof(HeaderText));
    });

    /// <summary>把全部日志导出为纯文本，供复制到剪贴板或粘贴给他人排查。</summary>
    public string Dump()
    {
        var sb = new StringBuilder();
        foreach (var item in Items.ToList())
        {
            sb.Append('[').Append(item.TimeText).Append("] ");
            if (item.Level != LogLevel.Info)
            {
                sb.Append('[').Append(LevelName(item.Level)).Append("] ");
            }

            sb.AppendLine(item.Text);
        }

        return sb.ToString();
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Detail => "细节",
        LogLevel.Success => "成功",
        LogLevel.Warn => "提示",
        LogLevel.Error => "错误",
        _ => "信息",
    };

    private void Add(LogLevel level, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var item = new LogItem
        {
            Level = level,
            Text = text.Trim(),
            IsVisible = level != LogLevel.Detail || ShowDetails,
        };

        // 集合是绑定到界面的，必须在 UI 线程上改。
        UiThread.Run(() =>
        {
            Items.Add(item);
            while (Items.Count > MaxItems)
            {
                Items.RemoveAt(0);
            }

            if (level == LogLevel.Error)
            {
                ErrorCount++;
            }
            else if (level == LogLevel.Warn)
            {
                WarnCount++;
            }

            OnPropertyChanged(nameof(HeaderText));
        });
    }

    private void RefreshVisibility()
    {
        UiThread.Run(() =>
        {
            foreach (var item in Items)
            {
                item.IsVisible = !item.IsDetail || ShowDetails;
            }
        });
    }
}
