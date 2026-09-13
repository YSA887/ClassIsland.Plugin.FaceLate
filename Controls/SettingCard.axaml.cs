using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace ClassIsland.Plugin.FaceLate.Controls;

/// <summary>
/// 一行一事的设置卡片：左边图标 + 标题 / 一行副标题，右边放调用方给的控件。
/// <para>
/// 用法不变：
/// <c>&lt;controls:SettingCard Title="启用" Subtitle="..." IconGlyph="&#xE713;"&gt;控件&lt;/controls:SettingCard&gt;</c>
/// </para>
/// <para>
/// <b>为什么是「模板化控件 + ControlTheme」，而不是带 XAML 内容的 UserControl？</b>
/// <see cref="UserControl"/> 的「自定义内容」本身就是 <see cref="ContentControl.Content"/>，
/// 于是它自己 XAML 里那棵树（卡片外框、图标、标题）和调用方写在标签里的那个控件
/// 抢的是同一个属性 —— 调用方的赋值发生在后，把前面那棵树整个覆盖掉。
/// 表现就是「卡片框、图标、标题全体消失，只剩右边一个光秃秃的开关」。
/// 走模板就没这个问题：模板负责画外观，<c>Content</c> 专门留给调用方。
/// </para>
/// </summary>
public class SettingCard : ContentControl
{
    private const string ThemeResourceKey = "FaceLateSettingCardTheme";

    private static readonly Uri ThemeUri =
        new("avares://ClassIsland.Plugin.FaceLate/Controls/SettingCard.axaml");

    private static readonly object ThemeLock = new();
    private static ControlTheme? _theme;
    private static bool _themeLoaded;

    /// <summary>标题。</summary>
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<SettingCard, string>(nameof(Title), "");

    /// <summary>副标题（一行小灰字，只说这个设置是干什么的）。</summary>
    public static readonly StyledProperty<string> SubtitleProperty =
        AvaloniaProperty.Register<SettingCard, string>(nameof(Subtitle), "");

    /// <summary>左侧图标（Segoe Fluent Icons 字形）。</summary>
    public static readonly StyledProperty<string> IconGlyphProperty =
        AvaloniaProperty.Register<SettingCard, string>(nameof(IconGlyph), "");

    /// <summary>有没有图标（模板里用它决定那一格显不显示）。</summary>
    public static readonly StyledProperty<bool> HasIconProperty =
        AvaloniaProperty.Register<SettingCard, bool>(nameof(HasIcon));

    /// <summary>有没有副标题（模板里用它决定那一行显不显示）。</summary>
    public static readonly StyledProperty<bool> HasSubtitleProperty =
        AvaloniaProperty.Register<SettingCard, bool>(nameof(HasSubtitle));

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public string IconGlyph
    {
        get => GetValue(IconGlyphProperty);
        set => SetValue(IconGlyphProperty, value);
    }

    public bool HasIcon
    {
        get => GetValue(HasIconProperty);
        private set => SetValue(HasIconProperty, value);
    }

    public bool HasSubtitle
    {
        get => GetValue(HasSubtitleProperty);
        private set => SetValue(HasSubtitleProperty, value);
    }

    public SettingCard()
    {
        // 把外观挂上去。主题字典在插件程序集里，这里自己取一次并缓存，
        // 不去动 Application.Current —— 插件初始化时机比 Application 更早，靠不住。
        var theme = ResolveTheme();
        if (theme != null)
        {
            Theme = theme;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IconGlyphProperty)
        {
            SetCurrentValue(HasIconProperty, !string.IsNullOrEmpty(IconGlyph));
        }
        else if (change.Property == SubtitleProperty)
        {
            SetCurrentValue(HasSubtitleProperty, !string.IsNullOrEmpty(Subtitle));
        }
    }

    private static ControlTheme? ResolveTheme()
    {
        if (_themeLoaded)
        {
            return _theme;
        }

        lock (ThemeLock)
        {
            if (_themeLoaded)
            {
                return _theme;
            }

            try
            {
                if (AvaloniaXamlLoader.Load(ThemeUri) is ResourceDictionary dict
                    && dict.TryGetResource(ThemeResourceKey, null, out var value)
                    && value is ControlTheme theme)
                {
                    _theme = theme;
                }
            }
            catch
            {
                // 主题加载失败就退化成默认外观，至少不炸。
                _theme = null;
            }

            _themeLoaded = true;
            return _theme;
        }
    }
}
