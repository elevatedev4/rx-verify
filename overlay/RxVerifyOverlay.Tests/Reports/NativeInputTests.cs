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
}
