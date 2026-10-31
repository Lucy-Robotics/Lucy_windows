using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lucy.Mvvm;

namespace Lucy.Views;

/// <summary>Log well with sticky auto-scroll (follows the tail unless the user scrolled up).</summary>
public partial class LogConsole : UserControl
{
    public static readonly DependencyProperty BufferProperty = DependencyProperty.Register(
        nameof(Buffer), typeof(LogBuffer), typeof(LogConsole), new PropertyMetadata(null, OnBufferChanged));

    private ScrollViewer? _scroller;
    private bool _follow = true;

    public LogConsole()
    {
        InitializeComponent();
        List.Loaded += (_, _) =>
        {
            _scroller = FindScroller(List);
            if (_scroller != null) _scroller.ScrollChanged += OnScrollChanged;
            ScrollToEnd();
        };
        List.KeyDown += (_, e) =>
        {
            if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { OnCopySelected(this, e); e.Handled = true; }
        };
    }

    public LogBuffer? Buffer { get => (LogBuffer?)GetValue(BufferProperty); set => SetValue(BufferProperty, value); }

    private static void OnBufferChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (LogConsole)d;
        if (e.OldValue is LogBuffer old) old.Appended -= self.OnAppended;
        if (e.NewValue is LogBuffer nb)
        {
            self.List.ItemsSource = nb.Lines;
            nb.Appended += self.OnAppended;
            self._follow = true;
            self.ScrollToEnd();
        }
        else self.List.ItemsSource = null;
    }

    private void OnAppended()
    {
        if (_follow) ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        if (List.Items.Count == 0) return;
        _scroller ??= FindScroller(List);
        _scroller?.ScrollToEnd();
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_scroller == null) return;
        // Only user scrolling changes the follow mode; growth of the extent does not.
        if (e.ExtentHeightChange == 0)
            _follow = _scroller.VerticalOffset >= _scroller.ScrollableHeight - 1;
    }

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindScroller(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }

    private void OnCopySelected(object sender, RoutedEventArgs e)
    {
        var lines = List.SelectedItems.Cast<string>().ToList();
        if (lines.Count == 0) return;
        // Keep the on-screen order, not the click order.
        var ordered = Buffer?.Lines.Where(l => lines.Contains(l)).ToList() ?? lines;
        TrySetClipboard(string.Join(Environment.NewLine, ordered));
    }

    private void OnCopyAll(object sender, RoutedEventArgs e)
    {
        if (Buffer != null) TrySetClipboard(Buffer.AllText());
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e) => Core.Integration.OpenFolder(Core.Log.Directory);

    private static void TrySetClipboard(string text)
    {
        try { Clipboard.SetText(text); } catch { /* clipboard busy */ }
    }
}
