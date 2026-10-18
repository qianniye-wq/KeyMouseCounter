namespace KeyMouseCounter.Native;

/// <summary>解析 "Ctrl+Alt+K" 形式的热键字符串（§5.8）。</summary>
internal static class HotkeyParser
{
    public static bool TryParse(string? spec, out uint modifiers, out uint vk, out string? error)
    {
        modifiers = 0;
        vk = 0;
        error = null;

        if (string.IsNullOrWhiteSpace(spec))
        {
            error = "热键为空";
            return false;
        }

        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            error = "热键为空";
            return false;
        }

        bool keySeen = false;
        foreach (var raw in parts)
        {
            var p = raw.ToLowerInvariant();
            switch (p)
            {
                case "ctrl" or "control": modifiers |= NativeMethods.MOD_CONTROL; continue;
                case "alt": modifiers |= NativeMethods.MOD_ALT; continue;
                case "shift": modifiers |= NativeMethods.MOD_SHIFT; continue;
                case "win" or "windows" or "meta": modifiers |= NativeMethods.MOD_WIN; continue;
            }

            if (keySeen)
            {
                error = "热键只能有一个主键";
                return false;
            }
            if (!TryParseKey(raw, out vk))
            {
                error = $"无法识别的按键：{raw}";
                return false;
            }
            keySeen = true;
        }

        if (!keySeen)
        {
            error = "热键缺少主键";
            return false;
        }
        if (modifiers == 0)
        {
            error = "热键至少要有一个修饰键（Ctrl/Alt/Shift/Win）";
            return false;
        }

        modifiers |= NativeMethods.MOD_NOREPEAT;
        return true;
    }

    private static bool TryParseKey(string token, out uint vk)
    {
        vk = 0;
        var t = token.Trim();

        if (t.Length == 1)
        {
            char c = char.ToUpperInvariant(t[0]);
            if (c is >= 'A' and <= 'Z') { vk = c; return true; }
            if (c is >= '0' and <= '9') { vk = c; return true; }
        }

        if (t.Length is > 1 and <= 3 && (t[0] is 'F' or 'f') && int.TryParse(t[1..], out int fn) && fn is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + (fn - 1));
            return true;
        }

        switch (t.ToLowerInvariant())
        {
            case "space": vk = 0x20; return true;
            case "tab": vk = 0x09; return true;
            case "enter" or "return": vk = 0x0D; return true;
            case "esc" or "escape": vk = 0x1B; return true;
            case "backspace": vk = 0x08; return true;
            case "insert": vk = 0x2D; return true;
            case "delete" or "del": vk = 0x2E; return true;
            case "home": vk = 0x24; return true;
            case "end": vk = 0x23; return true;
            case "pageup" or "pgup": vk = 0x21; return true;
            case "pagedown" or "pgdn": vk = 0x22; return true;
            case "left": vk = 0x25; return true;
            case "up": vk = 0x26; return true;
            case "right": vk = 0x27; return true;
            case "down": vk = 0x28; return true;
            default: return false;
        }
    }

    public static string Normalize(string spec)
    {
        if (!TryParse(spec, out uint mods, out uint vk, out _)) return spec;
        var parts = new List<string>(5);
        if ((mods & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((mods & NativeMethods.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(KeyName(vk));
        return string.Join("+", parts);
    }

    private static string KeyName(uint vk)
    {
        if (vk is >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x30 and <= 0x39) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x70 + 1);
        return vk switch
        {
            0x20 => "Space",
            0x09 => "Tab",
            0x0D => "Enter",
            0x1B => "Esc",
            0x08 => "Backspace",
            0x2D => "Insert",
            0x2E => "Delete",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            _ => $"0x{vk:X2}"
        };
    }
}
