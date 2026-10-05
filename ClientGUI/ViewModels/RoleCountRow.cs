namespace ClientGUI.ViewModels;

/// <summary>
/// 「房主设置 → 身份配置」里的一行：身份名 + 数量输入框。
/// 数量用字符串承载，避免输入过程中（半截数字 / 空串）触发绑定转换异常。
/// </summary>
public sealed class RoleCountRow : ObservableObject
{
    /// <summary>身份名（<see cref="Werewolf.Client.Share.Roles"/> 里的中文名，作为布局的键）。</summary>
    public string Role { get; init; } = "";

    /// <summary>界面显示（带 emoji）。</summary>
    public string Display { get; init; } = "";

    private string _countText = "0";

    public string CountText
    {
        get => _countText;
        set => Set(ref _countText, value);
    }

    /// <summary>解析后的数量；非法输入按 0 处理。</summary>
    public int Count => int.TryParse(_countText, out var n) && n > 0 ? n : 0;

    public void SetCount(int count) => CountText = Math.Max(0, count).ToString();
}
