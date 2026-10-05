namespace Werewolf.Client.Share;

/// <summary>
/// 游戏元数据：界面显示名 / 图标 / 简介 / 人数上下限 /（可选）规则说明。
/// 由各游戏模块声明，服务器下发（gamelist / RoomInfoDto）给客户端；界面层只从这里取显示名与图标。
/// </summary>
public sealed class GameInfo
{
    /// <summary>机器名（<see cref="GameIds"/>）；协议与存储的唯一键。</summary>
    public string GameId { get; set; } = "";

    /// <summary>中文短名，如「狼人杀」。</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>单个 emoji，如「🐺」。</summary>
    public string Emoji { get; set; } = "";

    /// <summary>一句话简介（下拉 / 图鉴共用）。</summary>
    public string Tagline { get; set; } = "";

    /// <summary>开始一局所需最少人数。</summary>
    public int MinPlayers { get; set; }

    /// <summary>房间允许的最大人数。</summary>
    public int MaxPlayers { get; set; }

    /// <summary>可选：设置 / 图鉴展示的规则说明。</summary>
    public string? RulesText { get; set; }

    /// <summary>列表 / 标题里的「图标 + 显示名」，映射集中在此，避免两端拼写漂移。</summary>
    public string Label => string.IsNullOrEmpty(Emoji) ? DisplayName : $"{Emoji} {DisplayName}";
}
