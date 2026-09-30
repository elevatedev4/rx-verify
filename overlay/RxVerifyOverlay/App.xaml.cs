using System.Windows;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.Reports;
using RxVerifyOverlay.Update;
using RxVerifyOverlay.VelopackIntegration;

namespace RxVerifyOverlay;

public partial class App : Application
{
    /// <summary>
    /// INTEGRATED DISPLAY MODE: replaces App.xaml's old
    /// StartupUri="MainWindow.xaml" (which unconditionally Shows the
    /// window) with an explicit construct-then-maybe-Show, so a session
    /// that quit in Integrated mode last time starts with MainWindow
    /// HIDDEN — the boxes/control-box layer is the visible UI instead
    /// (see MainWindow.xaml.cs's IntegratedOverlayCoordinator wiring).
    /// MainWindow's own Loaded-triggered first refresh never fires for a
    /// window that's never shown, so StartupCompleted() runs the
    /// equivalent explicitly in that branch.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // W-T92 round 3 (GOAL brief step 1): log the build's own git sha +
        // build time on every single startup, first line, so a support
        // conversation with Will can start with "what does Startup:
        // Rx Verify build ... say" instead of guessing whether he's on
        // current code. See Update/BuildInfo.cs's own doc for why this
        // exists.
        ReportsLog.Append($"Startup: {BuildInfo.Summary}");

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;

        if (mainWindow.InitialDisplayMode == DisplayMode.Separate)
        {
            mainWindow.Show();
        }
        else
        {
            _ = mainWindow.StartupCompleted();
        }

        // Velopack installer channel: fire-and-forget, AFTER every other
        // startup step above (including the display-mode branch and the
        // existing Update/UpdateChecker.cs flow the MainWindow drives on
        // its own timer), so a slow/failed network check can never delay
        // window construction or the initial refresh. VelopackUpdater.
        // CheckAndApplyAsync gates itself on UpdateManager.IsInstalled and
        // no-ops entirely for this process when it wasn't installed by
        // Setup.exe — see that class's own doc comment — so this call is a
        // no-op for every existing bootstrap-fresh.ps1 + shortcut launch.
        _ = VelopackUpdater.CheckAndApplyAsync(
            message => ReportsLog.Append(message));
    }
}
