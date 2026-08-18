using System.Windows;

namespace CutFlow;

public partial class UninstallConfirmWindow : Window
{
    public bool DeleteUserData => DeleteDataCheck.IsChecked == true;
    public UninstallConfirmWindow() => InitializeComponent();
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
