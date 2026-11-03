using System;
using System.Collections.Generic;

namespace RxVerifyOverlay.OrderAssist.Ocr;

/// <summary>
/// ORDER ASSIST "TEXT ONLY, IGNORE COLORS" row reading (Will, verbatim:
/// "when reading the items on recommended order, it is messing up when rows
/// are colored. It should only be looking at the text and ignore all colors
/// so that it doesnt miss rows."). Replaces the color GATE that used to
/// decide, per full-width scanline, whether a row was a "highlight" worth
/// normalizing (RowHighlightColorDetector.IsHighlightScanline: chroma >= 30,
/// luma 40..250, >= 50% of the WHOLE window-width scanline near one median
/// color). Every threshold in that gate was an unmeasured guess and each
/// has a real way to miss a colored row: a fill covering under half the
/// captured width (scrollbar / chrome / a second differently-colored cell
/// dilutes it), a very light fill (luma over 250), or a white in-edit cell
/// sitting inside a colored row (drags the median).
///
/// WHAT THIS DOES INSTEAD (pure over a BGRA byte[]; no System.Drawing, so it
/// is unit tested on any OS):
///   1. FLAT (background) pixels are found by shape, not color: a pixel is
///      "flat" when it sits in a horizontal run of at least
///      <see cref="MinFlatRunPixels"/> near-identical pixels AND matches the
///      pixel above and below it. Glyph strokes are 1-2 px thick and never
///      satisfy that; fills, cell backgrounds and padding always do. Hue,
///      lightness and fill width do not matter.
///   2. Every other pixel (text, anti-aliased edges, thin grid lines) is
///      compared with the nearest flat pixel above/below it in the same
///      column (fallback: nearest flat pixel left/right) -- its LOCAL
///      background -- and rewritten as 255 minus its per-channel contrast
///      with that background. Background becomes white, text becomes dark
///      in proportion to how different it is from ITS OWN background:
///      white-on-dark-blue, black-on-pale-yellow and dark-on-light-green
///      all end up dark-on-white. The result is CONTINUOUS gray, never hard
///      black/white, so anti-aliasing survives.
///   3. Wherever the local background is already plain light
///      (<see cref="PlainMinChannel"/> on every channel -- ordinary white or
///      near-white rows, white in-edit cells inside a colored row) the
///      pixel is left byte-for-byte UNCHANGED. A plain white row therefore
///      reads exactly as it does without any normalization, and a screen
///      with no colored fill at all returns before the output pass.
///
/// WHY THIS IS NOT ROUND 6 AGAIN (74cd116, reverted by 0503325 -- "broke
/// Order Assist in the field"). Round 6 deleted the gate and hard-
/// binarized EVERY scanline against that scanline's single median color
/// (+/-40 per channel -> pure white, else pure black). The code review in
/// this branch's report lists the failure modes; the ones this class
/// designs out:
///   - ONE reference color per full-width scanline: any cell/fill that
///     differs from the scanline median by more than 40 on any channel
///     (a pale-yellow flag cell, a white edit cell, a blue selection next
///     to a white column) became a solid BLACK block, erasing the text in
///     it. Here the reference is local to each pixel.
///   - HARD threshold on every row: anti-aliased small text (strokes mostly
///     mid-gray) got fattened into blobs, grid lines and light borders
///     turned solid black, digit counters filled in -- applied to the plain
///     white rows that read fine before. Here output is proportional (no
///     threshold) and plain rows are not touched at all.
///   - Per-scanline median via LINQ OrderBy x3 on every row of a
///     full-window capture (2M pixels at 1080p): the dominant per-tick
///     cost. This class is O(pixels) with flat byte/int arrays, no LINQ.
///
/// COST: three linear passes over width*height plus, for non-flat pixels
/// only (roughly 10-25% of a table screen), a bounded vertical scan of at
/// most 2*<see cref="VerticalSearchRows"/> flags. Plain-only screens exit
/// after pass 2. Estimated well under 50 ms at 1080p, versus the several
/// hundred ms of the LINQ medians it replaces.
/// </summary>
public static class LocalContrastNormalizer
{
    /// <summary>Minimum width of a horizontal run of near-identical pixels to count as background (rather than a horizontal text stroke). 8 px: wider than any glyph bar at the 9-12 pt grid fonts, narrower than any cell.</summary>
    public const int MinFlatRunPixels = 8;

    /// <summary>Max per-channel step between neighbouring pixels inside one flat run (allows slow gradients, rejects glyph edges).</summary>
    public const int RunStepTolerance = 10;

    /// <summary>Max per-channel distance from the first pixel of a flat run (stops a long gradient from chaining forever).</summary>
    public const int RunDriftTolerance = 28;

    /// <summary>Max per-channel difference to the pixel above and below for a pixel to be flat (rejects 1-2 px horizontal strokes and grid lines).</summary>
    public const int VerticalTolerance = 16;

    /// <summary>How far up/down (rows) to look for the local background of a non-flat pixel. A table row is ~18-24 px with ~9 px text, so padding is always inside this.</summary>
    public const int VerticalSearchRows = 8;

    /// <summary>Fallback horizontal search distance (px) when no flat pixel exists above/below (very tight rows).</summary>
    public const int HorizontalSearchPixels = 24;

    /// <summary>A background whose every channel is at least this is "plain" (white / near-white / light gray): its pixels are left untouched.</summary>
    public const int PlainMinChannel = 235;

    /// <summary>Contrast at or below this (per-channel) counts as background noise and is written as pure white.</summary>
    public const int NoiseFloor = 10;

    /// <summary>
    /// Result of one <see cref="Normalize"/> call: how many pixels were
    /// rewritten and the Y ranges (inclusive top, exclusive bottom) of the
    /// rows that contained at least one rewritten pixel -- diagnostics only.
    /// </summary>
    public readonly record struct Result(int ChangedPixels, IReadOnlyList<(int Top, int Bottom)> ChangedBands);

    private static readonly Result NoChange = new(0, Array.Empty<(int, int)>());

    /// <summary>
    /// Rewrites <paramref name="bgra"/> in place (Format32bppArgb byte order:
    /// B, G, R, A; alpha is never touched). <paramref name="stride"/> is the
    /// byte distance between rows. Never throws for a well-formed buffer;
    /// returns <see cref="NoChange"/> for degenerate sizes.
    /// </summary>
    public static Result Normalize(byte[] bgra, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || bgra.Length < stride * height) return NoChange;

        var flat = ComputeFlatMap(bgra, width, height, stride);

        // Exact short-circuit: a non-plain reference can only come from a
        // non-plain FLAT pixel, so if there is none, nothing would change.
        var anyColoredFlat = false;
        for (var y = 0; y < height && !anyColoredFlat; y++)
        {
            var rowFlat = y * width;
            var rowPix = y * stride;
            for (var x = 0; x < width; x++)
            {
                if (flat[rowFlat + x] != 0 && !IsPlain(bgra, rowPix + x * 4))
                {
                    anyColoredFlat = true;
                    break;
                }
            }
        }
        if (!anyColoredFlat) return NoChange;

        // Results are staged in side arrays: reference pixels are read from
        // the ORIGINAL buffer, so nothing may be written back until the end.
        var gray = new byte[width * height];
        var touched = new byte[width * height];
        var changedRows = new bool[height];
        var changed = 0;

        for (var y = 0; y < height; y++)
        {
            var rowPix = y * stride;
            var rowFlat = y * width;

            for (var x = 0; x < width; x++)
            {
                var p = rowPix + x * 4;
                var i = rowFlat + x;

                if (flat[i] != 0)
                {
                    // Background pixel: colored fill -> white; plain stays.
                    if (!IsPlain(bgra, p))
                    {
                        gray[i] = 255;
                        touched[i] = 1;
                    }
                    continue;
                }

                var refOffset = FindReferenceOffset(bgra, flat, width, height, stride, x, y);
                if (refOffset < 0 || IsPlain(bgra, refOffset)) continue;

                var contrast = Math.Max(
                    Math.Abs(bgra[p] - bgra[refOffset]),
                    Math.Max(Math.Abs(bgra[p + 1] - bgra[refOffset + 1]), Math.Abs(bgra[p + 2] - bgra[refOffset + 2])));

                gray[i] = contrast <= NoiseFloor ? (byte)255 : (byte)(255 - contrast);
                touched[i] = 1;
            }
        }

        for (var y = 0; y < height; y++)
        {
            var rowPix = y * stride;
            var rowFlat = y * width;
            for (var x = 0; x < width; x++)
            {
                var i = rowFlat + x;
                if (touched[i] == 0) continue;

                var p = rowPix + x * 4;
                var g = gray[i];
                bgra[p] = g;
                bgra[p + 1] = g;
                bgra[p + 2] = g;
                changedRows[y] = true;
                changed++;
            }
        }

        return new Result(changed, ToBands(changedRows));
    }

    /// <summary>
    /// Pass 1+2: 1 where the pixel is background by SHAPE (long horizontal
    /// run of near-identical pixels, stable vertically). Exposed for tests.
    /// Index = y * width + x.
    /// </summary>
    public static byte[] ComputeFlatMap(byte[] bgra, int width, int height, int stride)
    {
        var flat = new byte[width * height];

        for (var y = 0; y < height; y++)
        {
            var rowPix = y * stride;
            var rowFlat = y * width;
            var x = 0;
            while (x < width)
            {
                var runStart = x;
                var anchor = rowPix + x * 4;
                x++;
                while (x < width &&
                       Cheb(bgra, rowPix + x * 4, rowPix + (x - 1) * 4) <= RunStepTolerance &&
                       Cheb(bgra, rowPix + x * 4, anchor) <= RunDriftTolerance)
                {
                    x++;
                }

                if (x - runStart >= MinFlatRunPixels)
                {
                    for (var k = runStart; k < x; k++) flat[rowFlat + k] = 1;
                }
            }
        }

        // Vertical stability: drop run pixels that differ from the pixel
        // above or below (horizontal strokes 1-2 px thick, grid lines).
        for (var y = 0; y < height; y++)
        {
            var rowPix = y * stride;
            var rowFlat = y * width;
            for (var x = 0; x < width; x++)
            {
                if (flat[rowFlat + x] == 0) continue;
                var p = rowPix + x * 4;
                if (y > 0 && Cheb(bgra, p, p - stride) > VerticalTolerance) { flat[rowFlat + x] = 0; continue; }
                if (y < height - 1 && Cheb(bgra, p, p + stride) > VerticalTolerance) flat[rowFlat + x] = 0;
            }
        }

        return flat;
    }

    /// <summary>
    /// Byte offset (into the pixel buffer) of the nearest flat pixel above or
    /// below (x, y) in the same column -- nearer wins, ties go up -- else the
    /// nearest flat pixel to the left/right on the same row, else -1.
    /// </summary>
    private static int FindReferenceOffset(byte[] bgra, byte[] flat, int width, int height, int stride, int x, int y)
    {
        var up = 0;
        var down = 0;
        for (var k = 1; k <= VerticalSearchRows; k++)
        {
            if (up == 0 && y - k >= 0 && flat[(y - k) * width + x] != 0) up = k;
            if (down == 0 && y + k < height && flat[(y + k) * width + x] != 0) down = k;
            if (up != 0 && down != 0) break;
        }

        if (up != 0 || down != 0)
        {
            if (down == 0 || (up != 0 && up <= down)) return (y - up) * stride + x * 4;
            return (y + down) * stride + x * 4;
        }

        var rowFlat = y * width;
        for (var k = 1; k <= HorizontalSearchPixels; k++)
        {
            if (x - k >= 0 && flat[rowFlat + x - k] != 0) return y * stride + (x - k) * 4;
            if (x + k < width && flat[rowFlat + x + k] != 0) return y * stride + (x + k) * 4;
        }

        return -1;
    }

    private static bool IsPlain(byte[] bgra, int p) =>
        bgra[p] >= PlainMinChannel && bgra[p + 1] >= PlainMinChannel && bgra[p + 2] >= PlainMinChannel;

    /// <summary>Largest per-channel absolute difference between two pixels of the same buffer.</summary>
    private static int Cheb(byte[] bgra, int p, int q)
    {
        var db = Math.Abs(bgra[p] - bgra[q]);
        var dg = Math.Abs(bgra[p + 1] - bgra[q + 1]);
        var dr = Math.Abs(bgra[p + 2] - bgra[q + 2]);
        return Math.Max(db, Math.Max(dg, dr));
    }

    private static IReadOnlyList<(int Top, int Bottom)> ToBands(bool[] rows)
    {
        var bands = new List<(int Top, int Bottom)>();
        var open = -1;
        for (var y = 0; y < rows.Length; y++)
        {
            if (rows[y])
            {
                if (open < 0) open = y;
            }
            else if (open >= 0)
            {
                bands.Add((open, y));
                open = -1;
            }
        }
        if (open >= 0) bands.Add((open, rows.Length));
        return bands;
    }
}
