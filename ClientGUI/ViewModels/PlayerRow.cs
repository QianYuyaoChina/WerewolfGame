using System.Windows.Media;
using Werewolf.Client.Share;

namespace ClientGUI.ViewModels;

/// <summary>玩家列表中的一行（左侧面板）。平台只提供通用字段；游戏自定义标签由 <see cref="Tag"/> / <see cref="TagColor"/> 承载。</summary>
public sealed class PlayerRow : ObservableObject
{
    private bool _selectable;
    private bool _isTarget;

    public int Id { get; init; }
    public string Name { get; init; } = "";
    public bool IsAlive { get; init; } = true;
    public bool IsHost { get; init; }
    public bool IsYou { get; init; }
    public bool IsConnected { get; init; } = true;

    /// <summary>游戏自定义短标签（狼人杀 = 身份名，如「狼人」）；无标签为 null。</summary>
    public string? Tag { get; init; }

    /// <summary>标签颜色（十六进制，如 "#FFA07A"）；为空则用主题默认色。</summary>
    public string? TagColor { get; init; }

    /// <summary>
    /// 玩家的**称号**（v2.5.0 第 4 条，服务端权威）。空 = 无称号 → 称号胶囊不渲染。
    /// </summary>
    public string? Title { get; init; }

    /// <summary>是否有可展示的称号（供界面决定要不要渲染胶囊）。</summary>
    public bool HasTitle => !string.IsNullOrWhiteSpace(Title);

    /// <summary>当前阶段是否可以点选该玩家作为目标。</summary>
    public bool Selectable
    {
        get => _selectable;
        set
        {
            if (Set(ref _selectable, value))
            {
                OnPropertyChanged(nameof(RowBackground));
                OnPropertyChanged(nameof(RowBorderBrush));
                OnPropertyChanged(nameof(Cursor));
            }
        }
    }

    /// <summary>是否为当前已选中的目标。</summary>
    public bool IsTarget
    {
        get => _isTarget;
        set
        {
            if (Set(ref _isTarget, value))
            {
                OnPropertyChanged(nameof(RowBorderBrush));
            }
        }
    }

    public bool IsDisconnected => !IsConnected;
    public bool IsDead => !IsAlive;

    public string Badge => $"[{Id}]";
    public string Slot => Id.ToString();

    /// <summary>标签文案（无标签为空串）。</summary>
    public string TagText => Tag ?? "";

    /// <summary>是否有可展示的游戏标签。</summary>
    public bool HasTag => !string.IsNullOrEmpty(Tag);

    /// <summary>标签画刷：按 <see cref="TagColor"/> 解析，缺省用主题弱化色。</summary>
    public Brush TagBrush => UiBrushes.FromHex(TagColor, UiBrushes.Muted);

    public string Tags
    {
        get
        {
            var tags = new List<string>();
            if (IsYou) tags.Add("你");
            if (IsHost) tags.Add("👑 房主");
            if (!IsConnected) tags.Add("⚠ 掉线");
            if (!IsAlive) tags.Add("已出局");
            return string.Join(" · ", tags);
        }
    }

    public bool HasTags => Tags.Length > 0;

    // 画刷统一从 UiBrushes 取缓存实例：行样式会在每次布局测量时被读取，不能每次 new。
    public Brush RowBackground => !IsAlive
        ? UiBrushes.RowBackgroundDead
        : Selectable
            ? UiBrushes.RowBackgroundSelectable
            : UiBrushes.RowBackgroundNormal;

    public Brush RowBorderBrush => IsTarget ? UiBrushes.RowBorderTarget : UiBrushes.RowBorderNormal;

    public System.Windows.Input.Cursor Cursor =>
        Selectable ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;
}
