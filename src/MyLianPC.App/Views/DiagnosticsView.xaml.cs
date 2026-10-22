using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MyLianPC.App.Services;

namespace MyLianPC.App.Views;

public partial class DiagnosticsView : UserControl
{
    public DiagnosticsView()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var report = await AppState.Current.Client.DiagnosticsAsync();
            var local = $"App {typeof(DiagnosticsView).Assembly.GetName().Version?.ToString(3)} · user {Environment.UserName} · service: {SystemIntegration.ServiceState()} · " +
                        $"Windows Dynamic Lighting on motherboard (this account): {SystemIntegration.DynamicLightingEnabledForMotherboard?.ToString() ?? "n/a"}";
            Report.Text = local + Environment.NewLine + Environment.NewLine + report;
        }
        catch (Exception e)
        {
            Report.Text = $"Could not get the report from the service: {e.Message}\n\nService state: {SystemIntegration.ServiceState()}\n" +
                          @"Service log folder: C:\ProgramData\MyLianPC\logs";
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void Copy_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(Report.Text);

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { FileName = $"MyLianPC-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.txt", Filter = "Text file|*.txt" };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) await File.WriteAllTextAsync(dlg.FileName, Report.Text);
    }
}
