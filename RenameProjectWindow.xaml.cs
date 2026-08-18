using System.Windows;
using System.Windows.Input;

namespace CutFlow;

public partial class RenameProjectWindow : Window
{
    public string ProjectName => NameBox.Text.Trim();
    public RenameProjectWindow(string currentName)
    {
        InitializeComponent();
        NameBox.Text = currentName;
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }
    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ProjectName)) return;
        DialogResult = true;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void NameBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Rename_Click(sender, e); }
}
