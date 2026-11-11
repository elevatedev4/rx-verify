using System;

namespace RxVerifyOverlay.Reports;

// The two places this feature talks to another window, written against a
// small host interface so the decisions (when a name may be written, when a
// key may be sent) are unit tested on any OS with a fake host. The real host
// is PioneerReportDriver's NativeSaveAsHost (user32 calls). No UI Automation,
// no typed keys for the Save As dialog at all.

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

    /// <summary>Keyboard: select all, then type the text. Goes to whatever is focused - callers only use it while IsForeground(dialog) holds.</summary>
    bool TypeFileName(string text);

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

    /// <summary>Fallback: the dialog was the foreground window; the name was typed and Alt+S pressed.</summary>
    ClickedByKeyboard,

    /// <summary>The dialog changed (closed, other process/class) since it was accepted: nothing was sent.</summary>
    StaleWindow,

    /// <summary>The direct route failed (WM_SETTEXT undeliverable, read-back different, no identified file-name box) and the dialog is not the foreground window: no key was sent.</summary>
    NotForeground,

    /// <summary>The direct route failed and the keyboard route could not be completed (typing failed, or the dialog lost the foreground before Alt+S).</summary>
    KeyboardFailed,

    /// <summary>The name was set and verified but neither BM_CLICK nor Alt+S could press Save.</summary>
    ClickFailed
}

public static class SaveAsActions
{
    /// <summary>
    /// Acts only on the confirmed dialog's own handles. Direct route: WM_SETTEXT
    /// into its file-name Edit, WM_GETTEXT read-back, and ONLY when the read-back
    /// equals the path, BM_CLICK on its Save button - no foreground needed, no
    /// keys. If that route fails (no identified Edit, WM_SETTEXT refused,
    /// read-back different, BM_CLICK refused) the fallback is exactly what Will
    /// asked for - type the name, Alt+S - and it runs ONLY while the dialog is
    /// the foreground window (one attempt to bring it forward first), re-checked
    /// before the typing and again before Alt+S. Nothing here closes any window:
    /// the caller cancels the dialog on any non-success result.
    /// <paramref name="edit"/> is IntPtr.Zero when no file-name Edit could be identified.
    /// </summary>
    public static SaveAsEntryResult EnterNameAndSave(
        ISaveAsNativeHost host, SaveAsCandidate dialog, IntPtr edit, IntPtr saveButton, string path, Action<string> log)
    {
        if (!host.IsWindow(dialog.Handle)
            || !host.IsVisible(dialog.Handle)
            || host.ProcessIdOf(dialog.Handle) != dialog.ProcessId
            || !string.Equals(host.ClassOf(dialog.Handle), dialog.ClassName, StringComparison.Ordinal))
        {
            log("the dialog changed after it was accepted (closed or replaced) - sending nothing.");
            return SaveAsEntryResult.StaleWindow;
        }

        // The Save button (and the Edit, when one was identified) must still be live children of this dialog with the classes they were found with.
        var hasEdit = edit != IntPtr.Zero;
        if (!host.IsWindow(saveButton) || !host.IsChildOf(dialog.Handle, saveButton)
            || !string.Equals(host.ClassOf(saveButton), "Button", StringComparison.OrdinalIgnoreCase)
            || (hasEdit && (!host.IsWindow(edit) || !host.IsChildOf(dialog.Handle, edit)
                            || !string.Equals(host.ClassOf(edit), "Edit", StringComparison.OrdinalIgnoreCase))))
        {
            log("the File name box or Save button is no longer a child of the dialog - sending nothing.");
            return SaveAsEntryResult.StaleWindow;
        }

        var nameVerified = false;
        if (!hasEdit)
        {
            log("no file-name box could be identified by control id - going straight to the keyboard route.");
        }
        else if (!host.SetText(edit, path))
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
            log("keyboard route: dialog is the foreground window - typing the file name.");
            if (!host.TypeFileName(path))
            {
                log("keyboard route: typing the file name failed.");
                return SaveAsEntryResult.KeyboardFailed;
            }

            if (hasEdit)
            {
                var typed = host.GetText(edit);
                log(SaveAsPathBuilder.TextMatches(typed, path)
                    ? "keyboard route: typed file name read back (matched)."
                    : $"keyboard route: typed file name read back different (length {typed?.Length ?? 0}, expected {path.Length}) - not pressing Save.");
                if (!SaveAsPathBuilder.TextMatches(typed, path)) return SaveAsEntryResult.KeyboardFailed;
            }
        }

        // Re-check immediately before the Alt+S: focus may have moved while typing.
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

    private static bool EnsureForeground(ISaveAsNativeHost host, IntPtr handle)
    {
        if (host.IsForeground(handle)) return true;
        host.BringToForeground(handle);
        return host.WaitUntil(() => host.IsForeground(handle), TimeSpan.FromSeconds(1));
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
