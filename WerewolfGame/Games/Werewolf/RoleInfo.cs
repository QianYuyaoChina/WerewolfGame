namespace Werewolf.Server;

/// <summary>狼人杀阵营角色。警长在本实现中作为一种独立身份（只影响投票权重与警徽移交）。</summary>
public enum Role { Villager, Werewolf, Seer, Witch, Hunter, Guard, Cupid, Sheriff }

/// <summary>
/// 狼人杀角色元数据（名称 / 说明 / 夜晚顺序解析 / 玩家标签颜色）。
/// 命名空间保持 <c>Werewolf.Server</c>（既有测试直接引用），但文件归属狼人杀模块。
/// </summary>
public static class RoleInfo
{
    /// <summary>夜晚会“轮到行动”的标准角色，按默认行动顺序排列（丘比特 → 守卫 → 狼人 → 女巫 → 预言家）。</summary>
    public static readonly IReadOnlyList<Role> NightRoles = new[]
    {
        Role.Cupid, Role.Guard, Role.Werewolf, Role.Witch, Role.Seer
    };

    /// <summary>只有第一夜行动的角色（丘比特）。</summary>
    public static bool IsFirstNightOnly(Role r) => r == Role.Cupid;

    public static string Name(Role r) => r switch
    {
        Role.Villager => "平民",
        Role.Werewolf => "狼人",
        Role.Seer => "预言家",
        Role.Witch => "女巫",
        Role.Hunter => "猎人",
        Role.Guard => "守卫",
        Role.Cupid => "丘比特",
        Role.Sheriff => "警长",
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
        Role.Cupid => "第一夜指定两名玩家成为情侣；一方出局，另一方立刻殉情。",
        Role.Sheriff => "投票时算 1.5 票；被淘汰时可把警徽移交给一名存活玩家。",
        _ => ""
    };

    /// <summary>
    /// 玩家行标签颜色（十六进制）。
    /// 沿用旧客户端 <c>PlayerRow.RoleBrush</c> 的配色（WPF 命名色对应值），保证观感零变化。
    /// </summary>
    public static string? ColorHex(Role r) => r switch
    {
        Role.Werewolf => "#FFA07A",   // Salmon
        Role.Seer => "#9370DB",       // MediumPurple
        Role.Witch => "#00FA9A",      // MediumSpringGreen
        Role.Hunter => "#F4A460",     // SandyBrown
        Role.Guard => "#87CEFA",      // LightSkyBlue
        Role.Cupid => "#FF7FB0",      // 粉（新）
        Role.Sheriff => "#FFD75E",    // 金（新）
        Role.Villager => "#DCDCDC",   // Gainsboro
        _ => null
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
