using System.Windows.Controls;

namespace ClientGUI.Views;

/// <summary>
/// 彩蛋输入框的浮层内容（<see cref="EggViewModel"/> 的视图）。
///
/// 它不做任何业务判断：口令是什么、称号多长、能不能保存，全部由服务端裁定；
/// 这里只负责把「输口令 → 变成称号编辑 → 保存 / 清除」这条流程画成一个小程序风格的卡片正文。
/// </summary>
public partial class EggDialog : UserControl
{
    public EggDialog()
    {
        InitializeComponent();
    }
}
