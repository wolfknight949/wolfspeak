using System.Runtime.InteropServices;
using System.Windows;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace WolfSpeak;

public enum TrayStatus { Idle, Ringing, InCall, Muted }

/// <summary>Notification-area icon with a status dot and a dark right-click menu.</summary>
public sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon icon;
    readonly Forms.ToolStripMenuItem muteItem, hangUpItem;
    readonly Dictionary<TrayStatus, Drawing.Icon> icons = [];
    TrayStatus status = (TrayStatus)(-1);
    string text = "";

    public event Action? OpenRequested;
    public event Action? ToggleMuteRequested;
    public event Action? HangUpRequested;
    public event Action? QuitRequested;

    public TrayIcon()
    {
        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new DarkRenderer(),
            ShowImageMargin = false,
            Font = new Drawing.Font("Segoe UI", 9.5f),
            BackColor = DarkColors.Background,
            ForeColor = DarkColors.Text,
            Padding = new Forms.Padding(2, 4, 2, 4),
        };
        var openItem = Item("Open WolfSpeak", () => OpenRequested?.Invoke());
        openItem.Font = new Drawing.Font(menu.Font, Drawing.FontStyle.Bold);
        muteItem = Item("Mute microphone", () => ToggleMuteRequested?.Invoke());
        hangUpItem = Item("Hang up", () => HangUpRequested?.Invoke());
        hangUpItem.Visible = false;
        menu.Items.AddRange([openItem, new Forms.ToolStripSeparator(), muteItem, hangUpItem,
            new Forms.ToolStripSeparator(), Item("Quit WolfSpeak", () => QuitRequested?.Invoke())]);

        using (var stream = Application.GetResourceStream(new Uri("pack://application:,,,/assets/wolfspeak.ico")).Stream)
        {
            var baseIcon = new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
            icons[TrayStatus.Idle] = baseIcon;
            icons[TrayStatus.Ringing] = WithDot(baseIcon, Drawing.Color.FromArgb(0xFB, 0xBF, 0x24));
            icons[TrayStatus.InCall] = WithDot(baseIcon, Drawing.Color.FromArgb(0x34, 0xD3, 0x99));
            icons[TrayStatus.Muted] = WithDot(baseIcon, Drawing.Color.FromArgb(0xF4, 0x3F, 0x5E));
        }

        icon = new Forms.NotifyIcon { Icon = icons[TrayStatus.Idle], Text = "WolfSpeak", ContextMenuStrip = menu, Visible = true };
        icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) OpenRequested?.Invoke(); };
        icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();
    }

    static Forms.ToolStripMenuItem Item(string text, Action onClick) =>
        new(text, null, (_, _) => onClick()) { Padding = new Forms.Padding(6, 5, 18, 5) };

    public void Update(TrayStatus newStatus, string tooltip, bool muted)
    {
        if (newStatus != status)
        {
            status = newStatus;
            icon.Icon = icons[newStatus];
            hangUpItem.Visible = newStatus != TrayStatus.Idle;
            hangUpItem.Text = newStatus == TrayStatus.Ringing ? "Decline / cancel call" : "Hang up";
        }
        muteItem.Text = muted ? "Unmute microphone" : "Mute microphone";
        tooltip = tooltip.Length > 63 ? tooltip[..62] + "…" : tooltip;
        if (tooltip != text) icon.Text = text = tooltip;
    }

    public void Notify(string title, string message) =>
        icon.ShowBalloonTip(4000, title, message, Forms.ToolTipIcon.None);

    /// <summary>Base icon with a small status dot in the bottom-right corner.</summary>
    static Drawing.Icon WithDot(Drawing.Icon baseIcon, Drawing.Color color)
    {
        int w = baseIcon.Width, h = baseIcon.Height;
        using var bmp = new Drawing.Bitmap(w, h);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.DrawIcon(baseIcon, new Drawing.Rectangle(0, 0, w, h));
            float d = w * 0.5f;
            var dot = new Drawing.RectangleF(w - d, h - d, d - 0.5f, d - 0.5f);
            using var ring = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x0B, 0x0E, 0x14));
            using var fill = new Drawing.SolidBrush(color);
            g.FillEllipse(ring, Drawing.RectangleF.Inflate(dot, 1.2f, 1.2f));
            g.FillEllipse(fill, dot);
        }
        IntPtr handle = bmp.GetHicon();
        var result = (Drawing.Icon)Drawing.Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        return result;
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        foreach (var i in icons.Values) i.Dispose();
    }

    sealed class DarkRenderer() : Forms.ToolStripProfessionalRenderer(new DarkColors())
    {
        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = DarkColors.Text;
            base.OnRenderItemText(e);
        }
    }

    sealed class DarkColors : Forms.ProfessionalColorTable
    {
        public static readonly Drawing.Color Background = Drawing.Color.FromArgb(0x16, 0x1C, 0x29);
        public static readonly Drawing.Color Text = Drawing.Color.FromArgb(0xE9, 0xED, 0xF5);
        static readonly Drawing.Color Hover = Drawing.Color.FromArgb(0x23, 0x2C, 0x40);
        static readonly Drawing.Color Line = Drawing.Color.FromArgb(0x33, 0x40, 0x5A);

        public override Drawing.Color ToolStripDropDownBackground => Background;
        public override Drawing.Color ImageMarginGradientBegin => Background;
        public override Drawing.Color ImageMarginGradientMiddle => Background;
        public override Drawing.Color ImageMarginGradientEnd => Background;
        public override Drawing.Color MenuBorder => Line;
        public override Drawing.Color MenuItemBorder => Hover;
        public override Drawing.Color MenuItemSelected => Hover;
        public override Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override Drawing.Color MenuItemPressedGradientBegin => Hover;
        public override Drawing.Color MenuItemPressedGradientEnd => Hover;
        public override Drawing.Color SeparatorDark => Drawing.Color.FromArgb(0x2A, 0x33, 0x46);
        public override Drawing.Color SeparatorLight => Background;
    }
}
