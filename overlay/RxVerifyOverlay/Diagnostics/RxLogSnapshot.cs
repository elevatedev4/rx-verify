using System;
using System.Collections.Generic;
using RxVerifyOverlay.Models;

namespace RxVerifyOverlay.Diagnostics;

/// <summary>
/// Plain, ViewModel-independent input to RxLogFormatter.BuildLogBlob — see
/// that class for why this stays a separate DTO (keeps the formatter a
/// pure, directly unit-testable function; see
/// RxVerifyOverlay.Tests/RxLogFormatterTests.cs). Built fresh every time
/// OverlayViewModel.BuildCurrentLogBlob() runs, entirely from whatever is
/// ALREADY bound to the overlay UI at that instant (Categories/Rows,
/// OcrStatusText, LastOcrRawText, Notes, StatusMessage, summary counts) —
/// nothing here is stored/accumulated separately, so the copied blob
/// always reflects only the Rx currently on screen and never grows a
/// history across scripts (see the "Copy logs" button brief: "not save
/// anything else").
/// </summary>
public sealed class RxLogSnapshot
{
    public DateTime CapturedAt { get; init; }
    public string AppVersion { get; init; } = "unknown";
    public string CommitSha { get; init; } = "unknown";
    public string Method { get; init; } = "";
    public string? RxWindowTitle { get; init; }
    public string StatusMessage { get; init; } = "";

    public string? OcrStatusText { get; init; }
    public string? RawOcrText { get; init; }
    public IReadOnlyList<OcrWord>? OcrWords { get; init; }

    public IReadOnlyList<RxLogCategorySnapshot> Categories { get; init; } = Array.Empty<RxLogCategorySnapshot>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public int GreenCount { get; init; }
    public int YellowCount { get; init; }
    public int RedCount { get; init; }
}

/// <summary>One rolled-up category (Patient/Prescriber/Rx/Sig) and its field rows, mirroring ViewModels/OverlayViewModel.cs CategoryViewModel/VerdictRowViewModel but decoupled from WPF binding.</summary>
public sealed record RxLogCategorySnapshot(string Name, string StatusText, IReadOnlyList<RxLogFieldSnapshot> Rows);

/// <summary>One field's verdict — source/entered values, status, and the reason code/explanation that's normally hidden behind a row hover tooltip (see MainWindow.xaml VerdictRowViewModel.TooltipText).</summary>
public sealed record RxLogFieldSnapshot(
    string FieldKey,
    string DisplayName,
    string Status,
    string SourceValue,
    string EnteredValue,
    string ReasonCode,
    string Explanation);
