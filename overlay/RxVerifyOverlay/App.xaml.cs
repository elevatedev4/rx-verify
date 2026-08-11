using System.Windows;
using RxVerifyOverlay.Models;

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
    }
}
