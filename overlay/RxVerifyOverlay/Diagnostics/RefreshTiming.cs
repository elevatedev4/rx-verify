namespace RxVerifyOverlay.Diagnostics;

/// <summary>
/// End-to-end per-refresh timing breakdown (latency fix — Will's field
/// report that verdicts took noticeably longer to show than the ~773ms
/// OCR pipeline alone suggested). Built fresh at the start of every
/// OverlayViewModel.RefreshAsync call and populated as each stage
/// completes — see RefreshAsync/RefreshFromOcrAsync/RefreshFromUiaAsync.
///
/// Phase2Ms is populated SEPARATELY and later, once the background drug
/// lookup resolves (see OverlayViewModel.ApplyDrugResult) — it stays
/// null if Phase 2 hasn't finished yet (or was superseded by a newer
/// refresh before it could), so RxLogFormatter.FormatTimingLine can
/// render a Phase-1-only line in that case rather than a fake/zero
/// value.
/// </summary>
public sealed class RefreshTiming
{
    /// <summary>Time to find + attach the PioneerRx window (PioneerRxWindow.TryAttach).</summary>
    public long AttachMs { get; set; }

    /// <summary>
    /// Latency-fix diagnosis (uia-read-latency branch): true when
    /// TryAttach's fast path reused the previously-resolved PioneerRx
    /// window (see PioneerRxWindow.WasAttachCacheHit / AttachCacheDecision)
    /// instead of paying for a full top-level-window enumeration +
    /// disambiguation. AttachMs should read near-zero whenever this is
    /// true. Null before the first refresh sets it (mirrors Phase2Ms's
    /// "not yet known" convention).
    /// </summary>
    public bool? AttachCacheHit { get; set; }

    /// <summary>
    /// Sub-part of UiaMs (latency-fix diagnosis, uia-read-latency branch):
    /// cumulative time spent doing FRESH FindFirstDescendant walks
    /// across all ~14 entered-side fields — zero (or near it) on cache
    /// hits once Uia/EnteredFieldElementCache.cs has an element cached
    /// for every field this window's session has read so far. See
    /// Uia/FieldReader.cs LastReadFindMs.
    /// </summary>
    public long UiaFindMs { get; set; }

    /// <summary>
    /// Sub-part of UiaMs: cumulative time re-reading each field's
    /// CURRENT value (cached element or freshly found) — this cost is
    /// NOT eliminated by the element cache (see branch brief item 4:
    /// values are never cached, only re-read), so it's the floor UiaMs
    /// can reach even with a 100% cache-hit refresh. See Uia/
    /// FieldReader.cs LastReadValueMs.
    /// </summary>
    public long UiaReadMs { get; set; }

    /// <summary>
    /// Time to read the ENTERED-side fields via UIA (FieldReader.
    /// ReadEntered) — always UIA regardless of source method (Ocr vs
    /// Uia), see OverlayViewModel.RefreshAsync doc. Computed (mirroring
    /// CaptureMs) as UiaFindMs + UiaReadMs so it can never drift from
    /// its two sub-parts.
    /// </summary>
    public long UiaMs => UiaFindMs + UiaReadMs;

    /// <summary>
    /// Sub-part of CaptureMs (latency-fix diagnosis, branch brief item 2):
    /// time to resolve the screen rectangle to capture — a UIA tree walk
    /// on a cache miss, near-zero on a cache hit (Ocr/CaptureRegionCache.cs).
    /// Previously this cost existed but wasn't attributed to ANY timing
    /// bucket at all; it's broken out explicitly now instead of staying
    /// invisible inside "capture".
    /// </summary>
    public long CaptureRegionResolveMs { get; set; }

    /// <summary>Sub-part of CaptureMs: time in IOverlayVisibilityController.HideForCaptureAsync (hide the overlay + wait for the screen to repaint). Zero once SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) is active and the hide/show round-trip is skipped — see MainWindow.xaml.cs.</summary>
    public long CaptureHideWaitMs { get; set; }

    /// <summary>Sub-part of CaptureMs: the GDI Graphics.CopyFromScreen blit itself.</summary>
    public long CaptureBlitMs { get; set; }

    /// <summary>
    /// Screen-region capture time (Ocr/EscriptImageCapture.cs, via
    /// OcrFieldReader). Post-review fix: computed (mirroring
    /// Phase1TotalMs below) instead of separately set from
    /// OcrCaptureResult.CaptureMs, so this can never drift from the sum
    /// of the three sub-parts above. Zero on the Uia verification
    /// method, which never captures a screenshot (all three sub-parts
    /// stay at their default 0 in that path).
    /// </summary>
    public long CaptureMs => CaptureRegionResolveMs + CaptureHideWaitMs + CaptureBlitMs;

    /// <summary>Windows OCR recognition time (OcrFieldReader). Zero on the Uia verification method.</summary>
    public long OcrMs { get; set; }

    /// <summary>Phase 1 engine call (EngineClient.VerifyAsync with skipDrugLookup=true) — everything except the drug lookup.</summary>
    public long EngineMs { get; set; }

    /// <summary>Time to turn the Phase 1 VerifyResult into bound rows (OverlayViewModel.PopulateRows + UpdateSummary).</summary>
    public long RenderMs { get; set; }

    /// <summary>
    /// Phase 2 (background drug lookup) elapsed time, set once
    /// ApplyDrugResult applies a result for the SAME refresh generation
    /// this timing belongs to. Null until then.
    /// </summary>
    public long? Phase2Ms { get; set; }

    /// <summary>Sum of the Phase 1 segments — "detect-&gt;render" in RxLogFormatter.FormatTimingLine's output.</summary>
    public long Phase1TotalMs => AttachMs + UiaMs + CaptureMs + OcrMs + EngineMs + RenderMs;
}
