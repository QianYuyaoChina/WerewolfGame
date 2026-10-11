using Werewolf.Client.Share;

namespace ClientGUI.ViewModels;

/// <summary>
/// 「服务器设置」浮层的视图模型（v2.5.0 第 5 条）：
/// 拉取可编辑资源清单 → 取某个资源全文 → 整份提交 → 原子落盘。
///
/// 平台侧**不认识**任何资源的格式：格式说明（<see cref="FormatHint"/>）与有效条数
/// 都是服务端（游戏模块）自己算出来下发的，这里只做展示与回传。
/// </summary>
public sealed class ServerSettingsViewModel : ObservableObject
{
    private readonly List<DataResourceItemDto> _items = new();

    private DataResourceItemDto? _selected;
    private string _content = "";
    private string _formatHint = "";
    private string _countText = "";
    private string _message = "";
    private bool _messageIsError = true;
    private bool _isBusy;

    /// <summary>服务端当前下发的资源指纹（保存时回带，用于「内容已被改过」的二次确认）。</summary>
    private string _digest = "";

    /// <summary>资源清单（狼人杀没有可编辑资源 → 空，界面给空态文案）。</summary>
    public IReadOnlyList<DataResourceItemDto> Items => _items;

    /// <summary>清单是否为空（空态时显示一行人话 + 提示）。</summary>
    public bool HasItems => _items.Count > 0;

    /// <summary>当前选中的资源；取不到时为 null。</summary>
    public DataResourceItemDto? SelectedItem
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value)) OnPropertyChanged(nameof(CanSave));
        }
    }

    /// <summary>正在编辑的全文。</summary>
    public string Content
    {
        get => _content;
        set => Set(ref _content, value ?? "");
    }

    /// <summary>格式说明（服务端下发，原样展示，客户端不解释）。</summary>
    public string FormatHint
    {
        get => _formatHint;
        private set
        {
            if (Set(ref _formatHint, value ?? "")) OnPropertyChanged(nameof(HasFormatHint));
        }
    }

    /// <summary>是否显示格式说明。</summary>
    public bool HasFormatHint => _formatHint.Length > 0;

    /// <summary>当前有效条数文案（如「当前 23 条」）。</summary>
    public string CountText
    {
        get => _countText;
        private set
        {
            if (Set(ref _countText, value ?? "")) OnPropertyChanged(nameof(HasCountText));
        }
    }

    /// <summary>是否显示条数文案。</summary>
    public bool HasCountText => _countText.Length > 0;

    /// <summary>就地提示（保存结果 / 失败原因，不弹框）。</summary>
    public string Message
    {
        get => _message;
        private set
        {
            if (Set(ref _message, value ?? "")) OnPropertyChanged(nameof(HasMessage));
        }
    }

    /// <summary>是否显示提示。</summary>
    public bool HasMessage => _message.Length > 0;

    /// <summary>提示是否按错误着色。</summary>
    public bool MessageIsError
    {
        get => _messageIsError;
        private set => Set(ref _messageIsError, value);
    }

    /// <summary>是否正在读写（期间禁止重复提交）。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) OnPropertyChanged(nameof(CanSave));
        }
    }

    /// <summary>「保存」是否可用：选了资源且当前空闲。</summary>
    public bool CanSave => !_isBusy && _selected != null;

    /// <summary>清空（每次打开浮层时调用）。</summary>
    public void Reset()
    {
        _items.Clear();
        SelectedItem = null;
        Content = "";
        FormatHint = "";
        CountText = "";
        Message = "";
        MessageIsError = true;
        _digest = "";

        OnPropertyChanged(nameof(Items));
        OnPropertyChanged(nameof(HasItems));
    }

    /// <summary>拉取资源清单；成功后自动选中第一项并载入它的全文。</summary>
    /// <returns>是否成功拿到清单。</returns>
    public async Task<bool> LoadAsync(GameClient client)
    {
        IsBusy = true;
        try
        {
            var payload = await client.ListDataResourcesAsync();
            if (payload == null)
            {
                MessageIsError = true;
                Message = ServerMessages.AdminOnly;
                return false;
            }

            _items.Clear();
            _items.AddRange(payload.Items);
            OnPropertyChanged(nameof(Items));
            OnPropertyChanged(nameof(HasItems));

            if (_items.Count == 0)
            {
                SelectedItem = null;
                Content = "";
                FormatHint = "";
                CountText = "";
                MessageIsError = false;
                Message = "服务器当前没有可编辑的数据资源。";
                return true;
            }

            SelectedItem = _items[0];
            Message = "";
            return true;
        }
        catch (Exception ex)
        {
            MessageIsError = true;
            Message = "无法连接：" + ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>载入当前选中资源的全文。</summary>
    /// <returns>是否成功。</returns>
    public async Task<bool> LoadSelectedAsync(GameClient client)
    {
        var item = _selected;
        if (item == null) return false;

        IsBusy = true;
        try
        {
            var dto = await client.GetDataResourceAsync(item.Id);
            if (dto == null)
            {
                MessageIsError = true;
                Message = ServerMessages.ResourceNotFound;
                return false;
            }

            Content = dto.Content;
            FormatHint = dto.FormatHint;
            CountText = $"当前 {dto.Count} 条";
            _digest = dto.Digest;
            Message = "";
            return true;
        }
        catch (Exception ex)
        {
            MessageIsError = true;
            Message = "无法连接：" + ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 整份提交当前内容。成功 / 失败都以**服务端文案**为准（含「第几行格式不对」）。
    /// 成功后把条数与指纹更新为服务端返回值，便于连续编辑。
    /// </summary>
    /// <returns>是否保存成功。</returns>
    public async Task<bool> SaveAsync(GameClient client)
    {
        var item = _selected;
        if (item == null)
        {
            MessageIsError = true;
            Message = ServerMessages.ResourceNotFound;
            return false;
        }

        IsBusy = true;
        try
        {
            var result = await client.SaveDataResourceAsync(item.Id, Content, _digest);

            MessageIsError = !result.Ok;
            Message = result.Message;

            if (!result.Ok) return false;

            CountText = $"当前 {result.Count} 条";
            _digest = DataResourceDigest.Of(Content);
            return true;
        }
        catch (Exception ex)
        {
            MessageIsError = true;
            Message = "无法连接：" + ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
