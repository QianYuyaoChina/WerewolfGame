using System.Windows.Threading;
using ClientGUI.Models;

namespace ClientGUI.ViewModels;

/// <summary>
/// 阶段倒计时：只做“阶段 → 剩余秒数 → 文案”的本地提示，服务器才是真正的裁决者。
/// </summary>
public sealed class PhaseCountdown
{
    private readonly DispatcherTimer _timer;
    private int _secondsLeft;

    /// <summary>倒计时文案变化；空串表示当前阶段不限时。</summary>
    public event Action<string>? TextChanged;

    public PhaseCountdown()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>进入新阶段：按阶段秒数启动（不限时的阶段直接停表）。</summary>
    public void Start(string phase)
    {
        _secondsLeft = Phases.CountdownSeconds(phase);
        if (_secondsLeft > 0) _timer.Start();
        else _timer.Stop();
        RaiseTextChanged();
    }

    public void Stop()
    {
        _timer.Stop();
        _secondsLeft = 0;
        RaiseTextChanged();
    }

    private void Tick()
    {
        if (_secondsLeft > 0) _secondsLeft--;
        RaiseTextChanged();
    }

    private void RaiseTextChanged()
        => TextChanged?.Invoke(_secondsLeft > 0 ? $"⏱ 剩余约 {_secondsLeft} 秒" : "");
}
