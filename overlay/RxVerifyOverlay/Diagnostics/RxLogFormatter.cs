using System.Text;

namespace RxVerifyOverlay.Diagnostics;

/// <summary>
/// Formats one RxLogSnapshot into the single copy-pasteable text blob
/// behind the "Copy logs" button (MainWindow.xaml/.cs OnCopyLogsClick) —
/// per Will's ask: everything needed to debug the CURRENT Rx (raw OCR
/// text + word geometry, parsed/mapped fields, match verdicts, warnings/
/// errors) in one clipboard copy, nothing accumulated across scripts. A
/// pure function of its input (no file/clipboard/UI access here) so it's
/// directly unit-testable without standing up a whole OverlayViewModel —
/// see RxVerifyOverlay.Tests/RxLogFormatterTests.cs.
/// </summary>
public static class RxLogFormatter
{
    public static string BuildLogBlob(RxLogSnapshot s)
    {
        var sb = new StringBuilder();

        sb.AppendLine("=== Rx Verify — copied log ===");
        sb.AppendLine($"Captured: {s.CapturedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"App version: {s.AppVersion}    Commit: {s.CommitSha}");
        sb.AppendLine($"Method: {s.Method}");
        if (!string.IsNullOrEmpty(s.RxWindowTitle))
        {
            sb.AppendLine($"Rx window: {s.RxWindowTitle}");
        }
        sb.AppendLine($"Status: {s.StatusMessage}");
        sb.AppendLine();

        sb.AppendLine("--- Verdicts ---");
        sb.AppendLine($"Summary: {s.GreenCount} green / {s.YellowCount} yellow / {s.RedCount} red");
        foreach (var category in s.Categories)
        {
            sb.AppendLine($"[{category.Name} — {category.StatusText}]");
            if (category.Rows.Count == 0)
            {
                sb.AppendLine("  (no data)");
                continue;
            }

            foreach (var row in category.Rows)
            {
                sb.AppendLine($"  {row.DisplayName} ({row.FieldKey}): {row.Status}");
                sb.AppendLine($"    source=\"{row.SourceValue}\"  entered=\"{row.EnteredValue}\"");
                if (!string.IsNullOrEmpty(row.ReasonCode) || !string.IsNullOrEmpty(row.Explanation))
                {
                    sb.AppendLine($"    reason=[{row.ReasonCode}] {row.Explanation}");
                }
            }
        }
        sb.AppendLine();

        if (s.Notes.Count > 0)
        {
            sb.AppendLine("--- E-script notes ---");
            foreach (var note in s.Notes)
            {
                sb.AppendLine($"  {note}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("--- OCR ---");
        sb.AppendLine(string.IsNullOrEmpty(s.OcrStatusText) ? "(no OCR read yet)" : s.OcrStatusText);
        if (!string.IsNullOrEmpty(s.RawOcrText))
        {
            sb.AppendLine("Raw OCR text:");
            sb.AppendLine(s.RawOcrText);
        }
        if (s.OcrWords is { Count: > 0 })
        {
            sb.AppendLine($"OCR words ({s.OcrWords.Count}), text @ (x, y, w, h):");
            foreach (var word in s.OcrWords)
            {
                sb.AppendLine($"  \"{word.Text}\" @ ({word.X:0}, {word.Y:0}, {word.W:0}, {word.H:0})");
            }
        }

        return sb.ToString();
    }
}
