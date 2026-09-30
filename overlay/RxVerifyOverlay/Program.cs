using System;
using System.Threading;
using System.Windows;

namespace RxVerifyOverlay;

/// <summary>
/// Explicit entry point, replacing the WPF SDK's auto-generated Main().
///
/// HOW THE GENERATED Main() WAS REPLACED: App.xaml's Build Action stays
/// "ApplicationDefinition" (so the XAML compiler still generates
/// App.InitializeComponent() from App.xaml — merged resource dictionaries,
/// etc. — and still honors x:Class="RxVerifyOverlay.App"). What the XAML
/// compiler normally ALSO generates is a `static void Main()` on the same
/// partial App class, wrapped by the WPF build targets in
/// `#if !DISABLE_XAML_GENERATED_MAIN`. RxVerifyOverlay.csproj now defines
/// that symbol (see its DefineConstants) plus StartupObject (belt-and-
/// suspenders — see the csproj comment for why both are needed), which
/// suppresses that generated Main() — this is Microsoft's own documented
/// mechanism for supplying a custom entry point in a WPF app (the pattern
/// Velopack's own docs recommend for exactly this reason), NOT a hack.
/// This Program.cs then does by hand exactly what the generated Main()
/// used to do (`new App(); app.InitializeComponent(); app.Run();`), with
/// the Velopack bootstrap and single-instance mutex added in front of it.
///
/// App.xaml's StartupUri was already removed before this change (see that
/// file's own comment — INTEGRATED DISPLAY MODE) — App.xaml.cs's
/// OnStartup builds MainWindow by hand and only Shows it conditionally, so
/// InitializeComponent() here does not auto-show a window; it only wires
/// up resources the same way it always did.
/// </summary>
internal static class Program
{
    /// <summary>Same "Global\" (per-machine, not just per-session) mutex
    /// pattern as vaccine-assist's Program.cs (see the installer plan) —
    /// makes sure an installed build and the existing bootstrap-fresh.ps1
    /// checkout can never run at the same time on one PC.</summary>
    private const string SingleInstanceMutexName = "Global\\RxVerifyOverlay-SingleInstance";

    [STAThread]
    private static void Main()
    {
        // MUST be the very first line (Velopack's own documented
        // requirement) — handles Velopack's install/update/uninstall
        // lifecycle hooks when this process was launched BY Velopack for
        // one of those events, then returns immediately for a normal
        // launch. Safe to call unconditionally, including when this build
        // was never installed by Setup.exe at all (dotnet build/run +
        // the existing bootstrap-fresh.ps1 checkout flow): Velopack
        // detects "not installed" internally and no-ops rather than
        // throwing. See Velopack/VelopackUpdater.cs for the SEPARATE gate
        // (UpdateManager.IsInstalled) that keeps the actual update
        // check/download/apply calls out of that same unpackaged path.
        Velopack.VelopackApp.Build().Run();

        using var singleInstanceMutex = new Mutex(
            initiallyOwned: true, name: SingleInstanceMutexName, out var createdNew);

        if (!createdNew)
        {
            MessageBox.Show(
                "Rx Verify is already running",
                "Rx Verify",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
