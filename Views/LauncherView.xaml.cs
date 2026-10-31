using System.Windows.Controls;
using System.Windows.Input;

namespace Lucy.Views;

public partial class LauncherView : UserControl
{
    public LauncherView()
    {
        InitializeComponent();
        // TUI keys: Enter applies (Ctrl+Enter here, so Enter on a focused row still toggles it).
        PreviewKeyDown += (_, e) =>
        {
            if (DataContext is not LauncherViewModel vm) return;
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && vm.ApplyCommand.CanExecute(null))
            {
                vm.ApplyCommand.Execute(null);
                e.Handled = true;
            }
        };
    }
}
