namespace ClientGUI.Models;

/// <summary>一条版本更新记录。</summary>
public sealed class ChangelogEntry
{
    /// <summary>版本号，如 <c>1.3.0</c>。</summary>
    public required string Version { get; init; }

    /// <summary>发布日期（yyyy-MM-dd）。</summary>
    public required string Date { get; init; }

    /// <summary>该版本的一句话主题。</summary>
    public string Title { get; init; } = "";

    /// <summary>更新要点。</summary>
    public IReadOnlyList<string> Items { get; init; } = Array.Empty<string>();

    /// <summary>是否为最新版本（界面据此高亮版本徽章）。</summary>
    public bool IsLatest { get; init; }
}

/// <summary>客户端的版本历史（最新的排在最前）。</summary>
public static class Changelog
{
    public static IReadOnlyList<ChangelogEntry> All { get; } = new[]
    {
        new ChangelogEntry
        {
            Version = "1.5.1",
            Date = "2026-10-04",
            Title = "修复：房主标识丢失 / 保留房间不弹窗 / 邀请码无法复制",
            IsLatest = true,
            Items = new[]
            {
                "修复主界面可能空白：进房（建房 / 加入 / 邀请码）发生在界面创建之前时，服务器那次状态广播没人接收，导致玩家列表与房主标识一直是空的、开始游戏按钮被禁用。现在进房后会主动拉取一次房间状态，客户端也会缓存最近一次状态用于即时回填。",
                "修复房主独自退房时不弹「是否保留房间」：根因同上（房主标识一直是 false）。",
                "邀请码改用独立窗口展示（不再是弹窗）：文本框可选中，可 Ctrl+A 全选后手动复制。",
                "邀请码复制加入剪贴板重试（剪贴板被其他程序占用时会返回 CLIPBRD_E_CANT_OPEN，重试可显著提高成功率）；仍失败则明确提示手动复制。"
            }
        },
        new ChangelogEntry
        {
            Version = "1.5.0",
            Date = "2026-10-04",
            Title = "房主交接与房间保留 + 邀请码一键进房",
            Items = new[]
            {
                "房主退出房间时按「加入顺序」自动把房主转让给下一位（掉线同样处理），并广播「X 成为新房主」。",
                "房主退出且房间内已无其他玩家时，弹出「是否保留房间」：保留则房间一直存在到服务器关闭，不保留则服务端立即关闭房间。",
                "新增邀请码：房主一键生成（主窗口 Ctrl+I 或控制台 invite），把「服务器地址 + 房间号 + 房间密码」加密封装成一段可复制的码。",
                "邀请码免输密码：在登录窗口或房间列表粘贴即可直接进房；若码指向另一台服务器会自动切换并重新登录。",
                "服务端新增房间保留标记：被保留的空房不参与宽限期回收。"
            }
        },
        new ChangelogEntry
        {
            Version = "1.4.0",
            Date = "2026-10-04",
            Title = "握手身份认证 + 规则可配置化 + 断线韧性 + 界面记忆",
            Items = new[]
            {
                "握手加入预共享密钥（PSK）身份认证：在快速幂 DH 之上用 HMAC 双向校验，防止中间人攻击。",
                "会话密钥经「DH 共享密钥 + 双方随机数 + PSK」派生，不掌握 PSK 的第三方无法算出密钥。",
                "游戏规则外置到服务端 appsettings.json：夜晚顺序、各阶段时长、人数上下限均可配置，并随状态下发给客户端。",
                "客户端倒计时与「至少 N 人」提示改为跟随服务端规则，不再各自硬编码。",
                "断线自动重连改为无限退避重试（0/2/5/10/20 秒后每 30 秒一次），重连后自动重新握手并恢复身份。",
                "新增心跳：每 20 秒测一次往返时延，状态栏显示连接指示灯（已连接 / 重连中 / 未连接）与延迟。",
                "新增界面状态持久化：记住窗口尺寸位置、三栏显隐、上次服务器地址与用户名。"
            }
        },
        new ChangelogEntry
        {
            Version = "1.3.0",
            Date = "2026-10-04",
            Title = "主界面菜单化，新增「关于 / 更新日志」",
            Items = new[]
            {
                "主窗口新增「游戏 / 视图 / 帮助」菜单栏，标题栏上方独立成行。",
                "菜单集成常用功能：开始游戏、退出房间、退出程序。",
                "「视图」菜单可一键显示/隐藏玩家列表、房间消息、操作面板，并支持清空消息记录。",
                "新增「关于」窗口：产品信息、版本号与技术栈一览。",
                "新增「更新日志」窗口：按版本倒序查看改动要点。",
                "快捷键：F1 打开关于、Ctrl+Q 退出程序；聊天输入框回车发送保持不变。"
            }
        },
        new ChangelogEntry
        {
            Version = "1.2.0",
            Date = "2026-10-04",
            Title = "网络收发重构：快速幂密钥协商 + 会话加密",
            Items = new[]
            {
                "不再依赖 TLS，改为双方用快速幂 Diffie-Hellman 约定共享密钥。",
                "会话密钥经 HKDF-SHA256 派生，报文以 AES-GCM 加密信封收发（带完整性校验）。",
                "抽出共享核心库 Werewolf.Client.Share，GUI 与控制台客户端共用同一套网络层与 DTO。",
                "服务端只保留 Handshake + Call 两个加密入口，业务分发仍归 GameRoom。"
            }
        },
        new ChangelogEntry
        {
            Version = "1.1.0",
            Date = "2026-10-03",
            Title = "界面层重构与玩法校验",
            Items = new[]
            {
                "登录（服务器地址 / 用户名 / 密码）从主窗口抽离为独立登录窗口。",
                "主窗口拆分为玩家列表、房间消息、操作面板三个可复用组件。",
                "新增基于快速幂的加密算法，用于账号密码的哈希存储。",
                "服务端新增「未轮到不能放技能」校验：夜晚只接受当前行动角色的提交。"
            }
        },
        new ChangelogEntry
        {
            Version = "1.0.0",
            Date = "2026-10-02",
            Title = "首个版本",
            Items = new[]
            {
                "登录注册、房间大厅、房主开始游戏。",
                "夜晚行动：狼人刀人、预言家验人、女巫用药、守卫守护。",
                "白天讨论与投票放逐、猎人出局开枪。",
                "房间内实时消息与状态同步。"
            }
        }
    };
}
