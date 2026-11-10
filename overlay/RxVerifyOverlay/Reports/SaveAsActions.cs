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

    /// <summary>WM_CLOSE to a window. False when it could not be posted.</summary>
    bool PostClose(IntPtr handle);

    /// <summary>Alt+F4 to the foreground window. Callers only use it after confirming the target IS the foreground window.</summary>
    bool SendAltF4();

    /// <summary>Polls <paramref name="condition"/> until true or the timeout elapses.</summary>
    bool WaitUntil(Func<bool> condition, TimeSpan timeout);
}

public enum SaveAsEntryResult
{
    /// <summary>Name written, read back equal, Save clicked.</summary>
    Clicked,

    /// <summary>The dialog changed (closed, other process/class, lost the foreground) since it was accepted: nothing was sent.</summary>
    StaleWindow,

    /// <summary>WM_SETTEXT could not be delivered; the dialog was closed with WM_CLOSE.</summary>
    SetTextFailed,

    /// <summary>The read-back did not equal the target path; the dialog was closed with WM_CLOSE and Save was NOT clicked.</summary>
    TextMismatch,

    /// <summary>BM_CLICK could not be delivered; the dialog was closed with WM_CLOSE.</summary>
    ClickFailed
}

public static class SaveAsActions
{
    /// <summary>
    /// Writes <paramref name="path"/> into the dialog's file-name Edit with
    /// WM_SETTEXT, reads it back with WM_GETTEXT and, ONLY when it equals the
    /// path, clicks the Save button with BM_CLICK. Any other outcome closes
    /// the dialog with WM_CLOSE and sends no keys. Before anything is sent
    /// the dialog is re-validated (still a window, still visible, same pid
    /// and class as when it was accepted, still the foreground window).
    /// </summary>
    public static SaveAsEntryResult EnterNameAndSave(
        ISaveAsNativeHost host, SaveAsCandidate dialog, IntPtr edit, IntPtr saveButton, string path, Action<string> log)
    {
        if (!host.IsWindow(dialog.Handle)
            || !host.IsVisible(dialog.Handle)
            || host.ProcessIdOf(dialog.Handle) != dialog.ProcessId
            || !string.Equals(host.ClassOf(dialog.Handle), dialog.ClassName, StringComparison.Ordinal)
            || !host.IsForeground(dialog.Handle))
        {
            log("the dialog changed after it was accepted (closed, replaced or lost the foreground) - sending nothing.");
            return SaveAsEntryResult.StaleWindow;
        }

        // The Edit and Save handles must still be live children of this dialog with the classes they were found with.
        if (!host.IsWindow(edit) || !host.IsChildOf(dialog.Handle, edit)
            || !string.Equals(host.ClassOf(edit), "Edit", StringComparison.OrdinalIgnoreCase)
            || !host.IsWindow(saveButton) || !host.IsChildOf(dialog.Handle, saveButton)
            || !string.Equals(host.ClassOf(saveButton), "Button", StringComparison.OrdinalIgnoreCase))
        {
            log("the File name box or Save button is no longer a child of the dialog - sending nothing.");
            return SaveAsEntryResult.StaleWindow;
        }

        if (!host.SetText(edit, path))
        {
            log("could not write the file name into the dialog (WM_SETTEXT not delivered) - closing the dialog.");
            host.PostClose(dialog.Handle);
            return SaveAsEntryResult.SetTextFailed;
        }

        var readBack = host.GetText(edit);
        if (!SaveAsPathBuilder.TextMatches(readBack, path))
        {
            log($"file name read-back did not match (read-back length {readBack?.Length ?? 0}, expected {path.Length}) - closing the dialog without saving.");
            host.PostClose(dialog.Handle);
            return SaveAsEntryResult.TextMismatch;
        }

        log("file name written and read back (matched).");

        if (!host.ClickButton(saveButton))
        {
            log("could not click Save (BM_CLICK not delivered) - closing the dialog.");
            host.PostClose(dialog.Handle);
            return SaveAsEntryResult.ClickFailed;
        }

        log("Save clicked.");
        return SaveAsEntryResult.Clicked;
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
