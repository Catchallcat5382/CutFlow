using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CutFlow.Models;
using CutFlow.Controls;
using CutFlow.Services;

namespace CutFlow;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _working;
    private bool _loading = true;
    private bool _syncingColor;
    public AppSettings Result => _working;

    public SettingsWindow(AppSettings current)
    {
        _working = SettingsService.Clone(current);
        InitializeComponent();
        ThemeCombo.SelectedIndex = ThemeIndex(_working.ThemeMode);
        WindowModeCombo.SelectedIndex = WindowModeIndex(_working.WindowMode);
        RBox.Text = _working.CustomBackgroundR.ToString();
        GBox.Text = _working.CustomBackgroundG.ToString();
        BBox.Text = _working.CustomBackgroundB.ToString();
        CustomColorPicker.SelectedColor = Color.FromRgb(_working.CustomBackgroundR, _working.CustomBackgroundG, _working.CustomBackgroundB);
        SurfaceRBox.Text = _working.CustomSurfaceR.ToString();
        SurfaceGBox.Text = _working.CustomSurfaceG.ToString();
        SurfaceBBox.Text = _working.CustomSurfaceB.ToString();
        SurfaceColorPicker.SelectedColor = Color.FromRgb(_working.CustomSurfaceR, _working.CustomSurfaceG, _working.CustomSurfaceB);
        VolumeSlider.Value = Math.Clamp(_working.PlaybackVolume, 0, 1);
        SkipSlider.Value = Math.Clamp(_working.SkipSeconds, 2, 15);
        AutosaveSlider.Value = Math.Clamp(_working.AutosaveSeconds, 15, 120);
        NoiseCombo.SelectedIndex = NoiseIndex(_working.DefaultNoiseProfile);
        PreviewCutsCheck.IsChecked = _working.PreviewWithoutSilenceByDefault;
        FollowPlayheadCheck.IsChecked = _working.FollowPlayhead;
        UiSoundsCheck.IsChecked = _working.UiSoundsEnabled;
        SnappingCheck.IsChecked = _working.SnappingEnabled;
        SnapThresholdSlider.Value = Math.Clamp(_working.SnapThresholdSeconds, 0.02, 0.30);
        RefreshCombo.SelectedIndex = RefreshIndex(_working.UiRefreshHz);
        SelectComboText(ExportFpsCombo, _working.DefaultExportFps);
        SelectComboText(ExportResolutionCombo, _working.DefaultExportResolution);
        LoadKeyBindings();
        AppVersionText.Text = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "Unknown";
        InstallLocationText.Text = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var installed = UninstallService.FindUninstaller() is not null;
        UninstallButton.IsEnabled = installed;
        UninstallHintText.Text = installed
            ? "Uninstall uses the CutFlow Setup uninstaller. You can optionally remove projects, autosaves, settings and cached media too."
            : "Uninstall is available after CutFlow has been installed with CutFlow Setup.";
        _loading = false;
        RefreshLabels();
        RefreshRgbState();
        ShowSettingsPage(0);
    }

    private void SettingsNav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AppearancePanel is null) return;
        ShowSettingsPage(Math.Max(0, SettingsNav.SelectedIndex));
    }

    private void ShowSettingsPage(int index)
    {
        AppearancePanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PlaybackPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        EditingPanel.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        PerformancePanel.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        AppPanel.Visibility = index == 5 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_loading) RefreshRgbState(); }
    private void Rgb_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _syncingColor) return;
        if (TryByte(RBox.Text, out var r) && TryByte(GBox.Text, out var g) && TryByte(BBox.Text, out var b))
            CustomColorPicker.SelectedColor = Color.FromRgb(r, g, b);
        RefreshPreview();
    }
    private void SurfaceRgb_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _syncingColor) return;
        if (TryByte(SurfaceRBox.Text, out var r) && TryByte(SurfaceGBox.Text, out var g) && TryByte(SurfaceBBox.Text, out var b))
            SurfaceColorPicker.SelectedColor = Color.FromRgb(r, g, b);
        RefreshPreview();
    }

    private void SurfaceColorPicker_ColorChanged(object sender, ColorChangedEventArgs e)
    {
        if (_loading) return;
        _syncingColor = true;
        try
        {
            SurfaceRBox.Text = e.Color.R.ToString();
            SurfaceGBox.Text = e.Color.G.ToString();
            SurfaceBBox.Text = e.Color.B.ToString();
            SurfaceColorPreview.Background = new SolidColorBrush(e.Color);
        }
        finally { _syncingColor = false; }
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!_loading) RefreshLabels(); }
    private void SkipSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!_loading) RefreshLabels(); }
    private void AutosaveSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!_loading) RefreshLabels(); }
    private void SnapThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!_loading) RefreshLabels(); }

    private void RefreshLabels()
    {
        VolumeLabel.Text = $"{VolumeSlider.Value * 100:0}%";
        SkipLabel.Text = $"{SkipSlider.Value:0}s";
        AutosaveLabel.Text = $"{AutosaveSlider.Value:0}s";
        if (SnapThresholdLabel is not null) SnapThresholdLabel.Text = $"{SnapThresholdSlider.Value:0.00}s";
    }

    private void RefreshRgbState()
    {
        var custom = SelectedText(ThemeCombo) == "Custom";
        RgbPanel.IsEnabled = custom;
        RgbPanel.Opacity = custom ? 1 : 0.45;
        SurfaceRgbPanel.IsEnabled = custom;
        SurfaceRgbPanel.Opacity = custom ? 1 : 0.45;
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        if (TryByte(RBox.Text, out var r) && TryByte(GBox.Text, out var g) && TryByte(BBox.Text, out var b))
            ColorPreview.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        if (TryByte(SurfaceRBox.Text, out var sr) && TryByte(SurfaceGBox.Text, out var sg) && TryByte(SurfaceBBox.Text, out var sb))
            SurfaceColorPreview.Background = new SolidColorBrush(Color.FromRgb(sr, sg, sb));
    }

    private void CustomColorPicker_ColorChanged(object sender, ColorChangedEventArgs e)
    {
        if (_loading) return;
        _syncingColor = true;
        try
        {
            RBox.Text = e.Color.R.ToString();
            GBox.Text = e.Color.G.ToString();
            BBox.Text = e.Color.B.ToString();
            ColorPreview.Background = new SolidColorBrush(e.Color);
        }
        finally { _syncingColor = false; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryByte(RBox.Text, out var r) || !TryByte(GBox.Text, out var g) || !TryByte(BBox.Text, out var b) ||
            !TryByte(SurfaceRBox.Text, out var sr) || !TryByte(SurfaceGBox.Text, out var sg) || !TryByte(SurfaceBBox.Text, out var sb))
        {
            MessageBox.Show("RGB values must each be between 0 and 255.", "CutFlow", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _working.ThemeMode = SelectedText(ThemeCombo);
        _working.WindowMode = SelectedTag(WindowModeCombo, "FullScreen");
        _working.CustomBackgroundR = r;
        _working.CustomBackgroundG = g;
        _working.CustomBackgroundB = b;
        _working.CustomSurfaceR = sr;
        _working.CustomSurfaceG = sg;
        _working.CustomSurfaceB = sb;
        _working.PlaybackVolume = VolumeSlider.Value;
        _working.SkipSeconds = SkipSlider.Value;
        _working.AutosaveSeconds = (int)Math.Round(AutosaveSlider.Value);
        _working.DefaultNoiseProfile = SelectedText(NoiseCombo);
        _working.PreviewWithoutSilenceByDefault = PreviewCutsCheck.IsChecked == true;
        _working.FollowPlayhead = FollowPlayheadCheck.IsChecked == true;
        _working.UiSoundsEnabled = UiSoundsCheck.IsChecked == true;
        _working.SnappingEnabled = SnappingCheck.IsChecked == true;
        _working.SnapThresholdSeconds = SnapThresholdSlider.Value;
        _working.UiRefreshHz = SelectedTagInt(RefreshCombo, 30);
        _working.DefaultExportFps = SelectedText(ExportFpsCombo);
        _working.DefaultExportResolution = SelectedText(ExportResolutionCombo);
        SaveKeyBindings();
        DialogResult = true;
    }
    private IEnumerable<TextBox> KeyBindingBoxes()
    {
        yield return PlayPauseKeyBox; yield return StepBackKeyBox; yield return StepForwardKeyBox;
        yield return RewindKeyBox; yield return FastForwardKeyBox; yield return CutKeyBox;
        yield return KeepKeyBox; yield return CutAllKeyBox; yield return UndoKeyBox; yield return RedoKeyBox;
        yield return SaveKeyBox; yield return ExportKeyBox; yield return FullScreenKeyBox;
    }

    private void LoadKeyBindings()
    {
        foreach (var box in KeyBindingBoxes())
        {
            var action = box.Tag?.ToString() ?? string.Empty;
            box.Text = KeyBindingService.Get(_working, action);
        }
    }

    private void SaveKeyBindings()
    {
        _working.KeyBindings ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var box in KeyBindingBoxes())
        {
            var action = box.Tag?.ToString();
            if (string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(box.Text)) continue;
            _working.KeyBindings[action] = box.Text;
        }
    }

    private void KeyBindingBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box) box.SelectAll();
    }

    private void KeyBindingBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        var actual = e.Key == Key.System ? e.SystemKey : e.Key;
        if (KeyBindingService.IsModifierKey(actual)) { e.Handled = true; return; }
        if (actual == Key.Escape) { Keyboard.ClearFocus(); e.Handled = true; return; }
        var binding = KeyBindingService.FromEvent(e);
        if (!string.IsNullOrWhiteSpace(binding)) box.Text = binding;
        e.Handled = true;
    }



    private void OpenAppData_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutFlow");
        Directory.CreateDirectory(path);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void UninstallApp_Click(object sender, RoutedEventArgs e)
    {
        var uninstaller = UninstallService.FindUninstaller();
        if (uninstaller is null)
        {
            MessageBox.Show("This copy of CutFlow was not installed with CutFlow Setup, so there is no installer-managed uninstaller to run.", "CutFlow", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = new UninstallConfirmWindow { Owner = this };
        if (confirm.ShowDialog() != true) return;
        try
        {
            UninstallService.BeginUninstall(uninstaller, confirm.DeleteUserData);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"CutFlow could not start the uninstaller.\n\n{ex.GetBaseException().Message}", "Uninstall CutFlow", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void TestSound_Click(object sender, RoutedEventArgs e)
    {
        if (UiSoundsCheck.IsChecked != true)
        {
            UiFeedbackService.Stop();
            return;
        }
        UiFeedbackService.Play("success");
    }

    private void ResetControls_Click(object sender, RoutedEventArgs e)
    {
        _working.KeyBindings = new Dictionary<string, string>(KeyBindingService.Defaults, StringComparer.OrdinalIgnoreCase);
        LoadKeyBindings();
    }

    private void ResetAppearance_Click(object sender, RoutedEventArgs e)
    {
        ThemeCombo.SelectedIndex = 0;
        WindowModeCombo.SelectedIndex = 0;
        RBox.Text = "34"; GBox.Text = "36"; BBox.Text = "38";
        SurfaceRBox.Text = "31"; SurfaceGBox.Text = "33"; SurfaceBBox.Text = "36";
        CustomColorPicker.SelectedColor = Color.FromRgb(34, 36, 38);
        SurfaceColorPicker.SelectedColor = Color.FromRgb(31, 33, 36);
        RefreshRgbState();
    }

    private void ResetPlayback_Click(object sender, RoutedEventArgs e)
    {
        VolumeSlider.Value = 1.0;
        SkipSlider.Value = 5.0;
        FollowPlayheadCheck.IsChecked = true;
        UiSoundsCheck.IsChecked = true;
    }

    private void ResetEditing_Click(object sender, RoutedEventArgs e)
    {
        NoiseCombo.SelectedIndex = 0;
        AutosaveSlider.Value = 30;
        PreviewCutsCheck.IsChecked = true;
        SnappingCheck.IsChecked = true;
        SnapThresholdSlider.Value = 0.08;
    }

    private void ResetPerformance_Click(object sender, RoutedEventArgs e)
    {
        RefreshCombo.SelectedIndex = 1;
        ExportFpsCombo.SelectedIndex = 0;
        ExportResolutionCombo.SelectedIndex = 0;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }

    private static string SelectedText(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;
    private static int SelectedTagInt(ComboBox box, int fallback) => int.TryParse((box.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var value) ? value : fallback;
    private static bool TryByte(string text, out byte value) => byte.TryParse(text, out value);
    private static int ThemeIndex(string value) => value switch { "Light" => 1, "Dark" => 2, "Custom" => 3, _ => 0 };
    private static int WindowModeIndex(string value) => value switch { "Borderless" => 1, "Windowed" => 2, _ => 0 };
    private static string SelectedTag(ComboBox box, string fallback) => (box.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;
    private static int NoiseIndex(string value) => value switch { "Noisy Room" => 1, "Voice Focus" => 2, "Keep Ambience" => 3, _ => 0 };
    private static int RefreshIndex(int value) => value switch { <= 20 => 0, <= 30 => 1, <= 45 => 2, _ => 3 };

    private static void SelectComboText(ComboBox box, string value)
    {
        for (var i = 0; i < box.Items.Count; i++)
        {
            if ((box.Items[i] as ComboBoxItem)?.Content?.ToString()?.Equals(value, StringComparison.OrdinalIgnoreCase) == true)
            {
                box.SelectedIndex = i;
                return;
            }
        }
        box.SelectedIndex = 0;
    }
}
