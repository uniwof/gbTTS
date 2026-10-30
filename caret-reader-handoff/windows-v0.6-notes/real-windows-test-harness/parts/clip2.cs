    // =====================================================================
    // 方法 2：剪贴板
    // =====================================================================
    // 这些编辑器在“什么都没选中”时按 Ctrl+C 会复制整行，没法用 Ctrl+C 判断有没有选中文字 → 不做“选中”探测
    private static readonly HashSet<string> LineCopyClasses = new(StringComparer.OrdinalIgnoreCase) { "Scintilla" };
    private static readonly HashSet<string> LineCopyProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "notepad++", "sublime_text", "Code", "devenv", "idea64", "pycharm64", "webstorm64", "rider64", "atom", "zed",
    };

    private static async Task<Result> TryClipboardAsync(Target target, bool caretToEnd)
    {
        // 用户按快捷键时 Ctrl/Alt 往往还没松开；这时模拟 Ctrl+C 实际会变成 Ctrl+Alt+C（很多程序里什么都不做，
        // 或者触发别的功能），这正是 v0.5 “后台按快捷键读不到”的原因之一。
        // v0.6：先等修饰键松开（最多 1.2 秒）；还没松开就由程序发送“松开”（带防 Alt 菜单的掩码键）。
        if (!await Native.WaitModifiersReleasedAsync(1200))
            return new Result(null, "剪贴板", "修饰键一直没有松开");

        // 发按键之前，目标程序必须在前台（按键只会发给前台窗口）
        if (Native.GetForegroundWindow() != target.Window && !await Native.RestoreForegroundAsync(target.Window, 600))
            return new Result(null, "剪贴板", "目标窗口不在前台，没有发送按键") { Unsupported = true };

        // 1) 备份剪贴板原有内容。备份失败、而剪贴板里又有东西 → 宁可不读，也不毁掉用户的剪贴板
        var (backupOk, backup) = BackupClipboard();
        if (!backupOk && Native.CountClipboardFormats() > 0)
            return new Result(null, "剪贴板", "无法备份剪贴板（可能被其它程序占用），为保护剪贴板内容已放弃");
        uint seq0 = Native.GetClipboardSequenceNumber();

        // 1.5) v0.4 选中朗读：先只按 Ctrl+C。复制出了文字 = 用户选中了文字 → 只读这些，
        //      不按 ←（保留用户的选区），还原剪贴板后直接返回。没复制出东西 = 没有选中 → 照旧从光标读。
        if (!LineCopyClasses.Contains(target.FocusClass) && !LineCopyProcesses.Contains(target.Process))
        {
            Native.SendChord(Native.VK_CONTROL, Native.VK_C);
            sw.Restart();
            while (Native.GetClipboardSequenceNumber() == seq0 && sw.ElapsedMilliseconds < 600)
                await Task.Delay(20);
            if (Native.GetClipboardSequenceNumber() != seq0)
            {
                await Task.Delay(30);
                string? sel = ReadClipboardText();
                if (!string.IsNullOrWhiteSpace(sel))
                {
                    await RestoreWithLateCheckAsync(backup);
                    if (sel.Length > MaxChars) sel = sel.Substring(0, MaxChars);
                    return new Result(Clean(sel), "剪贴板", "选中的文字") { Selection = true };
                }
            }
            seq0 = Native.GetClipboardSequenceNumber();
        }

        // v0.6：浏览器、PDF 阅读器等没有“光标”的程序只做上面的“选中”探测，不按 Ctrl+Shift+End
        if (!caretToEnd)
        {
            await RestoreWithLateCheckAsync(backup);
            return new Result(null, "剪贴板", "没有选中的文字（这个程序没有可用的文字光标）") { NoCaret = true };
        }

        // 2) 选中“光标到文末”
        Native.SendChord(Native.VK_CONTROL, Native.VK_SHIFT, Native.VK_END);
        await Task.Delay(80);

        // 3) 复制
        Native.SendChord(Native.VK_CONTROL, Native.VK_C);

        // 4) 等剪贴板真的变化（最多 2 秒，Word 复制很长的文档会慢一些）
        sw.Restart();
        while (Native.GetClipboardSequenceNumber() == seq0 && sw.ElapsedMilliseconds < 2000)
            await Task.Delay(20);
        bool copied = Native.GetClipboardSequenceNumber() != seq0;
        if (!copied) Log.Write("剪贴板方式：2 秒内没有检测到复制");

        string? text = null;
        if (copied)
        {
            await Task.Delay(30);
            text = ReadClipboardText();
        }

        // 5) 按 ← 取消选中：选区会收缩到开头，也就是原来光标的位置。
        //    v0.2：复制失败时也要按（否则文字一直被选中，用户一打字就会把它们替换掉）。
        //    唯一例外：复制成功但内容为空 = 确定什么都没选中（光标本来就在文末），这时按 ← 反而会移动光标。
        if (!(copied && text == ""))
            Native.SendChord(Native.VK_LEFT);

        // 6) 还原剪贴板（带“不记入剪贴板历史”的标记）+ 7) 防“迟到的复制”
        await RestoreWithLateCheckAsync(backup);

        if (!copied) return new Result(null, "剪贴板", "复制没有生效（目标程序可能不支持 Ctrl+C）");
        text ??= "";
        if (text.Length > MaxChars) text = text.Substring(0, MaxChars);
        return new Result(Clean(text), "剪贴板", null);
    }

    /// <summary>
    /// 还原剪贴板；目标程序的复制可能“迟到”（超时之后才写入剪贴板），覆盖掉刚还原的内容：过 300 毫秒再检查一次。
    /// </summary>
    private static async Task RestoreWithLateCheckAsync(DataObject? backup)
    {
        RestoreClipboard(backup);
        uint seqRestored = Native.GetClipboardSequenceNumber();
        await Task.Delay(300);
        if (Native.GetClipboardSequenceNumber() != seqRestored)
        {
            Log.Write("剪贴板在还原后又被改动（迟到的复制），再还原一次");
            RestoreClipboard(backup);
        }
    }

    private static string? ReadClipboardText()
    {
        // 剪贴板可能暂时被别的程序占用，重试几次
        for (int i = 0; i < 5; i++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText(TextDataFormat.UnicodeText) : ""; }
            catch { Thread.Sleep(30); }
        }
        return null;
    }

    // 只备份这些常见格式（v0.2）：全部格式都备份会迫使 Excel 等程序“现做”大量延迟数据，可能卡几秒、占几十 MB
    private static readonly string[] BackupFormats =
    {
        DataFormats.UnicodeText, DataFormats.Text, DataFormats.Rtf, DataFormats.Html, DataFormats.FileDrop, DataFormats.Bitmap,
    };
    private const int MaxBackupChars = 8_000_000;   // 超过这个长度的文字格式不备份（约 16MB）

    /// <summary>
    /// 备份剪贴板里常见的格式。返回 (是否成功, 备份)；剪贴板本来为空时返回 (true, null)。
    /// </summary>
    private static (bool ok, DataObject? data) BackupClipboard()
    {
        try
        {
            var src = Clipboard.GetDataObject();
            if (src == null) return (true, null);
            var copy = new DataObject();
            int n = 0;
            var skipped = new List<string>();
            foreach (var fmt in src.GetFormats(false))
            {
                if (Array.IndexOf(BackupFormats, fmt) < 0) { skipped.Add(fmt); continue; }
                try
                {
                    var v = src.GetData(fmt, false);
                    if (v is string str && str.Length > MaxBackupChars) { skipped.Add(fmt + "(过大)"); continue; }
                    if (v != null) { copy.SetData(fmt, false, v); n++; }
                }
                catch { skipped.Add(fmt + "(读取失败)"); }
            }
            if (skipped.Count > 0) Log.Write("剪贴板备份跳过的格式: " + string.Join(", ", skipped.Take(12)));
            return (true, n > 0 ? copy : null);
        }
        catch (Exception ex)
        {
            Log.Write("备份剪贴板失败: " + ex.Message);
            return (false, null);
        }
    }

    /// <summary>
    /// 还原剪贴板。附带三个系统认可的标记，让 Windows 剪贴板历史 (Win+V)、云剪贴板和剪贴板管理软件
    /// 忽略这次“还原”，不再多记一条。
    /// </summary>
    private static void RestoreClipboard(DataObject? backup)
    {
        try
        {
            if (backup != null)
            {
                MarkPrivate(backup);
                Clipboard.SetDataObject(backup, copy: true, retryTimes: 5, retryDelay: 50);
            }
            else Clipboard.Clear();
        }
        catch (Exception ex)
        {
            Log.Write("还原剪贴板失败: " + ex.Message);
        }
    }

    private static void MarkPrivate(DataObject d)
    {
        // https://learn.microsoft.com/windows/win32/dataxchg/clipboard-formats#cloud-clipboard-and-clipboard-history-formats
        static MemoryStream Dword(int v) => new(BitConverter.GetBytes(v));
        d.SetData("ExcludeClipboardContentFromMonitorProcessing", false, Dword(0));
        d.SetData("CanIncludeInClipboardHistory", false, Dword(0));
        d.SetData("CanUploadToCloudClipboard", false, Dword(0));
    }

    // =====================================================================

    /// <summary>
    /// 清理 Word 等程序返回的特殊字符：
    ///   \v（Word 的软回车）、\a（表格单元格结束）、\f（分页）→ 换行；
    ///   \uFFFC（嵌入对象占位符）和其它控制字符 → 删除。
    /// </summary>
    private static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c is '\v' or '\a' or '\f') sb.Append('\n');
            else if (c == '\uFFFC') continue;
            else if (char.IsControl(c) && c is not '\n' and not '\r' and not '\t') continue;
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
