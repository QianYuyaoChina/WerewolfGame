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

// 传输对象（PlayerDto / GameStateDto）统一定义在共享库 Werewolf.Client.Share，
// 服务端与两个客户端复用同一份，避免字段漂移。

public static class RoleInfo
{
    /// <summary>夜晚会“轮到行动”的标准角色，按默认行动顺序排列（守卫 → 狼人 → 女巫 → 预言家）。</summary>
    public static readonly IReadOnlyList<Role> NightRoles = new[]
    {
        Role.Guard, Role.Werewolf, Role.Witch, Role.Seer
    };

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

    /// <summary>把角色中文名解析回枚举（用于读取配置里的夜晚顺序）；无法识别返回 null。</summary>
    public static Role? TryParse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim();

        foreach (var role in Enum.GetValues<Role>())
        {
            if (string.Equals(Name(role), name, StringComparison.Ordinal)) return role;
        }
        return null;
    }

    /// <summary>
    /// 把配置里的夜晚顺序规范化：过滤未知角色、去重，并把配置遗漏的标准角色
    /// 按默认顺序补到末尾，保证夜晚流程不会因为配置写漏而跳过某个角色。
    /// </summary>
    public static List<Role> ResolveNightOrder(IEnumerable<string>? configured)
    {
        var order = new List<Role>();
        foreach (var name in configured ?? Enumerable.Empty<string>())
        {
            var role = TryParse(name);
            if (role.HasValue && !order.Contains(role.Value)) order.Add(role.Value);
        }

        foreach (var role in NightRoles)
            if (!order.Contains(role)) order.Add(role);

        return order;
    }
}