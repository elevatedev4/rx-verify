using System;

namespace RxVerifyOverlay.Reports;

// The two places this feature talks to another window, written against a
// small host interface so the decisions (when a name may be written, when a
// key may be sent) are unit tested on any OS with a fake host. The real host
// is PioneerReportDriver's NativeSaveAsHost (user32 calls). No UI Automation,
// no typed keys for the Save As dialog unless the direct route fails and the dialog is the foreground window.

/// <summary>Win32 operations the Save As / preview-close steps need.</summary>
public interface ISaveAsNativeHost
{
    bool IsWindow(IntPtr handle);
    bool IsVisible(IntPtr handle);
    int ProcessIdOf(IntPtr handle);
    string ClassOf(IntPtr handle);

    /// <summary>True when <paramref name="child"/> is a child (any depth) of <paramref name="parent"/> (user32 IsChild).</summary>
    bool IsChildOf(IntPtr parent, IntPtr child);
    bool IsForeground(IntPtr handle);

    /// <summary>WM_SETTEXT to an Edit control. False when the message could not be delivered.</summary>
    bool SetText(IntPtr edit, string text);

    /// <summary>WM_GETTEXT from an Edit control; null when it could not be read.</summary>
    string? GetText(IntPtr edit);

    /// <summary>BM_CLICK to a button HWND. False when it could not be delivered.</summary>
    bool ClickButton(IntPtr button);

    /// <summary>Asks Windows to make this window the foreground window (it may refuse); callers re-check IsForeground afterwards.</summary>
    bool BringToForeground(IntPtr handle);

    /// <summary>Moves keyboard focus to <paramref name="control"/> inside <paramref name="dialog"/> (WM_NEXTDLGCTL) and returns true only once that control is confirmed to have focus.</summary>
    bool FocusControl(IntPtr dialog, IntPtr control);

    /// <summary>True when <paramref name="control"/> currently has keyboard focus in its own thread.</summary>
    bool IsFocused(IntPtr control);

    /// <summary>Keyboard: Ctrl+A. Goes to the focused control of the foreground window - callers check both first.</summary>
    bool SelectAllKeys();

    /// <summary>Keyboard: one character. Callers re-check foreground and focus before EVERY character.</summary>
    bool TypeCharacter(char character);

    /// <summary>Keyboard: Alt+S. Goes to the foreground window - callers only use it while IsForeground(dialog) holds.</summary>
    bool SendAltS();

    /// <summary>WM_CLOSE to a window. False when it could not be posted.</summary>
    bool PostClose(IntPtr handle);

    /// <summary>Alt+F4 to the foreground window. Callers only use it after confirming the target IS the foreground window.</summary>
    bool SendAltF4();

    /// <summary>Polls <paramref name="condition"/> until true or the timeout elapses.</summary>
    bool WaitUntil(Func<bool> condition, TimeSpan timeout);
}

public enum SaveAsEntryResult
{
    /// <summary>Name written into the dialog's own file-name Edit (WM_SETTEXT), read back equal, Save clicked with BM_CLICK.</summary>
    Clicked,

    /// <summary>Fallback: the name was typed into the focused file-name Edit while the dialog was the foreground window, read back equal, and Alt+S pressed.</summary>
    ClickedByKeyboard,

    /// <summary>The dialog changed (closed, other process/class, handles no longer its children) since it was accepted: nothing was sent.</summary>
    StaleWindow,

    /// <summary>The direct route failed and the dialog is not the foreground window: no key was sent.</summary>
    NotForeground,

    /// <summary>The direct route failed and the keyboard route stopped (focus could not be set, foreground or focus changed during typing, typing failed, or the typed name did not read back).</summary>
    KeyboardFailed,

    /// <summary>The name was set and verified but neither BM_CLICK nor Alt+S could press Save.</summary>
    ClickFailed
}

public enum SaveAsCancelResult
{
    /// <summary>The handle was zero, gone, or no longer the same process / class as when confirmed: nothing was sent.</summary>
    Skipped,

    Closed,

    /// <summary>The Cancel click and WM_CLOSE were sent but the dialog is still open.</summary>
    StillOpen
}

public static class SaveAsActions
{
    /// <summary>
    /// Acts only on the confirmed dialog's own handles. Direct route: WM_SETTEXT
    /// into its identified file-name Edit, WM_GETTEXT read-back, and ONLY when the
    /// read-back equals the path, BM_CLICK on its Save button - no foreground
    /// needed, no keys. If that fails (WM_SETTEXT refused, read-back different,
    /// BM_CLICK refused) the fallback is what Will asked for - type the name, Alt+S -
    /// under these rules: the dialog must be the foreground window (one attempt to
    /// bring it forward), the file-name Edit is given focus and confirmed focused,
    /// foreground AND focus are re-checked before EVERY character (stop at once if
    /// either changed), and Alt+S is pressed only after the Edit reads back the full
    /// path and the dialog is still the foreground window. Nothing here closes any
    /// window: the caller cancels the dialog on any non-success result.
    /// </summary>
    public static SaveAsEntryResult EnterNameAndSave(
        ISaveAsNativeHost host, SaveAsCandidate dialog, IntPtr edit, IntPtr saveButton, string path, Action<string> log)
    {
        if (dialog.Handle == IntPtr.Zero || edit == IntPtr.Zero || saveButton == IntPtr.Zero
            || !host.IsWindow(dialog.Handle)
            || !host.IsVisible(dialog.Handle)
            || host.ProcessIdOf(dialog.Handle) != dialog.ProcessId
            || !string.Equals(host.ClassOf(dialog.Handle), dialog.ClassName, StringComparison.Ordinal))
        {
            log("the dialog changed after it was accepted (closed or replaced) - sending nothing.");
            return SaveAsEntryResult.StaleWindow;
        }

        // The Edit and Save handles must still be live children of this dialog with the classes they were found with.
        if (!host.IsWindow(saveButton) || !host.IsChildOf(dialog.Handle, saveButton)
            || !string.Equals(host.ClassOf(saveButton), "Button", StringComparison.OrdinalIgnoreCase)
            || !host.IsWindow(edit) || !host.IsChildOf(dialog.Handle, edit)
            || !string.Equals(host.ClassOf(edit), "Edit", StringComparison.OrdinalIgnoreCase))
        {
            log("the File name box or Save button is no longer a child of the dialog - sending nothing.");
            return SaveAsEntryResult.StaleWindow;
        }

        var nameVerified = false;
        if (!host.SetText(edit, path))
        {
            log("could not write the file name into the dialog (WM_SETTEXT not delivered).");
        }
        else
        {
            var readBack = host.GetText(edit);
            if (SaveAsPathBuilder.TextMatches(readBack, path))
            {
                nameVerified = true;
                log("file name written (WM_SETTEXT) and read back (matched).");
            }
            else
            {
                log($"file name read-back did not match (read-back length {readBack?.Length ?? 0}, expected {path.Length}).");
            }
        }

        if (nameVerified)
        {
            if (host.ClickButton(saveButton))
            {
                log("Save clicked (BM_CLICK).");
                return SaveAsEntryResult.Clicked;
            }

            log("could not click Save (BM_CLICK not delivered).");
        }

        // ---- keyboard route: only while THIS dialog is the foreground window ---------
        if (!EnsureForeground(host, dialog.Handle))
        {
            log("the direct route did not complete and the dialog is not the foreground window - sending no keys.");
            return SaveAsEntryResult.NotForeground;
        }

        if (!nameVerified)
        {
            if (!host.FocusControl(dialog.Handle, edit))
            {
                log("keyboard route: could not give the File name box keyboard focus - sending no keys.");
                return SaveAsEntryResult.KeyboardFailed;
            }

            log("keyboard route: dialog is the foreground window and the File name box has focus - typing the file name.");
            if (!KeyAllowed(host, dialog.Handle, edit) || !host.SelectAllKeys())
            {
                log("keyboard route: foreground or focus changed before typing - stopping.");
                return SaveAsEntryResult.KeyboardFailed;
            }

            for (var i = 0; i < path.Length; i++)
            {
                if (!KeyAllowed(host, dialog.Handle, edit))
                {
                    log($"keyboard route: the dialog lost the foreground or the File name box lost focus after {i} of {path.Length} characters - stopping.");
                    return SaveAsEntryResult.KeyboardFailed;
                }

                if (!host.TypeCharacter(path[i]))
                {
                    log($"keyboard route: typing failed at character {i + 1} of {path.Length}.");
                    return SaveAsEntryResult.KeyboardFailed;
                }
            }
        }

        // Alt+S only when the Edit holds the full name and the dialog is still the foreground window.
        var finalText = host.GetText(edit);
        if (!SaveAsPathBuilder.TextMatches(finalText, path))
        {
            log($"keyboard route: the File name box does not hold the full name (length {finalText?.Length ?? 0}, expected {path.Length}) - not pressing Save.");
            return SaveAsEntryResult.KeyboardFailed;
        }

        if (!host.IsForeground(dialog.Handle))
        {
            log("keyboard route: the dialog lost the foreground before Alt+S - sending no further keys.");
            return SaveAsEntryResult.KeyboardFailed;
        }

        if (!host.SendAltS())
        {
            log("keyboard route: Alt+S could not be sent.");
            return nameVerified ? SaveAsEntryResult.ClickFailed : SaveAsEntryResult.KeyboardFailed;
        }

        log("Save pressed (Alt+S, keyboard route).");
        return SaveAsEntryResult.ClickedByKeyboard;
    }

    private static bool KeyAllowed(ISaveAsNativeHost host, IntPtr dialog, IntPtr edit) => host.IsForeground(dialog) && host.IsFocused(edit);

    private static bool EnsureForeground(ISaveAsNativeHost host, IntPtr handle)
    {
        if (host.IsForeground(handle)) return true;
        host.BringToForeground(handle);
        return host.WaitUntil(() => host.IsForeground(handle), TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Cancels the CONFIRMED dialog so Pioneer is not left stuck on it: Cancel
    /// button (BM_CLICK), then WM_CLOSE to the same handle if it is still open.
    /// Before each send the handle is re-checked to still be a visible window of
    /// the same process and class as when it was confirmed (a recycled handle
    /// gets nothing). A zero handle (nothing was confirmed) sends nothing.
    /// </summary>
    public static SaveAsCancelResult CancelDialog(
        ISaveAsNativeHost host, SaveAsCandidate dialog, IntPtr cancelButton, TimeSpan confirmTimeout, Action<string> log)
    {
        bool StillOurs() =>
            dialog.Handle != IntPtr.Zero
            && host.IsWindow(dialog.Handle)
            && host.IsVisible(dialog.Handle)
            && host.ProcessIdOf(dialog.Handle) == dialog.ProcessId
            && string.Equals(host.ClassOf(dialog.Handle), dialog.ClassName, StringComparison.Ordinal);

        if (!StillOurs())
        {
            log("cancel: the dialog is gone or is no longer the window that was confirmed - sending nothing.");
            return SaveAsCancelResult.Skipped;
        }

        log("cancelling the Save As dialog so Pioneer is not left stuck on it.");
        bool Gone() => !host.IsWindow(dialog.Handle) || !host.IsVisible(dialog.Handle);

        var pressed = false;
        if (cancelButton != IntPtr.Zero && host.IsWindow(cancelButton) && host.IsChildOf(dialog.Handle, cancelButton)
            && string.Equals(host.ClassOf(cancelButton), "Button", StringComparison.OrdinalIgnoreCase))
        {
            pressed = host.ClickButton(cancelButton);
        }

        if (pressed && host.WaitUntil(Gone, confirmTimeout))
        {
            log("Save As dialog closed (Cancel, confirmed).");
            return SaveAsCancelResult.Closed;
        }

        if (!StillOurs())
        {
            log("Save As dialog closed (confirmed).");
            return SaveAsCancelResult.Closed;
        }

        log(pressed ? "Cancel did not close it - sending WM_CLOSE to the same dialog." : "no usable Cancel button - sending WM_CLOSE to the dialog.");
        host.PostClose(dialog.Handle);
        if (host.WaitUntil(Gone, confirmTimeout))
        {
            log("Save As dialog closed (WM_CLOSE, confirmed).");
            return SaveAsCancelResult.Closed;
        }

        log($"Save As dialog still open {confirmTimeout.TotalSeconds:0}s after the cancel request.");
        return SaveAsCancelResult.StillOpen;
    }
}

public enum PreviewCloseOutcome
{
    /// <summary>No handle, or the window was already gone.</summary>
    AlreadyClosed,

    /// <summary>The handle is not a Pioneer-process window: refused, nothing sent.</summary>
    NotPioneerWindow,

    ClosedByMessage,
    ClosedByAltF4,

    /// <summary>Still open after the message and (when allowed) Alt+F4.</summary>
    StillOpen
}

public static class PreviewCloser
{
    /// <summary>
    /// Closes the report preview. WM_CLOSE to that exact HWND first (after
    /// IsWindow and a check that it belongs to Pioneer's process); Alt+F4
    /// only if the window is STILL open after <paramref name="timeout"/> AND
    /// is the foreground window right now.
    /// </summary>
    public static PreviewCloseOutcome Close(ISaveAsNativeHost host, IntPtr handle, int pioneerProcessId, TimeSpan timeout, Action<string> log)
    {
        if (handle == IntPtr.Zero || !host.IsWindow(handle)) return PreviewCloseOutcome.AlreadyClosed;

        if (pioneerProcessId == 0 || host.ProcessIdOf(handle) != pioneerProcessId)
        {
            log("the report preview handle is not a Pioneer window - leaving it alone.");
            return PreviewCloseOutcome.NotPioneerWindow;
        }

        bool Gone() => !host.IsWindow(handle) || !host.IsVisible(handle);

        host.PostClose(handle);
        if (host.WaitUntil(Gone, timeout)) return PreviewCloseOutcome.ClosedByMessage;

        if (host.IsWindow(handle) && host.IsVisible(handle) && host.IsForeground(handle))
        {
            log("report preview still open after the close message and it is the foreground window - sending Alt+F4.");
            host.SendAltF4();
            return host.WaitUntil(Gone, timeout) ? PreviewCloseOutcome.ClosedByAltF4 : PreviewCloseOutcome.StillOpen;
        }

        return PreviewCloseOutcome.StillOpen;
    }
}
