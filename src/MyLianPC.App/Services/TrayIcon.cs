using WinForms = System.Windows.Forms;

namespace MyLianPC.App.Services;

/// <summary>Notification-area icon with quick profile switching.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly Action _show;
    private readonly Action _exit;

    public TrayIcon(Action show, Action exit)
    {
        _show = show;
        _exit = exit;
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("app.ico")!;
        _icon = new WinForms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, 16, 16),
            Text = "MyLianPC",
            Visible = true,
            ContextMenuStrip = new WinForms.ContextMenuStrip(),
        };
        _icon.DoubleClick += (_, _) => _show();
        // Readings are not polled while the window is in the tray, so refresh the profile list when the menu opens.
        _icon.ContextMenuStrip.Opening += (_, _) =>
        {
            _ = AppState.Current.RefreshProfilesAsync();
            BuildMenu();
        };
        BuildMenu();
    }

    private void BuildMenu()
    {
        var menu = _icon.ContextMenuStrip!;
        menu.Items.Clear();
        menu.Items.Add("Open MyLianPC", null, (_, _) => _show());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        var state = AppState.Current;
        foreach (var p in state.Profiles.Profiles)
        {
            var item = new WinForms.ToolStripMenuItem(p.Name) { Checked = p.Id == state.Profiles.ActiveProfileId };
            var id = p.Id;
            item.Click += async (_, _) =>
            {
                await AppState.TryAsync(c => c.ActivateAsync(id), "Activating the profile");
                await state.RefreshProfilesAsync();
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit (hardware control keeps running)", null, (_, _) => _exit());
    }

    public void SetTooltip(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
