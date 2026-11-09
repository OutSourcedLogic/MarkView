using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace MarkView;

/// <summary>
/// Notepad-style tabbed window with its tabs in the title bar. Each tab is a <see cref="Document"/>
/// with its own raw-text editor; one shared WebView2 shows the active tab's formatted view, fed
/// from memory through a private https host (markview.local). Ctrl+E switches between the two.
/// </summary>
sealed class MainForm : Form
{
    const string AppName = "MarkView";
    const string Host = "markview.local";
    const string OpenFilter =
        "Markdown, JSON and text|*.md;*.markdown;*.mdown;*.mkd;*.json;*.jsonc;*.txt|" +
        "Markdown (*.md)|*.md;*.markdown;*.mdown;*.mkd|JSON (*.json)|*.json;*.jsonc|" +
        "Text documents (*.txt)|*.txt|All files (*.*)|*.*";
    const string SaveFilter = "Markdown (*.md)|*.md|JSON (*.json)|*.json|Text document (*.txt)|*.txt|All files (*.*)|*.*";

    public static readonly Icon AppIcon = new(typeof(MainForm).Assembly.GetManifestResourceStream("MarkView.ico")!);

    readonly Settings settings;
    Palette palette;
    readonly List<Document> docs = [];
    Document doc = null!;  // the active tab
    Document? tabMenuTarget;

    readonly TitleBar titleBar = new();
    readonly FindBar findBar = new();
    readonly Panel content = new() { Dock = DockStyle.Fill };
    readonly WebView2 web = new() { Dock = DockStyle.Fill };
    readonly MenuStrip menu = new() { ShowItemToolTips = true, Padding = new Padding(6, 2, 6, 2) };
    readonly StatusStrip status = new() { SizingGrip = false };
    readonly ContextMenuStrip tabMenu = new();
    readonly ToolStripStatusLabel stMessage = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripStatusLabel stPos = Cell(), stZoom = Cell(), stKind = Cell(), stEol = Cell(), stEnc = Cell();
    readonly ToolStripSeparator sepPos = new(), sepZoom = new(), sepKind = new(), sepEol = new(), sepEnc = new();

    ToolStripMenuItem miRaw = null!, miWrap = null!, miStatusBar = null!, miUndo = null!, miRedo = null!, miCut = null!,
        miPaste = null!, miDelete = null!, miReplace = null!, miGoTo = null!, miTimeDate = null!, miFormat = null!,
        miExpand = null!, miCollapse = null!;
    readonly Dictionary<ThemeMode, ToolStripMenuItem> themeItems = [];
    ToolStripButton tbView = null!;
    readonly List<ToolStripItem> formatTools = [];

    Font editorFont;
    bool webReady, webFailed, printAfterRender;
    double zoom = 1.0;
    double? restoreScroll;
    int findAnchor;

    static ToolStripStatusLabel Cell() => new() { Padding = new Padding(8, 0, 8, 0) };

    public MainForm(IReadOnlyList<string> startFiles, Settings settings)
    {
        this.settings = settings;
        palette = Theme.Resolve(settings.Theme);
        Text = AppName;
        Icon = AppIcon;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1000, 720);
        MinimumSize = new Size(520, 340);
        AllowDrop = true;
        editorFont = ZoomedFont();
        RestoreWindowBounds();

        BuildMenu();
        BuildToolbar();
        BuildTabMenu();
        status.Items.AddRange([stMessage, sepPos, stPos, sepZoom, stZoom, sepKind, stKind, sepEol, stEol, sepEnc, stEnc]);
        status.Visible = settings.StatusBar;
        content.Controls.Add(web);
        // Fill first, then the edges; the last-added docks first, so the title bar ends up on top.
        Controls.Add(content);
        Controls.Add(findBar);
        Controls.Add(status);
        Controls.Add(menu);
        Controls.Add(titleBar);
        MainMenuStrip = menu;

        titleBar.TabClicked += i => SelectTab(docs[i]);
        titleBar.CloseClicked += i => CloseTab(docs[i]);
        titleBar.NewClicked += NewTab;
        titleBar.TabMoved += (from, to) =>
        {
            var d = docs[from];
            docs.RemoveAt(from);
            docs.Insert(to, d);
            RefreshTabs();
        };
        titleBar.TabRightClicked += (i, pt) => { tabMenuTarget = docs[i]; tabMenu.Show(titleBar, pt); };

        findBar.SearchChanged += () => FindStep(forward: true, fromAnchor: true);
        findBar.NextClicked += () => FindStep(forward: true);
        findBar.PreviousClicked += () => FindStep(forward: false);
        findBar.ReplaceClicked += ReplaceOne;
        findBar.ReplaceAllClicked += ReplaceAll;
        findBar.CloseClicked += CloseFind;

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        Shown += async (_, _) => await InitWebAsync();
        FormClosing += OnFormClosing;
        Activated += (_, _) => { titleBar.WindowActive = true; titleBar.Invalidate(); };
        Deactivate += (_, _) => { titleBar.WindowActive = false; titleBar.Invalidate(); };
        Resize += (_, _) => { titleBar.Maximized = WindowState == FormWindowState.Maximized; titleBar.Invalidate(); };
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        ApplyTheme();
        OpenFiles(startFiles);
        if (docs.Count == 0) NewTab();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        base.Dispose(disposing);
    }

    // ================================================================ custom frame

    // The client area covers the whole window except the side and bottom resize borders: the
    // system caption is gone and the title bar control draws tabs and caption buttons instead.

    int FrameThickness =>
        Native.GetSystemMetricsForDpi(Native.SM_CYSIZEFRAME, (uint)DeviceDpi) +
        Native.GetSystemMetricsForDpi(Native.SM_CXPADDEDBORDER, (uint)DeviceDpi);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.SetDarkFrame(Handle, palette.Dark);
        Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_FRAMECHANGED | Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    static bool IsCaptionButton(int hit) => hit is Native.HTMINBUTTON or Native.HTMAXBUTTON or Native.HTCLOSE;

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Native.WM_NCCALCSIZE when m.WParam != IntPtr.Zero:
            {
                var before = Marshal.PtrToStructure<Native.RECT>(m.LParam);
                base.WndProc(ref m);
                var after = Marshal.PtrToStructure<Native.RECT>(m.LParam);
                // Keep the default side/bottom borders, drop the caption. A maximized window hangs
                // over the screen edge by its frame thickness, so keep that much at the top.
                after.Top = before.Top + (Native.IsZoomed(Handle) ? FrameThickness : 0);
                Marshal.StructureToPtr(after, m.LParam, false);
                m.Result = IntPtr.Zero;
                return;
            }
            case Native.WM_NCHITTEST:
            {
                base.WndProc(ref m);
                if ((int)m.Result != Native.HTCLIENT) return;  // side/bottom borders are still native
                var p = PointToClient(Native.PointFromLParam(m.LParam));
                int band = FrameThickness;
                if (!Native.IsZoomed(Handle) && p.Y < band)
                {
                    m.Result = p.X < band * 2 ? Native.HTTOPLEFT : p.X >= ClientSize.Width - band * 2 ? Native.HTTOPRIGHT : Native.HTTOP;
                    return;
                }
                if (titleBar.Bounds.Contains(p))
                    m.Result = titleBar.CaptionHitTest(new Point(p.X - titleBar.Left, p.Y - titleBar.Top));
                return;
            }
            case Native.WM_NCMOUSEMOVE:
            {
                int hit = (int)m.WParam;
                titleBar.SetHot(IsCaptionButton(hit) ? hit : 0);
                if (IsCaptionButton(hit))
                {
                    var tme = new Native.TRACKMOUSEEVENT
                    {
                        cbSize = Marshal.SizeOf<Native.TRACKMOUSEEVENT>(),
                        dwFlags = Native.TME_LEAVE | Native.TME_NONCLIENT,
                        hwndTrack = Handle,
                    };
                    Native.TrackMouseEvent(ref tme);
                }
                break;
            }
            // Our caption buttons are drawn by us: handle their clicks so Windows doesn't draw its own.
            case Native.WM_NCLBUTTONDOWN or Native.WM_NCLBUTTONDBLCLK when IsCaptionButton((int)m.WParam):
                titleBar.SetPressed((int)m.WParam);
                m.Result = IntPtr.Zero;
                return;
            case Native.WM_NCLBUTTONUP when IsCaptionButton((int)m.WParam):
            {
                int hit = (int)m.WParam;
                bool clicked = titleBar.PressedButton == hit;
                titleBar.SetPressed(0);
                if (clicked) BeginInvoke(() => CaptionCommand(hit));
                m.Result = IntPtr.Zero;
                return;
            }
            case Native.WM_NCMOUSELEAVE:
                titleBar.SetHot(0);
                titleBar.SetPressed(0);
                break;
        }
        base.WndProc(ref m);
    }

    void CaptionCommand(int hit)
    {
        switch (hit)
        {
            case Native.HTMINBUTTON: WindowState = FormWindowState.Minimized; break;
            case Native.HTMAXBUTTON: WindowState = Native.IsZoomed(Handle) ? FormWindowState.Normal : FormWindowState.Maximized; break;
            case Native.HTCLOSE: Close(); break;
        }
    }

    void RestoreWindowBounds()
    {
        if (settings.WindowBounds is not [int x, int y, int w, int h] || w < 200 || h < 150) return;
        var r = new Rectangle(x, y, w, h);
        if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r))) return;  // monitor since unplugged
        StartPosition = FormStartPosition.Manual;
        Bounds = r;
        if (settings.Maximized) WindowState = FormWindowState.Maximized;
    }

    void SaveWindowBounds()
    {
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        settings.WindowBounds = [b.X, b.Y, b.Width, b.Height];
        settings.Maximized = WindowState == FormWindowState.Maximized;
        settings.Save();
    }

    // ================================================================ theme

    void SetTheme(ThemeMode mode)
    {
        settings.Theme = mode;
        settings.Save();
        palette = Theme.Resolve(mode);
        ApplyTheme();
    }

    void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (settings.Theme != ThemeMode.System || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            var p = Theme.Resolve(ThemeMode.System);
            if (p.Dark == palette.Dark) return;
            palette = p;
            ApplyTheme();
        });
    }

    void ApplyTheme()
    {
        var p = palette;
#pragma warning disable WFO5001
        try { Application.SetColorMode(p.Dark ? SystemColorMode.Dark : SystemColorMode.Classic); } catch { }
#pragma warning restore WFO5001
        BackColor = p.Chrome;
        content.BackColor = p.EditorBack;
        titleBar.Palette = p;
        titleBar.Invalidate();
        ToolStripManager.Renderer = new ThemeRenderer(p);
        foreach (ToolStrip strip in new ToolStrip[] { menu, status, tabMenu })
        {
            strip.RenderMode = ToolStripRenderMode.ManagerRenderMode;
            strip.BackColor = p.Chrome;
            strip.ForeColor = p.Text;
            strip.Invalidate();
        }
        findBar.ApplyPalette(p);
        foreach (var d in docs) ThemeEditor(d);
        web.DefaultBackgroundColor = p.EditorBack;
        if (webReady)
            web.CoreWebView2.Profile.PreferredColorScheme = p.Dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
        if (IsHandleCreated) Theme.SetDarkFrame(Handle, p.Dark);
        foreach (var (mode, item) in themeItems) item.Checked = mode == settings.Theme;
    }

    void ThemeEditor(Document d)
    {
        d.Loading = true;
        d.Editor.BackColor = palette.EditorBack;
        d.Editor.ForeColor = palette.EditorText;
        d.Loading = false;
        Theme.SetScrollbarTheme(d.Editor, palette.Dark);
    }

    // ================================================================ menus and toolbar

    void BuildMenu()
    {
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.AddRange(new ToolStripItem[]
        {
            Item("&New Tab", Keys.Control | Keys.N, (_, _) => NewTab()),
            Item("New &Window", Keys.Control | Keys.Shift | Keys.N, (_, _) => NewWindow()),
            Item("&Open...", Keys.Control | Keys.O, (_, _) => OpenDialog()),
            Item("&Reload", Keys.Control | Keys.R, (_, _) => Reload()),
            new ToolStripSeparator(),
            Item("&Save", Keys.Control | Keys.S, (_, _) => Save(doc)),
            Item("Save &As...", Keys.Control | Keys.Shift | Keys.S, (_, _) => SaveAs(doc)),
            Item("Save A&ll", Keys.Control | Keys.Alt | Keys.S, (_, _) => SaveAll()),
            new ToolStripSeparator(),
            Item("&Print...", Keys.Control | Keys.P, (_, _) => Print()),
            Item("Open Containing &Folder", Keys.None, (_, _) => { if (doc.FilePath is { } p) RevealInExplorer(p); }),
            new ToolStripSeparator(),
            Item("&Close Tab", Keys.Control | Keys.W, (_, _) => CloseTab(doc)),
            Item("Close Win&dow", Keys.Control | Keys.Shift | Keys.W, (_, _) => Close()),
            Item("E&xit", Keys.None, (_, _) => Close()),
        });

        // Display-only shortcuts are handled natively by the editor and the browser.
        miUndo = Item("&Undo", "Ctrl+Z", (_, _) => doc.Editor.Undo());
        miRedo = Item("&Redo", "Ctrl+Y", (_, _) => doc.Editor.Redo());
        miCut = Item("Cu&t", "Ctrl+X", (_, _) => doc.Editor.Cut());
        var copy = Item("&Copy", "Ctrl+C", (_, _) => Copy());
        miPaste = Item("&Paste", "Ctrl+V", (_, _) => doc.Editor.PastePlain());
        miDelete = Item("De&lete", "Del", (_, _) => doc.Editor.DeleteSelectionOrNext());
        miReplace = Item("R&eplace...", Keys.Control | Keys.H, (_, _) => ShowFind(replace: true));
        miGoTo = Item("&Go To...", Keys.Control | Keys.G, (_, _) => GoTo());
        miTimeDate = Item("Time/&Date", Keys.F5, (_, _) => InsertTimeDate());

        var heading = new ToolStripMenuItem("&Heading");
        for (int level = 1; level <= 6; level++)
        {
            int l = level;
            heading.DropDownItems.Add(Item($"Heading &{l}", Keys.None, (_, _) => Format(ed => MarkdownFormat.Heading(ed, l))));
        }
        heading.DropDownItems.Add(Item("&Normal text", Keys.None, (_, _) => Format(ed => MarkdownFormat.Heading(ed, 0))));
        miFormat = new ToolStripMenuItem("F&ormat");
        miFormat.DropDownItems.AddRange(new ToolStripItem[]
        {
            Item("&Bold", Keys.Control | Keys.B, (_, _) => Format(ed => MarkdownFormat.Wrap(ed, "**", "**", "bold text"))),
            Item("&Italic", Keys.Control | Keys.I, (_, _) => Format(ed => MarkdownFormat.Wrap(ed, "*", "*", "italic text"))),
            Item("&Strikethrough", Keys.None, (_, _) => Format(ed => MarkdownFormat.Wrap(ed, "~~", "~~", "text"))),
            Item("Inline &Code", Keys.None, (_, _) => Format(MarkdownFormat.Code)),
            Item("&Link", Keys.Control | Keys.K, (_, _) => Format(MarkdownFormat.Link)),
            new ToolStripSeparator(),
            heading,
            new ToolStripSeparator(),
            Item("B&ulleted List", Keys.None, (_, _) => Format(MarkdownFormat.Bullets)),
            Item("&Numbered List", Keys.None, (_, _) => Format(MarkdownFormat.Numbers)),
            Item("&Task List", Keys.None, (_, _) => Format(MarkdownFormat.Tasks)),
            Item("&Quote", Keys.None, (_, _) => Format(MarkdownFormat.Quote)),
        });

        var edit = new ToolStripMenuItem("&Edit");
        edit.DropDownItems.AddRange(new ToolStripItem[]
        {
            miUndo, miRedo, new ToolStripSeparator(), miCut, copy, miPaste, miDelete, new ToolStripSeparator(),
            Item("&Find...", Keys.Control | Keys.F, (_, _) => ShowFind(replace: false)),
            Item("Find &Next", Keys.F3, (_, _) => FindNext(forward: true)),
            Item("Find Pre&vious", Keys.Shift | Keys.F3, (_, _) => FindNext(forward: false)),
            miReplace, miGoTo, new ToolStripSeparator(),
            Item("Select &All", "Ctrl+A", (_, _) => SelectAll()),
            miTimeDate, new ToolStripSeparator(),
            miFormat,
        });
        edit.DropDownOpening += (_, _) =>
        {
            miUndo.Enabled = doc.RawMode && doc.Editor.CanUndo;
            miRedo.Enabled = doc.RawMode && doc.Editor.CanRedo;
        };

        miRaw = Item("&Raw Text", Keys.Control | Keys.E, (_, _) => SetRawMode(!doc.RawMode));
        miWrap = Item("&Word Wrap", Keys.None, (_, _) => SetWordWrap(!settings.WordWrap));
        miWrap.Checked = settings.WordWrap;
        miStatusBar = Item("&Status Bar", Keys.None, (_, _) =>
        {
            settings.StatusBar = !settings.StatusBar;
            status.Visible = miStatusBar.Checked = settings.StatusBar;
            settings.Save();
        });
        miStatusBar.Checked = settings.StatusBar;
        var zoomMenu = new ToolStripMenuItem("&Zoom");
        zoomMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            Item("Zoom &In", "Ctrl+Plus", (_, _) => SetZoom(zoom + 0.1)),
            Item("Zoom &Out", "Ctrl+Minus", (_, _) => SetZoom(zoom - 0.1)),
            Item("&Restore Default Zoom", "Ctrl+0", (_, _) => SetZoom(1)),
        });
        var themeMenu = new ToolStripMenuItem("&Theme");
        foreach (var (mode, label) in new[] { (ThemeMode.System, "&System default"), (ThemeMode.Light, "&Light"), (ThemeMode.Dark, "&Dark") })
        {
            var item = Item(label, Keys.None, (_, _) => SetTheme(mode));
            themeItems[mode] = item;
            themeMenu.DropDownItems.Add(item);
        }
        miExpand = Item("&Expand All", Keys.None, (_, _) => SetJsonOpen(true));
        miCollapse = Item("&Collapse All", Keys.None, (_, _) => SetJsonOpen(false));
        var view = new ToolStripMenuItem("&View");
        view.DropDownItems.AddRange(new ToolStripItem[]
        {
            miRaw, miWrap, miStatusBar, zoomMenu, Item("&Font...", Keys.None, (_, _) => ChooseFont()), themeMenu,
            new ToolStripSeparator(),
            Item("Ne&xt Tab", "Ctrl+Tab", (_, _) => StepTab(+1)),
            Item("Pre&vious Tab", "Ctrl+Shift+Tab", (_, _) => StepTab(-1)),
            new ToolStripSeparator(), miExpand, miCollapse,
        });

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(Item("&About MarkView", Keys.None, (_, _) => About()));

        menu.Items.AddRange([file, edit, view, help]);
    }

    // Menu row, as in Notepad: Markdown formatting centered (while editing Markdown) and the
    // formatted/raw switch on the right. ToolStrips can't center a group, so a blank spacer
    // before the tools is resized to keep them centered (CenterFormatTools).
    readonly ToolStripLabel formatSpacer = new() { AutoSize = false, Width = 0, Margin = Padding.Empty, Padding = Padding.Empty };

    void BuildToolbar()
    {
        tbView = new ToolStripButton { Alignment = ToolStripItemAlignment.Right, ToolTipText = "Switch view (Ctrl+E)", AutoToolTip = false };
        tbView.Click += (_, _) => SetRawMode(!doc.RawMode);
        menu.Items.Add(tbView);
        menu.Items.Add(formatSpacer);
        formatTools.Add(formatSpacer);

        var icon = Theme.IconFont(10f);
        var ui = new Font("Segoe UI", 10f);
        ToolStripButton Tool(string text, string tip, Action<TextBoxBase> action, Font font)
        {
            var b = new ToolStripButton(text) { Font = font, ToolTipText = tip, AutoToolTip = false };
            b.Click += (_, _) => Format(action);
            return b;
        }

        var heading = new ToolStripDropDownButton("H") { Font = new Font("Segoe UI Semibold", 10f), ToolTipText = "Heading", AutoToolTip = false };
        for (int level = 1; level <= 6; level++)
        {
            int l = level;
            heading.DropDownItems.Add(new ToolStripMenuItem($"Heading {l}", null, (_, _) => Format(ed => MarkdownFormat.Heading(ed, l))));
        }
        heading.DropDownItems.Add(new ToolStripMenuItem("Normal text", null, (_, _) => Format(ed => MarkdownFormat.Heading(ed, 0))));

        ToolStripItem[] leftToRight =
        [
            heading,
            Tool("\uE8DD", "Bold (Ctrl+B)", ed => MarkdownFormat.Wrap(ed, "**", "**", "bold text"), icon),
            Tool("\uE8DB", "Italic (Ctrl+I)", ed => MarkdownFormat.Wrap(ed, "*", "*", "italic text"), icon),
            Tool("S", "Strikethrough", ed => MarkdownFormat.Wrap(ed, "~~", "~~", "text"), new Font("Segoe UI", 10f, FontStyle.Strikeout)),
            Tool("\uE943", "Code", MarkdownFormat.Code, icon),
            Tool("\uE71B", "Link (Ctrl+K)", MarkdownFormat.Link, icon),
            Tool("\uE8FD", "Bulleted list", MarkdownFormat.Bullets, icon),
            Tool("1.", "Numbered list", MarkdownFormat.Numbers, ui),
            Tool("\uE73A", "Task list", MarkdownFormat.Tasks, icon),
            Tool("\u201C", "Quote", MarkdownFormat.Quote, new Font("Segoe UI", 13f)),
        ];
        foreach (var item in leftToRight)
        {
            menu.Items.Add(item);
            formatTools.Add(item);
        }
        menu.SizeChanged += (_, _) => CenterFormatTools();
    }

    void CenterFormatTools()
    {
        if (!formatSpacer.Visible) return;
        var tools = formatTools.Where(t => t != formatSpacer).ToList();
        int toolsWidth = tools.Sum(t => t.Width + t.Margin.Horizontal);
        var lastMenu = menu.Items.OfType<ToolStripMenuItem>().Last();  // Help
        int menusEnd = lastMenu.Bounds.Right + lastMenu.Margin.Right;
        int width = Math.Max(0, (menu.ClientSize.Width - toolsWidth) / 2 - menusEnd);
        if (formatSpacer.Width != width) formatSpacer.Width = width;
    }

    void BuildTabMenu()
    {
        var copyPath = Item("Copy &Path", Keys.None, (_, _) => { if (tabMenuTarget?.FilePath is { } p) Clipboard.SetText(p); });
        var reveal = Item("Open Containing &Folder", Keys.None, (_, _) => { if (tabMenuTarget?.FilePath is { } p) RevealInExplorer(p); });
        var closeOthers = Item("Close &Other Tabs", Keys.None, (_, _) => CloseWhere(d => d != tabMenuTarget));
        var closeRight = Item("Close Tabs to the &Right", Keys.None, (_, _) =>
        {
            int i = tabMenuTarget is null ? -1 : docs.IndexOf(tabMenuTarget);
            CloseWhere(d => docs.IndexOf(d) > i);
        });
        tabMenu.Items.AddRange(new ToolStripItem[]
        {
            Item("&Close Tab", Keys.None, (_, _) => { if (tabMenuTarget is not null) CloseTab(tabMenuTarget); }),
            closeOthers, closeRight, new ToolStripSeparator(), copyPath, reveal,
        });
        tabMenu.Opening += (_, _) =>
        {
            int i = tabMenuTarget is null ? -1 : docs.IndexOf(tabMenuTarget);
            closeOthers.Enabled = docs.Count > 1;
            closeRight.Enabled = i >= 0 && i < docs.Count - 1;
            copyPath.Enabled = reveal.Enabled = tabMenuTarget?.FilePath is not null;
        };
    }

    static ToolStripMenuItem Item(string text, Keys keys, EventHandler onClick) =>
        new(text, null, onClick) { ShortcutKeys = keys };

    static ToolStripMenuItem Item(string text, string keyDisplay, EventHandler onClick) =>
        new(text, null, onClick) { ShortcutKeyDisplayString = keyDisplay };

    static ToolStripMenuItem? FindShortcut(ToolStripItemCollection items, Keys keys)
    {
        foreach (var item in items.OfType<ToolStripMenuItem>())
        {
            if (item.ShortcutKeys == keys && keys != Keys.None && item.Enabled) return item;
            if (FindShortcut(item.DropDownItems, keys) is { } found) return found;
        }
        return null;
    }

    bool FormattingAvailable => doc.RawMode && (doc.FilePath is null || doc.Kind == DocKind.Markdown);

    /// <summary>Enables what makes sense for the active tab and mode (menu shortcuts follow Enabled).</summary>
    void UpdateCommands()
    {
        bool raw = doc.RawMode;
        miCut.Enabled = miPaste.Enabled = miDelete.Enabled = miReplace.Enabled = miGoTo.Enabled = miTimeDate.Enabled = raw;
        miFormat.Enabled = FormattingAvailable;
        foreach (var t in formatTools) t.Visible = FormattingAvailable;
        if (FormattingAvailable) { menu.PerformLayout(); CenterFormatTools(); }
        miExpand.Enabled = miCollapse.Enabled = doc.Kind == DocKind.Json && !raw && webReady;
        miRaw.Checked = raw;
        tbView.Text = raw ? "Raw text" : "Formatted";
        tbView.Enabled = !webFailed;
        findBar.ReplaceAllowed = raw;
    }

    // Tab keys that menu ShortcutKeys can't express (Ctrl+Tab, Ctrl+1..9) or that are aliases (Ctrl+T).
    Action? TabShortcut(Keys k)
    {
        switch (k)
        {
            case Keys.Control | Keys.Tab or Keys.Control | Keys.PageDown: return () => StepTab(+1);
            case Keys.Control | Keys.Shift | Keys.Tab or Keys.Control | Keys.PageUp: return () => StepTab(-1);
            case Keys.Control | Keys.T: return NewTab;
            case Keys.Control | Keys.D9: return () => SelectTab(docs[^1]);
            case >= (Keys.Control | Keys.D1) and <= (Keys.Control | Keys.D8):
                int i = k - (Keys.Control | Keys.D1);
                return () => { if (i < docs.Count) SelectTab(docs[i]); };
            default: return null;
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (TabShortcut(keyData) is { } action) { action(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ================================================================ tabs

    Document CreateDocument()
    {
        var editor = new PlainEditor
        {
            WordWrap = settings.WordWrap,
            Font = editorFont,
            Visible = false,
            BackColor = palette.EditorBack,
            ForeColor = palette.EditorText,
        };
        var d = new Document { Editor = editor };
        editor.TextChanged += (_, _) =>
        {
            if (d.Loading) return;
            d.EditorChanged = true;
            SetDirty(d, true);
            if (d == doc) UpdatePos();
        };
        editor.SelectionChanged += (_, _) => { if (d == doc && !d.Loading) UpdatePos(); };
        editor.KeyDown += EditorKeyDown;
        editor.MouseWheel += (_, e) => { if (ModifierKeys == Keys.Control) SetZoom(zoom + (e.Delta > 0 ? 0.1 : -0.1)); };
        editor.DragEnter += OnDragEnter;
        editor.DragDrop += OnDragDrop;
        editor.HandleCreated += (_, _) => Theme.SetScrollbarTheme(editor, palette.Dark);
        d.ReloadTimer.Tick += (_, _) => { d.ReloadTimer.Stop(); OnFileChangedOnDisk(d); };
        content.Controls.Add(editor);
        return d;
    }

    /// <summary>
    /// New empty Untitled tab, ready for typing like Notepad: no questions asked. What it holds is
    /// worked out from the text (JSON, Markdown or plain text) for the formatted view and for Save.
    /// </summary>
    void NewTab()
    {
        var d = CreateDocument();
        d.RawMode = true;
        docs.Add(d);
        SelectTab(d);
        stMessage.Text = "Ctrl+O opens a file. Ctrl+E shows what you type formatted.";
    }

    static void NewWindow() => Process.Start(Environment.ProcessPath!);  // no arguments = a new window

    void SelectTab(Document d)
    {
        var prev = doc;
        if (prev is not null && prev != d && webReady && !prev.RawMode) _ = SaveScrollAsync(prev);
        doc = d;
        foreach (var x in docs) if (x != d) x.Editor.Visible = false;

        if (d.RawMode || webFailed)
        {
            d.RawMode = true;
            d.Editor.Visible = true;
            web.Visible = false;
            if (!findBar.ContainsFocus) d.Editor.Focus();
        }
        else
        {
            if (d.EditorChanged || d.FilePath is null) { SyncFromEditor(d); BuildHtml(d); }
            web.Visible = true;
            d.Editor.Visible = false;
            restoreScroll = d.Scroll > 0 ? d.Scroll : null;
            if (webReady) Navigate();
            if (!findBar.ContainsFocus) web.Focus();
        }
        if (prev != d) { stMessage.Text = ""; findBar.SetStatus("", false); }
        findAnchor = d.Editor.SelectionStart;
        RefreshTabs();
        UpdateTitle();
        UpdateStatus();
        UpdateCommands();
    }

    void StepTab(int delta)
    {
        int n = docs.Count, i = docs.IndexOf(doc);
        if (n > 1) SelectTab(docs[((i + delta) % n + n) % n]);
    }

    bool CloseTab(Document d)
    {
        if (d.Dirty)
        {
            if (d != doc) SelectTab(d);
            if (!ConfirmDiscard(d)) return false;
        }
        int i = docs.IndexOf(d);
        docs.RemoveAt(i);
        content.Controls.Remove(d.Editor);
        d.Dispose();
        if (docs.Count == 0)  // closing the last tab closes the window, as in Notepad
        {
            BeginInvoke(Close);
            return true;
        }
        if (d == doc)
        {
            doc = null!;
            SelectTab(docs[Math.Min(i, docs.Count - 1)]);
        }
        else RefreshTabs();
        return true;
    }

    void CloseWhere(Func<Document, bool> predicate)
    {
        foreach (var d in docs.Where(predicate).ToList())
            if (!CloseTab(d)) break;
    }

    void RefreshTabs()
    {
        titleBar.Tabs = docs;
        titleBar.SelectedIndex = docs.IndexOf(doc);
        titleBar.UpdateTabs();
    }

    void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        foreach (var d in docs.Where(x => x.Dirty).ToList())
        {
            SelectTab(d);
            if (!ConfirmDiscard(d)) { e.Cancel = true; return; }
        }
        SaveWindowBounds();
    }

    /// <summary>Files passed by a second launch (e.g. a double-click in Explorer) open as tabs here.</summary>
    public void OpenFromOtherInstance(string[] files)
    {
        OpenFiles(files);
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    // ================================================================ WebView

    async Task InitWebAsync()
    {
        try
        {
            string dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName, "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            webFailed = true;
            MessageBox.Show(this,
                "The formatted view needs the Microsoft Edge WebView2 Runtime, which couldn't be started. " +
                "Files will open as raw text.\n\n" + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            SelectTab(doc);
            return;
        }

        var core = web.CoreWebView2;
        var s = core.Settings;
        s.IsScriptEnabled = false;            // documents never run code; the app still can via ExecuteScriptAsync
        s.AreDevToolsEnabled = false;
        s.AreHostObjectsAllowed = false;
        s.IsWebMessageEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsReputationCheckingRequired = false;  // SmartScreen can't vet markview.local and stalls each page load ~2 s
        core.Profile.PreferredColorScheme = palette.Dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;

        core.AddWebResourceRequestedFilter($"https://{Host}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += (_, e) => { e.Handled = true; BeginInvoke(() => OpenLink(e.Uri)); };
        web.ZoomFactorChanged += (_, _) => SetZoom(web.ZoomFactor);
        // WebView2 raises accelerator keys as KeyDown on the control instead of routing them to the
        // menu, so map them here. Actions are deferred: the browser waits on this handler.
        web.KeyDown += (_, e) =>
        {
            Action? action = TabShortcut(e.KeyData);
            if (action is null && FindShortcut(menu.Items, e.KeyData) is { } item) action = item.PerformClick;
            if (action is null && e.KeyData == Keys.F5) action = Reload;  // F5 in the formatted view: reload
            if (action is null) return;
            e.Handled = true;
            BeginInvoke(action);
        };
        webReady = true;
        if (zoom != 1) web.ZoomFactor = zoom;
        if (!doc.RawMode) Navigate();
        UpdateCommands();
    }

    /// <summary>Rebuilds a tab's page from its text (the view itself is refreshed separately).</summary>
    static void BuildHtml(Document d)
    {
        if (d.FilePath is null) d.Kind = Renderer.Guess(d.Text);
        d.Html = d.FilePath is null && d.Text.Length == 0
            ? Renderer.Welcome()
            : Renderer.Render(d.Text, d.Kind, d.DisplayName);
        d.LineCount = d.Text.Length == 0 ? 0 : d.Text.Count(c => c == '\n') + (d.Text.EndsWith('\n') ? 0 : 1);
        d.EditorChanged = false;
    }

    /// <summary>Rebuilds a tab's page and, if it's on screen, redraws it in place.</summary>
    void Refresh(Document d)
    {
        BuildHtml(d);
        if (d == doc)
        {
            UpdatePos();
            if (webReady && !d.RawMode) _ = NavigateKeepingScrollAsync();
        }
    }

    // Navigations the app started. Tab switches can queue several, and their NavigationStarting
    // events may arrive after the active tab has already changed again.
    readonly HashSet<string> ownNavigations = [];

    void Navigate()
    {
        string url = $"https://{Host}{UrlPathFor(doc)}";
        ownNavigations.Add(url);
        web.CoreWebView2.Navigate(url);
    }

    async Task<double> GetScrollAsync()
    {
        try
        {
            string y = await web.CoreWebView2.ExecuteScriptAsync("window.scrollY");
            return double.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }
        catch { return 0; }
    }

    async Task SaveScrollAsync(Document d) => d.Scroll = await GetScrollAsync();

    async Task NavigateKeepingScrollAsync()
    {
        var d = doc;
        double y = await GetScrollAsync();
        if (d != doc) return;  // tab changed meanwhile; SelectTab already navigated
        restoreScroll = y > 0 ? y : null;
        Navigate();
    }

    void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (restoreScroll is double y)
        {
            restoreScroll = null;
            _ = web.CoreWebView2.ExecuteScriptAsync($"window.scrollTo(0,{y.ToString(CultureInfo.InvariantCulture)})");
        }
        if (printAfterRender)
        {
            printAfterRender = false;
            web.CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
        }
    }

    // The document lives at a URL that mirrors its real path (C:\a\b.md -> /C/a/b.md), so relative
    // links and images (including ../) resolve naturally and are served from disk below.
    static string UrlPathFor(Document d)
    {
        if (d.FilePath is not { } file) return $"/~untitled/{d.Id}";
        string[] parts = file.StartsWith(@"\\")
            ? ["UNC", .. file[2..].Split('\\')]
            : [file[..1], .. file[3..].Split('\\')];
        return "/" + string.Join('/', parts.Select(Uri.EscapeDataString));
    }

    static string? LocalPathFromUrl(Uri uri)
    {
        string[] parts = uri.AbsolutePath.TrimStart('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
        if (parts.Length < 2) return null;
        if (parts[0] == "UNC") return @"\\" + string.Join('\\', parts[1..]);
        if (parts[0].Length == 1 && char.IsAsciiLetter(parts[0][0])) return parts[0] + @":\" + string.Join('\\', parts[1..]);
        return null;
    }

    static bool IsPageOf(Document d, Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.Host == Host &&
        (d.FilePath is null
            ? uri.AbsolutePath == UrlPathFor(d)
            : string.Equals(LocalPathFromUrl(uri), d.FilePath, StringComparison.OrdinalIgnoreCase));

    void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var env = web.CoreWebView2.Environment;
        var uri = new Uri(e.Request.Uri);
        // Serve the page of whichever tab this URL belongs to: requests can lag behind tab switches.
        if (docs.FirstOrDefault(d => IsPageOf(d, uri)) is { } owner)
        {
            e.Response = env.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(owner.Html)), 200, "OK",
                "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
            return;
        }
        if (LocalPathFromUrl(uri) is string file && File.Exists(file))
        {
            try
            {
                e.Response = env.CreateWebResourceResponse(new MemoryStream(File.ReadAllBytes(file)), 200, "OK",
                    $"Content-Type: {MimeType(file)}\r\nCache-Control: no-store");
                return;
            }
            catch { /* unreadable: fall through to 404 */ }
        }
        e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "");
    }

    void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (ownNavigations.Remove(e.Uri)) return;
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && IsPageOf(doc, uri)) return;  // e.g. #anchor links
        e.Cancel = true;  // the view never leaves the document; links are handled by the app
        BeginInvoke(() => OpenLink(e.Uri));
    }

    void OpenLink(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;

        if (uri.IsFile)  // a file dropped onto the formatted view
        {
            OpenFile(uri.LocalPath);
            return;
        }

        if (uri.Scheme == Uri.UriSchemeHttps && uri.Host == Host)  // relative link inside the document
        {
            string? target = LocalPathFromUrl(uri);
            if (target is null) return;
            if (Renderer.KindFor(target) != DocKind.Text && File.Exists(target)) OpenFile(target);
            else if (File.Exists(target) || Directory.Exists(target)) RevealInExplorer(target);  // never execute
            else stMessage.Text = "Link target not found: " + target;
            return;
        }

        if (uri.Scheme is "http" or "https" or "mailto")
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        // any other scheme (ms-settings:, custom protocol handlers, ...) is ignored on purpose
    }

    static string MimeType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".ico" => "image/x-icon",
        ".avif" => "image/avif",
        ".css" => "text/css",
        _ => "application/octet-stream",
    };

    // ================================================================ files

    void OpenDialog()
    {
        using var dlg = new OpenFileDialog { Filter = OpenFilter, Multiselect = true };
        if (doc.FilePath is not null) dlg.InitialDirectory = Path.GetDirectoryName(doc.FilePath);
        if (dlg.ShowDialog(this) == DialogResult.OK) OpenFiles(dlg.FileNames);
    }

    void OpenFiles(IEnumerable<string> files)
    {
        foreach (string f in files) OpenFile(f);
    }

    /// <summary>Opens a file in a new tab (or switches to it if it's already open).</summary>
    bool OpenFile(string file)
    {
        string full;
        try { full = Path.GetFullPath(file); }
        catch { return false; }

        if (docs.FirstOrDefault(x => string.Equals(x.FilePath, full, StringComparison.OrdinalIgnoreCase)) is { } open)
        {
            SelectTab(open);
            return true;
        }

        bool reuse = doc is { IsPristine: true };  // an empty Untitled tab gets taken over
        var d = reuse ? doc : CreateDocument();
        try
        {
            LoadFromDisk(d, full);
        }
        catch (Exception ex)
        {
            if (!reuse) { content.Controls.Remove(d.Editor); d.Dispose(); }
            MessageBox.Show(this, $"Couldn't open {file}.\n\n{ex.Message}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        d.RawMode = d.Kind == DocKind.Text;  // plain text opens in the editor, like Notepad
        d.Scroll = 0;
        BuildHtml(d);
        Watch(d);
        if (!reuse) docs.Add(d);
        SelectTab(d);
        return true;
    }

    static void LoadFromDisk(Document d, string full)
    {
        (d.Text, d.Encoding) = ReadText(full);
        d.FilePath = full;
        d.Kind = Renderer.KindFor(full);
        d.Eol = d.Text.Contains("\r\n") ? "\r\n" : d.Text.Contains('\n') ? "\n" : "\r\n";
        d.Loading = true;
        d.Editor.Text = d.Text.ReplaceLineEndings("\n");
        d.Editor.Select(0, 0);
        d.Editor.ClearUndo();  // undo shouldn't empty the file you just opened
        d.Loading = false;
        d.EditorChanged = false;
        d.Dirty = false;
    }

    static (string, Encoding) ReadText(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        string t = reader.ReadToEnd();
        return (t, reader.CurrentEncoding);
    }

    void Reload()
    {
        var d = doc;
        if (d.FilePath is null) return;
        if (d.Dirty && MessageBox.Show(this, $"Discard your changes to {d.DisplayName} and reload it from disk?",
                AppName, MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        try
        {
            LoadFromDisk(d, d.FilePath);
            SetDirty(d, false);
            UpdateStatus();
            Refresh(d);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    static void SyncFromEditor(Document d)
    {
        if (d.EditorChanged) d.Text = d.Editor.Text.ReplaceLineEndings(d.Eol);
    }

    bool Save(Document d) => d.FilePath is null ? SaveAs(d) : WriteTo(d, d.FilePath);

    void SaveAll()
    {
        foreach (var d in docs.Where(x => x.Dirty).ToList())
        {
            if (d.FilePath is null) SelectTab(d);  // Save As needs to show which tab it's asking about
            if (!Save(d)) return;
        }
    }

    bool SaveAs(Document d)
    {
        SyncFromEditor(d);
        // Untitled: suggest the type that matches what was typed (valid JSON, Markdown syntax, or text).
        var kind = d.FilePath is null ? Renderer.Guess(d.Text) : d.Kind;
        using var dlg = new SaveFileDialog
        {
            Filter = SaveFilter,
            FilterIndex = kind switch { DocKind.Markdown => 1, DocKind.Json => 2, _ => 3 },
            DefaultExt = Renderer.Extension(kind).TrimStart('.'),
            AddExtension = true,
            FileName = d.FilePath is null ? "Untitled" + Renderer.Extension(kind) : Path.GetFileName(d.FilePath),
        };
        if (d.FilePath is not null) dlg.InitialDirectory = Path.GetDirectoryName(d.FilePath);
        if (dlg.ShowDialog(this) != DialogResult.OK || !WriteTo(d, dlg.FileName)) return false;
        d.FilePath = Path.GetFullPath(dlg.FileName);
        d.Kind = Renderer.KindFor(d.FilePath);
        Watch(d);
        RefreshTabs();
        UpdateTitle();
        UpdateStatus();
        UpdateCommands();
        d.EditorChanged = true;  // same text, but the page title, kind and URL changed
        if (d == doc && !d.RawMode) SelectTab(d);
        return true;
    }

    bool WriteTo(Document d, string file)
    {
        SyncFromEditor(d);
        try
        {
            d.IgnoreChangesUntil = DateTime.UtcNow.AddSeconds(1);
            File.WriteAllText(file, d.Text, d.Encoding);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't save {file}.\n\n{ex.Message}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        SetDirty(d, false);
        if (d == doc) stMessage.Text = $"Saved {DateTime.Now:t}";
        return true;
    }

    bool ConfirmDiscard(Document d)
    {
        if (!d.Dirty) return true;
        var answer = MessageBox.Show(this, $"Do you want to save changes to {d.DisplayName}?", AppName, MessageBoxButtons.YesNoCancel);
        return answer == DialogResult.Yes ? Save(d) : answer == DialogResult.No;
    }

    // Auto-reload when another program changes the file (e.g. an editor or a build step).
    void Watch(Document d)
    {
        if (d.FilePath is null) return;
        string dir = Path.GetDirectoryName(d.FilePath)!, name = Path.GetFileName(d.FilePath);
        if (d.Watcher is { } w && w.Path == dir && w.Filter == name) return;
        d.Watcher?.Dispose();
        d.Watcher = null;
        try
        {
            var fsw = new FileSystemWatcher(dir, name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                SynchronizingObject = this,
            };
            void Restart(object? s, EventArgs e) { d.ReloadTimer.Stop(); d.ReloadTimer.Start(); }
            fsw.Changed += Restart;
            fsw.Created += Restart;
            fsw.Renamed += Restart;
            fsw.EnableRaisingEvents = true;
            d.Watcher = fsw;
        }
        catch { /* e.g. some network shares; live reload just won't happen */ }
    }

    void OnFileChangedOnDisk(Document d)
    {
        if (d.FilePath is null || DateTime.UtcNow < d.IgnoreChangesUntil || !docs.Contains(d)) return;
        string? note = null;
        if (d.Dirty) note = $"{d.DisplayName} changed on disk. Your unsaved edits are kept; Ctrl+R reloads.";
        else if (!File.Exists(d.FilePath)) note = $"{d.DisplayName} was moved or deleted.";
        else
        {
            try
            {
                LoadFromDisk(d, d.FilePath);
                SetDirty(d, false);
                if (d == doc) UpdateStatus();
                Refresh(d);
                note = $"Reloaded {DateTime.Now:t}";
            }
            catch (IOException) { d.ReloadTimer.Start(); }  // still being written; try again shortly
        }
        if (note is not null && d == doc) stMessage.Text = note;
    }

    void OnDragEnter(object? sender, DragEventArgs e) =>
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;

    void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) OpenFiles(files);
    }

    static void RevealInExplorer(string target) => Process.Start("explorer.exe", $"/select,\"{target}\"");

    // ================================================================ view commands

    void SetRawMode(bool raw)
    {
        if (!raw && webFailed) return;  // no formatted view available
        var d = doc;
        if (raw && !d.RawMode && webReady) _ = SaveScrollAsync(d);
        d.RawMode = raw;
        if (raw)
        {
            d.Editor.Visible = true;
            web.Visible = false;
            d.Editor.Focus();
            findAnchor = d.Editor.SelectionStart;
            UpdateStatus();
            UpdateCommands();
        }
        else SelectTab(d);  // rebuilds the page if the text changed and restores the scroll position
    }

    void SetWordWrap(bool wrap)
    {
        settings.WordWrap = wrap;
        settings.Save();
        miWrap.Checked = wrap;
        foreach (var d in docs) d.Editor.WordWrap = wrap;
    }

    Font ZoomedFont() => new(settings.FontFamily, Math.Max(4f, (float)(settings.FontSize * zoom)));

    void ApplyEditorFont()
    {
        var old = editorFont;
        editorFont = ZoomedFont();
        foreach (var d in docs)
        {
            d.Loading = true;
            d.Editor.Font = editorFont;
            d.Loading = false;
        }
        old.Dispose();
    }

    void ChooseFont()
    {
        using var current = new Font(settings.FontFamily, settings.FontSize);
        using var dlg = new FontDialog { Font = current, ShowEffects = false, AllowVerticalFonts = false, FontMustExist = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        settings.FontFamily = dlg.Font.FontFamily.Name;
        settings.FontSize = dlg.Font.SizeInPoints;
        settings.Save();
        ApplyEditorFont();
    }

    void SetZoom(double z)
    {
        z = Math.Clamp(Math.Round(z, 2), 0.25, 5);
        if (Math.Abs(z - zoom) > 0.001)
        {
            zoom = z;
            ApplyEditorFont();
        }
        if (webReady && Math.Abs(web.ZoomFactor - z) > 0.001) web.ZoomFactor = z;
        stZoom.Text = $"{Math.Round(zoom * 100)}%";
    }

    void Print()
    {
        if (!webReady) return;
        if (doc.RawMode)
        {
            printAfterRender = true;  // switch to the formatted view, print once it's drawn
            SetRawMode(false);
            return;
        }
        web.CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
    }

    async void Copy()
    {
        if (doc.RawMode) { doc.Editor.Copy(); return; }
        if (!webReady) return;
        string json = await web.CoreWebView2.ExecuteScriptAsync("window.getSelection().toString()");
        string? selection = JsonSerializer.Deserialize<string>(json);
        if (!string.IsNullOrEmpty(selection)) Clipboard.SetText(selection);
    }

    void SelectAll()
    {
        if (doc.RawMode) doc.Editor.SelectAll();
        else if (webReady) _ = web.CoreWebView2.ExecuteScriptAsync("window.getSelection().selectAllChildren(document.body)");
    }

    void SetJsonOpen(bool open)
    {
        if (webReady)
            _ = web.CoreWebView2.ExecuteScriptAsync(
                $"document.querySelectorAll('details').forEach(d => d.open = {(open ? "true" : "false")})");
    }

    // ================================================================ editing tools

    /// <summary>Runs a Markdown formatting command on the active editor (raw Markdown tabs only).</summary>
    void Format(Action<TextBoxBase> command)
    {
        if (!FormattingAvailable || findBar.ContainsFocus) return;
        command(doc.Editor);
        doc.Editor.Focus();
    }

    void InsertTimeDate()
    {
        if (!doc.RawMode || findBar.ContainsFocus) return;
        var now = DateTime.Now;
        doc.Editor.SelectedText = $"{now:t} {now:d}";  // Notepad's format, e.g. "3:42 PM 11/6/2026"
    }

    void GoTo()
    {
        if (!doc.RawMode) return;
        var ed = doc.Editor;
        int total = ed.LineCount();
        var box = new NumericUpDown { Minimum = 1, Maximum = total, Value = Math.Min(ed.Caret().Line, total), Width = LogicalToDeviceUnits(160) };
        var body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        body.Controls.Add(new Label { Text = "Line number:", AutoSize = true });
        body.Controls.Add(box);
        using var dlg = SmallDialog("Go To Line", body, "Go To");
        dlg.Shown += (_, _) => { box.Select(0, box.Text.Length); box.Focus(); };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        ed.GoToLine((int)box.Value);
        ed.Focus();
    }

    Form SmallDialog(string title, Control body, string okText)
    {
        var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
            ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(LogicalToDeviceUnits(12)),
            Font = SystemFonts.MessageBoxFont,
        };
        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, RowCount = 2 };
        var ok = new Button { Text = okText, DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Anchor = AnchorStyles.Right,
            Margin = new Padding(0, LogicalToDeviceUnits(12), 0, 0),
        };
        buttons.Controls.AddRange([cancel, ok]);
        layout.Controls.Add(body, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        f.Controls.Add(layout);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        Theme.Apply(f, palette);
        f.HandleCreated += (_, _) => Theme.SetDarkFrame(f.Handle, palette.Dark);
        return f;
    }

    // ---------------------------------------------------------------- find and replace

    void ShowFind(bool replace)
    {
        if (replace && !doc.RawMode) SetRawMode(true);
        string? initial = null;
        if (doc.RawMode)
        {
            string sel = doc.Editor.SelectedText;
            if (sel.Length > 0 && !sel.Contains('\n')) initial = sel;
            findAnchor = doc.Editor.SelectionStart;
        }
        findBar.ReplaceAllowed = doc.RawMode;
        findBar.Open(replace, initial);
        if (!doc.RawMode && webReady) _ = PrefillFromPageAsync();
    }

    async Task PrefillFromPageAsync()
    {
        string json = await web.CoreWebView2.ExecuteScriptAsync("window.getSelection().toString()");
        string? sel = JsonSerializer.Deserialize<string>(json);
        if (!string.IsNullOrEmpty(sel) && !sel.Contains('\n') && sel.Length < 200)
        {
            findBar.FindBox.Text = sel;
            findBar.FindBox.SelectAll();
        }
    }

    void FindNext(bool forward)
    {
        if (findBar.Term.Length == 0) ShowFind(replace: false);
        else FindStep(forward);
    }

    StringComparison FindComparison => findBar.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    void FindStep(bool forward, bool fromAnchor = false)
    {
        string term = findBar.Term;
        if (term.Length == 0) { findBar.SetStatus("", false); return; }
        if (!doc.RawMode) { _ = FindInPageAsync(term, forward, restart: fromAnchor); return; }

        var ed = doc.Editor;
        string t = ed.Text;
        var cmp = FindComparison;
        int i;
        if (forward)
        {
            int from = Math.Min(fromAnchor ? findAnchor : ed.SelectionStart + ed.SelectionLength, t.Length);
            i = t.IndexOf(term, from, cmp);
            if (i < 0) i = t.IndexOf(term, cmp);  // wrap around
        }
        else
        {
            i = ed.SelectionStart > 0 ? t.LastIndexOf(term, ed.SelectionStart - 1, cmp) : -1;
            if (i < 0) i = t.LastIndexOf(term, cmp);
        }
        if (i < 0) { findBar.SetStatus("No results", true); return; }
        ed.Select(i, term.Length);
        ed.ScrollToCaret();
        findBar.SetStatus(MatchStatus(t, term, cmp, i), false);
    }

    static string MatchStatus(string text, string term, StringComparison cmp, int current)
    {
        int count = 0, index = 0;
        for (int p = text.IndexOf(term, cmp); p >= 0; p = text.IndexOf(term, p + term.Length, cmp))
        {
            count++;
            if (p == current) index = count;
        }
        return index > 0 ? $"{index} of {count}" : $"{count} matches";
    }

    async Task FindInPageAsync(string term, bool forward, bool restart)
    {
        if (!webReady) return;
        // window.find searches from the current selection; when the term changes, start where the last match began.
        string js = (restart ? "if (getSelection().rangeCount) getSelection().collapseToStart();" : "") +
            $"window.find({JsonSerializer.Serialize(term)}, {(findBar.MatchCase ? "true" : "false")}, {(forward ? "false" : "true")}, true)";
        string found = await web.CoreWebView2.ExecuteScriptAsync(js);
        if (term == findBar.Term) findBar.SetStatus(found == "true" ? "" : "No results", found != "true");
    }

    void ReplaceOne()
    {
        if (!doc.RawMode || findBar.Term.Length == 0) return;
        var ed = doc.Editor;
        if (ed.SelectionLength == findBar.Term.Length && string.Equals(ed.SelectedText, findBar.Term, FindComparison))
            ed.SelectedText = findBar.Replacement;
        FindStep(forward: true);
    }

    void ReplaceAll()
    {
        if (!doc.RawMode || findBar.Term.Length == 0) return;
        var ed = doc.Editor;
        string t = ed.Text, term = findBar.Term;
        var cmp = FindComparison;
        var sb = new StringBuilder(t.Length);
        int n = 0, last = 0;
        for (int p = t.IndexOf(term, cmp); p >= 0; p = t.IndexOf(term, p + term.Length, cmp))
        {
            sb.Append(t, last, p - last).Append(findBar.Replacement);
            last = p + term.Length;
            n++;
        }
        if (n == 0) { findBar.SetStatus("No results", true); return; }
        sb.Append(t, last, t.Length - last);
        int caret = ed.SelectionStart;
        ed.SelectAll();
        ed.SelectedText = sb.ToString();  // one undo step
        ed.Select(Math.Min(caret, ed.TextLength), 0);
        ed.ScrollToCaret();
        findBar.SetStatus($"Replaced {n}", false);
    }

    void CloseFind()
    {
        findBar.Visible = false;
        if (doc.RawMode) doc.Editor.Focus();
        else web.Focus();
    }

    void EditorKeyDown(object? sender, KeyEventArgs e)
    {
        bool handled = true;
        if (e.Control && e.KeyCode is Keys.Oemplus or Keys.Add) SetZoom(zoom + 0.1);
        else if (e.Control && e.KeyCode is Keys.OemMinus or Keys.Subtract) SetZoom(zoom - 0.1);
        else if (e.Control && e.KeyCode is Keys.D0 or Keys.NumPad0) SetZoom(1);
        else if (e.KeyCode == Keys.Escape && findBar.Visible) CloseFind();
        else handled = false;
        if (handled) e.SuppressKeyPress = true;
    }

    // ================================================================ title / status

    void SetDirty(Document d, bool value)
    {
        if (d.Dirty == value) return;
        d.Dirty = value;
        if (d == doc) UpdateTitle();
        titleBar.Invalidate();
    }

    void UpdateTitle() => Text = $"{(doc.Dirty ? "*" : "")}{doc.DisplayName} - {AppName}";

    void UpdateStatus()
    {
        bool hasFile = doc.FilePath is not null;
        stKind.Text = hasFile ? Renderer.KindName(doc.Kind) : "Untitled";
        stEol.Text = doc.Eol == "\n" ? "Unix (LF)" : "Windows (CRLF)";
        stEnc.Text = EncodingName(doc.Encoding);
        stEol.Visible = stEnc.Visible = sepEol.Visible = sepEnc.Visible = hasFile;
        stZoom.Text = $"{Math.Round(zoom * 100)}%";
        UpdatePos();
    }

    void UpdatePos()
    {
        if (doc.RawMode)
        {
            var (line, col) = doc.Editor.Caret();
            stPos.Text = $"Ln {line}, Col {col}";
        }
        else
        {
            stPos.Text = doc.FilePath is null ? "" : doc.LineCount == 1 ? "1 line" : $"{doc.LineCount:N0} lines";
        }
        stPos.Visible = sepPos.Visible = stPos.Text.Length > 0;
    }

    static string EncodingName(Encoding e) => e.CodePage switch
    {
        65001 => e.GetPreamble().Length > 0 ? "UTF-8 with BOM" : "UTF-8",
        1200 => "UTF-16 LE",
        1201 => "UTF-16 BE",
        _ => e.WebName.ToUpperInvariant(),
    };

    void About()
    {
        string version = Application.ProductVersion.Split('+')[0];
        MessageBox.Show(this,
            $"{AppName} {version}\n\nA lightweight viewer and editor for Markdown, JSON and text files.\n\n" +
            "Ctrl+E switches between the formatted view and raw text.\n" +
            "Ctrl+N opens a new tab, Ctrl+W closes one, Ctrl+Tab moves between them.\n" +
            "Ctrl+F finds, Ctrl+H replaces, Ctrl+G goes to a line.",
            $"About {AppName}");
    }

    // File types ("Open with" for .md, .json, .txt) are registered by the installer, so the
    // uninstaller can remove every trace. See installer\MarkView.iss.
}
