using System.IO;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.Plugin.FaceLate.Controls;
using ClassIsland.Plugin.FaceLate.Models;
using ClassIsland.Plugin.FaceLate.Services;
using ClassIsland.Plugin.FaceLate.Services.Automations;
using ClassIsland.Plugin.FaceLate.Views.SettingsPages;
using ClassIsland.Shared.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassIsland.Plugin.FaceLate;

/// <summary>
/// 插件入口。
/// </summary>
[PluginEntrance]
public class Plugin : PluginBase
{
    /// <summary>
    /// 插件设置。
    /// </summary>
    public FaceLateSettings Settings { get; private set; } = new();

    /// <inheritdoc />
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        // 把插件配置目录记下来，后面所有数据（花名册、模型、抓拍照片）都放在这里。
        FaceLatePaths.PluginConfigFolder = PluginConfigFolder;
        Directory.CreateDirectory(PluginConfigFolder);

        var configPath = Path.Combine(PluginConfigFolder, "Settings.json");
        Settings = ConfigureFileHelper.LoadConfig<FaceLateSettings>(configPath);
        Settings.PropertyChanged += (_, _) => ConfigureFileHelper.SaveConfig(configPath, Settings);

        // 旧版只有「一个时间 + 只在周一至周五执行」，这里把它迁移成按星期的时间计划。
        Settings.NormalizeSchedule();

        // —— 服务 ——
        services.AddSingleton(Settings);
        // 运行日志：所有页面共用一份，默认折叠、细节默认隐藏。
        services.AddSingleton<ActivityLog>();
        services.AddSingleton<FaceModelProvider>();
        services.AddSingleton<FaceEngine>();
        services.AddSingleton<CameraCapture>();
        services.AddSingleton<RosterService>();
        services.AddSingleton<AttendanceService>();
        services.AddSingleton<EnrollmentService>();

        // 定时考勤：既注册成单例，也作为托管服务在应用启动时自动开始跑。
        services.AddSingleton<LateCheckScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<LateCheckScheduler>());

        // —— 自动化行动 ——
        // 挂到规则集上就能跟着课表/规则跑（晚自习、临时调课、周末托管都靠它）。
        services.AddAction<RunFaceLateCheckAction>();

        // —— 主界面组件 ——
        services.AddComponent<LateListComponent, LateListComponentSettingsControl>();

        // —— 设置界面 ——
        // 先把设置页收进一个分组，再注册各个子页。
        // 这样左侧只出现一个可折叠的「人脸考勤」，而不是把一堆页面平铺在最外层。
        services.AddSettingsPageGroup(PluginGroupIds.FaceLate, "\uE722", "人脸考勤");
        services.AddSettingsPage<FaceLateSettingsPage>();   // 主设置
        services.AddSettingsPage<RosterSettingsPage>();     // 花名册
        services.AddSettingsPage<EnrollSettingsPage>();     // 批量导入
        services.AddSettingsPage<ModelSettingsPage>();      // 识别与模型
        services.AddSettingsPage<RunLogSettingsPage>();     // 运行与日志
    }
}
