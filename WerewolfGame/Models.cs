namespace Werewolf.Server;

public enum Role { Villager, Werewolf, Seer, Witch, Hunter, Guard }

public enum Phase { Waiting, Night, Day, Discussion, Vote, HunterShoot, GameOver }

public class Player
{
    public string Username { get; set; } = "";   // 唯一标识
    public string ConnectionId { get; set; } = "";
    public bool IsConnected { get; set; } = true;
    public Role Role { get; set; }
    public bool IsAlive { get; set; } = true;
    public bool IsHost { get; set; }

    public bool HasAntidote { get; set; } = true;
    public bool HasPoison { get; set; } = true;
    public int LastGuardTarget { get; set; } = -1;

    public int NightTarget { get; set; } = -1;
    public string? NightActionType { get; set; }
    public bool NightActionSubmitted { get; set; }

    public int VoteTarget { get; set; } = -1;
    public bool VoteSubmitted { get; set; }

    public bool HunterCanShoot { get; set; }
    public bool HunterShotSubmitted { get; set; }

    public bool IsWolf => Role == Role.Werewolf;
}

// ---------- 传输对象 ----------

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

public class GameStateDto
{
    public string Phase { get; set; } = "";
    public int Day { get; set; }
    public int YourId { get; set; }
    public string YourRole { get; set; } = "";
    public string YourRoleDesc { get; set; } = "";
    public bool IsAlive { get; set; }
    public List<PlayerDto> Players { get; set; } = new();
}

public static class RoleInfo
{
    public static string Name(Role r) => r switch
    {
        Role.Villager => "平民",
        Role.Werewolf => "狼人",
        Role.Seer => "预言家",
        Role.Witch => "女巫",
        Role.Hunter => "猎人",
        Role.Guard => "守卫",
        _ => "未知"
    };

    public static string Desc(Role r) => r switch
    {
        Role.Villager => "没有特殊能力，靠推理与投票找出狼人。",
        Role.Werewolf => "夜晚与队友共同击杀一名玩家，白天伪装成好人。",
        Role.Seer => "每晚可查验一名玩家是狼人还是好人。",
        Role.Witch => "拥有解药与毒药各一瓶，各只能使用一次。",
        Role.Hunter => "被狼人击杀或被投票放逐时，可开枪带走一名玩家（被毒死不能开枪）。",
        Role.Guard => "每晚守护一名玩家免受狼人击杀，不能连续两晚守护同一人。",
        _ => ""
    };
}