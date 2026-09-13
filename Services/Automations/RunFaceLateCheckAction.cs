using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;
using Microsoft.Extensions.Logging;

namespace ClassIsland.Plugin.FaceLate.Services.Automations;

/// <summary>
/// 自动化行动：执行一次人脸考勤。
/// <para>
/// 之所以要做成「行动」而不是只靠内置定时器：晚自习、临时调课、周末托管这些场合
/// 跟早读的规律完全不同，与其在插件里再堆一套时间表，不如直接挂到 ClassIsland 的
/// 自动化规则集上——由课表/规则去决定什么时候跑，插件只负责「跑一次」。
/// </para>
/// <para>
/// 用法：在自动化里新建规则集（例如「晚自习考勤」），触发条件随意
/// （到点、打完铃、上课等），行动里选「人脸考勤：执行一次考勤」即可。
/// </para>
/// </summary>
[ActionInfo("facelate.actions.runCheck", "人脸考勤：执行一次考勤", "\uE7C1", true, "")]
public class RunFaceLateCheckAction : ActionBase
{
    private readonly AttendanceService _attendance;
    private readonly ActivityLog _log;
    private readonly ILogger<RunFaceLateCheckAction> _logger;

    public RunFaceLateCheckAction(
        AttendanceService attendance,
        ActivityLog log,
        ILogger<RunFaceLateCheckAction> logger)
    {
        _attendance = attendance;
        _log = log;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task OnInvoke()
    {
        // 基类实现（目前为空，但官方文档要求重写时先调用一次，留着以防将来基类做生命周期处理）。
        await base.OnInvoke();

        _logger.LogInformation("FaceLate 由自动化规则触发考勤");
        _log.Info("自动化规则触发：开始考勤…");

        // manual: false —— 当作「自动考勤」处理，跟定时器触发走同一条路径。
        var record = await _attendance.RunCheckAsync(false).ConfigureAwait(false);

        if (!record.Success)
        {
            _logger.LogWarning("FaceLate 自动化触发的考勤未成功：{Message}", record.Message);
        }
    }
}
