using CaretReader.Core;
using Xunit;

namespace CaretReader.Tests;

/// <summary>
/// 取文字验证清单里“能自动化”的部分：剪贴板方式在各类窗口里允许 / 禁止什么。
/// 真机才能验证的部分（UIA 是否真读到、是否丢焦点）见 docs/P0_真机验证清单.md。
/// </summary>
public class GrabRulesTests
{
    [Theory]
    [InlineData("Notepad", "RichEditD2DPT", "notepad", 50030, true, true, true)]          // Win11 记事本
    [InlineData("OpusApp", "_WwG", "WINWORD", 0, false, true, true)]                       // Word
    [InlineData("OpusApp", "", "wps", 0, false, true, true)]                                // WPS 文字
    [InlineData("Chrome_WidgetWin_1", "Chrome_RenderWidgetHostHWND", "msedge", 50030, true, true, false)]  // Edge 网页正文：只探测选中
    [InlineData("Chrome_WidgetWin_1", "Chrome_RenderWidgetHostHWND", "chrome", 50004, true, true, true)]   // 网页输入框
    [InlineData("AcrobatSDIWindow", "AVL_AVView", "Acrobat", 50033, true, true, false)]     // Adobe PDF
    [InlineData("SUMATRA_PDF_FRAME", "SUMATRA_PDF_CANVAS", "SumatraPDF", 0, false, true, false)]
    public void Allowed_windows(string top, string focus, string proc, int ctl, bool hasCtl, bool allowed, bool caretToEnd)
    {
        var r = ClipboardRules.Decide(top, focus, proc, ctl, hasCtl);
        Assert.Equal(allowed, r.allowed);
        Assert.Equal(caretToEnd, r.caretToEnd);
    }

    [Theory]
    [InlineData("CASCADIA_HOSTING_WINDOW_CLASS", "", "WindowsTerminal", 50004, true)]   // 终端：Ctrl+C 会中断程序
    [InlineData("ConsoleWindowClass", "", "cmd", 0, false)]
    [InlineData("CabinetWClass", "DirectUIHWND", "explorer", 0, false)]                // 资源管理器：Ctrl+C 会复制文件
    [InlineData("TscShellContainerClass", "", "mstsc", 0, false)]                      // 远程桌面
    public void Never_send_ctrl_c(string top, string focus, string proc, int ctl, bool hasCtl)
    {
        Assert.False(ClipboardRules.Decide(top, focus, proc, ctl, hasCtl).allowed);
    }
}
