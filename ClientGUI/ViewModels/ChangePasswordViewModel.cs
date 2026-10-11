using Werewolf.Client.Share;

namespace ClientGUI.ViewModels;

/// <summary>
/// 「修改密码」浮层的视图模型（v2.5.0 第 7 条）。
///
/// 职责边界：
/// <list type="bullet">
///   <item><b>客户端先校验一次</b>：两遍新密码必须一致 —— 不一致时确定按钮不可点，**不发请求**；</item>
///   <item>长度 / 「与旧密码相同」这类规则**由服务端裁定**（文案直接透传服务端），
///         客户端只用共享的 <see cref="PasswordRules"/> 做一次提前提示，避免白跑一趟；</item>
///   <item>成功后**不断开、不强制重登**（PRD 7.4）。</item>
/// </list>
/// </summary>
public sealed class ChangePasswordViewModel : ObservableObject
{
    private string _oldPassword = "";
    private string _newPassword = "";
    private string _confirmPassword = "";
    private string _message = "";
    private bool _messageIsError = true;
    private bool _isBusy;

    /// <summary>当前（旧）密码。</summary>
    public string OldPassword
    {
        get => _oldPassword;
        set
        {
            if (!Set(ref _oldPassword, value ?? "")) return;
            OnPropertyChanged(nameof(CanSubmit));
            OnPropertyChanged(nameof(MismatchHint));
        }
    }

    /// <summary>新密码。</summary>
    public string NewPassword
    {
        get => _newPassword;
        set
        {
            if (!Set(ref _newPassword, value ?? "")) return;
            OnPropertyChanged(nameof(CanSubmit));
            OnPropertyChanged(nameof(MismatchHint));
        }
    }

    /// <summary>再输一遍新密码（只为防手误，不发给服务端以外的任何地方）。</summary>
    public string ConfirmPassword
    {
        get => _confirmPassword;
        set
        {
            if (!Set(ref _confirmPassword, value ?? "")) return;
            OnPropertyChanged(nameof(CanSubmit));
            OnPropertyChanged(nameof(MismatchHint));
        }
    }

    /// <summary>就地提示文案（错误 / 成功都走这里，不弹框）。</summary>
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

    /// <summary>提示是否按错误着色（成功时用弱化色）。</summary>
    public bool MessageIsError
    {
        get => _messageIsError;
        private set => Set(ref _messageIsError, value);
    }

    /// <summary>是否正在提交（提交期间禁止重复点）。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) OnPropertyChanged(nameof(CanSubmit));
        }
    }

    /// <summary>两遍新密码不一致时的就地提示（空 = 没问题）。</summary>
    public string MismatchHint
    {
        get
        {
            if (_newPassword.Length == 0 && _confirmPassword.Length == 0) return "";
            return _newPassword == _confirmPassword ? "" : ServerMessages.PasswordMismatch;
        }
    }

    /// <summary>「确定」是否可用：三框都填了、两遍一致、且不在提交中。</summary>
    public bool CanSubmit
        => !_isBusy
           && _oldPassword.Length > 0
           && _newPassword.Length > 0
           && _newPassword == _confirmPassword;

    /// <summary>清空（每次打开浮层时调用，避免看到上一次的残留）。</summary>
    public void Reset()
    {
        OldPassword = "";
        NewPassword = "";
        ConfirmPassword = "";
        Message = "";
        MessageIsError = true;
    }

    /// <summary>
    /// 提交：先做客户端一致性校验（不通过直接返回并给提示，**不发请求**），
    /// 再把服务端返回的文案原样显示出来。
    /// </summary>
    /// <returns>是否修改成功。</returns>
    public async Task<bool> SubmitAsync(GameClient client)
    {
        if (_newPassword != _confirmPassword)
        {
            MessageIsError = true;
            Message = ServerMessages.PasswordMismatch;
            return false;
        }

        var (okFormat, formatError) = PasswordRules.Validate(_newPassword);
        if (!okFormat)
        {
            MessageIsError = true;
            Message = formatError;
            return false;
        }

        IsBusy = true;
        try
        {
            var (ok, msg) = await client.ChangePasswordAsync(_oldPassword, _newPassword);

            MessageIsError = !ok;
            Message = msg;

            if (ok)
            {
                // 成功不清空输入框之外的东西：不留旧密码在界面上。
                OldPassword = "";
                NewPassword = "";
                ConfirmPassword = "";
            }

            return ok;
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
