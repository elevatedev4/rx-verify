using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace RxVerifyOverlay.Reports;

/// <summary>
/// Round 4 reviewer fix (blocking finding 1): raw Win32 SendInput P/Invoke
/// for Unicode keyboard text entry, extracted out of PioneerReportDriver.cs
/// into its own file with zero FlaUI/WPF dependency so
/// Marshal.SizeOf&lt;INPUT&gt;() can be unit-tested (NativeInputTests.cs, and
/// copied into an isolated non-Windows classlib for a macOS-executable
/// check) without a live Windows UIA session.
///
/// THE BUG THIS FIXES: the original InputUnion declared only a KEYBDINPUT
/// member (24 bytes on x64) with no MOUSEINPUT/HARDWAREINPUT members and no
/// explicit size override, so Marshal.SizeOf&lt;INPUT&gt;() computed 32 bytes
/// (4-byte type + 4-byte padding + 24-byte KEYBDINPUT) instead of the real
/// Win32 INPUT struct's 40 bytes on x64 (the union is sized to its largest
/// member, MOUSEINPUT at 32 bytes, plus the leading DWORD type + alignment
/// padding — 28 bytes on x86, where ULONG_PTR is 4 bytes). SendInput
/// validates cbSize strictly and silently sends nothing (returns 0) on any
/// mismatch — the classic P/Invoke gotcha — and the original code never
/// checked the return value, so every TypeUnicodeText call was a silent
/// no-op on a real Windows machine: every date field would read back
/// whatever it already held, look like a mismatch, fall into the one
/// clipboard-paste retry, and only that keystroke path would ever actually
/// enter a date. Now every union member is declared at FieldOffset(0)
/// (giving INPUT its correct native size — see
/// NativeInputTests.SizeOfInputMatchesNativeWin32Layout), and
/// TypeUnicodeText checks SendInput's actual return value instead of
/// assuming success.
/// </summary>
public static class NativeInput
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    /// <summary>All three real Win32 union members, every one at FieldOffset(0) — this is what gives the union (and so INPUT) its correct native size. The original bug: only `ki` was declared here.</summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_UNICODE = 0x0004;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>
    /// Reviewer round 5 blocking finding 1: MapVirtualKeyW(VK_DOWN,
    /// MAPVK_VK_TO_VSC) returns the BASE scan code, which Windows shares
    /// with the NumPad-2 key — without this flag set, whether the OS
    /// delivers the keystroke as the arrow key or as NumPad '2' depends on
    /// the target's NumLock state, not on wVk. Every "extended" key (the
    /// arrow cluster, Home/End/Insert/Delete/PageUp/PageDown, NumPad
    /// divide, NumLock, right Ctrl/Alt — see IsExtendedKey) must OR this
    /// into dwFlags on both key down and key up, exactly like a physical
    /// extended key's own scan-code prefix (0xE0) would signal.
    /// </summary>
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    private const uint MAPVK_VK_TO_VSC = 0;
    private const ushort VK_SHIFT = 0x10;

    /// <summary>
    /// The Win32 virtual-key codes that MUST carry KEYEVENTF_EXTENDEDKEY —
    /// every one of these shares its base scan code with a NumPad key (or,
    /// for RCONTROL/RMENU, with its left-hand counterpart), so the OS can
    /// only tell them apart from the NumLock-affected NumPad key or the
    /// left-hand key by that flag. Pure data, no P/Invoke — unit-testable
    /// directly (NativeInputTests.IsExtendedKeyIsTrueForVkDown, etc.).
    /// </summary>
    private static readonly HashSet<ushort> ExtendedKeyVirtualKeys = new()
    {
        0x21, // VK_PRIOR (Page Up)
        0x22, // VK_NEXT (Page Down)
        0x23, // VK_END
        0x24, // VK_HOME
        0x25, // VK_LEFT
        0x26, // VK_UP
        0x27, // VK_RIGHT
        0x28, // VK_DOWN
        0x2D, // VK_INSERT
        0x2E, // VK_DELETE
        0x6F, // VK_DIVIDE (NumPad /)
        0x90, // VK_NUMLOCK
        0xA3, // VK_RCONTROL
        0xA5, // VK_RMENU (right Alt)
    };

    /// <summary>True for a virtual-key code that must carry KEYEVENTF_EXTENDEDKEY on every SendInput key event — see ExtendedKeyVirtualKeys' own doc.</summary>
    public static bool IsExtendedKey(ushort vk) => ExtendedKeyVirtualKeys.Contains(vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    /// <summary>VkKeyScanW('0'..'9', '-', etc.) -&gt; low byte is the virtual-key code, high byte's low 3 bits are the shift state (bit 0 = Shift, bit 1 = Ctrl, bit 2 = Alt) needed to produce that character on the current keyboard layout. Returns -1 (0xFFFF as a signed short) when the layout cannot produce the character at all.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanW(char ch);

    /// <summary>MapVirtualKeyW(vk, MAPVK_VK_TO_VSC) -&gt; the hardware scan code for a virtual-key code, on the current keyboard layout.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

    /// <summary>Between-character settle for TypeUnicodeText — GOAL brief step 2: "Keep ~50-100 ms settle between keys."</summary>
    public static readonly TimeSpan CharSettleDelay = TimeSpan.FromMilliseconds(75);

    /// <summary>
    /// W-T92 round 5 (Will, verbatim: "I have ... given you even the exact
    /// keystrokes that are needed through the original macro file I sent
    /// you"). Macro Express's default "Text Type" mode is "simulate
    /// keystrokes" — real VK + scan-code key-down/key-up pairs, at roughly
    /// this per-key delay, NOT KEYEVENTF_UNICODE packets (see
    /// TypeKeystrokes's own doc). Named separately from CharSettleDelay
    /// because the two methods are tuned to two different real inputs
    /// (Macro Express's own default keystroke delay vs. the original
    /// KEYEVENTF_UNICODE path's more conservative settle) — they are not
    /// meant to be the same constant.
    /// </summary>
    public static readonly TimeSpan KeystrokeCharDelay = TimeSpan.FromMilliseconds(30);

    /// <summary>
    /// Types <paramref name="text"/> into whatever currently has keyboard
    /// focus, one character at a time, via SendInput with KEYEVENTF_UNICODE
    /// (WM_CHAR-style — wVk=0, wScan=the UTF-16 code unit, no virtual-key or
    /// scan-code lookup at all, so NumLock/numpad state can never turn a
    /// digit into an arrow key). Returns false the moment SendInput reports
    /// it accepted fewer events than were sent for any single keystroke
    /// (wrong cbSize, a blocked/secure desktop, UIPI blocking a lower-
    /// integrity sender, etc.) — <paramref name="onSendFailure"/> is called
    /// with a diagnostic line so the caller can log it, and the caller MUST
    /// treat that as the whole TypeText action having failed rather than
    /// assuming the text landed.
    /// </summary>
    public static bool TypeUnicodeText(string text, Action<string>? onSendFailure = null)
    {
        var inputSize = Marshal.SizeOf<INPUT>();

        foreach (var ch in text)
        {
            var down = new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE, time = 0, dwExtraInfo = IntPtr.Zero } } };
            var sentDown = SendInput(1, new[] { down }, inputSize);
            if (sentDown != 1)
            {
                onSendFailure?.Invoke($"SendInput sent {sentDown} of 1 (key down '{ch}', err {Marshal.GetLastWin32Error()})");
                return false;
            }

            var up = new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP, time = 0, dwExtraInfo = IntPtr.Zero } } };
            var sentUp = SendInput(1, new[] { up }, inputSize);
            if (sentUp != 1)
            {
                onSendFailure?.Invoke($"SendInput sent {sentUp} of 1 (key up '{ch}', err {Marshal.GetLastWin32Error()})");
                return false;
            }

            Thread.Sleep(CharSettleDelay);
        }

        return true;
    }

    /// <summary>
    /// W-T92 round 5 (Will's own working Macro Express macro, replayed
    /// keystroke-for-keystroke — see Reports/recipes/
    /// README-macro-strings.txt and ReportParameterKeys.cs's round-5 doc).
    /// Macro Express's default "Text Type" setting is "simulate
    /// keystrokes": for each character it presses/releases a real virtual
    /// key + hardware scan code (not a KEYEVENTF_UNICODE packet), holding
    /// Shift for characters that need it, at roughly a 30 ms per-key
    /// delay. TypeUnicodeText (KEYEVENTF_UNICODE, wVk=0) was round 4's
    /// theory for why a Pioneer date field could read a digit as an arrow
    /// key — it wasn't the actual bug (see NativeInput's own class doc,
    /// the real bug was INPUT's size), but Pioneer's Report Parameters
    /// popup is still a Macro-Express-replayed field, not a generic
    /// Unicode-aware one, so matching the macro's own real-keystroke
    /// mechanism exactly is what "figure it out" means here — this is now
    /// the method PioneerReportDriver uses for both date fields.
    ///
    /// For each character: VkKeyScanW resolves the virtual-key code and
    /// required shift state on the CURRENT keyboard layout, MapVirtualKeyW
    /// resolves the matching hardware scan code, and both wVk and wScan
    /// are sent together (down, then up) — exactly like a physical key
    /// press, never scan-code-only or VK-only. A character VkKeyScanW
    /// cannot map at all (returns -1) falls back to the KEYEVENTF_UNICODE
    /// packet TypeUnicodeText uses, since there is no real key for it on
    /// this layout to press. Returns false (and calls
    /// <paramref name="onSendFailure"/> once) the moment any single
    /// SendInput call reports it accepted fewer events than were sent —
    /// same contract as TypeUnicodeText, and the caller must treat that as
    /// the whole action having failed.
    /// </summary>
    public static bool TypeKeystrokes(string text, int perKeyDelayMs, Action<string>? onSendFailure = null)
    {
        var inputSize = Marshal.SizeOf<INPUT>();

        foreach (var ch in text)
        {
            var scanResult = VkKeyScanW(ch);
            if (scanResult == -1)
            {
                // No key on this layout produces this character at all -
                // fall back to the Unicode packet path for just this one
                // character (still real down/up events, just wVk=0).
                if (!SendUnicodeCharPair(ch, inputSize, onSendFailure)) return false;
                Thread.Sleep(perKeyDelayMs);
                continue;
            }

            var (vk, needsShift) = DecodeVkKeyScan(scanResult);
            var scan = (ushort)MapVirtualKeyW(vk, MAPVK_VK_TO_VSC);

            // Non-blocking reviewer fix (round 5): guarantee Shift-up is
            // sent even if the character's own down/up fails after
            // Shift-down succeeded - a failed send must never leave Shift
            // physically "held" for every keystroke sent afterward.
            var shiftIsDown = false;
            bool charSendOk;
            try
            {
                if (needsShift)
                {
                    var shiftDownScan = (ushort)MapVirtualKeyW(VK_SHIFT, MAPVK_VK_TO_VSC);
                    if (!SendKeyEvent(VK_SHIFT, shiftDownScan, down: true, inputSize, onSendFailure, "Shift down")) return false;
                    shiftIsDown = true;
                }

                charSendOk = SendKeyEvent(vk, scan, down: true, inputSize, onSendFailure, $"key down '{ch}'")
                    && SendKeyEvent(vk, scan, down: false, inputSize, onSendFailure, $"key up '{ch}'");
            }
            finally
            {
                if (shiftIsDown)
                {
                    var shiftUpScan = (ushort)MapVirtualKeyW(VK_SHIFT, MAPVK_VK_TO_VSC);
                    SendKeyEvent(VK_SHIFT, shiftUpScan, down: false, inputSize, onSendFailure, "Shift up");
                }
            }

            if (!charSendOk) return false;

            Thread.Sleep(perKeyDelayMs);
        }

        return true;
    }

    /// <summary>
    /// Decodes a VkKeyScanW raw return value (low byte = virtual-key code;
    /// high byte's bit 0 = Shift required, bits 1/2 = Ctrl/Alt — never set
    /// for a plain digit or '-' so not decoded here) into (vk, needsShift).
    /// Pure bit math, no P/Invoke — unit-testable on any platform, including
    /// macOS, by feeding it the well-known raw values VkKeyScanW itself
    /// returns for a given character on a standard US keyboard layout (see
    /// NativeInputTests: '0'-'9' -&gt; VK codes 0x30-0x39 unshifted, matching
    /// their own ASCII code points on every layout; '-' -&gt; VK_OEM_MINUS
    /// 0xBD unshifted on a US layout) — the only part of the real
    /// VkKeyScanW/MapVirtualKeyW P/Invoke calls in TypeKeystrokes above that
    /// isn't itself a native call.
    /// </summary>
    public static (ushort Vk, bool NeedsShift) DecodeVkKeyScan(short vkKeyScanResult)
    {
        var vk = (ushort)(vkKeyScanResult & 0xFF);
        var shiftState = (byte)((vkKeyScanResult >> 8) & 0xFF);
        return (vk, (shiftState & 0x1) != 0);
    }

    /// <summary>
    /// Sends one non-character key (Tab, Arrow Down, F12, ...) as a real
    /// wVk+wScan down/up pair, same as a physical key press — W-T92 round
    /// 5's "verify Tab and F12 sends also carry wVk + wScan" fix. Callers
    /// pass a Win32 virtual-key code (FlaUI.Core.WindowsAPI.VirtualKeyShort
    /// casts directly to ushort). Returns false (and calls
    /// <paramref name="onSendFailure"/> once) the moment either SendInput
    /// call reports a short send, same contract as TypeUnicodeText/
    /// TypeKeystrokes.
    /// </summary>
    public static bool SendSpecialKey(ushort vk, Action<string>? onSendFailure = null)
    {
        var inputSize = Marshal.SizeOf<INPUT>();
        var scan = (ushort)MapVirtualKeyW(vk, MAPVK_VK_TO_VSC);

        if (!SendKeyEvent(vk, scan, down: true, inputSize, onSendFailure, "key down")) return false;
        if (!SendKeyEvent(vk, scan, down: false, inputSize, onSendFailure, "key up")) return false;
        return true;
    }

    private static bool SendKeyEvent(ushort vk, ushort scan, bool down, int inputSize, Action<string>? onSendFailure, string label)
    {
        var flags = down ? 0u : KEYEVENTF_KEYUP;
        if (IsExtendedKey(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        var input = new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero } } };
        var sent = SendInput(1, new[] { input }, inputSize);
        if (sent != 1)
        {
            onSendFailure?.Invoke($"SendInput sent {sent} of 1 ({label}, err {Marshal.GetLastWin32Error()})");
            return false;
        }
        return true;
    }

    private static bool SendUnicodeCharPair(char ch, int inputSize, Action<string>? onSendFailure)
    {
        var down = new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE, time = 0, dwExtraInfo = IntPtr.Zero } } };
        var sentDown = SendInput(1, new[] { down }, inputSize);
        if (sentDown != 1)
        {
            onSendFailure?.Invoke($"SendInput sent {sentDown} of 1 (unicode key down '{ch}', err {Marshal.GetLastWin32Error()})");
            return false;
        }

        var up = new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP, time = 0, dwExtraInfo = IntPtr.Zero } } };
        var sentUp = SendInput(1, new[] { up }, inputSize);
        if (sentUp != 1)
        {
            onSendFailure?.Invoke($"SendInput sent {sentUp} of 1 (unicode key up '{ch}', err {Marshal.GetLastWin32Error()})");
            return false;
        }

        return true;
    }
}
