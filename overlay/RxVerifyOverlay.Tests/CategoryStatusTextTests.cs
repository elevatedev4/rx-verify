using RxVerifyOverlay.Models;
using RxVerifyOverlay.ViewModels;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for CategoryViewModel.StatusText (ViewModels/OverlayViewModel.cs)
/// — the text label shown on the Patient/Prescriber/Rx header rows
/// alongside the existing leading status glyph, per Will's live-test
/// feedback (W-T8 item 4). Pure logic, no UIA/engine/PHI.
/// </summary>
public class CategoryStatusTextTests
{
    [Fact]
    public void GreenStatusWithDataReadsExactMatch()
    {
        var category = new CategoryViewModel { Name = "Patient", HasData = true, Status = VerdictStatus.Green };
        Assert.Equal("Exact match", category.StatusText);
    }

    [Fact]
    public void YellowStatusWithDataReadsPartialMatch()
    {
        var category = new CategoryViewModel { Name = "Prescriber", HasData = true, Status = VerdictStatus.Yellow };
        Assert.Equal("Partial match", category.StatusText);
    }

    [Fact]
    public void RedStatusWithDataReadsLikelyError()
    {
        var category = new CategoryViewModel { Name = "Rx", HasData = true, Status = VerdictStatus.Red };
        Assert.Equal("Likely Error", category.StatusText);
    }

    [Fact]
    public void NoDataOverridesStatusRegardlessOfRolledUpValue()
    {
        // HasData=false must win even if Status happens to still hold a
        // stale Green from before the category was cleared (mirrors the
        // existing Glyph behavior, which the same HasData check gates).
        var category = new CategoryViewModel { Name = "Patient", HasData = false, Status = VerdictStatus.Green };
        Assert.Equal("No data", category.StatusText);
    }
}
