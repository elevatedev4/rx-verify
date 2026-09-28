using System;
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    /// <summary>Between-character settle for TypeUnicodeText — GOAL brief step 2: "Keep ~50-100 ms settle between keys."</summary>
    public static readonly TimeSpan CharSettleDelay = TimeSpan.FromMilliseconds(75);

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
}
