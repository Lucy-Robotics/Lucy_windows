using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Lucy.Core;
using Lucy.Views;

namespace Lucy;

public partial class MainWindow : Window
{
    private bool _closeAllowed;
    private bool _closing;

    public MainWindow(ShellViewModel shell)
    {
        InitializeComponent();
        Shell = shell;
        DataContext = shell;
        StateChanged += (_, _) => OnStateChanged();
    }

    public ShellViewModel Shell { get; }

    /// <summary>
    /// Every way of closing (✕, Alt+F4, taskbar "Close window", Exit buttons) ends here. The page
    /// gets to stop what it started before the window really closes.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_closeAllowed) { base.OnClosing(e); return; }
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        // Close() cannot be called again from inside OnClosing: run the shutdown afterwards.
        Dispatcher.BeginInvoke(new Action(async () => await ShutdownAsync()), DispatcherPriority.Normal);
    }

    private async Task ShutdownAsync()
    {
        try
        {
            var ok = Shell.Page == null || await Shell.Page.RequestCloseAsync(Shell);
            if (ok) ForceClose();
        }
        catch (Exception ex)
        {
            Log.Error("shutdown failed", ex);
            Shell.Page?.EmergencyShutdown();
            ForceClose();
        }
        finally { _closing = false; }
    }

    /// <summary>Close without asking the page (it already cleaned up).</summary>
    public void ForceClose()
    {
        _closeAllowed = true;
        Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Normal);
    }

    private void OnStateChanged()
    {
        // WindowChrome maximises past the screen edge; pad it back in.
        var max = WindowState == WindowState.Maximized;
        Root.BorderThickness = max ? new Thickness(7) : new Thickness(1);
        Root.BorderBrush = (System.Windows.Media.Brush)FindResource(max ? "BgBrush" : "LineStrongBrush");
        MaxButton.Content = max ? "" : "";
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
