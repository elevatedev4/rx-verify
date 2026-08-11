using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;

namespace RxVerifyOverlay.Integrated;

/// <summary>
/// Shared "flash green + checkmark" success feedback for every copy-logs
/// button — MainWindow's "Copy logs"/"Copy logs (no HIPAA)" AND the
/// control box's equivalents (item 5, owner asked twice — must ship).
/// Replaces the old MessageBox.Show("Log copied...") confirmation popup
/// everywhere it existed: the clicked button itself turns green with a
/// "✓ Copied" label for ~1.5s, then reverts — no modal interruption.
/// Genuine FAILURES (couldn't build the log, clipboard locked) still show
/// a MessageBox at each call site — this only replaces the SUCCESS
/// confirmation, which is the one the owner flagged as unwanted.
/// </summary>
public static class ButtonFeedback
{
    private static readonly SolidColorBrush SuccessBackground = new(Color.FromRgb(0x2E, 0x7D, 0x32)); // matches MainWindow.xaml's GreenBrush
    private static readonly SolidColorBrush SuccessForeground = new(Colors.White);

    /// <summary>
    /// TRUE original appearance per button, captured the FIRST time it
    /// starts flashing and not overwritten by an overlapping second click
    /// — see FlashSuccessAsync's re-entrancy note. ConditionalWeakTable
    /// (not a plain Dictionary) so a button never leaks here for the
    /// lifetime of the app even if a restore is somehow skipped.
    /// </summary>
    private static readonly ConditionalWeakTable<Button, OriginalAppearance> Originals = new();

    private sealed class OriginalAppearance
    {
        public Brush? Background;
        public Brush? Foreground;
        public object? Content;
    }

    /// <summary>
    /// Temporarily overrides <paramref name="button"/>'s Background/
    /// Foreground/Content, waits <paramref name="duration"/> (default
    /// 1.5s), then restores exactly what was there before the FIRST call
    /// in a burst. Re-entrant-safe: if the pharmacist clicks the same
    /// button again while it's still green from a previous click, the
    /// SAME captured original is reused rather than capturing the
    /// mid-flash green as if it were the real original (which would leave
    /// the button stuck green forever after the first call's restore).
    /// </summary>
    public static async Task FlashSuccessAsync(Button button, string flashText = "✓ Copied", TimeSpan? duration = null)
    {
        if (!Originals.TryGetValue(button, out var original))
        {
            original = new OriginalAppearance
            {
                Background = button.Background,
                Foreground = button.Foreground,
                Content = button.Content
            };
            Originals.Add(button, original);
        }

        button.Background = SuccessBackground;
        button.Foreground = SuccessForeground;
        button.Content = flashText;

        await Task.Delay(duration ?? TimeSpan.FromSeconds(1.5));

        button.Background = original.Background;
        button.Foreground = original.Foreground;
        button.Content = original.Content;
        Originals.Remove(button);
    }
}
