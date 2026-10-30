using CaretReader.Core;
using System.Windows.Forms;

namespace CaretReader.App.Ui;

/// <summary>
/// 右键菜单 / 音色下拉菜单：明确带 WS_EX_NOACTIVATE（硬要求 8），弹出和点选都不抢文档的焦点。
/// </summary>
internal sealed class NoActivateMenu : ContextMenuStrip
{
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }
}
