    private static string SafeClass(UIA.IUIAutomationElement el)
    {
        try { return el.CurrentClassName ?? "?"; } catch { return "?"; }
    }

