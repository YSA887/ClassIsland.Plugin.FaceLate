using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace ClassIsland.Plugin.FaceLate.Services;

/// <summary>
/// 摄像头采集封装。使用 OpenCV VideoCapture，Windows 下优先用 DirectShow，
/// 失败再依次回退到 MSMF 与自动选择，尽量兼容各类 USB / 笔记本内置摄像头。
/// </summary>
public sealed class CameraCapture : IDisposable
{
    private readonly ILogger<CameraCapture> _logger;
    private readonly object _sync = new();
    private VideoCapture? _capture;

    public CameraCapture(ILogger<CameraCapture> logger)
    {
        _logger = logger;
    }

    /// <summary>摄像头是否已打开。</summary>
    public bool IsOpen
    {
        get
        {
            lock (_sync)
            {
                return _capture is { } c && c.IsOpened();
            }
        }
    }

    /// <summary>当前实际使用的采集接口，用于在界面展示。</summary>
    public string CurrentBackend { get; private set; } = "";

    /// <summary>实际协商到的分辨率。</summary>
    public Size ActualSize { get; private set; }

    /// <summary>
    /// 打开摄像头。
    /// </summary>
    public bool Open(int index, int width, int height)
    {
        Close();

        var backends = new[] { VideoCaptureAPIs.DSHOW, VideoCaptureAPIs.MSMF, VideoCaptureAPIs.ANY };
        var names = new[] { "DirectShow", "MSMF", "Auto" };

        for (var i = 0; i < backends.Length; i++)
        {
            VideoCapture? candidate = null;
            try
            {
                candidate = new VideoCapture();
                if (!candidate.Open(index, backends[i]) || !candidate.IsOpened())
                {
                    candidate.Dispose();
                    continue;
                }

                _capture = candidate;
                CurrentBackend = names[i];
                break;
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "FaceLate 使用 {Backend} 打开摄像头 {Index} 失败", names[i], index);
                candidate?.Dispose();
            }
        }

        if (_capture == null)
        {
            _logger.LogError("FaceLate 无法打开摄像头 {Index}", index);
            return false;
        }

        try
        {
            _capture.Set(VideoCaptureProperties.FrameWidth, width);
            _capture.Set(VideoCaptureProperties.FrameHeight, height);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "FaceLate 设置摄像头分辨率失败，将使用默认分辨率");
        }

        try
        {
            ActualSize = new Size((int)_capture.Get(VideoCaptureProperties.FrameWidth),
                (int)_capture.Get(VideoCaptureProperties.FrameHeight));
        }
        catch
        {
            ActualSize = new Size(width, height);
        }

        return true;
    }

    /// <summary>
    /// 读取一帧并返回副本；失败返回 null。
    /// </summary>
    public Mat? Grab()
    {
        lock (_sync)
        {
            if (_capture == null || !_capture.IsOpened())
            {
                return null;
            }

            try
            {
                using var frame = new Mat();
                if (!_capture.Read(frame) || frame.Empty())
                {
                    return null;
                }

                return frame.Clone();
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "FaceLate 读取摄像头画面失败");
                return null;
            }
        }
    }

    /// <summary>
    /// 预热：丢掉若干帧，让自动曝光/白平衡稳定下来。
    /// </summary>
    public async Task WarmUpAsync(int seconds, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(0, seconds));
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            Grab()?.Dispose();
            await Task.Delay(60, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 连续抓拍多张照片。
    /// </summary>
    /// <param name="count">张数。</param>
    /// <param name="intervalMs">间隔毫秒。</param>
    /// <param name="onFrame">每抓到一张时的回调（可用于界面预览）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<List<Mat>> CaptureSeriesAsync(
        int count,
        int intervalMs,
        Action<Mat, int>? onFrame,
        CancellationToken cancellationToken)
    {
        var frames = new List<Mat>();
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var frame = Grab();
            if (frame != null)
            {
                frames.Add(frame);
                onFrame?.Invoke(frame, frames.Count);
            }

            if (i < count - 1 && intervalMs > 0)
            {
                await Task.Delay(intervalMs, cancellationToken).ConfigureAwait(false);
            }
        }

        return frames;
    }

    /// <summary>
    /// 关闭摄像头。
    /// </summary>
    public void Close()
    {
        lock (_sync)
        {
            try
            {
                _capture?.Release();
            }
            catch
            {
                // 忽略
            }

            _capture?.Dispose();
            _capture = null;
            CurrentBackend = "";
        }
    }

    /// <inheritdoc />
    public void Dispose() => Close();
}
