using System.Text.Json.Nodes;

namespace Werewolf.Client.Share;

/// <summary>
/// 加密通道上的“操作名”（客户端 → 服务器）。
/// 不再为每个动作开一个 SignalR 方法，而是统一走单一加密入口 <c>Call</c>。
/// </summary>
public static class Ops
{
    /// <summary>账号校验（首次即注册）。</summary>
    public const string Join = "join";

    /// <summary>
    /// 通用游戏生命周期：房主开局（对所有游戏通用）。框架理解它，但不理解任何具体游戏的玩法。
    /// </summary>
    public const string Start = "start";

    public const string Chat = "chat";

    /// <summary>心跳：用于测量往返时延并探测“半开连接”。</summary>
    public const string Ping = "ping";

    // ---------------- 房间操作（多房间改造新增） ----------------

    /// <summary>查询服务器上的房间列表（只读）。</summary>
    public const string RoomList = "roomlist";

    // ---------------- 房主设置（v2.1） ----------------

    /// <summary>房主拉取本房间的设置（最大人数 / 是否加密 / 邀请令牌 / 身份布局）。仅房主可用。</summary>
    public const string HostSettings = "hostsettings";

    /// <summary>房主修改房间设置（最大人数 / 密码 / 身份布局）。仅房主可用。</summary>
    public const string RoomSettings = "roomsettings";

    /// <summary>房主作废并重新生成邀请令牌（旧的邀请码随即失效）。</summary>
    public const string ResetInvite = "resetinvite";

    /// <summary>创建房间并成为房主。</summary>
    public const string CreateRoom = "createroom";

    /// <summary>按房间号加入（加密房间需密码）。</summary>
    public const string JoinRoom = "joinroom";

    /// <summary>离开当前房间（不拆连接）。</summary>
    public const string LeaveRoom = "leaveroom";

    /// <summary>
    /// 主动拉取一次「我视角的房间状态」。
    /// 房间状态平时靠服务器推送，但进房（createroom/joinroom）发生在新界面创建之前时，
    /// 那次广播没人接收，新界面就会一直显示为空——用这个操作补一次初始状态。
    /// </summary>
    public const string State = "roomstate";

    /// <summary>查询「服务器当前可用游戏」（只读）。</summary>
    public const string GameList = "gamelist";

    // ---------------- 账号与彩蛋（v2.5.0） ----------------

    /// <summary>查询自己的会话身份（用户名 / 是否管理员 / 当前称号）。</summary>
    public const string Session = "session";

    /// <summary>设置 / 清除**自己**的称号（请求里不带用户名，服务端按会话身份写入）。</summary>
    public const string SetTitle = "settitle";

    /// <summary>彩蛋口令：内容由服务端返回，客户端不内嵌任何彩蛋文案。</summary>
    public const string Egg = "egg";

    // ---------------- 密码与服务器数据资源（v2.5.0 第 5 / 7 条） ----------------

    /// <summary>修改**自己**的密码（旧密码 + 新密码）。请求里不带用户名，服务端按会话身份处理。</summary>
    public const string ChangePassword = "changepassword";

    /// <summary>可编辑数据资源清单（**仅管理员**）。</summary>
    public const string DataResources = "dataresources";

    /// <summary>取某个可编辑资源的全文（**仅管理员**）。</summary>
    public const string DataResourceGet = "dataresourceget";

    /// <summary>整份提交某个可编辑资源，原子落盘（**仅管理员**）。</summary>
    public const string DataResourceSave = "dataresourcesave";

    // ⚠️ 注意：night / vote / hunter 不再是框架 op；它们由狼人杀模块自己的
    //    WerewolfOps 声明，走 GameHub 的「默认分支」路由到当前房间的游戏实例。
}

/// <summary>服务器推送类型（服务器 → 客户端）。</summary>
public static class PushTypes
{
    public const string State = "state";
    public const string Message = "message";
}

/// <summary>统一文案（三端复用，避免前后台拼写漂移）。</summary>
public static class ServerMessages
{
    public const string Ok = "ok";
    public const string NotLoggedIn = "尚未登录，请先登录";
    public const string NotInRoom = "你尚未加入任何房间";
    public const string RoomNotFound = "房间不存在或已关闭";
    public const string WrongPassword = "密码错误";
    public const string GameInProgress = "游戏进行中，无法加入";
    public const string OnlyHostCanStart = "只有房主可以开始游戏";
    public const string TooManyRooms = "房间数已达上限，请稍后再试";
    public const string AlreadyInRoom = "该账号已在房间中";

    /// <summary>createroom 的 GameId 服务器不支持。</summary>
    public const string GameUnavailable = "该游戏不可用";

    /// <summary>gamelist 不可用（理论不出现，兜底）。</summary>
    public const string GameListFailed = "无法获取游戏列表";

    /// <summary>出局玩家尝试发言 / 操作。</summary>
    public const string Dead = "你已出局，无法发言。";

    /// <summary>轮流发言的玩法里，没轮到的玩家抢着发言（v2.3.1）。</summary>
    public const string NotYourTurn = "还没轮到你发言。";

    // ---------------- 房主设置（v2.1） ----------------

    /// <summary>非房主尝试修改房间设置。</summary>
    public const string OnlyHostCanEdit = "只有房主可以修改房间设置";

    /// <summary>游戏进行中不允许修改该设置（人数 / 身份布局）。</summary>
    public const string SettingLockedInGame = "游戏进行中，无法修改该设置";

    /// <summary>邀请令牌无效（邀请码已失效或被重置）。</summary>
    public const string InviteExpired = "邀请码已失效，请向房主要一个新的";

    // ---------------- 管理员与账号（v2.5.0） ----------------

    /// <summary>非管理员发起管理员请求。</summary>
    public const string AdminOnly = "仅管理员可操作";

    /// <summary>普通注册流程抢占了管理员保留用户名。</summary>
    public const string AdminReserved = "该用户名为系统保留，请换一个";

    // ---------------- 称号（v2.5.0 第 4 条） ----------------

    /// <summary>称号已保存。</summary>
    public const string TitleSaved = "称号已保存";

    /// <summary>称号已清除。</summary>
    public const string TitleCleared = "称号已清除";

    /// <summary>称号过长。</summary>
    public const string TitleTooLong = "称号太长（最多 8 个汉字或 16 个字符）";

    /// <summary>称号含换行或控制字符。</summary>
    public const string TitleBadChars = "称号不能包含换行或控制字符";

    // ---------------- 彩蛋（v2.5.0 第 3 条） ----------------

    /// <summary>口令未命中时的占位文案（**不报错**，内容待定）。</summary>
    public const string EggPending = "这个彩蛋还在准备中 🐣";

    /// <summary>命中 surprise：称号编辑已解锁。</summary>
    public const string EggTitleUnlocked = "称号编辑已解锁";

    /// <summary>与服务器不通（客户端侧兜底文案，不弹异常堆栈）。</summary>
    public const string NetworkUnreachable = "连不上服务器，请检查网络后重试";

    // ---------------- 修改密码（v2.5.0 第 7 条，TODO T7：规则常量单一来源） ----------------

    /// <summary>旧密码不正确。</summary>
    public const string OldPasswordWrong = "旧密码错误";

    /// <summary>新密码与旧密码相同（等于没改）。</summary>
    public const string PasswordSameAsOld = "新密码不能与旧密码相同";

    /// <summary>新密码与确认密码不一致（客户端就地提示，不发请求）。</summary>
    public const string PasswordMismatch = "两次输入的新密码不一致";

    /// <summary>修改成功（**不强制重登**，当前会话继续可用）。</summary>
    public const string PasswordChanged = "密码已修改，下次登录请用新密码";

    /// <summary>密码长度下限（服务端与客户端共用，避免两处各写一个数字）。</summary>
    public static string PasswordMinLength(int n) => $"密码至少 {n} 位";

    // ---------------- 服务器数据资源（v2.5.0 第 5 条） ----------------

    /// <summary>请求的资源不存在（或服务器当前没有可编辑资源）。</summary>
    public const string ResourceNotFound = "该数据资源不存在";

    /// <summary>提交的内容里一行有效内容都没有。</summary>
    public const string ResourceEmpty = "至少要有一行有效内容";

    /// <summary>保存失败（磁盘等原因），服务器上的内容未改动。</summary>
    public const string ResourceSaveFailed = "保存失败，服务器数据未改动";

    /// <summary>保存成功（含有效条数）。</summary>
    public static string ResourceSaved(int n) => $"已保存 {n} 条，下一局生效";

    /// <summary>某一行格式不对（整份不落盘）。</summary>
    public static string ResourceLineError(int line) => $"第 {line} 行格式不对，整份未保存";

    public static string RoomFull(int max) => $"房间已满（最多 {max} 人）";
}

/// <summary>
/// 密码规则的**唯一**口径（v2.5.0 第 7 条 / TODO T7）：
/// 三端（服务端账号存储、服务端 op 入口、客户端对话框）都从这里取，
/// 避免「服务端说 6 位、客户端说 3 位」这类前后台规则漂移。
/// </summary>
public static class PasswordRules
{
    /// <summary>密码长度下限（与既有注册口径一致）。</summary>
    public const int MinLength = 3;

    /// <summary>密码长度上限（防止有人塞一篇小说进来）。</summary>
    public const int MaxLength = 64;

    /// <summary>校验新密码的格式（长度）。返回 (是否通过, 错误文案)。</summary>
    public static (bool ok, string error) Validate(string? password)
    {
        var text = password ?? "";
        if (text.Length < MinLength) return (false, ServerMessages.PasswordMinLength(MinLength));
        if (text.Length > MaxLength) return (false, $"密码太长（最多 {MaxLength} 位）");
        return (true, "");
    }
}

/// <summary>房间在列表中的状态（服务端是唯一权威，客户端只做展示映射）。</summary>
public static class RoomStatus
{
    public const string Waiting = "Waiting";
    public const string Running = "Running";
    public const string GameOver = "GameOver";

    public static string Name(string status) => status switch
    {
        Waiting => "等待中",
        Running => "游戏进行中",
        GameOver => "已结束待重开",
        _ => status
    };
}

/// <summary>加密请求信封的明文结构。</summary>
public sealed class RpcRequest
{
    public string Op { get; set; } = "";
    public JsonNode? Payload { get; set; }
}

/// <summary>加密响应信封的明文结构。</summary>
public sealed class RpcReply
{
    public bool Ok { get; set; } = true;
    public string Msg { get; set; } = "";
    public JsonNode? Payload { get; set; }

    public static RpcReply Success(string msg = "") => new() { Ok = true, Msg = msg };
    public static RpcReply Error(string msg) => new() { Ok = false, Msg = msg };
}

/// <summary>加密推送信封的明文结构。</summary>
public sealed class PushMessage
{
    public string Type { get; set; } = "";
    public JsonNode? Payload { get; set; }
}

// ---------------- 握手（身份认证） ----------------

/// <summary>客户端握手上行：DH 公钥 + 客户端随机数。</summary>
public sealed class HandshakeHello
{
    public string ClientPublic { get; set; } = "";
    public string ClientNonce { get; set; } = "";
}

/// <summary>服务端握手下行：DH 公钥 + 服务端随机数 + 服务端身份校验值（HMAC）。</summary>
public sealed class HandshakeReply
{
    public string ServerPublic { get; set; } = "";
    public string ServerNonce { get; set; } = "";
    public string ServerProof { get; set; } = "";
}

// ---------------- 各操作的负载 ----------------

/// <summary>加入（登录/注册）：仅做账号校验，不再隐式进房。</summary>
public sealed class JoinPayload
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

/// <summary>夜间行动：<c>Action</c> 为 "do"/"heal"/"poison"/"none"。</summary>
public sealed class NightPayload
{
    public int TargetId { get; set; }
    public string Action { get; set; } = "do";
}

/// <summary>仅带一个目标编号的操作（投票 / 猎人开枪）。</summary>
public sealed class TargetPayload
{
    public int TargetId { get; set; }
}

/// <summary>聊天发言。</summary>
public sealed class ChatPayload
{
    public string Text { get; set; } = "";
}

/// <summary>服务器广播/私聊文本的负载。</summary>
public sealed class MessagePayload
{
    public string Text { get; set; } = "";
}

// ---------------- 房间请求负载 ----------------

/// <summary>创建房间。Name 可空 → 服务端回退 "&lt;用户名&gt; 的房间"；Password 空/null → 公开房间。</summary>
public sealed class CreateRoomPayload
{
    public string Name { get; set; } = "";
    public string? Password { get; set; }

    /// <summary>房间绑定的游戏机器名；null/空白 → 服务端回退 <see cref="GameIds.Werewolf"/>。</summary>
    public string? GameId { get; set; }
}

/// <summary>加入房间。RoomId 必填；Password 仅对加密房间有效。</summary>
public sealed class JoinRoomPayload
{
    public string RoomId { get; set; } = "";
    public string? Password { get; set; }

    /// <summary>
    /// 邀请令牌（来自邀请码）。令牌匹配时**跳过密码校验**，
    /// 这样房主即使改了房间密码，先前发出的邀请码依然有效。
    /// </summary>
    public string? InviteToken { get; set; }
}

// ---------------- 房主设置（v2.1） ----------------

/// <summary>房主视角的房间设置（含邀请令牌，因此**只发给房主**）。</summary>
public sealed class HostSettingsDto
{
    public string RoomId { get; set; } = "";
    public string RoomName { get; set; } = "";

    /// <summary>房间当前的最大人数（可能是房主设过的覆盖值）。</summary>
    public int MaxPlayers { get; set; }

    /// <summary>游戏允许的最小人数（下限，房主不能改到它以下）。</summary>
    public int MinPlayers { get; set; }

    /// <summary>游戏允许的最大人数（上限，房主不能改到它以上）。</summary>
    public int MaxPlayersLimit { get; set; }

    public bool HasPassword { get; set; }

    /// <summary>本房间的邀请令牌：房间生命周期内稳定，改密码 / 改人数都不会变。</summary>
    public string InviteToken { get; set; } = "";

    /// <summary>是否已开局（开局后人数与身份布局不可改）。</summary>
    public bool Running { get; set; }

    /// <summary>当前参与人数（用于校验身份布局）。</summary>
    public int PlayerCount { get; set; }

    /// <summary>当前游戏私有配置的原始 JSON（未自定义时为空）。由各游戏的「设置」界面解析。</summary>
    public string? GameSettingsJson { get; set; }

    /// <summary>游戏私有配置是否为房主自定义（false 表示跟随游戏的默认配置）。</summary>
    public bool CustomGameSettings { get; set; }

    // ---- 游戏身份（v2.3.1）：房主设置按游戏显示不同内容 ----

    /// <summary>本房间绑定的游戏机器名。</summary>
    public string GameId { get; set; } = "";

    /// <summary>游戏中文名（如「狼人杀」）。</summary>
    public string GameName { get; set; } = "";

    /// <summary>游戏图标 emoji（如「🐺」）。</summary>
    public string GameEmoji { get; set; } = "";

    /// <summary>游戏主题色（十六进制），用于房间标识。</summary>
    public string GameAccent { get; set; } = "#7C8AA5";

    /// <summary>
    /// 本游戏是否有「身份 / 玩法配置」页。false 时房主设置**隐藏**身份配置标签页
    /// （谁是卧底没有身份配置，不该显示狼人杀的）。
    /// </summary>
    public bool HasPlayerConfig { get; set; }
}

/// <summary>房主修改房间设置。只需要传要改的字段（null 表示不动）。</summary>
public sealed class UpdateRoomSettingsPayload
{
    /// <summary>新的最大人数；null 表示不改。</summary>
    public int? MaxPlayers { get; set; }

    /// <summary>新的房间密码；null 表示不改。</summary>
    public string? NewPassword { get; set; }

    /// <summary>true 表示改成公开房间（忽略 NewPassword）。</summary>
    public bool ClearPassword { get; set; }

    /// <summary>
    /// 本游戏的私有配置（不透明 JSON 字符串，由游戏自己解释；狼人杀放的是身份布局）。
    /// null 表示不改、空字符串表示恢复默认、其他值原样保存。
    /// </summary>
    public string? GameSettingsJson { get; set; }
}

/// <summary>丘比特第一夜指定两名玩家成为情侣。</summary>
public sealed class CupidPayload
{
    /// <summary>情侣中的第一位玩家编号。</summary>
    public int TargetA { get; set; }

    /// <summary>情侣中的第二位玩家编号。</summary>
    public int TargetB { get; set; }
}

/// <summary>
/// 离开房间。
/// <c>KeepRoom=true</c> 且房间内没有其他人时，房间被标记为「保留」（一直存在到服务器退出）；
/// 否则房间若已无人会被立即关闭。房间内还有其他成员时该字段无意义（房主会转让给下一位）。
/// </summary>
public sealed class LeaveRoomPayload
{
    public bool KeepRoom { get; set; }
}

// ---------------- 房间数据对象 ----------------

/// <summary>房间列表摘要（服务端 → 客户端）。</summary>
public sealed class RoomInfoDto
{
    /// <summary>5 位房间号（唯一标识）。</summary>
    public string Id { get; set; } = "";

    /// <summary>房间名（可重名）。</summary>
    public string Name { get; set; } = "";

    /// <summary>房间绑定的游戏机器名（<see cref="GameIds"/>）。</summary>
    public string GameId { get; set; } = GameIds.Werewolf;

    /// <summary>游戏显示名（冗余，便于列表即时渲染，无需再查游戏目录）。</summary>
    public string GameName { get; set; } = "";

    /// <summary>游戏图标 emoji（冗余）。</summary>
    public string GameEmoji { get; set; } = "";

    /// <summary>房间绑定的游戏主题色（十六进制）：用于在列表里一眼区分不同游戏的房间。</summary>
    public string GameAccent { get; set; } = "";

    /// <summary>名单人数。</summary>
    public int PlayerCount { get; set; }

    /// <summary>上限（来自该游戏的 <see cref="GameInfo.MaxPlayers"/>）。</summary>
    public int MaxPlayers { get; set; }

    /// <summary>"Waiting" | "Running" | "GameOver"（用 <see cref="RoomStatus"/>）。</summary>
    public string Status { get; set; } = "";

    /// <summary>是否加密。</summary>
    public bool HasPassword { get; set; }

    /// <summary>房主用户名（无则 ""）。</summary>
    public string Host { get; set; } = "";

    /// <summary>
    /// 房主的**称号**（v2.5.0 第 4 条）：随房间列表自然下发，零额外往返。空 = 无称号。
    /// </summary>
    public string HostTitle { get; set; } = "";
}

/// <summary>房间列表结果（同时用作 roomlist 的 Payload 与客户端返回类型）。</summary>
public sealed class RoomListResult
{
    /// <summary>客户端用；服务端 payload 中恒 true。</summary>
    public bool Ok { get; set; } = true;

    /// <summary>客户端用；服务端 payload 中恒 ""。</summary>
    public string Msg { get; set; } = "";

    public List<RoomInfoDto> Rooms { get; set; } = new();
}

/// <summary>createroom / joinroom 的 Payload。</summary>
public sealed class RoomActionResult
{
    public string RoomId { get; set; } = "";
    public bool Reconnected { get; set; }

    /// <summary>冗余一份摘要，便于客户端即时显示。</summary>
    public RoomInfoDto? Room { get; set; }
}
