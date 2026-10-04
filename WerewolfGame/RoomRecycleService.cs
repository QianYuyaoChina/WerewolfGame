using Microsoft.Extensions.Hosting;

namespace Werewolf.Server;

/// <summary>
/// 后台房间回收服务：用 <see cref="PeriodicTimer"/> 周期调用 <see cref="RoomManager.RecycleExpired"/>，
/// 把「无在线玩家且超过宽限期」的房间从注册表移除。
/// 采用 <see cref="BackgroundService"/>（而非在注册表构造里起线程）以保持注册表纯粹、可被测试直接 new。
/// </summary>
public sealed class RoomRecycleService : BackgroundService
{
    private readonly RoomManager _manager;
    private readonly RoomOptions _options;

    public RoomRecycleService(RoomManager manager, RoomOptions options)
    {
        _manager = manager;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.RecycleScanSeconds));
        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { _manager.RecycleExpired(); }
                catch { /* 单次回收失败不影响下一轮 */ }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
    }
}
