using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using BoostStudio.Core.Models;
using Forms = System.Windows.Forms;

namespace BoostStudio.App.Services;

/// <summary>Tray icon and quick actions. NotifyIcon is a WinForms component, isolated here.</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _processingItem;
    private readonly Forms.ToolStripMenuItem _bassItem;
    private readonly Forms.ToolStripMenuItem _volumeItem;
    private readonly Forms.ToolStripMenuItem _equalizerItem;
    private readonly Icon? _icon;
    private bool _disposed;

    public event EventHandler? ShowRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ToggleProcessingRequested;
    public event EventHandler? ToggleBassRequested;
    public event EventHandler? ToggleVolumeRequested;
    public event EventHandler? ToggleEqualizerRequested;

    public TrayIconService()
    {
        _icon = LoadApplicationIcon();
        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new Forms.ToolStripProfessionalRenderer(new DarkMenuRenderer()),
            ShowImageMargin = false,
            Font = new Font("Segoe UI", 9f),
            BackColor = Color.FromArgb(20, 28, 38),
            ForeColor = Color.FromArgb(235, 241, 247)
        };

        var showItem = new Forms.ToolStripMenuItem("Open Boost Studio") { Font = new Font("Segoe UI Semibold", 9f) };
        showItem.Click += (_, _) => Raise(ShowRequested);
        _processingItem = CreateItem("Master processing", () => Raise(ToggleProcessingRequested));
        _bassItem = CreateItem("Bass Booster", () => Raise(ToggleBassRequested));
        _volumeItem = CreateItem("Volume Booster", () => Raise(ToggleVolumeRequested));
        _equalizerItem = CreateItem("Equalizer", () => Raise(ToggleEqualizerRequested));
        var settingsItem = CreateItem("Settings", () => Raise(SettingsRequested), Forms.Keys.None);
        var exitItem = CreateItem("Exit Boost Studio", () => Raise(ExitRequested), Forms.Keys.None);

        menu.Items.Add(showItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_processingItem);
        menu.Items.Add(_bassItem);
        menu.Items.Add(_volumeItem);
        menu.Items.Add(_equalizerItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "Boost Studio",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => Raise(ShowRequested);
        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
                Raise(ShowRequested);
        };
    }

    private Forms.ToolStripMenuItem CreateItem(string text, Action action, Forms.Keys shortcut = Forms.Keys.None)
    {
        var item = new Forms.ToolStripMenuItem(text)
        {
            ForeColor = Color.FromArgb(235, 241, 247),
            ShortcutKeys = shortcut
        };
        item.Click += (_, _) => action();
        return item;
    }

    public void UpdateState(AppSettings settings, string? engineSummary = null)
    {
        if (_disposed) return;

        void Update()
        {
            _processingItem.Checked = settings.ProcessingEnabled;
            _bassItem.Checked = settings.Bass.Enabled;
            _volumeItem.Checked = settings.Volume.Enabled;
            _equalizerItem.Checked = settings.Equalizer.Enabled;

            var state = !settings.ProcessingEnabled
                ? "Bypassed"
                : string.Join(" · ", new[]
                {
                    settings.Bass.Enabled ? "Bass" : null,
                    settings.Volume.Enabled ? "Volume" : null,
                    settings.Equalizer.Enabled ? "EQ" : null
                }.Where(s => s is not null));

            if (string.IsNullOrWhiteSpace(state)) state = "Standby";
            var text = $"Boost Studio — {state}";
            if (!string.IsNullOrWhiteSpace(engineSummary))
                text += $" · {engineSummary}";
            _notifyIcon.Text = text.Length > 63 ? text[..63] : text;
        }

        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(Update, DispatcherPriority.Background);
        else
            Update();
    }

    public void ShowNotification(string title, string message)
    {
        if (_disposed) return;

        void Show()
        {
            try
            {
                _notifyIcon.ShowBalloonTip(3500, title, message, Forms.ToolTipIcon.Info);
            }
            catch
            {
                // Notification failures are non-fatal.
            }
        }

        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(Show, DispatcherPriority.Background);
        else
            Show();
    }

    private void Raise(EventHandler? handler) => handler?.Invoke(this, EventArgs.Empty);

    private static Icon? LoadApplicationIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/app.ico"));
            if (resource?.Stream is not null)
            {
                using var source = new Icon(resource.Stream);
                var size = Forms.SystemInformation.SmallIconSize;
                return new Icon(source, size);
            }
        }
        catch
        {
            // Fall through to the executable icon.
        }

        try
        {
            var executable = Environment.ProcessPath;
            return string.IsNullOrWhiteSpace(executable)
                ? SystemIcons.Application
                : Icon.ExtractAssociatedIcon(executable);
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.ContextMenuStrip?.Dispose();
            _notifyIcon.Dispose();
        }
        catch
        {
            // Ignore tray teardown failures.
        }

        _icon?.Dispose();
    }

    private sealed class DarkMenuRenderer : Forms.ProfessionalColorTable
    {
        public override Color MenuItemSelected => Color.FromArgb(39, 56, 72);
        public override Color MenuItemBorder => Color.FromArgb(53, 224, 195);
        public override Color ToolStripDropDownBackground => Color.FromArgb(20, 28, 38);
        public override Color MenuBorder => Color.FromArgb(45, 60, 76);
        public override Color ImageMarginGradientBegin => Color.FromArgb(20, 28, 38);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(20, 28, 38);
        public override Color ImageMarginGradientEnd => Color.FromArgb(20, 28, 38);
        public override Color MenuItemPressedGradientBegin => Color.FromArgb(26, 38, 50);
        public override Color MenuItemPressedGradientEnd => Color.FromArgb(26, 38, 50);
        public override Color SeparatorDark => Color.FromArgb(38, 52, 66);
        public override Color SeparatorLight => Color.FromArgb(38, 52, 66);
    }
}
