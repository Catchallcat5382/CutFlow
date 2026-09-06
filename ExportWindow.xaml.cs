using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CutFlow.Models;
using Microsoft.Win32;

namespace CutFlow;

public partial class ExportWindow : Window
{
    private readonly CutFlowProject _project;
    private readonly AppSettings _settings;
    private bool _loading = true;
    public ExportOptions? Result { get; private set; }

    public ExportWindow(CutFlowProject project, AppSettings settings)
    {
        _project = project;
        _settings = settings;
        InitializeComponent();
        ProjectNameLabel.Text = "•  " + project.Name;
        SourceInfoLabel.Text = project.Media.HasVideo
            ? $"Source: {project.Media.Width}×{project.Media.Height} • {project.Media.FrameRate:0.##} fps"
            : "Source: audio";

        FormatCombo.SelectedIndex = project.Media.HasVideo ? 0 : 3;
        QualityCombo.SelectedIndex = 1;
        AudioQualityCombo.SelectedIndex = 1;
        SelectSpeed(settings.DefaultExportSpeed);
        CleanupCheck.IsChecked = settings.ApplyCleanupOnExport;
        SelectComboPrefix(ResolutionCombo, settings.DefaultExportResolution);
        SelectFps(settings.DefaultExportFps);
        DestinationBox.Text = BuildDefaultPath(SelectedText(FormatCombo));
        _loading = false;
        RefreshUi();
    }

    private void FormatCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DestinationBox is null) return;
        var old = DestinationBox.Text;
        var format = SelectedText(FormatCombo);
        if (!string.IsNullOrWhiteSpace(old)) DestinationBox.Text = Path.ChangeExtension(old, ExtensionFor(format));
        RefreshUi();
    }

    private void ExportSetting_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!_loading) RefreshUi();
    }

    private void RefreshUi()
    {
        if (FormatCombo is null) return;
        var format = SelectedText(FormatCombo);
        var audioOnly = format is "MP3" or "WAV" or "M4A";
        VideoOptionsPanel.IsEnabled = !audioOnly;
        FpsOptionsPanel.IsEnabled = !audioOnly;
        QualityOptionsPanel.IsEnabled = !audioOnly;
        VideoOptionsPanel.Opacity = audioOnly ? 0.38 : 1;
        FpsOptionsPanel.Opacity = audioOnly ? 0.38 : 1;
        QualityOptionsPanel.Opacity = audioOnly ? 0.38 : 1;

        var resolution = audioOnly ? "Audio only" : SelectedText(ResolutionCombo);
        var fps = audioOnly ? string.Empty : SelectedText(FpsCombo);
        var quality = audioOnly ? string.Empty : SelectedText(QualityCombo);
        var speed = SelectedTagText(SpeedCombo, "Fast");
        var cleanup = CleanupCheck?.IsChecked == true;
        var smartCopy = !audioOnly && speed.Equals("Fast", StringComparison.OrdinalIgnoreCase) &&
                        resolution.Equals("Original", StringComparison.OrdinalIgnoreCase) &&
                        fps.Equals("Original", StringComparison.OrdinalIgnoreCase);
        ExportSummary.Text = audioOnly
            ? $"{format} • {SelectedText(AudioQualityCombo)} • {(cleanup ? "voice cleanup enabled" : "fast audio export")}"
            : smartCopy
                ? $"{format} • Smart Export: video copied without re-encoding{(cleanup ? "; only audio cleanup is processed" : "; audio copied when compatible")}"
                : $"{format} • {resolution} • {fps} • {quality} • {speed} • {SelectedText(AudioQualityCombo)} audio";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var format = SelectedText(FormatCombo);
        var ext = ExtensionFor(format);
        var dialog = new SaveFileDialog
        {
            Title = "Export CutFlow media",
            FileName = Path.GetFileName(string.IsNullOrWhiteSpace(DestinationBox.Text) ? BuildDefaultPath(format) : DestinationBox.Text),
            InitialDirectory = SafeDirectory(DestinationBox.Text),
            DefaultExt = ext,
            Filter = FilterFor(format)
        };
        if (dialog.ShowDialog() == true) DestinationBox.Text = dialog.FileName;
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var destination = DestinationBox.Text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(destination))
        {
            MessageBox.Show("Choose where to save the export first.", "CutFlow", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var format = SelectedText(FormatCombo);
        var wantedExt = ExtensionFor(format);
        if (!Path.GetExtension(destination).Equals(wantedExt, StringComparison.OrdinalIgnoreCase))
            destination = Path.ChangeExtension(destination, wantedExt);

        var (width, height) = ResolveResolution(SelectedText(ResolutionCombo));
        var chosenDirectory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(chosenDirectory)) _settings.LastExportDirectory = chosenDirectory;

        var speedMode = SelectedTagText(SpeedCombo, "Fast");
        var applyCleanup = CleanupCheck.IsChecked == true;
        _settings.DefaultExportSpeed = speedMode;
        _settings.ApplyCleanupOnExport = applyCleanup;

        Result = new ExportOptions
        {
            DestinationPath = destination,
            Format = format,
            Width = width,
            Height = height,
            FrameRate = SelectedTagDouble(FpsCombo),
            VideoCrf = SelectedTagInt(QualityCombo, 18),
            AudioBitrateKbps = SelectedTagInt(AudioQualityCombo, 256),
            SpeedMode = speedMode,
            ApplyAudioCleanup = applyCleanup
        };
        DialogResult = true;
    }

    private (int width, int height) ResolveResolution(string setting)
    {
        if (!_project.Media.HasVideo || setting.Equals("Original", StringComparison.OrdinalIgnoreCase)) return (0, 0);
        var shortEdge = setting switch { "2160p" => 2160, "1440p" => 1440, "1080p" => 1080, "720p" => 720, "480p" => 480, _ => 0 };
        if (shortEdge <= 0 || _project.Media.Width <= 0 || _project.Media.Height <= 0) return (0, 0);

        if (_project.Media.Width >= _project.Media.Height)
        {
            var height = shortEdge;
            var width = Even((int)Math.Round(shortEdge * (_project.Media.Width / (double)_project.Media.Height)));
            return (width, Even(height));
        }
        else
        {
            var width = shortEdge;
            var height = Even((int)Math.Round(shortEdge * (_project.Media.Height / (double)_project.Media.Width)));
            return (Even(width), height);
        }
    }

    private string BuildDefaultPath(string format)
    {
        var defaultExports = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutFlow", "Exports");
        Directory.CreateDirectory(defaultExports);
        var folder = _settings.LastExportDirectory;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) folder = defaultExports;
        return Path.Combine(folder, SanitizeFileName(_project.Name) + " - cut" + ExtensionFor(format));
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }

    private void SelectSpeed(string setting)
    {
        var wanted = string.IsNullOrWhiteSpace(setting) ? "Fast" : setting.Trim();
        for (var i = 0; i < SpeedCombo.Items.Count; i++)
        {
            var item = SpeedCombo.Items[i] as ComboBoxItem;
            if (item?.Tag?.ToString()?.Equals(wanted, StringComparison.OrdinalIgnoreCase) == true)
            {
                SpeedCombo.SelectedIndex = i;
                return;
            }
        }
        SpeedCombo.SelectedIndex = 0;
    }

    private void SelectFps(string setting)
    {
        if (string.IsNullOrWhiteSpace(setting) || setting.Equals("Original", StringComparison.OrdinalIgnoreCase)) { FpsCombo.SelectedIndex = 0; return; }
        for (var i = 0; i < FpsCombo.Items.Count; i++)
            if ((FpsCombo.Items[i] as ComboBoxItem)?.Tag?.ToString()?.Equals(setting, StringComparison.OrdinalIgnoreCase) == true) { FpsCombo.SelectedIndex = i; return; }
        FpsCombo.SelectedIndex = 0;
    }

    private static void SelectComboPrefix(ComboBox box, string value)
    {
        for (var i = 0; i < box.Items.Count; i++)
            if ((box.Items[i] as ComboBoxItem)?.Content?.ToString()?.StartsWith(value, StringComparison.OrdinalIgnoreCase) == true) { box.SelectedIndex = i; return; }
        box.SelectedIndex = 0;
    }

    private static string SelectedText(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;
    private static int SelectedTagInt(ComboBox box, int fallback) => int.TryParse((box.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var value) ? value : fallback;
    private static double SelectedTagDouble(ComboBox box) => double.TryParse((box.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var value) ? value : 0;
    private static string SelectedTagText(ComboBox box, string fallback) => (box.SelectedItem as ComboBoxItem)?.Tag?.ToString() is { Length: > 0 } value ? value : fallback;
    private static int Even(int value) => Math.Max(2, value % 2 == 0 ? value : value + 1);
    private static string ExtensionFor(string format) => "." + format.ToLowerInvariant();
    private static string FilterFor(string format) => $"{format} file|*{ExtensionFor(format)}";
    private static string? SafeDirectory(string path) { try { var dir = Path.GetDirectoryName(path); return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) ? dir : null; } catch { return null; } }
    private static string SanitizeFileName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
}
