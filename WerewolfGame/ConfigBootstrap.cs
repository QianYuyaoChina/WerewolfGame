using System.Text;
using System.Text.Json;

namespace Werewolf.Server;

/// <summary>
/// 启动期的配置文件自愈：<c>appsettings.json</c> **缺失或损坏**时自动生成一份默认配置并给出警告，
/// 让服务器永远能起来（而不是静默用内置缺省值，或者因为一处手改坏了整个服务起不来）。
///
/// 设计取舍：
/// <list type="bullet">
///   <item>缺失 → 直接生成默认文件（这是「零配置即可跑」的正常路径，只提醒一次）；</item>
///   <item>损坏 → **先备份**成 <c>appsettings.json.bad-时间戳</c>，再生成默认文件，
///         绝不静默丢弃用户手写的配置；</item>
///   <item>生成的文件里带注释说明各节点用途，用户可以直接照着改。</item>
/// </list>
/// </summary>
public static class ConfigBootstrap
{
    /// <summary>配置文件名（与 ASP.NET Core 默认约定一致）。</summary>
    public const string FileName = "appsettings.json";

    /// <summary>
    /// 内置默认配置：与仓库里随源码发布的 <c>appsettings.json</c> 内容保持一致。
    /// 各游戏自己的规则节点（如狼人杀的 <c>GameRules</c>）走这里；游戏模块用
    /// <c>IGameConfig.GetSection&lt;T&gt;("节点名")</c> 自取，缺失时回退各自的内置默认值。
    /// </summary>
    public const string DefaultJson = """
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",

  "//Games": "游戏元数据由各游戏模块（IGameModule.Info）自声明，经反射自动发现；此节点仅作说明，非必需。",
  "Games": {
    "//note": "新增游戏需要：① 新增 Games/<Game>/<Game>GameModule.cs（反射自动收录）；② 实现 <Game>GameInstance；③ 声明 <Game>Ops + payload。组合根无需改动。"
  },

  "//GameRules": "狼人杀规则：时长（秒）、夜晚顺序、人数上下限。改这里即可调整玩法，无需改代码。",
  "GameRules": {
    "NightActionSeconds": 40,
    "VoteSeconds": 45,
    "DiscussionSeconds": 45,
    "HunterShotSeconds": 30,
    "MinPlayers": 4,
    "MaxPlayers": 12,
    "NightOrder": [ "丘比特", "守卫", "狼人", "女巫", "预言家" ]
  },

  "//Undercover": "谁是卧底规则：各阶段秒数、人数上下限、白板人数阈值、词库文件名（描述阶段按顺序轮流发言；局中不告知玩家自己的阵营）。",
  "Undercover": {
    "DescribeSeconds": 45,
    "VoteSeconds": 30,
    "MinPlayers": 3,
    "MaxPlayers": 12,
    "//BlankMinPlayers": "人数达到该值才加入白板（白板没有词，算卧底阵营）；设为 0 表示完全关闭白板。",
    "BlankMinPlayers": 7,
    "//BlankTwoMinPlayers": "人数达到该值时加入 2 个白板；须大于 BlankMinPlayers，否则只会有 1 个白板。",
    "BlankTwoMinPlayers": 10,
    "WordFile": "words.undercover.txt"
  },

  "//Room": "多房间：空房宽限期（秒）、服务器房间上限、回收扫描间隔（秒）。",
  "Room": {
    "EmptyGraceSeconds": 60,
    "MaxRooms": 100,
    "RecycleScanSeconds": 10
  }
}

""";

    /// <summary>
    /// 确保工作目录下存在可用的 <c>appsettings.json</c>；返回写过的动作描述（无需处理时返回 null）。
    /// 调用方应把它输出的消息打到控制台。
    /// </summary>
    public static IReadOnlyList<string> Ensure(string? contentRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(contentRoot) ? Directory.GetCurrentDirectory() : contentRoot!;
        var path = Path.Combine(root, FileName);
        var messages = new List<string>();

        if (!File.Exists(path))
        {
            TryWrite(path);
            messages.Add($"⚠️ 未找到 {FileName}，已自动生成一份默认配置：{path}");
            messages.Add("   如需调整玩法（时长 / 人数 / 词库路径等），直接编辑该文件后重启即可。");
            return messages;
        }

        // 存在但要能用：坏了就备份 + 重建，避免手改坏一处导致整台服务器起不来。
        if (!IsValidJson(path, out var error))
        {
            var backup = $"{path}.bad-{DateTime.Now:yyyyMMdd-HHmmss}";
            try
            {
                File.Move(path, backup, overwrite: true);
                TryWrite(path);
                messages.Add($"⚠️ {FileName} 不是合法的 JSON（{error}），已备份为 {Path.GetFileName(backup)} 并重新生成默认配置。");
                messages.Add("   请把原文件里需要的修改重新加回新配置。");
            }
            catch (Exception ex)
            {
                messages.Add($"⚠️ {FileName} 不是合法的 JSON（{error}），且无法备份/重建：{ex.Message}");
                messages.Add("   服务器将使用内置缺省值继续启动。");
            }
        }

        return messages;
    }

    /// <summary>尽力写默认配置；失败时静默（调用方会说明服务器仍能用内置缺省值启动）。</summary>
    private static void TryWrite(string path)
    {
        try
        {
            File.WriteAllText(path, DefaultJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            // 目录只读等情况：不阻塞启动，配置回退内置默认值。
        }
    }

    private static bool IsValidJson(string path, out string error)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
