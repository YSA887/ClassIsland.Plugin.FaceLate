using ClassIsland.Plugin.FaceLate.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassIsland.Plugin.FaceLate.Services;

/// <summary>
/// 定时器：到达设定时间时自动执行一次考勤。
/// <para>
/// 判定逻辑：每 1 秒轮询一次，取出「今天这个星期几」的计划，
/// 找到第一个「已经到点、还没执行过、且没超出补执行窗口」的时刻就执行。
/// 执行过的时刻会记进 <c>_ranKeys</c>，同一天同一个时刻不会重复执行。
/// </para>
/// <para>
/// 一天可以有多个时刻（例如周一 <c>07:20,19:00</c>，早读和晚自习各一次），
/// 这些时刻各自独立计数，互不影响。抓拍时间支持 <c>HH:mm</c> 与 <c>HH:mm:ss</c>。
/// </para>
/// </summary>
public sealed class LateCheckScheduler : IHostedService, IDisposable
{
    private readonly FaceLateSettings _settings;
    private readonly AttendanceService _attendance;
    private readonly ActivityLog _log;
    private readonly ILogger<LateCheckScheduler> _logger;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    /// <summary>已经执行过的时刻，键为 <c>yyyyMMdd + HH:mm:ss</c>。</summary>
    private readonly HashSet<string> _ranKeys = new();

    public LateCheckScheduler(
        FaceLateSettings settings,
        AttendanceService attendance,
        ActivityLog log,
        ILogger<LateCheckScheduler> logger)
    {
        _settings = settings;
        _attendance = attendance;
        _log = log;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
        _logger.LogInformation("FaceLate 定时考勤已启动：{Plan}", DescribePlan(_settings));
        _log.Info("自动考勤已启动。" + DescribePlan(_settings));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts == null)
        {
            return;
        }

        try
        {
            await _cts.CancelAsync();
        }
        catch
        {
            // 忽略
        }

        if (_loopTask != null)
        {
            try
            {
                await Task.WhenAny(_loopTask, Task.Delay(2000, cancellationToken));
            }
            catch
            {
                // 忽略
            }
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (TryConsumeTrigger(DateTime.Now, out var target, out var reason))
                {
                    _logger.LogInformation("FaceLate 触发自动考勤：{Reason}", reason);
                    _log.Info($"到达设定时间 {Format(target)}，开始自动考勤…");
                    await _attendance.RunCheckAsync(false, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                // 定时任务里任何异常都不能让循环退出，否则当天的考勤就再也不会有机会执行了。
                _logger.LogError(e, "FaceLate 定时考勤执行出错");
                _log.Error("定时考勤出错：" + e.Message);
            }

            try
            {
                // 1 秒一次：抓拍时间支持精确到秒，轮询太慢会错过目标秒。
                // 循环体只是几个条件判断，开销可以忽略。
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 判断此刻是否应该执行考勤；如果应该，会把该时刻登记为「已执行」。
    /// <para>登记在返回 <c>true</c> 的同一刻完成，避免识别还没跑完时下一个轮询又触发一次。</para>
    /// </summary>
    /// <param name="now">当前时间。</param>
    /// <param name="target">命中的抓拍时刻。</param>
    /// <param name="reason">触发原因，用于日志。</param>
    public bool TryConsumeTrigger(DateTime now, out TimeSpan target, out string reason)
    {
        target = TimeSpan.Zero;
        reason = "";

        if (!_settings.Enabled)
        {
            return false;
        }

        var today = DateOnly.FromDateTime(now);
        PruneOldKeys(today);

        var day = _settings.WeekSchedule.FirstOrDefault(x => x.Day == now.DayOfWeek);
        if (day == null)
        {
            return false;
        }

        if (!day.Enabled)
        {
            return false;
        }

        var times = day.ParseTimes();
        if (times.Count == 0)
        {
            return false;
        }

        var grace = TimeSpan.FromMinutes(Math.Max(1, _settings.GraceMinutes));
        foreach (var candidate in times)
        {
            if (_ranKeys.Contains(Key(today, candidate)))
            {
                continue;
            }

            var moment = now.Date.Add(candidate);
            if (now < moment)
            {
                break;
            }

            // 超出补执行窗口的时刻直接放弃，不再补做（避免中午启动时把早读那次也补上）。
            if (now - moment > grace)
            {
                continue;
            }

            _ranKeys.Add(Key(today, candidate));
            target = candidate;
            reason = $"已到抓拍时间 {Format(candidate)}（{day.DayName}）";
            return true;
        }

        return false;
    }

    private void PruneOldKeys(DateOnly today)
    {
        var prefix = today.ToString("yyyyMMdd");
        _ranKeys.RemoveWhere(k => !k.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static string Key(DateOnly date, TimeSpan time) => date.ToString("yyyyMMdd") + "-" + Format(time);

    /// <summary>把时间格式化成 HH:mm:ss，用于日志和界面提示。</summary>
    public static string Format(TimeSpan value)
        => $"{value.Hours:00}:{value.Minutes:00}:{value.Seconds:00}";

    /// <summary>
    /// 用一句话描述当前的时间计划，用于日志与界面提示。
    /// <para>
    /// 连着几天时间一样时会并成一段（「周一至周五 07:20:00」），
    /// 七天完全一样就直接写「每天」—— 否则一周七天各念一遍，一句话变成一屏。
    /// </para>
    /// </summary>
    public static string DescribePlan(FaceLateSettings settings)
    {
        var map = settings.WeekSchedule
            .Where(x => x.Enabled && x.ParseTimes().Count > 0)
            .ToDictionary(x => x.Day, x => x);

        if (map.Count == 0)
        {
            return "还没有任何一天设置了抓拍时间。";
        }

        // 周一 ~ 周日 固定顺序，方便把连续的日子合并
        var order = new[]
        {
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
            DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
        };

        var segments = new List<(int Start, int End, string Times)>();
        for (var i = 0; i < order.Length; i++)
        {
            if (!map.TryGetValue(order[i], out var item))
            {
                continue;
            }

            var times = DescribeTimes(item.ParseTimes());
            var end = i;
            while (end + 1 < order.Length
                   && map.TryGetValue(order[end + 1], out var next)
                   && DescribeTimes(next.ParseTimes()) == times)
            {
                end++;
            }

            segments.Add((i, end, times));
            i = end;
        }

        if (segments.Count == 1 && segments[0].Start == 0 && segments[0].End == order.Length - 1)
        {
            return $"每天 {segments[0].Times}。";
        }

        var parts = segments.Select(s => s.Start == s.End
            ? $"{DayNameOf(order[s.Start])} {s.Times}"
            : $"{DayNameOf(order[s.Start])}至{DayNameOf(order[s.End])} {s.Times}");

        return string.Join("；", parts) + "。";
    }

    private static string DescribeTimes(IReadOnlyList<TimeSpan> times)
        => string.Join("/", times.Select(Format));

    private static string DayNameOf(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日",
    };

    /// <summary>
    /// 算出下一次执行的时间，用于在设置页给出「下次将在……执行」的提示。
    /// </summary>
    public static string DescribeNextRun(FaceLateSettings settings, DateTime now)
    {
        if (!settings.Enabled)
        {
            return "自动考勤已关闭。";
        }

        for (var offset = 0; offset <= 7; offset++)
        {
            var day = now.Date.AddDays(offset);
            var item = settings.WeekSchedule.FirstOrDefault(x => x.Day == day.DayOfWeek);
            if (item == null || !item.Enabled)
            {
                continue;
            }

            foreach (var time in item.ParseTimes())
            {
                if (day.Add(time) > now)
                {
                    var prefix = offset switch
                    {
                        0 => "今天",
                        1 => "明天",
                        _ => item.DayName,
                    };
                    return $"下次执行：{prefix} {Format(time)}";
                }
            }
        }

        return "未来 7 天内没有安排执行。";
    }

    /// <summary>
    /// 解析 HH:mm / H:mm / HH:mm:ss / H:mm:ss 格式的时间。
    /// <para>秒是可选的；不写秒时按 0 秒处理，兼容旧版只填到分钟的配置。</para>
    /// </summary>
    public static bool TryParseTime(string? text, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().Split(':');
        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out var hour) || !int.TryParse(parts[1], out var minute))
        {
            return false;
        }

        var second = 0;
        // 允许写成 "07:20:" 这种末尾留空的格式
        if (parts.Length == 3 && !string.IsNullOrWhiteSpace(parts[2]) && !int.TryParse(parts[2], out second))
        {
            return false;
        }

        if (hour is < 0 or > 23 || minute is < 0 or > 59 || second is < 0 or > 59)
        {
            return false;
        }

        result = new TimeSpan(hour, minute, second);
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cts?.Dispose();
        _cts = null;
    }
}
