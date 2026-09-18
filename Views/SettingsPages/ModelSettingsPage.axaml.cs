using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Plugin.FaceLate.Models;
using ClassIsland.Plugin.FaceLate.Services;

namespace ClassIsland.Plugin.FaceLate.Views.SettingsPages;

/// <summary>
/// 模型下拉框里的一项。
/// </summary>
public sealed class RecognizerOption
{
    /// <summary>模型 Id（"auto" 表示自动）。</summary>
    public string Id { get; init; } = "";

    /// <summary>界面上显示的名字。</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>说明。</summary>
    public string Description { get; init; } = "";

    /// <summary>磁盘上有没有这个模型文件。</summary>
    public bool Present { get; init; }

    /// <summary>右侧的状态标签。</summary>
    public string StatusText => Present ? "已就绪" : "未下载";
}

/// <summary>
/// 人脸考勤 · 识别与模型：模型文件、识别参数。
/// <para>
/// 这里的「模型」是<b>可选项</b>而不是写死的两个分支：<see cref="FaceModelCatalog"/> 里
/// 有几款，下拉框里就有几项，另外加一个「自动」。用户既可以让插件自己挑，
/// 也可以手动指定，还可以一次把全部模型都下下来离线备用。
/// </para>
/// </summary>
[SettingsPageInfo("facelate.attendance.models", "识别与模型", "\uE9D9", "\uE9D8")]
[Group(PluginGroupIds.FaceLate)]
public partial class ModelSettingsPage : SettingsPageBase
{
    private readonly FaceEngine _engine;
    private readonly FaceModelProvider _models;
    private readonly ActivityLog _log;
    private bool _initializing;

    /// <summary>
    /// 上一次自检是不是没通过。
    /// <para>用来在「由好变坏」的那一刻往运行日志里记一条，而不是每次刷新页面都记。</para>
    /// </summary>
    private bool _selfTestFailed;

    /// <summary>正在由代码写入阈值输入框（避免把「同步显示」当成用户手动修改存进设置）。</summary>
    private bool _syncingThreshold;

    /// <summary>插件设置，供 XAML 绑定。</summary>
    public FaceLateSettings Settings { get; }

    /// <summary>忙状态，供按钮禁用绑定。</summary>
    public BusyState Busy { get; } = new();

    /// <summary>下拉框里的所有可选项。</summary>
    public ObservableCollection<RecognizerOption> RecognizerOptions { get; } = new();

    /// <summary>
    /// 当前模型下的比对阈值。
    /// <para>
    /// 注意它<b>跟着模型走</b>：读的是 <see cref="FaceEngine.EffectiveThreshold"/>，
    /// 写入时记到「当前模型」名下（<c>FaceLateSettings.SetThresholdForModel</c>），
    /// 所以切模型不会把 A 模型上调好的值带到 B 模型上。
    /// </para>
    /// </summary>
    public decimal? ThresholdValue
    {
        get => (decimal)Math.Round(_engine.EffectiveThreshold, 3);
        set => SetIfHasValue(value, v =>
        {
            if (_syncingThreshold)
            {
                return;
            }

            Settings.SetThresholdForModel(_engine.EffectiveModelId, (float)v);
            RefreshThresholdHint();
        });
    }

    public decimal? MinHitsValue
    {
        get => Settings.MinHits;
        set => SetIfHasValue(value, v => Settings.MinHits = (int)v);
    }

    public decimal? MinFaceWidthValue
    {
        get => Settings.MinFaceWidth;
        set => SetIfHasValue(value, v => Settings.MinFaceWidth = (int)v);
    }

    public decimal? MaxDurationValue
    {
        get => Settings.MaxDurationSeconds;
        set => SetIfHasValue(value, v => Settings.MaxDurationSeconds = (int)v);
    }

    /// <summary>下拉框当前选中的那一项。</summary>
    public RecognizerOption? SelectedRecognizer
    {
        get => RecognizerOptions.FirstOrDefault(x => x.Id == Settings.RecognizerModelId)
               ?? RecognizerOptions.FirstOrDefault(x => x.Id == FaceModelCatalog.AutoId);
        set
        {
            if (value == null || _initializing)
            {
                return;
            }

            Settings.RecognizerModelId = value.Id;
            TextRecognizerChosen.Text = value.Id == FaceModelCatalog.AutoId
                ? "自动：挑一款磁盘上已有的模型，优先精度高的那款。"
                : $"已选定「{value.DisplayName}」。" + (value.Present
                    ? "重新载入模型后生效。"
                    : "文件还没下载，可以点「下载全部模型」，或导入本地的 .onnx。");
        }
    }

    private static void SetIfHasValue(decimal? raw, Action<decimal> setter)
    {
        if (raw.HasValue)
        {
            setter(raw.Value);
        }
    }

    public ModelSettingsPage(
        FaceLateSettings settings,
        FaceEngine engine,
        FaceModelProvider models,
        ActivityLog log)
    {
        Settings = settings;
        _engine = engine;
        _models = models;
        _log = log;

        settings.NormalizeRecognizerModel();

        InitializeComponent();
        DataContext = this;
        LogView.DataContext = _log;

        BuildRecognizerOptions();
        RefreshModelStatus();
    }

    /// <summary>
    /// 刷新「比对阈值」那一行下面的说明。
    /// <para>设置页继承的是 Avalonia 的 UserControl，它的 <c>OnPropertyChanged</c> 是
    /// <c>AvaloniaPropertyChangedEventArgs</c> 版本，普通属性的通知在这里调不通，
    /// 所以直接写控件文本（和这个页面里其他地方保持一致）。</para>
    /// </summary>
    private void RefreshThresholdHint()
    {
        if (TextThresholdHint == null)
        {
            return;
        }

        var recommended = _engine.RecommendedThreshold;
        var current = _engine.EffectiveThreshold;
        var customized = Math.Abs(current - recommended) > 1e-6f;

        TextThresholdHint.Text =
            $"当前模型 {_engine.EffectiveModelName}，推荐 {recommended:F3}"
            + (customized ? $"，你改成了 {current:F3}" : "")
            + "。" + _engine.EffectiveThresholdNote + "。"
            + "调高更容易漏认，调低更容易认错。";
    }

    /// <summary>把阈值恢复成当前模型的推荐值。</summary>
    private void MenuResetThreshold_OnClick(object? sender, RoutedEventArgs e)
    {
        Settings.ResetThresholdForModel(_engine.EffectiveModelId);
        BoxThreshold.Value = (decimal)Math.Round(_engine.EffectiveThreshold, 3);
        RefreshThresholdHint();
        _log.Info($"比对阈值已恢复为「{_engine.EffectiveModelName}」的推荐值 {_engine.RecommendedThreshold:F3}。");
    }

    /// <summary>按当前的模型清单重建下拉框选项，「自动」永远排第一。</summary>
    private void BuildRecognizerOptions()
    {
        _initializing = true;
        try
        {
            RecognizerOptions.Clear();

            RecognizerOptions.Add(new RecognizerOption
            {
                Id = FaceModelCatalog.AutoId,
                DisplayName = "自动（推荐）",
                Description = "插件自己挑一款磁盘上已有的模型；优先选精度更高的那款。",
                Present = true,
            });

            foreach (var model in FaceModelCatalog.All)
            {
                var present = _models.FindExisting("", new[] { model.FileName }) != null;
                RecognizerOptions.Add(new RecognizerOption
                {
                    Id = model.Id,
                    DisplayName = model.DisplayName,
                    Description = $"{model.Description}（{model.FeatureLength} 维，约 {model.ApproxSizeMb:F0} MB）",
                    Present = present,
                });
            }

            // 选中的那一项要显式塞回下拉框。
            // SelectedItem 是双向绑定，它在 InitializeComponent 时就已经读过一次值了，
            // 那时 RecognizerOptions 还是空的（选项是构造函数的后面才建的），绑定拿到 null。
            // 之后往集合里 Add 只会刷新下拉列表，不会让 SelectedItem 重新读一次 ——
            // 不补这一行，下拉框就会一直空着，看着像没选中任何模型。
            ComboRecognizer.SelectedItem = SelectedRecognizer;
        }
        finally
        {
            _initializing = false;
        }
    }

    /// <summary>
    /// 只读地刷新模型状态。这里不会下载、不会载入，只是把「磁盘上到底有什么」摊出来。
    /// </summary>
    private void RefreshModelStatus()
    {
        var status = _models.CheckStatus();
        TextModelStatus.Text = status.Summary;
        TextModelDetail.Text = status.Detail;

        // 「文件都在」不等于「跑得起来」：路径含中文、缺 VC++ 运行库、宿主位数不对，
        // 都会让检测器在创建那一刻失败，而失败会被上层吞成「未检测到人脸」，
        // 光看文件状态永远查不出来。这里真建一次检测器再空跑一次，把真实状态摆出来。
        if (!_engine.IsReady)
        {
            TextBusyHint.Text = "模型尚未载入内存。";
            _selfTestFailed = false;
        }
        else
        {
            var (ok, message) = _engine.SelfTest();
            TextBusyHint.Text = ok ? message : "自检未通过：" + message;

            // 只在「由好变坏」时记一条，避免每次刷新页面都往日志里塞重复错误。
            if (!ok && !_selfTestFailed)
            {
                _selfTestFailed = true;
                _log.Error("模型自检未通过：" + message);
            }
            else if (ok)
            {
                _selfTestFailed = false;
            }
        }

        // 换了模型，阈值和说明都可能变，一起刷新（写输入框时打个标记，别当成用户改的）。
        _syncingThreshold = true;
        BoxThreshold.Value = (decimal)Math.Round(_engine.EffectiveThreshold, 3);
        _syncingThreshold = false;
        RefreshThresholdHint();

        // 顺手刷新每个模型的「已就绪 / 未下载」标签。
        BuildRecognizerOptions();
    }

    // —— 模型操作 ——

    private async void MenuLoadModel_OnClick(object? sender, RoutedEventArgs e)
    {
        Busy.IsBusy = true;
        TextBusyHint.Text = "正在载入模型…";
        try
        {
            // 载入跑在后台线程（FaceEngine.LoadAsync 内部已经 Task.Run），界面不会卡住。
            var (ok, message) = await _engine.LoadAsync();
            if (ok)
            {
                _log.Success($"模型已载入：{Path.GetFileName(_engine.DetectorPath)} / {Path.GetFileName(_engine.RecognizerPath)}");
                _log.Detail($"检测：{_engine.DetectorPath}\n识别：{_engine.RecognizerPath}");
            }
            else
            {
                _log.Error("载入模型失败：" + message);
            }
        }
        finally
        {
            Busy.IsBusy = false;
            RefreshModelStatus();
        }
    }

    private async void MenuImportDetector_OnClick(object? sender, RoutedEventArgs e)
        => await ImportModelAsync(isDetector: true);

    private async void MenuImportRecognizer_OnClick(object? sender, RoutedEventArgs e)
        => await ImportModelAsync(isDetector: false);

    /// <summary>
    /// 一次性把清单里所有还没下载的模型都下下来，之后换模型就不用联网了。
    /// </summary>
    private async void MenuDownloadAll_OnClick(object? sender, RoutedEventArgs e)
    {
        if (Busy.IsBusy)
        {
            return;
        }

        Busy.IsBusy = true;
        _log.Info("开始下载全部识别模型…");

        var progress = new Progress<string>(p => TextBusyHint.Text = p);

        try
        {
            var (downloaded, alreadyHad, failures) = await _models.DownloadAllAsync(progress);

            var parts = new List<string>();
            if (downloaded > 0)
            {
                parts.Add($"新下载 {downloaded} 个");
            }

            if (alreadyHad > 0)
            {
                parts.Add($"已有 {alreadyHad} 个");
            }

            if (failures.Count > 0)
            {
                _log.Warn("下载完成：" + string.Join("、", parts) + $"；失败：{string.Join("、", failures)}"
                          + "（学校网络通常访问不了 HuggingFace，可以改用「导入识别模型」从本地选文件）");
            }
            else
            {
                _log.Success("下载完成：" + string.Join("、", parts) + "。");
            }
        }
        finally
        {
            Busy.IsBusy = false;
            TextBusyHint.Text = "";
            RefreshModelStatus();
        }
    }

    private async Task ImportModelAsync(bool isDetector)
    {
        var path = await PickModelFileAsync();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        Busy.IsBusy = true;
        TextBusyHint.Text = "正在导入模型…";
        try
        {
            var (ok, message) = await _models.ImportAsync(path, isDetector);
            if (!ok)
            {
                _log.Error(message);
                return;
            }

            _log.Success(message);

            // 导入完立刻重新载入，用户不用再点一次。
            var (loaded, loadMessage) = await _engine.LoadAsync();
            if (loaded)
            {
                _log.Success($"模型已重新载入：{Path.GetFileName(_engine.DetectorPath)} / {Path.GetFileName(_engine.RecognizerPath)}");
                _log.Detail($"检测：{_engine.DetectorPath}\n识别：{_engine.RecognizerPath}");
            }
            else
            {
                _log.Error("重新载入失败：" + loadMessage);
            }
        }
        finally
        {
            Busy.IsBusy = false;
            TextBusyHint.Text = "";
            RefreshModelStatus();
        }
    }

    /// <summary>
    /// 把「当前能找到哪些模型、在哪儿找的」写进日志。
    /// 详情行默认不显示，需要时打开「显示详细信息」即可——不再占着界面。
    /// </summary>
    private void MenuScanModels_OnClick(object? sender, RoutedEventArgs e)
    {
        var status = _models.CheckStatus();
        if (status.Ready)
        {
            _log.Success(status.Summary);
        }
        else
        {
            _log.Warn(status.Summary + "到「模型操作」里导入 .onnx 文件即可。");
        }

        _log.Detail(status.Detail);

        // 顺便把清单里每个模型在磁盘上的情况都报一遍，方便排查。
        foreach (var model in FaceModelCatalog.All)
        {
            var found = _models.FindExisting("", new[] { model.FileName });
            _log.Detail($"  {(found != null ? "✔" : "✘")} {model.DisplayName} —— {found ?? "未找到 " + model.FileName}");
        }

        RefreshModelStatus();
    }

    private void MenuOpenModelsFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        var error = FaceLatePaths.OpenInExplorer(FaceLatePaths.EnsureDirectory(_models.ModelDirectory));
        if (error.Length > 0)
        {
            _log.Warn("打不开模型目录：" + error);
        }
    }

    private async Task<string?> PickModelFileAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return null;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择人脸模型文件（.onnx）",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("ONNX 模型")
                {
                    Patterns = new[] { "*.onnx" },
                },
                FilePickerFileTypes.All,
            },
        });

        return files.FirstOrDefault()?.TryGetLocalPath();
    }
}
