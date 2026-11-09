namespace MarkView;

/// <summary>
/// Find and replace bar under the menu (Ctrl+F / Ctrl+H). Enter = next, Shift+Enter = previous,
/// Esc = close. The search itself lives in MainForm, which knows the editor and the web view.
/// </summary>
sealed class FindBar : Panel
{
    public event Action? SearchChanged, NextClicked, PreviousClicked, ReplaceClicked, ReplaceAllClicked, CloseClicked;

    public readonly TextBox FindBox = new() { BorderStyle = BorderStyle.FixedSingle, PlaceholderText = "Find" };
    public readonly TextBox ReplaceBox = new() { BorderStyle = BorderStyle.FixedSingle, PlaceholderText = "Replace" };
    readonly CheckBox matchCase = new() { Text = "Aa", Appearance = Appearance.Button, TextAlign = ContentAlignment.MiddleCenter, TabStop = false };
    readonly Button toggle, previous, next, close, replace, replaceAll;
    readonly Label status = new() { AutoSize = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly FlowLayoutPanel findRow = Row(), replaceRow = Row();
    readonly Panel replaceIndent = new() { Margin = Padding.Empty };
    readonly FlowLayoutPanel stack;
    Palette palette = Palette.LightPalette;
    bool statusIsError;

    public string Term => FindBox.Text;
    public string Replacement => ReplaceBox.Text;
    public bool MatchCase => matchCase.Checked;
    public bool ReplaceShown => replaceRow.Visible;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ReplaceAllowed
    {
        set { ReplaceBox.Enabled = replace.Enabled = replaceAll.Enabled = value; }
    }

    public FindBar()
    {
        Dock = DockStyle.Top;
        Visible = false;
        Padding = new Padding(8, 5, 8, 6);

        toggle = Glyph("", "Show replace (Ctrl+H)");
        previous = Glyph("", "Previous match (Shift+Enter)");
        next = Glyph("", "Next match (Enter)");
        close = Glyph("", "Close (Esc)");
        replace = TextButton("Replace");
        replaceAll = TextButton("Replace all");
        var tips = new ToolTip();
        tips.SetToolTip(matchCase, "Match case");

        findRow.Controls.AddRange([toggle, FindBox, matchCase, previous, next, status]);
        replaceRow.Controls.AddRange([replaceIndent, ReplaceBox, replace, replaceAll]);
        replaceRow.Visible = false;
        stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty, Dock = DockStyle.Left,
        };
        stack.Controls.AddRange([findRow, replaceRow]);
        Controls.Add(stack);
        close.Dock = DockStyle.Right;
        Controls.Add(close);

        FindBox.TextChanged += (_, _) => SearchChanged?.Invoke();
        matchCase.CheckedChanged += (_, _) => SearchChanged?.Invoke();
        toggle.Click += (_, _) => ShowReplace(!replaceRow.Visible);
        previous.Click += (_, _) => PreviousClicked?.Invoke();
        next.Click += (_, _) => NextClicked?.Invoke();
        close.Click += (_, _) => CloseClicked?.Invoke();
        replace.Click += (_, _) => ReplaceClicked?.Invoke();
        replaceAll.Click += (_, _) => ReplaceAllClicked?.Invoke();

        FindBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { (e.Shift ? PreviousClicked : NextClicked)?.Invoke(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape) { CloseClicked?.Invoke(); e.SuppressKeyPress = true; }
        };
        ReplaceBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { (e.Control && e.Alt ? ReplaceAllClicked : ReplaceClicked)?.Invoke(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape) { CloseClicked?.Invoke(); e.SuppressKeyPress = true; }
        };
    }

    static FlowLayoutPanel Row() => new()
    {
        FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty,
    };

    Button Glyph(string glyph, string tip)
    {
        var b = new Button { Text = glyph, Font = Theme.IconFont(9f), TabStop = false, Margin = new Padding(2, 0, 2, 0) };
        new ToolTip().SetToolTip(b, tip);
        return b;
    }

    static Button TextButton(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(6, 0, 0, 0) };

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Relayout(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Relayout(); }

    void Relayout()
    {
        int h = FindBox.PreferredHeight;
        FindBox.Width = ReplaceBox.Width = LogicalToDeviceUnits(280);
        FindBox.Margin = ReplaceBox.Margin = new Padding(4, 0, 4, 0);
        foreach (var b in new Control[] { toggle, previous, next, close, matchCase })
            b.Size = new Size(LogicalToDeviceUnits(30), h);
        matchCase.Margin = new Padding(2, 0, 2, 0);
        replace.Height = replaceAll.Height = h;
        replace.AutoSize = replaceAll.AutoSize = false;
        replace.Width = TextRenderer.MeasureText(replace.Text, replace.Font).Width + LogicalToDeviceUnits(20);
        replaceAll.Width = TextRenderer.MeasureText(replaceAll.Text, replaceAll.Font).Width + LogicalToDeviceUnits(20);
        replaceIndent.Size = new Size(toggle.Width + toggle.Margin.Horizontal, h);
        replaceRow.Margin = new Padding(0, LogicalToDeviceUnits(4), 0, 0);
        status.Margin = new Padding(LogicalToDeviceUnits(8), (h - status.PreferredHeight) / 2, 0, 0);
        FitHeight();
    }

    // Docked children don't count toward a panel's auto size, so size the bar to its rows by hand.
    void FitHeight()
    {
        int rows = findRow.PreferredSize.Height + (replaceRow.Visible ? replaceRow.PreferredSize.Height + replaceRow.Margin.Vertical : 0);
        Height = rows + Padding.Vertical;
    }

    public void Open(bool withReplace, string? initial)
    {
        if (initial is not null) FindBox.Text = initial;
        Visible = true;
        ShowReplace(withReplace);
        FindBox.Focus();
        FindBox.SelectAll();
    }

    void ShowReplace(bool show)
    {
        replaceRow.Visible = show;
        toggle.Text = show ? "" : "";
        if (IsHandleCreated) FitHeight();
    }

    public void SetStatus(string text, bool isError)
    {
        status.Text = text;
        statusIsError = isError;
        status.ForeColor = isError ? palette.Error : palette.TextDim;
    }

    public void ApplyPalette(Palette p)
    {
        palette = p;
        Theme.Apply(this, p);
        foreach (var b in new[] { toggle, previous, next, close }) b.FlatAppearance.BorderSize = 0;
        status.ForeColor = statusIsError ? p.Error : p.TextDim;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(palette.Separator);
        e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }
}
