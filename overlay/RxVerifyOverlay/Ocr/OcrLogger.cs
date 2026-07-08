using System;
using System.IO;
using System.Text;

namespace RxVerifyOverlay.Ocr;

/// <summary>
/// The headline v0 deliverable (see branch brief item 3): appends one
/// block per OCR read to a per-day log file under
/// %TEMP%\VerifyOCR\ocr-&lt;yyyyMMdd&gt;.log, containing the timestamp,
/// capture/OCR/total elapsed milliseconds, char count, and the FULL raw
/// OCR text — this is how Will proves &lt;1s end-to-end and judges text
/// quality against his real screen, without needing to reproduce a read
/// live in front of anyone. Also mirrored live in the overlay UI (status
/// line + raw-text expander — see ViewModels/OverlayViewModel.cs
/// OcrStatusText/LastOcrRawText and MainWindow.xaml).
///
/// %TEMP% (not %AppData%, unlike OverlaySettings) is deliberate: this is
/// a diagnostic log the owner will want to periodically clear, and %TEMP%
/// is the conventional place for exactly that kind of file. It CAN
/// contain real patient/prescriber data (it's a literal transcript of
/// whatever was on screen) — same handling caveat as the existing
/// "Dump UIA Tree" feature (see MainWindow.xaml.cs OnDumpTreeClick):
/// local-only, never transmitted, but the owner should clear it per his
/// usual workstation policy.
///
/// BOUNDED PHI LOG (post-review fix): raw OCR text is patient/prescriber
/// PHI (name, DOB, address, NPI, drug, sig), and auto-watch re-reads the
/// same on-screen Rx roughly once a second while it's open — logging
/// every single read unconditionally would grow this file unbounded, all
/// PHI, for no diagnostic benefit once a read is a pure repeat. Two
/// guards, applied in DedupAndBoundedAppend:
///   1. DE-DUP: a read whose raw text is byte-identical to the last
///      LOGGED read is skipped entirely (not even a "duplicate" marker
///      line — nothing new to review). Resets only when the OCR text
///      actually changes (a different Rx opened, the pharmacist scrolled/
///      edited the on-screen script, etc.), so every genuinely NEW read
///      still gets logged in full.
///   2. SIZE CAP: before any append, if today's file has already grown
///      past ~5 MB, it's rotated (truncated to a short marker line) so a
///      long shift with lots of distinct reads can't grow the file
///      without bound. This is a simple truncate-in-place, not a
///      numbered .1/.2 rollover — a v0 diagnostic log doesn't need
///      history depth beyond "the file didn't grow forever."
/// </summary>
public static class OcrLogger
{
    /// <summary>Per-day log file size cap before rotation (truncation) kicks in — see class doc "SIZE CAP".</summary>
    private const long MaxLogFileBytes = 5 * 1024 * 1024; // ~5 MB

    private static readonly object LockObj = new();

    /// <summary>Raw text of the last successfully LOGGED read (not every read — see class doc "DE-DUP"). Null until the first log call this process.</summary>
    private static string? _lastLoggedRawText;

    private static string LogDirectory => Path.Combine(Path.GetTempPath(), "VerifyOCR");

    private static string LogFilePath => Path.Combine(LogDirectory, $"ocr-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>
    /// Appends one successful-read block, subject to the de-dup + size
    /// cap guards above. Never throws — logging must never crash the
    /// overlay (see OcrFieldReader.ReadSourceFromOcrAsync, which calls
    /// this from inside its own try).
    /// </summary>
    public static void LogRead(long captureMs, long ocrMs, long totalMs, string rawText)
    {
        try
        {
            lock (LockObj)
            {
                // DE-DUP: compare-and-set inside the same lock as the
                // write below, so two near-simultaneous calls with
                // identical text can't both slip past the check before
                // either updates _lastLoggedRawText.
                if (_lastLoggedRawText == rawText) return;
                _lastLoggedRawText = rawText;

                Directory.CreateDirectory(LogDirectory);
                RotateIfOversized();

                var sb = new StringBuilder();
                sb.AppendLine("=====================================================");
                sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] OCR read");
                sb.AppendLine($"capture_ms={captureMs} ocr_ms={ocrMs} total_ms={totalMs} chars={rawText.Length}");
                sb.AppendLine("--- raw text ---");
                sb.AppendLine(rawText);
                sb.AppendLine("--- end raw text ---");
                sb.AppendLine();

                File.AppendAllText(LogFilePath, sb.ToString());
            }
        }
        catch
        {
            // Best-effort diagnostic logging only — a locked/unwritable
            // log file must never take down a live verification pass.
        }
    }

    /// <summary>Appends a capture/OCR failure block — see OcrFieldReader.ReadSourceFromOcrAsync's catch. Not subject to the de-dup check (errors are rare and each one is worth a line), but still subject to the size cap.</summary>
    public static void LogError(Exception ex)
    {
        try
        {
            lock (LockObj)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfOversized();

                var sb = new StringBuilder();
                sb.AppendLine("=====================================================");
                sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] OCR ERROR: {ex}");
                sb.AppendLine();

                File.AppendAllText(LogFilePath, sb.ToString());
            }
        }
        catch
        {
            // Same best-effort guarantee as LogRead above.
        }
    }

    /// <summary>
    /// Truncates today's log file to a short rotation marker if it's
    /// already past MaxLogFileBytes, so the append immediately after this
    /// call starts a fresh (small) file instead of piling onto an
    /// unbounded one. Must be called from inside `lock (LockObj)` — not
    /// re-entrant-safe on its own. Best-effort: if rotation itself fails
    /// (e.g. a transient file lock), falls through and lets the normal
    /// append happen anyway — a bit of extra size beats losing the read
    /// entirely.
    /// </summary>
    private static void RotateIfOversized()
    {
        try
        {
            var path = LogFilePath;
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxLogFileBytes)
            {
                File.WriteAllText(path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] --- log rotated: exceeded {MaxLogFileBytes / (1024 * 1024)} MB, earlier entries in this file truncated ---{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // Best-effort — see doc above.
        }
    }
}
