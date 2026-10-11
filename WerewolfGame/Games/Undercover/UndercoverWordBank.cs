using System.Reflection;
using System.Text;

namespace Werewolf.Server.Games.Undercover;

/// <summary>
/// 谁是卧底的词库：从纯文本文件读取「平民词 / 卧底词」对。
///
/// **格式**（UTF-8 文本，见文件头注释）：
/// <code>
/// # 注释行（以 # 开头）
/// 可乐|雪碧            ← 平民词|卧底词
/// 牛奶|豆浆|饮料        ← 可选第三列：分类提示
/// </code>
///
/// **默认词库随源码维护**（项目根目录的 <c>words.undercover.txt</c>）：
/// 编译时既**嵌进程序集**（<see cref="EmbeddedResourceName"/>），也**复制到输出目录**。
/// 因此无论部署到哪个目录，工作目录里没有词库时都能用内置这份兜底生成一份
/// （与 <c>appsettings.json</c> 一样的自愈策略）。词库在**服务端**，客户端只会收到属于自己的那一个词。
/// </summary>
public static class UndercoverWordBank
{
    /// <summary>词库文件里的一对词。</summary>
    public sealed record Pair(string Civilian, string Undercover, string Category);

    /// <summary>
    /// 默认词库在程序集里的资源名（csproj 里用 <c>LogicalName</c> 固定，避免受根命名空间影响）。
    /// </summary>
    public const string EmbeddedResourceName = "words.undercover.txt";

    /// <summary>
    /// 示例词库内容（首次运行时生成；用户可以直接在此基础上增删）。
    /// </summary>
    public const string SampleContent = """
# ══════════════════ 谁是卧底 · 词库 ══════════════════
#
# 每行写一对词，用竖线 | 分隔：
#     平民词|卧底词
#     平民词|卧底词|分类      ← 第三列可选，只作为给玩家的分类提示
#
# 规则：
#   · 以 # 开头的是注释，空行会被忽略
#   · 平民词与卧底词要「相近但不同」，越像越好玩
#   · 至少要有 1 行；改完**重启服务器**生效
#   · 文件编码必须是 UTF-8
#
# ── 饮料 ─────────────────────────────
可乐|雪碧|饮料
牛奶|豆浆|饮料
橙汁|柠檬水|饮料
奶茶|咖啡|饮料
矿泉水|苏打水|饮料

# ── 水果 ─────────────────────────────
苹果|梨|水果
橘子|橙子|水果
葡萄|提子|水果
西瓜|哈密瓜|水果
草莓|樱桃|水果

# ── 食物 ─────────────────────────────
饺子|馄饨|食物
包子|馒头|食物
米饭|面条|食物
火锅|麻辣烫|食物
蛋糕|面包|食物
巧克力|太妃糖|食物

# ── 日用品 ───────────────────────────
牙刷|牙膏|日用品
洗发水|沐浴露|日用品
毛巾|浴巾|日用品
雨伞|雨衣|日用品
眼镜|墨镜|日用品

# ── 场所 / 交通 ──────────────────────
地铁|公交|交通
飞机|高铁|交通
医院|药店|场所
图书馆|书店|场所
电影院|剧院|场所

# ── 影视 / 游戏 ──────────────────────
电影|电视剧|影视
漫画|小说|读物
象棋|围棋|棋类
篮球|足球|运动
钢琴|电子琴|乐器
""";

    /// <summary>
    /// 默认词库文件名（相对服务器工作目录）。
    /// 「对局读取」与「管理员远程编辑」共用这一个常量，杜绝两处路径漂移（架构 D5）。
    /// </summary>
    public const string DefaultWordFile = "words.undercover.txt";

    /// <summary>
    /// 把「数据根目录 + 词库文件名」解析成绝对路径的**唯一**纯函数（架构 D5）。
    ///
    /// 对局读取（<see cref="UndercoverGameInstance"/>）与远程编辑（<c>UndercoverWordResource</c>）
    /// 都必须经由它，否则会出现「编辑改了 A 文件、下一局读的却是 B 文件」。
    /// </summary>
    /// <param name="dataRoot">数据根目录（服务器工作目录）。</param>
    /// <param name="wordFile">词库文件名；空 / 空白 → <see cref="DefaultWordFile"/>。</param>
    public static string ResolvePath(string dataRoot, string? wordFile)
    {
        var file = (wordFile ?? "").Trim();
        if (file.Length == 0) file = DefaultWordFile;

        if (Path.IsPathRooted(file)) return file;

        var root = (dataRoot ?? "").Trim();
        return root.Length == 0 ? file : Path.Combine(root, file);
    }

    /// <summary>
    /// 读取**编译进程序集**的默认词库（源码目录的 <c>words.undercover.txt</c>）；
    /// 资源不存在或读取失败时返回 null（此时回退到 <see cref="SampleContent"/>）。
    /// </summary>
    public static string? ReadEmbedded()
    {
        try
        {
            using var stream = typeof(UndercoverWordBank).Assembly
                .GetManifestResourceStream(EmbeddedResourceName);
            if (stream == null) return null;

            using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>默认词库内容：优先用嵌入的那份，取不到才回退到内置的示例。</summary>
    public static string DefaultContent => ReadEmbedded() ?? SampleContent;

    /// <summary>
    /// 确保词库文件存在；不存在就用**默认词库**生成一份并返回警告文案（已存在则返回 null）。
    /// </summary>
    public static string? EnsureSample(string path)
    {
        try
        {
            if (File.Exists(path)) return null;

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var embedded = ReadEmbedded();
            var content = embedded ?? SampleContent;
            var source = embedded != null ? "程序内置默认词库" : "示例词库";

            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return $"⚠️ 未找到谁是卧底的词库 {path}，已用{source}生成一份（共 {Count(content)} 对）。";
        }
        catch (Exception ex)
        {
            return $"⚠️ 无法生成谁是卧底词库 {path}：{ex.Message}";
        }
    }

    /// <summary>
    /// 读取词库：优先读指定文件；文件不存在时**直接用程序内置默认词库**（不写盘）。
    /// 这样即使工作目录只读（写不进词库文件），玩法也不会因为「词库为空」开不了局。
    /// </summary>
    public static List<Pair> LoadOrDefault(string path)
    {
        if (File.Exists(path))
        {
            var fromFile = Load(path);
            if (fromFile.Count > 0) return fromFile;
        }

        return ParseLines((ReadEmbedded() ?? SampleContent).Split('\n'));
    }

    /// <summary>读取词库文件；文件不存在或没有有效行时返回空列表。</summary>
    public static List<Pair> Load(string path)
        => File.Exists(path) ? ParseLines(File.ReadAllLines(path)) : new List<Pair>();

    /// <summary>按词库格式解析若干行（文件与内置默认词库共用，避免两份解析逻辑漂移）。</summary>
    public static List<Pair> ParseLines(IEnumerable<string> lines)
    {
        var pairs = new List<Pair>();

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var parts = line.Split('|');
            if (parts.Length < 2) continue;

            var civilian = parts[0].Trim();
            var undercover = parts[1].Trim();
            var category = parts.Length >= 3 ? parts[2].Trim() : "";

            if (civilian.Length == 0 || undercover.Length == 0) continue;

            pairs.Add(new Pair(civilian, undercover, category));
        }

        return pairs;
    }

    private static int Count(string content)
        => content.Split('\n')
                  .Select(l => l.Trim())
                  .Count(l => l.Length > 0 && !l.StartsWith('#') && l.Contains('|'));
}
