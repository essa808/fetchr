using System.Drawing;
using System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace Fetchr.App;

/// <summary>
/// The notification-area icon: open, pause/resume everything, exit; and Windows notifications
/// (finished, unpacked, needs a password) that do something when clicked.
/// </summary>
public sealed class Tray : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Action _open;
    private readonly Action _pauseAll;
    private readonly Action _resumeAll;
    private readonly Action _exit;
    private Action? _balloonClick;

    public Tray(Action open, Action pauseAll, Action resumeAll, Action exit)
    {
        (_open, _pauseAll, _resumeAll, _exit) = (open, pauseAll, resumeAll, exit);
        _icon = new Forms.NotifyIcon { Icon = MakeIcon(), Text = "Fetchr", Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) _open(); };
        _icon.BalloonTipClicked += (_, _) => (_balloonClick ?? _open)();
        _icon.BalloonTipClosed += (_, _) => _balloonClick = null;
        RebuildMenu();
    }

    /// <summary>Call after the language changes.</summary>
    public void RebuildMenu()
    {
        var menu = new Forms.ContextMenuStrip { RightToLeft = App.Language == "ar" ? Forms.RightToLeft.Yes : Forms.RightToLeft.No };
        menu.Items.Add(App.Str("S.Tray.Open"), null, (_, _) => _open());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(App.Str("S.Tray.PauseAll"), null, (_, _) => _pauseAll());
        menu.Items.Add(App.Str("S.Tray.ResumeAll"), null, (_, _) => _resumeAll());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(App.Str("S.Tray.Exit"), null, (_, _) => _exit());
        var old = _icon.ContextMenuStrip;
        _icon.ContextMenuStrip = menu;
        old?.Dispose();
    }

    public void Notify(string title, string text, Action? onClick = null, bool warning = false)
    {
        _balloonClick = onClick;
        _icon.ShowBalloonTip(5000, title, text, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.None);
    }

    /// <summary>A small drawn icon (arrow into a tray) in the accent colour, so nothing is borrowed.</summary>
    private static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(Color.FromArgb(0x2A, 0x5B, 0xD7));
            using var path = new GraphicsPath();
            path.AddArc(1, 1, 10, 10, 180, 90);
            path.AddArc(21, 1, 10, 10, 270, 90);
            path.AddArc(21, 21, 10, 10, 0, 90);
            path.AddArc(1, 21, 10, 10, 90, 90);
            path.CloseFigure();
            g.FillPath(bg, path);
            using var pen = new Pen(Color.White, 3) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLine(pen, 16, 7, 16, 19);
            g.DrawLines(pen, [new PointF(10.5f, 14), new PointF(16, 19.5f), new PointF(21.5f, 14)]);
            g.DrawLine(pen, 9, 24.5f, 23, 24.5f);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
