namespace Werewolf.Client.Share;

/// <summary>与服务器 <c>PlayerDto</c> 对应的传输对象。</summary>
public class PlayerDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsAlive { get; set; }
    public bool IsHost { get; set; }
    public bool IsYou { get; set; }
    public bool IsConnected { get; set; }
    public string? Role { get; set; }
}

/// <summary>与服务器 <c>GameStateDto</c> 对应的传输对象。</summary>
public class GameStateDto
{
    public string Phase { get; set; } = "";
    public int Day { get; set; }
    public int YourId { get; set; }
    public string YourRole { get; set; } = "";
    public string YourRoleDesc { get; set; } = "";
    public bool IsAlive { get; set; }

    /// <summary>夜晚当前轮到行动的角色（中文名）；白天/等待/结束为空字符串。</summary>
    public string ActingRole { get; set; } = "";

    /// <summary>本局生效的规则（时长 / 夜晚顺序 / 人数上下限）；老客户端可能为 null。</summary>
    public GameRules? Rules { get; set; }

    /// <summary>当前所属房间号（5 位）。旧服务端可能为空字符串。</summary>
    public string RoomId { get; set; } = "";

    /// <summary>当前房间名。</summary>
    public string RoomName { get; set; } = "";

    /// <summary>当前房主用户名。</summary>
    public string HostName { get; set; } = "";

    public List<PlayerDto> Players { get; set; } = new();
}

/// <summary>服务器 <c>Join</c> / <c>Start</c> 的匿名返回值。</summary>
public class JoinResult
{
    public bool ok { get; set; }
    public string msg { get; set; } = "";
}
