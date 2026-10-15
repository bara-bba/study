using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Study.Core.Config;
using Study.Core.Models;
using Study.Native;
using Study.Services;
using Brushes = System.Windows.Media.Brushes;

namespace Study;

public partial class SettingsWindow : Window
{
    private const string CustomLabel = "Custom file…";

    private readonly StudySettings _start;
    private readonly AlarmPlayer _preview = new();
    private string _customPath;
    private string _logFolder;

    public SettingsWindow()
    {
        InitializeComponent();
        DwmHelper.ApplyPowerToysChrome(this);
        Icon = App.CurrentIconSource();
        _start = App.Settings;
        _customPath = _start.CustomSoundPath;
        _logFolder = _start.LogFolder;

        ConfigPathText.Text = $"Saved in {App.Store.Folder}";
        var sounds = AlarmPlayer.BuiltInSounds().ToList();
        SoundCombo.ItemsSource = sounds.Append(CustomLabel).ToList();
        SoundCombo.SelectedItem = _start.Sound == StudySettings.CustomSound ? CustomLabel
            : sounds.Contains(_start.Sound) ? _start.Sound : sounds.FirstOrDefault();
        CustomPathText.Text = string.IsNullOrEmpty(_customPath) ? "No file chosen (.mp3 or .wav)" : _customPath;

        VolumeSlider.Value = _start.Volume;
        AutoStopBox.Text = _start.AutoStopSeconds.ToString();
        PollBox.Text = _start.DefaultPollMs.ToString();
        SkipPingCheck.IsChecked = _start.SkipPing;
        CommAlarmCheck.IsChecked = _start.AlarmOnConnectionLost;
        NotifyCheck.IsChecked = _start.TrayNotifications;
        LogFolderText.Text = _logFolder;
        AccentBox.Text = _start.AccentColor;
        // The registry is the source of truth (the user can remove it in Task Manager → Startup).
        StartWithWindowsCheck.IsChecked = StartupRegistration.IsEnabled();

        RefreshCustomPanel();
        Closed += (_, _) => _preview.Stop();
    }

    private void RefreshCustomPanel() =>
        CustomPanel.Visibility = SoundCombo.SelectedItem as string == CustomLabel ? Visibility.Visible : Visibility.Collapsed;

    private void SoundCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshCustomPanel();

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        VolumeText.Text = $"{(int)VolumeSlider.Value}%";

    private void AccentBox_TextChanged(object sender, TextChangedEventArgs e) =>
        AccentSwatch.Background = AccentTheme.TryParse(AccentBox.Text, out var c)
            ? new SolidColorBrush(c)
            : Brushes.Transparent;

    private void ChooseSound_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Audio (*.mp3;*.wav)|*.mp3;*.wav", Title = "Choose alarm sound" };
        if (dlg.ShowDialog(this) == true)
        {
            _customPath = dlg.FileName;
            CustomPathText.Text = _customPath;
        }
    }

    private void TestSound_Click(object sender, RoutedEventArgs e)
    {
        if (BuildSettings(out _) is { } s)
        {
            _preview.Preview(s);
        }
    }

    private void StopSound_Click(object sender, RoutedEventArgs e) => _preview.Stop();

    private void ChooseLogFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Log folder", InitialDirectory = Directory.Exists(_logFolder) ? _logFolder : "" };
        if (dlg.ShowDialog(this) == true)
        {
            _logFolder = dlg.FolderName;
            LogFolderText.Text = _logFolder;
        }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_logFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_logFolder}\"") { UseShellExecute = true });
    }

    private StudySettings? BuildSettings(out string error)
    {
        error = "";
        var custom = SoundCombo.SelectedItem as string == CustomLabel;
        if (custom && !File.Exists(_customPath))
        {
            error = "Choose an existing .mp3/.wav file for the custom sound.";
            return null;
        }

        if (!int.TryParse(AutoStopBox.Text.Trim(), out var autoStop) || autoStop is < 0 or > 86_400)
        {
            error = "Auto-stop must be 0 – 86400 seconds.";
            return null;
        }

        if (!int.TryParse(PollBox.Text.Trim(), out var poll) || poll is < WatchDefinition.MinPollMs or > WatchDefinition.MaxPollMs)
        {
            error = $"Default polling time must be {WatchDefinition.MinPollMs} – {WatchDefinition.MaxPollMs} ms.";
            return null;
        }

        if (!AccentTheme.TryParse(AccentBox.Text, out var accent))
        {
            error = "Accent color must be #RRGGBB, e.g. #0078D4.";
            return null;
        }

        return _start with
        {
            Sound = custom ? StudySettings.CustomSound : SoundCombo.SelectedItem as string ?? _start.Sound,
            CustomSoundPath = _customPath,
            Volume = (int)VolumeSlider.Value,
            AutoStopSeconds = autoStop,
            DefaultPollMs = poll,
            SkipPing = SkipPingCheck.IsChecked == true,
            AlarmOnConnectionLost = CommAlarmCheck.IsChecked == true,
            TrayNotifications = NotifyCheck.IsChecked == true,
            LogFolder = _logFolder,
            AccentColor = AccentTheme.ToHex(accent),
            StartWithWindows = StartWithWindowsCheck.IsChecked == true,
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (BuildSettings(out var error) is not { } s)
        {
            ErrorText.Text = error;
            return;
        }

        if (s.StartWithWindows != StartupRegistration.IsEnabled() && !StartupRegistration.SetEnabled(s.StartWithWindows))
        {
            ErrorText.Text = "Windows refused the Start-with-Windows change (registry). Other settings were not saved.";
            return;
        }

        try
        {
            Directory.CreateDirectory(s.LogFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorText.Text = $"Cannot use log folder: {ex.Message}";
            return;
        }

        if (AccentTheme.TryParse(s.AccentColor, out var accent))
        {
            AccentTheme.Apply(accent);
        }

        App.Current.SaveSettings(s);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not TextBox)
        {
            DragMove();
        }
    }
}
