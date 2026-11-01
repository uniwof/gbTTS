namespace CaretReader.App;

/// <summary>
/// 一个快捷键组合，例如 Ctrl+Alt+R。
/// 负责：文字 ⇄ 按键 的互相转换、检查是否“安全可用”，以及向系统注册/注销全局快捷键。
/// </summary>
internal readonly record struct Hotkey(Keys Modifiers, Keys Key)
{
    /// <summary>主快捷键默认值：开始 / 暂停 / 继续。</summary>
    public static readonly Hotkey Default = new(Keys.Control | Keys.Alt, Keys.R);
    /// <summary>停止快捷键默认值。</summary>
    public static readonly Hotkey DefaultStop = new(Keys.Control | Keys.Alt, Keys.S);

    /// <summary>注册到系统时用的编号：主快捷键 / 停止快捷键。</summary>
    public const int MainId = 0xC0DE, StopId = 0xC0DF;
    /// <summary>第 3 轮：上一句 / 下一句 / 朗读剪贴板（固定键位）。</summary>
    public const int PrevId = 0xC0E0, NextId = 0xC0E1, ClipId = 0xC0E2, AddReadingId = 0xC0E3;

    /// <summary>是否是一个格式上完整的组合：至少要有 Ctrl 或 Alt，再加一个普通键。</summary>
    public bool IsValid =>
        Key != Keys.None && !IsModifierKey(Key) &&
        ((Modifiers & (Keys.Control | Keys.Alt)) != 0);

    public static bool IsModifierKey(Keys k) =>
        k is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey or
             Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey or
             Keys.Menu or Keys.LMenu or Keys.RMenu or Keys.LWin or Keys.RWin;

    private int ModCount =>
        (Modifiers.HasFlag(Keys.Control) ? 1 : 0) + (Modifiers.HasFlag(Keys.Alt) ? 1 : 0) + (Modifiers.HasFlag(Keys.Shift) ? 1 : 0);

    /// <summary>
    /// 这个组合能不能用作全局快捷键？能用返回 null，不能用返回一句中文原因。
    ///
    /// 不允许的组合（v0.2）：
    ///   * 编辑 / 系统常用键：Ctrl+C / V / X / Z / Y / A / S、Alt+F4、Alt+Tab、Alt+Esc、Alt+空格、Ctrl+Esc 等
    ///     —— 注册成全局快捷键后，所有程序里的复制、粘贴、关闭窗口都会失灵。
    ///   * 剪贴板备用方案自己要模拟的按键：Ctrl+C、Ctrl+Shift+End（以及 Ctrl/Shift + 方向键、Home、End 等编辑键）
    ///     —— 否则程序模拟按键时会触发自己的快捷键。
    ///   * 只有一个修饰键 + 字母/数字（例如 Ctrl+B、Alt+F）：会和各个程序的菜单、加粗等快捷键冲突，
    ///     所以字母/数字键至少要配两个修饰键（例如 Ctrl+Alt+R）。功能键 F1–F12 等配一个修饰键就可以。
    /// </summary>
    public string? Problem
    {
        get
        {
            if (!IsValid) return "需要包含 Ctrl 或 Alt，再加一个普通键";
            bool ctrl = Modifiers.HasFlag(Keys.Control), alt = Modifiers.HasFlag(Keys.Alt), shift = Modifiers.HasFlag(Keys.Shift);

            // 系统保留
            if (alt && !ctrl && Key is Keys.F4 or Keys.Tab or Keys.Escape or Keys.Space or Keys.Enter)
                return $"{this} 是系统快捷键，不能使用";
            if (ctrl && !alt && Key is Keys.Escape) return $"{this} 是系统快捷键，不能使用";
            if (ctrl && alt && Key is Keys.Delete or Keys.Escape or Keys.Tab) return $"{this} 是系统快捷键，不能使用";
            if (ctrl && shift && !alt && Key == Keys.Escape) return $"{this} 是系统快捷键（任务管理器），不能使用";

            // 编辑键：所有程序都在用，剪贴板备用方案也会模拟其中几个
            if (ctrl && !alt && !shift && Key is Keys.C or Keys.V or Keys.X or Keys.Z or Keys.Y or Keys.A or Keys.S or Keys.Insert)
                return $"{this} 是复制 / 粘贴 / 撤销 / 保存等常用键，不能使用";
            if (!alt && (ctrl || shift) && Key is Keys.End or Keys.Home or Keys.Left or Keys.Right or Keys.Up or Keys.Down
                    or Keys.PageUp or Keys.PageDown or Keys.Delete or Keys.Back or Keys.Insert or Keys.Enter or Keys.Tab or Keys.Space)
                return $"{this} 是编辑文字时常用的键，不能使用";

            // 字母 / 数字 / 符号键：至少两个修饰键
            bool isFunctionKey = Key >= Keys.F1 && Key <= Keys.F24;
            bool isNavKey = Key is Keys.Pause or Keys.Scroll or Keys.Insert or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown
                or Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Delete;
            if (!isFunctionKey && !isNavKey && ModCount < 2)
                return $"{this} 容易和其它程序的快捷键冲突，字母 / 数字键请至少配两个修饰键（例如 Ctrl+Alt+R）";
            return null;
        }
    }

    /// <summary>转成文字，例如 "Ctrl+Alt+R"（保存到 settings.json、显示在界面上）。</summary>
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(Keys.Shift)) parts.Add("Shift");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    private static string KeyName(Keys k) => k switch
    {
        >= Keys.D0 and <= Keys.D9 => ((char)('0' + (k - Keys.D0))).ToString(),
        Keys.Oemcomma => ",",
        Keys.OemPeriod => ".",
        Keys.OemMinus => "-",
        Keys.Oemplus => "=",
        Keys.OemQuestion => "/",
        Keys.OemSemicolon => ";",
        Keys.Oemtilde => "`",
        Keys.OemOpenBrackets => "[",
        Keys.OemCloseBrackets => "]",
        Keys.OemQuotes => "'",
        Keys.OemPipe => "\\",
        _ => k.ToString(),
    };

    /// <summary>从文字解析，例如 "Ctrl+Alt+R"。解析失败（或格式不完整）返回 null。</summary>
    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        Keys mods = Keys.None, key = Keys.None;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string p = raw.ToLowerInvariant();
            if (p is "ctrl" or "control") mods |= Keys.Control;
            else if (p == "alt") mods |= Keys.Alt;
            else if (p == "shift") mods |= Keys.Shift;
            else if (raw.Length == 1 && char.IsDigit(raw[0])) key = Keys.D0 + (raw[0] - '0');
            else
            {
                string? alias = raw switch
                {
                    "," => "Oemcomma", "." => "OemPeriod", "-" => "OemMinus", "=" => "Oemplus",
                    "/" => "OemQuestion", ";" => "OemSemicolon", "`" => "Oemtilde",
                    "[" => "OemOpenBrackets", "]" => "OemCloseBrackets", "'" => "OemQuotes", "\\" => "OemPipe",
                    _ => null
                };
                if (!Enum.TryParse(alias ?? raw, ignoreCase: true, out key)) return null;
            }
        }
        var hk = new Hotkey(mods, key);
        return hk.IsValid ? hk : null;
    }

    // ---------- 注册到系统 ----------

    /// <summary>注册为全局快捷键。被别的程序占用时返回 false。</summary>
    public bool Register(IntPtr hwnd, int id)
    {
        uint mods = Native.MOD_NOREPEAT;
        if (Modifiers.HasFlag(Keys.Control)) mods |= Native.MOD_CONTROL;
        if (Modifiers.HasFlag(Keys.Alt)) mods |= Native.MOD_ALT;
        if (Modifiers.HasFlag(Keys.Shift)) mods |= Native.MOD_SHIFT;
        return Native.RegisterHotKey(hwnd, id, mods, (uint)Key);
    }

    public static void Unregister(IntPtr hwnd, int id) => Native.UnregisterHotKey(hwnd, id);
}
