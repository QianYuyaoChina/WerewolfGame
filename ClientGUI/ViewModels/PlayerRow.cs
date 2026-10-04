using System.Windows.Media;
using Werewolf.Client.Share;

namespace ClientGUI.ViewModels;

/// <summary>玩家列表中的一行（左侧面板）。</summary>
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
    public string? Role { get; init; }

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

    public string RoleText => Role is { Length: > 0 } r ? Roles.Emoji(r) + " " + r : "";
    public bool HasRole => Role is { Length: > 0 };

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

    public Brush RoleBrush => Role switch
    {
        Roles.Werewolf => Brushes.Salmon,
        Roles.Seer => Brushes.MediumPurple,
        Roles.Witch => Brushes.MediumSpringGreen,
        Roles.Hunter => Brushes.SandyBrown,
        Roles.Guard => Brushes.LightSkyBlue,
        Roles.Villager => Brushes.Gainsboro,
        _ => Brushes.Gray
    };

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
