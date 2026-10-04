using System.Threading;
using System.Windows;

namespace ClientGUI.Services;

/// <summary>
/// 剪贴板写入的集中封装。
/// Windows 剪贴板是全局独占资源，别的程序（输入法、剪贴板管理器、远程桌面…）
/// 短暂占用时 <c>OpenClipboard</c> 会失败并抛 <c>CLIPBRD_E_CANT_OPEN (0x800401D0)</c>，
/// 表现为「复制没反应」。
///
/// 注意：<c>Clipboard.SetDataObject</c> 自己就会重试（约 10 次 × 100ms），
/// 所以这里的外层重试要**保守**（默认 2 次），否则最坏情况会阻塞界面好几秒。
/// 调用方应把复制放在用户主动点击之后，而不是窗口打开时。
/// </summary>
public static class ClipboardHelper
{
    /// <summary>
    /// 尽力把文本写入剪贴板；成功返回 true。
    /// 必须在 STA 线程（WPF 界面线程）上调用，且调用前最好先让界面刷新一次提示文案。
    /// </summary>
    public static bool TrySetText(string? text, int attempts = 2, int delayMs = 40)
    {
        if (string.IsNullOrEmpty(text)) return false;

        for (var attempt = 0; attempt < Math.Max(1, attempts); attempt++)
        {
            try
            {
                // copy: true → 立刻落盘，程序退出后剪贴板内容仍有效。
                Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), copy: true);
                return true;
            }
            catch
            {
                if (attempt < attempts - 1) Thread.Sleep(Math.Max(0, delayMs));
            }
        }

        return false;
    }
}
