using System.Text;
using System.Windows;
using MyLianPC.Core.Devices;

namespace MyLianPC.App.Views;

/// <summary>Shows exactly which parts of a profile were applied and which failed (never claims full success on partial failure).</summary>
public static class ApplyReportDialog
{
    public static void Show(ApplyReport report, bool onlyOnFailure = false)
    {
        if (onlyOnFailure && report.AllSucceeded) return;
        var sb = new StringBuilder();
        sb.AppendLine(report.AllSucceeded
            ? $"'{report.ProfileName}' was applied to every device."
            : $"'{report.ProfileName}' was only partly applied:");
        sb.AppendLine();
        foreach (var r in report.Results) sb.AppendLine($"{(r.Success ? "✔" : "✖")}  {r.Part}: {r.Message}");
        MessageBox.Show(Application.Current.MainWindow!, sb.ToString(), "Apply result", MessageBoxButton.OK,
            report.AllSucceeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }
}
