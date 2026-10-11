using Werewolf.Client.Share;

namespace Werewolf.Server.Games;

/// <summary>
/// 可编辑数据资源的**唯一**登记处（v2.5.0 第 5 条 / 架构 D5）。
///
/// 构造时遍历 <see cref="IGameCatalog.Modules"/>，逐个问「你有哪些可编辑资源」，
/// 按 <c>游戏机器名:资源机器名</c> 建索引。平台层只认识
/// <see cref="IEditableDataResource"/> 这六个成员，**不出现任何游戏专属知识**
/// （没有 <c>words.undercover.txt</c>、没有 <c>|</c> 分隔、不知道什么叫「平民词」）。
///
/// 线程安全：构造后只读，可被多线程并发读取。
/// </summary>
public sealed class EditableDataRegistry
{
    private readonly Dictionary<string, Entry> _byId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>构造：向所有游戏模块收集资源。</summary>
    /// <param name="catalog">游戏注册表（只读遍历）。</param>
    /// <param name="dataRoot">服务器数据根目录（= 工作目录）。</param>
    public EditableDataRegistry(IGameCatalog catalog, string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        DataRoot = dataRoot ?? "";

        foreach (var module in catalog.Modules)
        {
            var info = module.Info;
            if (info == null || string.IsNullOrWhiteSpace(info.GameId)) continue;

            IReadOnlyList<IEditableDataResource> resources;
            try
            {
                resources = module.CreateEditableResources(DataRoot) ?? Array.Empty<IEditableDataResource>();
            }
            catch
            {
                // 某个游戏模块自己出错不该拖垮服务器启动：跳过它，其余资源照常可用。
                continue;
            }

            foreach (var resource in resources)
            {
                if (resource == null || string.IsNullOrWhiteSpace(resource.ResourceId)) continue;

                var key = BuildKey(info.GameId, resource.ResourceId);
                _byId[key] = new Entry(info, resource);
            }
        }
    }

    /// <summary>服务器数据根目录（资源路径的基准；只读）。</summary>
    public string DataRoot { get; }

    /// <summary>资源标识 → 条目的稳定排序清单（供 <c>dataresources</c> 下发）。</summary>
    public IReadOnlyList<DataResourceItemDto> List()
        => _byId
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new DataResourceItemDto
            {
                Id = kv.Key,
                GameId = kv.Value.Game.GameId,
                GameName = kv.Value.Game.DisplayName,
                DisplayName = kv.Value.Resource.DisplayName,
                FormatHint = kv.Value.Resource.FormatHint,
                Count = CountOf(kv.Value.Resource)
            })
            .ToList();

    /// <summary>按标识取资源；不存在返回 false。</summary>
    public bool TryGet(string id, out IEditableDataResource resource)
    {
        if (!string.IsNullOrWhiteSpace(id) && _byId.TryGetValue(id.Trim(), out var entry))
        {
            resource = entry.Resource;
            return true;
        }

        resource = null!;
        return false;
    }

    /// <summary>资源显示名（取不到时回退到标识本身，便于人话提示）。</summary>
    public string DisplayNameOf(string id)
        => !string.IsNullOrWhiteSpace(id) && _byId.TryGetValue(id.Trim(), out var entry)
            ? entry.Resource.DisplayName
            : (id ?? "");

    /// <summary>
    /// 内容摘要：UTF-8 字节的 SHA-256 取前 8 字节 → 16 位十六进制。
    /// 与资源格式无关（平台算的），用于「服务器上的内容已被改过」的二次确认。
    /// 算法本体在共享层（<see cref="DataResourceDigest.Of"/>），客户端回带时用的是同一份。
    /// </summary>
    public static string DigestOf(string content) => DataResourceDigest.Of(content);

    /// <summary>把「游戏机器名 + 资源机器名」拼成对外的资源标识。</summary>
    public static string BuildKey(string gameId, string resourceId) => $"{gameId}:{resourceId}";

    /// <summary>有效条数：交给资源自己判定（校验不通过按 0 计）。</summary>
    private static int CountOf(IEditableDataResource resource)
    {
        try
        {
            var check = resource.Validate(resource.Load());
            return check.Ok ? check.Count : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>内部索引项：所属游戏元数据 + 资源本体。</summary>
    private readonly record struct Entry(GameInfo Game, IEditableDataResource Resource);
}
