using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace MarkView;

/// <summary>
/// The window's title bar, Notepad style: app icon, rounded tabs that merge into the menu bar
/// below, a "+" button, free space to drag the window by, and minimize / maximize / close.
///
/// The form has no system caption. Windows asks the form what is under the mouse (WM_NCHITTEST);
/// this control answers "not me" for everything except tabs and "+", and the form then reports
/// the caption buttons and the empty space as real caption parts. So dragging, double-click to
/// maximize, Aero Snap and the Snap layouts flyout on the maximize button all stay native.
/// </summary>
sealed class TitleBar : Control
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<Document> Tabs { get; set; } = [];
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Palette Palette { get; set; } = Palette.LightPalette;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool WindowActive { get; set; } = true;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Maximized { get; set; }

    public event Action<int>? TabClicked;
    public event Action<int>? CloseClicked;
    public event Action? NewClicked;
    public event Action<int, int>? TabMoved;          // from, to
    public event Action<int, Point>? TabRightClicked;

    readonly ToolTip tip = new() { InitialDelay = 600 };
    string tipText = "";
    int hover = -1, pressed = -1, scroll, hotButton, pressedButton;
    bool hoverClose, hoverPlus, pressedClose, pressedPlus, dragging;
    Point pressPoint;
    Icon? smallIcon;

    public TitleBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Top;
        Font = new Font("Segoe UI", 9f);
        Height = 40;
    }

    // ------------------------------------------------------------ geometry

    int Px(float v) => (int)Math.Round(v * DeviceDpi / 96f);
    int ButtonWidth => Px(46);
    int IconArea => Px(44);
    int PlusWidth => Px(40);
    int TabTop => Px(7);
    int TabsLimit => Width - 3 * ButtonWidth - Px(46);  // always leave some caption to drag the window by

    Rectangle CloseRect => new(Width - ButtonWidth, 0, ButtonWidth, Height);
    Rectangle MaxRect => new(Width - 2 * ButtonWidth, 0, ButtonWidth, Height);
    Rectangle MinRect => new(Width - 3 * ButtonWidth, 0, ButtonWidth, Height);

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Height = Px(40); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Height = Px(40); UpdateTabs(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); UpdateTabs(); }

    int TabWidth()
    {
        int n = Tabs.Count;
        return n == 0 ? 0 : Math.Clamp((TabsLimit - IconArea - PlusWidth) / n, Px(96), Px(240));
    }

    bool Overflow => IconArea + Tabs.Count * TabWidth() + PlusWidth > TabsLimit;
    int TabsRight => Overflow ? TabsLimit - PlusWidth : IconArea + Tabs.Count * TabWidth();
    int MaxScroll => Math.Max(0, Tabs.Count * TabWidth() - (TabsLimit - PlusWidth - IconArea));

    Rectangle TabRect(int i) => new(IconArea + i * TabWidth() - scroll, TabTop, TabWidth(), Height - TabTop);

    Rectangle PlusRect()
    {
        int x = (Overflow ? TabsLimit - PlusWidth : TabsRight) + Px(3);
        return new Rectangle(x, TabTop + Px(4), Px(34), Height - TabTop - Px(8));
    }

    Rectangle TabCloseRect(Rectangle tab)
    {
        int s = Px(24);
        return new Rectangle(tab.Right - s - Px(7), tab.Top + (tab.Height - s) / 2, s, s);
    }

    int HitTab(Point p)
    {
        if (p.X < IconArea || p.X >= TabsRight || p.Y < TabTop) return -1;
        for (int i = 0; i < Tabs.Count; i++)
            if (TabRect(i).Contains(p)) return i;
        return -1;
    }

    /// <summary>What the form should report to Windows for a point on this control.</summary>
    public int CaptionHitTest(Point p)
    {
        if (CloseRect.Contains(p)) return Native.HTCLOSE;
        if (MaxRect.Contains(p)) return Native.HTMAXBUTTON;
        if (MinRect.Contains(p)) return Native.HTMINBUTTON;
        if (HitTab(p) >= 0 || PlusRect().Contains(p)) return Native.HTCLIENT;
        return Native.HTCAPTION;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_NCHITTEST)
        {
            var p = PointToClient(Native.PointFromLParam(m.LParam));
            bool topResizeEdge = !Maximized && p.Y < Px(6);
            if (topResizeEdge || CaptionHitTest(p) != Native.HTCLIENT)
            {
                m.Result = Native.HTTRANSPARENT;  // let the form answer: caption, button or resize edge
                return;
            }
        }
        base.WndProc(ref m);
    }

    /// <summary>Call after the tab list or the selection changes.</summary>
    public void UpdateTabs()
    {
        if (Overflow && SelectedIndex >= 0 && SelectedIndex < Tabs.Count)
        {
            var r = TabRect(SelectedIndex);
            int right = TabsLimit - PlusWidth;
            if (r.Left < IconArea) scroll -= IconArea - r.Left;
            else if (r.Right > right) scroll += r.Right - right;
        }
        scroll = Math.Clamp(scroll, 0, MaxScroll);
        Invalidate();
    }

    public int PressedButton => pressedButton;

    public void SetHot(int hitCode)
    {
        if (hotButton == hitCode) return;
        hotButton = hitCode;
        Invalidate(new Rectangle(MinRect.Left, 0, Width - MinRect.Left, Height));
    }

    public void SetPressed(int hitCode)
    {
        if (pressedButton == hitCode) return;
        pressedButton = hitCode;
        Invalidate(new Rectangle(MinRect.Left, 0, Width - MinRect.Left, Height));
    }

    // ------------------------------------------------------------ painting

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Palette;
        var g = e.Graphics;
        g.Clear(p.TitleBar);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color text = WindowActive ? p.Text : p.TextDim;

        int iconSize = Px(16);
        if (smallIcon is null || smallIcon.Width != iconSize)
        {
            smallIcon?.Dispose();
            smallIcon = new Icon(MainForm.AppIcon, iconSize, iconSize);
        }
        int tabMid = (TabTop + Height) / 2;
        g.DrawIcon(smallIcon, new Rectangle(Px(15), tabMid - iconSize / 2, iconSize, iconSize));

        g.SetClip(Rectangle.FromLTRB(IconArea, 0, TabsRight, Height));
        for (int i = 0; i < Tabs.Count; i++)
        {
            var r = TabRect(i);
            if (r.Right < IconArea || r.Left > TabsRight) continue;
            var d = Tabs[i];
            bool sel = i == SelectedIndex, hot = i == hover;

            if (sel || hot)
            {
                using var path = TopRounded(r, Px(7));
                using var b = new SolidBrush(sel ? p.Chrome : p.Hover);
                g.FillPath(b, path);
            }
            else if (i < Tabs.Count - 1 && i + 1 != SelectedIndex && i + 1 != hover)
            {
                using var pen = new Pen(p.Separator);
                g.DrawLine(pen, r.Right - 1, r.Top + Px(9), r.Right - 1, r.Bottom - Px(9));
            }

            var close = TabCloseRect(r);
            var textRect = Rectangle.FromLTRB(r.Left + Px(12), r.Top, close.Left - Px(2), r.Bottom);
            TextRenderer.DrawText(g, d.DisplayName, Font, textRect, sel ? text : p.TextDim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

            if (d.Dirty && !hot)
            {
                int s = Px(8);
                using var b = new SolidBrush(sel ? text : p.TextDim);
                g.FillEllipse(b, close.Left + (close.Width - s) / 2, close.Top + (close.Height - s) / 2, s, s);
            }
            else if (sel || hot)
            {
                if (hot && hoverClose)
                {
                    using var path = Rounded(close, Px(4));
                    using var b = new SolidBrush(p.Pressed);
                    g.FillPath(b, path);
                }
                DrawGlyph(g, "", close, sel ? text : p.TextDim, 8f);
            }
        }
        g.ResetClip();

        var plus = PlusRect();
        if (hoverPlus)
        {
            using var path = Rounded(plus, Px(5));
            using var b = new SolidBrush(p.Hover);
            g.FillPath(b, path);
        }
        DrawGlyph(g, "", plus, text, 10f);

        DrawCaptionButton(g, MinRect, Native.HTMINBUTTON, "", text);
        DrawCaptionButton(g, MaxRect, Native.HTMAXBUTTON, Maximized ? "" : "", text);
        DrawCaptionButton(g, CloseRect, Native.HTCLOSE, "", text);
    }

    void DrawCaptionButton(Graphics g, Rectangle r, int code, string glyph, Color text)
    {
        bool hot = hotButton == code, down = pressedButton == code;
        if (code == Native.HTCLOSE && (hot || down))
        {
            using var b = new SolidBrush(down ? Color.FromArgb(148, 32, 22) : Color.FromArgb(196, 43, 28));
            g.FillRectangle(b, r);
            text = Color.White;
        }
        else if (hot || down)
        {
            using var b = new SolidBrush(down ? Palette.Pressed : Palette.Hover);
            g.FillRectangle(b, r);
        }
        DrawGlyph(g, glyph, r, text, 7.5f);
    }

    void DrawGlyph(Graphics g, string glyph, Rectangle r, Color color, float pixelsAt96)
    {
        using var font = new Font(Theme.IconFontName, Px(pixelsAt96 * 4 / 3f), GraphicsUnit.Pixel);
        TextRenderer.DrawText(g, glyph, font, r, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }

    static GraphicsPath TopRounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d - 1, r.Top, d, d, 270, 90);
        p.AddLine(r.Right - 1, r.Bottom, r.Left, r.Bottom);
        p.CloseFigure();
        return p;
    }

    static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ------------------------------------------------------------ mouse (tabs and "+")

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        pressPoint = e.Location;
        pressedPlus = PlusRect().Contains(e.Location);
        pressed = HitTab(e.Location);
        pressedClose = pressed >= 0 && TabCloseRect(TabRect(pressed)).Contains(e.Location);
        if (pressed >= 0 && !pressedClose) TabClicked?.Invoke(pressed);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHot(0);
        if (pressed >= 0 && !pressedClose && e.Button == MouseButtons.Left)
        {
            if (!dragging && Math.Abs(e.X - pressPoint.X) > Px(6)) dragging = true;
            if (dragging)
            {
                // swap with a neighbour once the pointer crosses its midpoint
                int target = pressed;
                if (pressed + 1 < Tabs.Count && e.X > TabRect(pressed + 1).Left + TabWidth() / 2) target = pressed + 1;
                else if (pressed > 0 && e.X < TabRect(pressed - 1).Left + TabWidth() / 2) target = pressed - 1;
                if (target != pressed)
                {
                    TabMoved?.Invoke(pressed, target);
                    pressed = hover = target;
                }
                return;
            }
        }

        int h = HitTab(e.Location);
        bool hc = h >= 0 && TabCloseRect(TabRect(h)).Contains(e.Location);
        bool hp = PlusRect().Contains(e.Location);
        if (h != hover || hc != hoverClose || hp != hoverPlus)
        {
            hover = h; hoverClose = hc; hoverPlus = hp;
            Invalidate();
        }
        string t = h >= 0 ? (Tabs[h].FilePath ?? "Untitled") : hp ? "New tab (Ctrl+N)" : "";
        if (t != tipText) { tipText = t; tip.SetToolTip(this, t); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int h = HitTab(e.Location);
        switch (e.Button)
        {
            case MouseButtons.Left:
                if (!dragging && pressedClose && h == pressed && TabCloseRect(TabRect(h)).Contains(e.Location))
                    CloseClicked?.Invoke(h);
                else if (pressedPlus && PlusRect().Contains(e.Location))
                    NewClicked?.Invoke();
                break;
            case MouseButtons.Middle when h >= 0:
                CloseClicked?.Invoke(h);
                break;
            case MouseButtons.Right when h >= 0:
                TabRightClicked?.Invoke(h, e.Location);
                break;
        }
        pressed = -1;
        pressedClose = pressedPlus = dragging = false;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hover = -1;
        hoverClose = hoverPlus = false;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        scroll = Math.Clamp(scroll - e.Delta / 2, 0, MaxScroll);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { tip.Dispose(); smallIcon?.Dispose(); }
        base.Dispose(disposing);
    }
}
