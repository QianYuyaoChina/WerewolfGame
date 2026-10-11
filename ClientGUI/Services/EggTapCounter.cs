namespace ClientGUI.Services;

/// <summary>
/// 彩蛋触发判定：**滚动窗口内 N 次点击**（PRD 第 3 条：3 秒内点 5 下）。
///
/// 判定口径（架构 §8.1 / PRD 8.3）：
/// <list type="bullet">
///   <item>窗口锚点是「最近 N 次点击里最早的那一次」——不是「距上一次点击」；</item>
///   <item>第 N 击时 <c>(tN - t1) ≤ windowMs</c> 才触发；</item>
///   <item>次数不足就停手、或窗口超时 → **静默清零**，不给任何可见反馈；</item>
///   <item>触发后立即清零：第 N+1 击不会重复触发。</item>
/// </list>
///
/// 为什么不写成依赖 <c>DispatcherTimer</c> 的窗口类：这里的逻辑必须可单测。
/// 时间由调用方以毫秒传入（<c>Environment.TickCount64</c>），本类不读时钟、不碰 UI。
/// </summary>
public sealed class EggTapCounter
{
    /// <summary>触发所需的点击次数（默认 5）。</summary>
    public int RequiredTaps { get; }

    /// <summary>时间窗口（毫秒，默认 3000）。</summary>
    public int WindowMs { get; }

    /// <summary>当前窗口内已累计的点击次数（触发或超时后自动归 0 / 1）。</summary>
    public int Count { get; private set; }

    /// <summary>滚动窗口内的点击时间戳（最多保留 <see cref="RequiredTaps"/> 个）。</summary>
    private readonly List<long> _taps = new();

    /// <param name="requiredTaps">触发所需的点击次数。</param>
    /// <param name="windowMs">时间窗口（毫秒）。</param>
    public EggTapCounter(int requiredTaps = 5, int windowMs = 3000)
    {
        RequiredTaps = requiredTaps < 1 ? 1 : requiredTaps;
        WindowMs = windowMs < 1 ? 1 : windowMs;
    }

    /// <summary>
    /// 记一次点击。
    /// </summary>
    /// <param name="nowMs">本次点击的时间戳（毫秒，单调即可）。</param>
    /// <returns>true = 命中彩蛋条件（调用方据此弹出输入框）；false = 静默。</returns>
    public bool RegisterTap(long nowMs)
    {
        _taps.Add(nowMs);

        // 滚动窗口只保留最近 RequiredTaps 次：更早的点击不再是窗口锚点。
        while (_taps.Count > RequiredTaps) _taps.RemoveAt(0);

        // 窗口已过期：静默清零，并从「本次点击」重新开始（PRD 8.3 第一行）。
        if (_taps.Count > 1 && nowMs - _taps[0] > WindowMs)
        {
            _taps.Clear();
            _taps.Add(nowMs);
            Count = _taps.Count;
            return false;
        }

        if (_taps.Count < RequiredTaps)
        {
            Count = _taps.Count;
            return false;
        }

        var triggered = nowMs - _taps[0] <= WindowMs;
        if (triggered) Reset();   // 触发后清零：第 N+1 击不会重复弹
        else Count = _taps.Count;

        return triggered;
    }

    /// <summary>清零（窗口超时、触发之后、或界面主动放弃时使用）。</summary>
    public void Reset()
    {
        _taps.Clear();
        Count = 0;
    }
}
