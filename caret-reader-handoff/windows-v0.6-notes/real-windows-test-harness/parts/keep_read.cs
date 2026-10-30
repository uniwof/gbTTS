    /// <summary>
    /// v0.4：读出选区里的文字（最多 MaxChars 字、最多 64 段）。全是空白 / 没有选中时返回 null。
    /// </summary>
    private static string? ReadSelection(UIA.IUIAutomationTextRangeArray sel)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < sel.Length && i < 64 && sb.Length < MaxChars; i++)
        {
            string t;
            try { t = sel.GetElement(i).GetText(MaxChars - sb.Length) ?? ""; }
            catch { continue; }
            if (t.Length == 0) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(t);
        }
        string text = Clean(sb.ToString());
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// 先读前 FirstChunkChars 字；如果文档还有更多，就在最后一个句末（。！？换行）处截断，
    /// 剩下的部分交给 Rest 在后台读取（重新读一次“光标到文末”，去掉已经读过的开头）。
    /// </summary>
    private static Result ReadInTwoSteps(UIA.IUIAutomationTextRange range, string who)
    {
        string first = range.GetText(FirstChunkChars) ?? "";
        if (first.Length != FirstChunkChars)
        {
            // 文档本来就短（或者控件不理会长度参数、一次全给了）：一步完成
            if (first.Length > MaxChars) first = first.Substring(0, MaxChars);
            return new Result(Clean(first), "UIA", who);
        }

        // 在最后一个句末处截断，避免把一句话切成两半分别处理
        int cut = first.LastIndexOfAny(new[] { '。', '！', '？', '!', '?', '\n', '\r' });
        string head = cut >= 200 ? first.Substring(0, cut + 1) : first;
        string? Rest()
        {
            try
            {
                string all = range.GetText(MaxChars) ?? "";
                if (!all.StartsWith(head, StringComparison.Ordinal))
                {
                    Log.Write("UIA 第二步：文档在朗读开始后被修改，只读第一部分");
                    return null;
                }
                return Clean(all.Substring(head.Length));
            }
            catch (Exception ex)
            {
                Log.Write("UIA 第二步读取失败: " + ex.Message);
                return null;
            }
        }
        return new Result(Clean(head), "UIA", who + " / 分两步", Rest);
    }

    private static UIA.IUIAutomation CreateUia()
    {
        // 优先用较新的 CUIAutomation8（Win8+），失败再用旧版 CUIAutomation
        foreach (var clsid in new[] { "e22ad333-b25f-460c-83d0-0581107395c9", "ff48dba4-60ef-4201-aa87-54103eef594e" })
        {
            try
            {
                var t = Type.GetTypeFromCLSID(new Guid(clsid), throwOnError: true)!;
                return (UIA.IUIAutomation)Activator.CreateInstance(t)!;
            }
            catch { }
        }
        throw new InvalidOperationException("无法创建 UI Automation 对象");
    }

