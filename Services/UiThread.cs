using Avalonia.Threading;

namespace ClassIsland.Plugin.FaceLate.Services;

/// <summary>
/// 把界面更新统一切回 Avalonia 的 UI 线程。
/// <para>
/// 考勤识别跑在后台线程（<see cref="AttendanceService"/> 里用 <c>Task.Run</c> 包住），
/// 而 Avalonia 的控件只允许在 UI 线程上修改，否则会抛
/// <c>InvalidOperationException: Call from invalid thread</c>。
/// 更麻烦的是 ClassIsland 捕获到插件抛出的异常后会把**整个插件自动禁用**，
/// 所以任何可能被后台线程触碰到的界面更新都必须经过这里。
/// </para>
/// </summary>
public static class UiThread
{
    /// <summary>
    /// 在 UI 线程上执行 <paramref name="action"/>；如果当前已经在 UI 线程上则立即执行。
    /// </summary>
    public static void Run(Action action)
    {
        bool onUiThread;
        try
        {
            onUiThread = Dispatcher.UIThread.CheckAccess();
        }
        catch
        {
            // 极端情况下（例如不在 Avalonia 环境里，单元测试/控制台宿主）没有调度器，
            // 这时直接执行，避免因为切线程反而把调用打挂。
            onUiThread = true;
        }

        if (onUiThread)
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    /// <summary>
    /// 在 UI 线程上**同步**执行 <paramref name="action"/> 并等它完成。
    /// <para>
    /// 用在「必须马上改完、后面还要读结果」的场合，典型就是往
    /// <c>ObservableCollection</c> 里增删元素：Avalonia 的列表控件是直接订阅
    /// <c>CollectionChanged</c> 的，从后台线程改集合会直接把界面打挂。
    /// </para>
    /// <para>
    /// 用 <c>Dispatcher.Invoke</c>（阻塞）而不是 <c>Post</c>（排队）是安全的：
    /// 调用方都是 «UI 线程 await 出来的后台任务»，UI 线程此时是空闲的，
    /// 不存在互相等待。
    /// </para>
    /// </summary>
    public static void RunSync(Action action)
    {
        bool onUiThread;
        try
        {
            onUiThread = Dispatcher.UIThread.CheckAccess();
        }
        catch
        {
            onUiThread = true;
        }

        if (onUiThread)
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Invoke(action);
        }
    }
}
