using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;

namespace WinKame.Tray;

/// <summary>Notification-area icon with the app menu.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _pause;

    public event Action? SettingsRequested;
    public event Action? ShelfRequested;
    public event Action? JobsRequested;
    public event Action<bool>? PauseToggled;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "WinKame",
            Visible = true,
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(T("Configurações"), null, (_, _) => SettingsRequested?.Invoke());
        menu.Items.Add(T("Prateleira"), null, (_, _) => ShelfRequested?.Invoke());
        menu.Items.Add(T("Tarefas recentes"), null, (_, _) => JobsRequested?.Invoke());
        menu.Items.Add(new Forms.ToolStripSeparator());
        _pause = new Forms.ToolStripMenuItem(T("Pausar a roda")) { CheckOnClick = true };
        _pause.CheckedChanged += (_, _) => PauseToggled?.Invoke(_pause.Checked);
        menu.Items.Add(_pause);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(T("Sair"), null, (_, _) => ExitRequested?.Invoke());
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) SettingsRequested?.Invoke();
        };
    }

    public bool Paused
    {
        get => _pause.Checked;
        set => _pause.Checked = value;
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/WinKame.ico"));
            if (res is not null)
            {
                using var s = res.Stream;
                return new System.Drawing.Icon(s, Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (IOException)
        {
        }
        return System.Drawing.SystemIcons.Application;
    }

    public void Balloon(string title, string text)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(5000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
