using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Study.Core.Monitoring;
using Study.Native;
using Study.ViewModels;

namespace Study;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _summaryTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow()
    {
        InitializeComponent();
        DwmHelper.ApplyPowerToysChrome(this);
        Icon = App.CurrentIconSource();
        TitleIcon.Source = App.CurrentVectorIcon();

        WatchList.ItemsSource = App.Monitor.Watches;
        App.Monitor.Watches.CollectionChanged += (_, _) => RefreshSummary();
        _summaryTimer.Tick += (_, _) => RefreshSummary();
        _summaryTimer.Start();

        RefreshSummary();
        RefreshAlarmBanner();
        RefreshPauseState();
    }

    /// <summary>Hides to the tray instead of exiting; monitoring keeps running.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    internal void RefreshAlarmBanner()
    {
        var ringing = App.Monitor.Ringing.ToList();
        AlarmBanner.Visibility = ringing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (ringing.Count == 0)
        {
            return;
        }

        AlarmTitle.Text = ringing.Count == 1 ? $"ALARM — {ringing[0].Title}" : $"{ringing.Count} ALARMS ACTIVE";
        AlarmDetail.Text = string.Join("   ·   ",
            ringing.Select(w => w.CommAlarm ? $"{w.Title}: connection lost" : $"{w.Title}: {w.Definition.Tag} = {w.ValueText}"));

        // Bring the window forward so an alarm is never missed behind other apps.
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
    }

    internal void RefreshPauseState()
    {
        PauseLabel.Text = App.Monitor.Paused ? "Resume monitoring" : "Pause monitoring";
        PauseGlyph.Data = System.Windows.Media.Geometry.Parse(App.Monitor.Paused
            ? "M 0,0 L 10,6 L 0,12 Z" // play
            : "M 0,0 H 3.5 V 12 H 0 Z M 6.5,0 H 10 V 12 H 6.5 Z"); // pause
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        var watches = App.Monitor.Watches;
        EmptyText.Visibility = watches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var connected = watches.Count(w => w.Status == WatchStatus.Connected);
        var problems = watches.Count(w => w.Status is WatchStatus.Unreachable or WatchStatus.Error);
        SummaryText.Text = App.Monitor.Paused
            ? "Paused"
            : $"{watches.Count} watch(es)  ·  {connected} connected" + (problems > 0 ? $"  ·  {problems} with problems" : "");
    }

    private static WatchItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as WatchItem;

    private void AddWatch_Click(object sender, RoutedEventArgs e)
    {
        if (App.Monitor.Plcs.Count == 0)
        {
            var r = MessageBox.Show(this,
                "The PLC list is empty. Open the PLC list now to import from RSLinx or a CSV file?",
                "study", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r == MessageBoxResult.Yes)
            {
                Plcs_Click(sender, e);
            }

            return;
        }

        var editor = new WatchEditorWindow(null) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is { } def)
        {
            App.Monitor.Upsert(def);
        }
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item)
        {
            return;
        }

        var editor = new WatchEditorWindow(item.Definition) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is { } def)
        {
            App.Monitor.Upsert(def);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item)
        {
            return;
        }

        var r = MessageBox.Show(this, $"Delete watch \"{item.Title}\"?", "study", MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes)
        {
            App.Monitor.Remove(item);
        }
    }

    private void Enabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox box && ItemOf(sender) is { } item)
        {
            App.Monitor.SetEnabled(item, box.IsChecked == true);
        }
    }

    private void Ack_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            App.Monitor.Acknowledge(item);
        }
    }

    private void AckAll_Click(object sender, RoutedEventArgs e) => App.Monitor.AcknowledgeAll();

    private void Plcs_Click(object sender, RoutedEventArgs e) => new PlcListWindow { Owner = this }.ShowDialog();

    private void Logs_Click(object sender, RoutedEventArgs e) => new LogViewerWindow().Show();

    private void Settings_Click(object sender, RoutedEventArgs e) => new SettingsWindow { Owner = this }.ShowDialog();

    private void Pause_Click(object sender, RoutedEventArgs e) => App.Current.TogglePause();

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();
}
