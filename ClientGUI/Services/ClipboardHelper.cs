using System.Threading;
using System.Windows;

namespace ClientGUI.Services;

/// <summary>
/// 剪贴板写入的集中封装。
/// Windows 剪贴板是全局独占资源，别的程序（输入法、剪贴板管理器、远程桌面…）
/// 短暂占用时 <c>OpenClipboard</c> 会失败并抛 <c>CLIPBRD_E_CANT_OPEN (0x800401D0)</c>，
/// 表现为「复制没反应」。因此这里重试若干次，并把结果如实返回给调用方
/// ——调用方据此提示用户改用「手动全选复制」。
/// </summary>
public static class ClipboardHelper
{
    /// <summary>
    /// 尽力把文本写入剪贴板；成功返回 true。
    /// 必须在 STA 线程（WPF 界面线程）上调用。
    /// </summary>
    public static bool TrySetText(string? text, int attempts = 6, int delayMs = 70)
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
