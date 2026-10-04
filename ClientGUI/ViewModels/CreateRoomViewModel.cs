namespace ClientGUI.ViewModels;

/// <summary>
/// 创建房间对话框的视图模型：房间名（可留空 → 服务端回退默认名）+ 可选密码。
/// 勾选「不设密码」即创建公开房间。
/// </summary>
public sealed class CreateRoomViewModel : ObservableObject
{
    private string _roomName = "";
    private string _password = "";
    private bool _noPassword = true;

    public CreateRoomViewModel()
    {
        CreateCommand = new RelayCommand(_ => Create());
    }

    /// <summary>校验通过、可提交创建时触发。</summary>
    public event Action? CreateSucceeded;

    public RelayCommand CreateCommand { get; }

    public string RoomName { get => _roomName; set => Set(ref _roomName, value); }

    public string Password { get => _password; set => Set(ref _password, value); }

    /// <summary>勾选「不设密码（公开房间）」。</summary>
    public bool NoPassword
    {
        get => _noPassword;
        set
        {
            if (Set(ref _noPassword, value)) OnPropertyChanged(nameof(IsPasswordEnabled));
        }
    }

    public bool IsPasswordEnabled => !NoPassword;

    private void Create()
    {
        // 房间名可留空（服务端回退默认名）；密码纯由「不设密码」决定。
        CreateSucceeded?.Invoke();
    }
}
