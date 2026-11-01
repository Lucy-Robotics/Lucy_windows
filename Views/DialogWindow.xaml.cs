using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Lucy.Views;

public partial class DialogWindow : Window
{
    public DialogWindow() => InitializeComponent();

    public int Result { get; private set; } = -1;

    internal static Func<string, string[], int>? AutoAnswer { get; set; }

    public static int Show(string title, string message, string? detail, bool danger, params string[] buttons) =>
        Show(title, message, detail, danger, -1, buttons);

    /// <param name="dangerIndex">Button drawn as destructive (red), whatever its position.</param>
    public static int Show(string title, string message, string? detail, bool danger, int dangerIndex, string[] buttons)
    {
        if (AutoAnswer != null) return AutoAnswer(title, buttons);
        var dlg = Create(title, message, detail, danger, dangerIndex, buttons);
        dlg.ShowDialog();
        return dlg.Result;
    }

    internal static DialogWindow Create(string title, string message, string? detail, bool danger, int dangerIndex, string[] buttons)
    {
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                    ?? Application.Current?.MainWindow;
        var dlg = new DialogWindow();
        if (owner != null && owner != dlg && owner.IsVisible) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;
        if (!string.IsNullOrEmpty(detail))
        {
            dlg.DetailText.Text = detail;
            dlg.DetailText.Visibility = Visibility.Visible;
        }
        if (danger) dlg.Tick.Fill = (System.Windows.Media.Brush)dlg.FindResource("ErrorBrush");

        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i;
            var primary = i == 0;
            var b = new Button
            {
                Content = buttons[i],
                Margin = new Thickness(10, 0, 0, 0),
                MinWidth = 96,
                IsDefault = primary,
                IsCancel = i == buttons.Length - 1 && buttons.Length > 1,
                // Every button gets a theme style (a null Style falls back to WPF's grey default).
                Style = (Style)dlg.FindResource(
                    index == dangerIndex || (primary && danger) ? "DangerButton"
                    : primary ? "PrimaryButton"
                    : (object)typeof(Button)),
            };
            b.Click += (_, _) => { dlg.Result = index; dlg.Close(); };
            dlg.Buttons.Children.Add(b);
        }
        return dlg;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}

public static class Dialogs
{
    public static bool Confirm(string title, string message, string yes = "Yes", string no = "Cancel", bool danger = false, string? detail = null) =>
        RunOnUi(() => DialogWindow.Show(title, message, detail, danger, yes, no) == 0);

    public static int Choose(string title, string message, string? detail, params string[] buttons) =>
        RunOnUi(() => DialogWindow.Show(title, message, detail, false, buttons));

    /// <summary>Choose, with one destructive option drawn in red.</summary>
    public static int ChooseDanger(string title, string message, string? detail, int dangerIndex, params string[] buttons) =>
        RunOnUi(() => DialogWindow.Show(title, message, detail, false, dangerIndex, buttons));

    public static void Info(string title, string message, string? detail = null) =>
        RunOnUi(() => DialogWindow.Show(title, message, detail, false, "OK"));

    public static void Error(string title, string message, string? detail = null) =>
        RunOnUi(() => DialogWindow.Show(title, message, detail, true, "OK"));

    private static T RunOnUi<T>(Func<T> f)
    {
        var d = Application.Current?.Dispatcher;
        return d == null || d.CheckAccess() ? f() : d.Invoke(f);
    }
}
