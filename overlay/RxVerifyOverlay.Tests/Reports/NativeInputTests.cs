using System.Runtime.InteropServices;
using RxVerifyOverlay.Reports;
using Xunit;

namespace RxVerifyOverlay.Tests.Reports;

/// <summary>
/// Unit tests for Reports/NativeInput.cs — pure struct-layout math, no
/// actual SendInput/user32.dll call involved (Marshal.SizeOf is computed by
/// the CLR from the struct's own field layout, so this runs and asserts a
/// real value on any platform, including macOS).
///
/// Reviewer round 4 (blocking finding 1): the original InputUnion declared
/// only KEYBDINPUT, so Marshal.SizeOf&lt;INPUT&gt;() came out to 32 bytes on
/// x64 instead of the real Win32 struct's 40 (28 on x86) — SendInput
/// validates cbSize strictly and silently sends nothing on a mismatch. This
/// test pins the fix so a future edit can never silently drop a union
/// member and reintroduce the bug.
/// </summary>
public class NativeInputTests
{
    [Fact]
    public void SizeOfInputMatchesNativeWin32Layout()
    {
        var expected = IntPtr.Size == 8 ? 40 : 28;

        Assert.Equal(expected, Marshal.SizeOf<NativeInput.INPUT>());
    }

    [Fact]
    public void SizeOfInputUnionIsSizedToItsLargestMember()
    {
        // MOUSEINPUT is the largest of the three union members (32 bytes on
        // x64, 24 on x86) — the union (and so INPUT) must be at least that
        // big, not just big enough for KEYBDINPUT (24/16 bytes).
        var expectedUnion = IntPtr.Size == 8 ? 32 : 24;

        Assert.Equal(expectedUnion, Marshal.SizeOf<NativeInput.InputUnion>());
    }

    [Fact]
    public void SizeOfKeybdinputIsUnchangedFromTheOriginalCorrectStruct()
    {
        // KEYBDINPUT itself was always correct - only the union wrapping it
        // was wrong. Pinned here so a future edit to KEYBDINPUT's own
        // fields is caught too.
        var expectedKeybdinput = IntPtr.Size == 8 ? 24 : 16;

        Assert.Equal(expectedKeybdinput, Marshal.SizeOf<NativeInput.KEYBDINPUT>());
    }

    // --- NativeInput.DecodeVkKeyScan (W-T92 round 5) ---
    //
    // VkKeyScanW itself is a real user32.dll P/Invoke and cannot run on
    // macOS/non-Windows CI, so these tests don't call it - they feed
    // DecodeVkKeyScan the well-known RAW SHORT values VkKeyScanW itself
    // returns for each character on a standard US keyboard layout, and
    // assert the (vk, needsShift) it decodes out of that raw value. '0'-'9'
    // and '-' are exactly the characters ReportDateKeys' "MM-dd-yyyy"/
    // "MM-dd-yy" date text ever contains, so this is the whole character
    // set NativeInput.TypeKeystrokes needs to resolve correctly for a date.

    [Theory]
    [InlineData('0', (short)0x0030, (ushort)0x30)]
    [InlineData('1', (short)0x0031, (ushort)0x31)]
    [InlineData('2', (short)0x0032, (ushort)0x32)]
    [InlineData('3', (short)0x0033, (ushort)0x33)]
    [InlineData('4', (short)0x0034, (ushort)0x34)]
    [InlineData('5', (short)0x0035, (ushort)0x35)]
    [InlineData('6', (short)0x0036, (ushort)0x36)]
    [InlineData('7', (short)0x0037, (ushort)0x37)]
    [InlineData('8', (short)0x0038, (ushort)0x38)]
    [InlineData('9', (short)0x0039, (ushort)0x39)]
    public void DecodeVkKeyScanResolvesDigitsToTheirOwnAsciiValueUnshifted(char digit, short rawVkKeyScanResult, ushort expectedVk)
    {
        // Win32 defines VK_0..VK_9 to equal the ASCII code points '0'..'9'
        // on every keyboard layout - not layout-specific, unlike letters.
        Assert.Equal((ushort)digit, expectedVk);

        var (vk, needsShift) = NativeInput.DecodeVkKeyScan(rawVkKeyScanResult);

        Assert.Equal(expectedVk, vk);
        Assert.False(needsShift);
    }

    [Fact]
    public void DecodeVkKeyScanResolvesDashToVkOemMinusUnshifted()
    {
        // VK_OEM_MINUS (0xBD) - the standard US-layout '-'/'_' key.
        // VkKeyScanW returns this unshifted for '-' on that layout (Shift
        // would be needed for '_' instead, a different character).
        const ushort vkOemMinus = 0xBD;

        var (vk, needsShift) = NativeInput.DecodeVkKeyScan((short)vkOemMinus);

        Assert.Equal(vkOemMinus, vk);
        Assert.False(needsShift);
    }

    [Fact]
    public void DecodeVkKeyScanReportsShiftRequiredWhenTheHighByteSetsBitZero()
    {
        // Any VK with the shift-state high byte's bit 0 set (e.g. 0x0130 -
        // Shift+'0', not a real date character, but exercises the decode
        // path a real shifted character like ':' would take).
        var (vk, needsShift) = NativeInput.DecodeVkKeyScan(0x0130);

        Assert.Equal((ushort)0x30, vk);
        Assert.True(needsShift);
    }

    // --- NativeInput.IsExtendedKey (reviewer round 5, blocking finding 1) ---
    //
    // MapVirtualKeyW(VK_DOWN, MAPVK_VK_TO_VSC) returns the BASE scan code,
    // which Windows shares with the NumPad-2 key - without
    // KEYEVENTF_EXTENDEDKEY set, whether the OS delivers the keystroke as
    // the arrow key or as NumPad '2' depends on the target's NumLock state.
    // Concrete failing scenario this fixes: NumLock ON -> Inventory
    // Valuation's trailing-keys plan (Tab, ArrowDown, F12, F12) delivers
    // its ArrowDown as NumPad '2' instead. Pure set-membership, no
    // P/Invoke - runs on any platform.

    [Theory]
    [InlineData((ushort)0x21)] // VK_PRIOR (Page Up)
    [InlineData((ushort)0x22)] // VK_NEXT (Page Down)
    [InlineData((ushort)0x23)] // VK_END
    [InlineData((ushort)0x24)] // VK_HOME
    [InlineData((ushort)0x25)] // VK_LEFT
    [InlineData((ushort)0x26)] // VK_UP
    [InlineData((ushort)0x27)] // VK_RIGHT
    [InlineData((ushort)0x28)] // VK_DOWN - the exact key ReplayReportParameterKeyPlan sends for Inventory Valuation's TrailingKeys
    [InlineData((ushort)0x2D)] // VK_INSERT
    [InlineData((ushort)0x2E)] // VK_DELETE
    [InlineData((ushort)0x6F)] // VK_DIVIDE (NumPad /)
    [InlineData((ushort)0x90)] // VK_NUMLOCK
    [InlineData((ushort)0xA3)] // VK_RCONTROL
    [InlineData((ushort)0xA5)] // VK_RMENU (right Alt)
    public void IsExtendedKeyIsTrueForEveryExtendedVirtualKey(ushort vk)
    {
        Assert.True(NativeInput.IsExtendedKey(vk));
    }

    [Theory]
    [InlineData((ushort)0x09)] // VK_TAB - ReplayReportParameterKeyPlan's Tab
    [InlineData((ushort)0x7B)] // VK_F12 - ReplayReportParameterKeyPlan's F12 (the "run report" key)
    [InlineData((ushort)0x30)] // '0' - a plain digit, TypeKeystrokes' own date-character path
    [InlineData((ushort)0xBD)] // VK_OEM_MINUS ('-') - the date separator character
    [InlineData((ushort)0x10)] // VK_SHIFT itself is never extended
    public void IsExtendedKeyIsFalseForVkTabVkF12AndDateCharacters(ushort vk)
    {
        Assert.False(NativeInput.IsExtendedKey(vk));
    }
}
