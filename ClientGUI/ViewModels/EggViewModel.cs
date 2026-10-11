using System.Windows.Threading;
using Werewolf.Client.Share;

namespace ClientGUI.ViewModels;

/// <summary>彩蛋输入框的两种形态：先输口令，命中后才切成称号编辑。</summary>
public enum EggDialogMode
{
    /// <summary>口令态：一个文本框，输入彩蛋口令。</summary>
    Prompt,

    /// <summary>称号编辑态：显示当前称号 + 输入新称号 + 保存 / 清除。</summary>
    Title
}

/// <summary>一次提交之后输入框该怎么做（由宿主窗口执行）。</summary>
public enum EggSubmitResult
{
    /// <summary>保持打开：口令未命中、切换形态、校验失败、网络不通。</summary>
    KeepOpen,

    /// <summary>关闭：称号保存 / 清除成功。</summary>
    Close
}

/// <summary>
/// 彩蛋输入框的视图模型（PRD 第 3 + 4 条）：
/// <list type="number">
///   <item>口令态：把用户输入交给服务端，由服务端决定「这是什么彩蛋」；</item>
///   <item>命中 <c>surprise</c> → 切成称号编辑态，并显示服务端回带的当前称号；</item>
///   <item>称号编辑态：保存（空 = 清除）/ 清除，全部由服务端裁定。</item>
/// </list>
///
/// 两条硬约束：
/// <list type="bullet">
///   <item><b>客户端不内嵌任何彩蛋文案</b>：所有提示都是服务端返回的（PRD 3.5）。</item>
///   <item><b>不弹异常</b>：网络不通只给人话提示，输入框保持打开可重试（PRD 8.3）。</item>
/// </list>
/// </summary>
public sealed class EggViewModel : ObservableObject
{
    private readonly GameClient _client;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    private EggDialogMode _mode = EggDialogMode.Prompt;
    private string _input = "";
    private string _currentTitle;
    private string _message = "";
    private bool _messageIsError = true;
    private bool _isBusy;
    private string _notice = "";

    public EggViewModel(GameClient client)
    {
        _client = client;
        _currentTitle = client.Title;

        ClearTitleCommand = new AsyncRelayCommand(_ => ClearTitleAsync());
    }

    // ==================== 集合与命令 ====================

    /// <summary>「清除称号」按钮（称号编辑态下的内联次按钮）。</summary>
    public AsyncRelayCommand ClearTitleCommand { get; }

    /// <summary>
    /// 清除成功后请求宿主关闭输入框（结果文案放在 <see cref="Notice"/> 里，由宿主显示）。
    /// </summary>
    public event Action? DismissRequested;

    // ==================== 绑定属性 ====================

    /// <summary>当前形态。</summary>
    public EggDialogMode Mode
    {
        get => _mode;
        private set
        {
            if (!Set(ref _mode, value)) return;
            OnPropertyChanged(nameof(IsPromptMode));
            OnPropertyChanged(nameof(IsTitleMode));
            OnPropertyChanged(nameof(InputLabel));
            OnPropertyChanged(nameof(PrimaryButtonText));
        }
    }

    public bool IsPromptMode => _mode == EggDialogMode.Prompt;
    public bool IsTitleMode => _mode == EggDialogMode.Title;

    /// <summary>是否有称号（无称号时界面显示「（无）」）。</summary>
    public bool HasTitle => !string.IsNullOrWhiteSpace(CurrentTitle);

    /// <summary>输入框上方的标签（两种形态共用同一个输入框）。</summary>
    public string InputLabel => IsTitleMode ? "新称号" : "彩蛋口令";

    /// <summary>主按钮文案（宿主的浮层按钮跟着它变）。</summary>
    public string PrimaryButtonText => IsTitleMode ? "保存" : "确定";

    /// <summary>用户输入（两种形态共用）。</summary>
    public string Input
    {
        get => _input;
        set => Set(ref _input, value);
    }

    /// <summary>当前称号（服务端权威值）。</summary>
    public string CurrentTitle
    {
        get => _currentTitle;
        private set
        {
            if (!Set(ref _currentTitle, value)) return;
            OnPropertyChanged(nameof(HasTitle));
        }
    }

    /// <summary>就地提示文案（写在按钮上方，不弹框）。</summary>
    public string Message
    {
        get => _message;
        private set
        {
            if (!Set(ref _message, value)) return;
            OnPropertyChanged(nameof(HasMessage));
        }
    }

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    /// <summary>提示是否按「错误」着色（服务端给的非命中文案用弱化色，不算错误）。</summary>
    public bool MessageIsError
    {
        get => _messageIsError;
        private set => Set(ref _messageIsError, value);
    }

    /// <summary>正在请求中（避免重复提交）。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) OnPropertyChanged(nameof(IsIdle));
        }
    }

    public bool IsIdle => !IsBusy;

    /// <summary>成功后要交给宿主显示的一句话（如「称号已保存」）。</summary>
    public string Notice
    {
        get => _notice;
        private set => Set(ref _notice, value);
    }

    // ==================== 操作 ====================

    /// <summary>回到口令态并清空输入（每次打开输入框时调用）。</summary>
    public void Reset()
    {
        _mode = EggDialogMode.Prompt;
        Input = "";
        Message = "";
        MessageIsError = true;
        Notice = "";
        CurrentTitle = _client.Title;
        OnPropertyChanged(nameof(IsPromptMode));
        OnPropertyChanged(nameof(IsTitleMode));
        OnPropertyChanged(nameof(InputLabel));
        OnPropertyChanged(nameof(PrimaryButtonText));
    }

    /// <summary>
    /// 主按钮提交。按当前形态分派：口令态去服务端问「这是什么彩蛋」，称号态直接保存。
    /// </summary>
    public async Task<EggSubmitResult> SubmitAsync()
    {
        if (IsBusy) return EggSubmitResult.KeepOpen;

        var text = (Input ?? "").Trim();

        if (_mode == EggDialogMode.Prompt)
        {
            if (text.Length == 0)
            {
                SetMessage("请输入内容", true);
                return EggSubmitResult.KeepOpen;
            }

            IsBusy = true;
            try
            {
                var reply = await _client.EggAsync(text).ConfigureAwait(true);
                if (reply == null)
                {
                    SetMessage(ServerMessages.NetworkUnreachable, true);
                    return EggSubmitResult.KeepOpen;
                }

                // 命中「惊喜」→ 进入称号编辑态（内容由服务端决定，客户端只认 Kind）。
                if (reply.Matched && string.Equals(reply.Kind, EggKinds.Title, StringComparison.OrdinalIgnoreCase))
                {
                    CurrentTitle = reply.Title ?? "";
                    Input = CurrentTitle;
                    Mode = EggDialogMode.Title;
                    SetMessage(reply.Message, false);
                    return EggSubmitResult.KeepOpen;
                }

                // 未命中：服务端给什么就显示什么，**不报错**（PRD 3.6）。
                SetMessage(string.IsNullOrEmpty(reply.Message) ? ServerMessages.EggPending : reply.Message, false);
                return EggSubmitResult.KeepOpen;
            }
            catch
            {
                SetMessage(ServerMessages.NetworkUnreachable, true);
                return EggSubmitResult.KeepOpen;
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ---- 称号编辑态：保存 ----
        // 先用与服务端同源的规则本地校验一次（真正的裁定仍在服务端）。
        var (ok, error) = TitleRules.Validate(text);
        if (!ok)
        {
            SetMessage(error, true);
            return EggSubmitResult.KeepOpen;
        }

        IsBusy = true;
        try
        {
            var (saved, msg) = await _client.SetTitleAsync(text).ConfigureAwait(true);
            if (!saved)
            {
                SetMessage(string.IsNullOrEmpty(msg) ? ServerMessages.NetworkUnreachable : msg, true);
                return EggSubmitResult.KeepOpen;
            }

            CurrentTitle = TitleRules.Normalize(text);
            Notice = msg;
            return EggSubmitResult.Close;
        }
        catch
        {
            SetMessage(ServerMessages.NetworkUnreachable, true);
            return EggSubmitResult.KeepOpen;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>清除称号（等价于保存空值，但走服务端同一条裁定路径）。</summary>
    public async Task<bool> ClearTitleAsync()
    {
        if (IsBusy) return false;

        IsBusy = true;
        try
        {
            var (ok, msg) = await _client.SetTitleAsync("").ConfigureAwait(true);
            if (!ok)
            {
                SetMessage(string.IsNullOrEmpty(msg) ? ServerMessages.NetworkUnreachable : msg, true);
                return false;
            }

            CurrentTitle = "";
            Input = "";
            Notice = msg;
            DismissRequested?.Invoke();
            return true;
        }
        catch
        {
            SetMessage(ServerMessages.NetworkUnreachable, true);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ==================== 内部 ====================

    private void SetMessage(string message, bool isError)
    {
        void Apply()
        {
            MessageIsError = isError;
            Message = message;
        }

        if (_ui == null) Apply();
        else _ui.Post(_ => Apply(), null);
    }
}
