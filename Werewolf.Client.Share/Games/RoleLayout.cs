namespace Werewolf.Client.Share;

/// <summary>
/// 一局的身份布局：每种身份的数量。键用 <see cref="Roles"/> 里的中文身份名（两端共用同一份真源）。
///
/// 房主可为自己的房间自定义布局；未自定义时按人数推导 <see cref="Default"/>（等价于改造前的内置比例，
/// 因此「不自定义」时行为与旧版完全一致）。
/// 开局时服务端会用 <see cref="ValidateForGame"/> 做**严格校验**并把最终配置公布给全房间。
/// </summary>
public sealed class RoleLayout
{
    /// <summary>身份名 → 数量（只保存数量 &gt; 0 的项，序列化更干净）。</summary>
    public Dictionary<string, int> Counts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>身份总数（应等于开局人数）。</summary>
    public int Total => Counts.Values.Sum();

    public int Get(string role)
        => Counts.TryGetValue(role, out var n) ? n : 0;

    /// <summary>设置数量；&lt;= 0 表示不启用该身份。</summary>
    public void Set(string role, int count)
    {
        if (string.IsNullOrWhiteSpace(role)) return;
        if (count <= 0) Counts.Remove(role);
        else Counts[role] = count;
    }

    public RoleLayout Clone()
    {
        var copy = new RoleLayout();
        foreach (var kv in Counts) copy.Counts[kv.Key] = kv.Value;
        return copy;
    }

    /// <summary>
    /// **按人数推导的默认布局**（房主未自定义时使用；也是「跟随人数自动配置」的依据）。
    ///
    /// 设计原则：随着人数增加**优先补神职而不是平民**，让每个阶段都有对应角色可用；
    /// 平民稳定在 2-3 个，避免「全是神职、没人可推理」或者「全是平民、夜晚无事发生」。
    ///
    ///  | 人数 | 狼 | 神职                                    | 民 |
    ///  |------|----|-----------------------------------------|----|
    ///  | 4    | 1  | 预言家                                    | 2  |
    ///  | 5    | 1  | 预言家、女巫                               | 2  |
    ///  | 6    | 2  | 预言家、女巫                               | 2  |
    ///  | 7    | 2  | 预言家、女巫、猎人                          | 2  |
    ///  | 8    | 2  | 预言家、女巫、猎人                          | 3  |
    ///  | 9    | 3  | 预言家、女巫、猎人、守卫                     | 2  |
    ///  | 10   | 3  | 预言家、女巫、猎人、守卫、丘比特               | 2  |
    ///  | 11   | 3  | 预言家、女巫、猎人、守卫、丘比特、警长          | 2  |
    ///  | 12   | 4  | 预言家、女巫、猎人、守卫、丘比特、警长          | 2  |
    ///
    /// 全部满足严格校验（总数 == 人数、狼人 ≥1 且少于好人）。
    /// </summary>
    public static RoleLayout Default(int playerCount)
    {
        var n = Math.Max(2, playerCount);
        var layout = new RoleLayout();

        // 极小局（管理员可以把最少人数配到 3）：1 狼 + 预言家 + 其余平民。
        // 注意不能把人数 clamp 到 4——否则 3 人局拿到的布局总数是 4，
        // 会在开局的「总数必须等于人数」校验里被自己拦下。
        if (n <= 4)
        {
            layout.Set(Roles.Werewolf, 1);
            if (n >= 3) layout.Set(Roles.Seer, 1);
            var smallVillagers = n - layout.Total;
            if (smallVillagers > 0) layout.Set(Roles.Villager, smallVillagers);
            return layout;
        }

        layout.Set(Roles.Werewolf, n switch { <= 5 => 1, <= 8 => 2, <= 11 => 3, _ => 4 });

        // 神职按「对玩法价值」排序，随人数逐个加入（阈值见上表）。
        layout.Set(Roles.Seer, 1);
        if (n >= 5) layout.Set(Roles.Witch, 1);
        if (n >= 7) layout.Set(Roles.Hunter, 1);
        if (n >= 9) layout.Set(Roles.Guard, 1);
        if (n >= 10) layout.Set(Roles.Cupid, 1);
        if (n >= 11) layout.Set(Roles.Sheriff, 1);

        var villagers = n - layout.Total;
        if (villagers > 0) layout.Set(Roles.Villager, villagers);

        return layout;
    }

    /// <summary>
    /// 配置期的结构校验（此时还不知道最终人数）：至少 1 名狼人、狼人少于好人。
    /// 人数相关的校验留给开局时的 <see cref="ValidateForGame"/>。
    /// </summary>
    public (bool ok, string msg) ValidateShape()
    {
        if (Total <= 0) return (false, "还没有配置任何身份。");

        var wolves = Get(Roles.Werewolf);
        if (wolves < 1) return (false, "至少要配置 1 名狼人。");
        if (wolves >= Total - wolves) return (false, "狼人数量必须少于好人数量。");

        foreach (var kv in Counts)
            if (kv.Value < 0) return (false, $"身份「{kv.Key}」的数量不能为负数。");

        if (Get(Roles.Sheriff) > 1) return (false, "警长最多只能有 1 名。");

        return (true, "");
    }

    /// <summary>开局前的严格校验：总数必须等于当前人数，且阵营比例合理。</summary>
    public (bool ok, string msg) ValidateForGame(int playerCount)
    {
        if (playerCount <= 0) return (false, "还没有玩家。");
        if (Total != playerCount)
            return (false, $"身份配置共 {Total} 个，与当前 {playerCount} 名玩家不一致，请调整身份配置。");

        var shape = ValidateShape();
        if (!shape.ok) return shape;

        return (true, "");
    }

    /// <summary>展示文案，如「🐺 狼人×2、🔮 预言家×1、👤 平民×2」。用于开局公布与界面常显。</summary>
    public string Describe()
    {
        var parts = new List<string>();
        foreach (var role in Roles.DisplayOrder)
        {
            var count = Get(role);
            if (count > 0) parts.Add($"{Roles.Emoji(role)} {role}×{count}");
        }

        // DisplayOrder 之外的自定义身份（理论上不会有）兜底排在后面。
        foreach (var kv in Counts.Where(kv => !Roles.DisplayOrder.Contains(kv.Key)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            parts.Add($"{kv.Key}×{kv.Value}");

        return parts.Count == 0 ? "（未配置）" : string.Join("、", parts);
    }
}
