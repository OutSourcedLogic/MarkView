using Microsoft.Win32;

namespace MarkView;

public enum ThemeMode { System, Light, Dark }

/// <summary>Every color the app draws with. One instance per theme.</summary>
sealed record Palette(
    bool Dark,
    Color TitleBar,    // tab row behind the tabs
    Color Chrome,      // selected tab, menu bar, find bar, status bar
    Color Hover,
    Color Pressed,
    Color Border,
    Color Text,
    Color TextDim,
    Color Separator,
    Color EditorBack,
    Color EditorText,
    Color InputBack,
    Color Error)
{
    public static readonly Palette DarkPalette = new(
        Dark: true,
        TitleBar: Rgb(32, 32, 32), Chrome: Rgb(44, 44, 44), Hover: Rgb(58, 58, 58), Pressed: Rgb(70, 70, 70),
        Border: Rgb(78, 78, 78), Text: Rgb(235, 235, 235), TextDim: Rgb(160, 160, 160), Separator: Rgb(64, 64, 64),
        EditorBack: Rgb(39, 39, 39), EditorText: Rgb(230, 230, 230), InputBack: Rgb(32, 32, 32), Error: Rgb(255, 123, 114));

    public static readonly Palette LightPalette = new(
        Dark: false,
        TitleBar: Rgb(238, 238, 238), Chrome: Rgb(249, 249, 249), Hover: Rgb(230, 230, 230), Pressed: Rgb(218, 218, 218),
        Border: Rgb(204, 204, 204), Text: Rgb(26, 26, 26), TextDim: Rgb(96, 96, 96), Separator: Rgb(224, 224, 224),
        EditorBack: Color.White, EditorText: Rgb(26, 26, 26), InputBack: Color.White, Error: Rgb(196, 43, 28));

    static Color Rgb(int r, int g, int b) => Color.FromArgb(r, g, b);
}

static class Theme
{
    public static bool SystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static Palette Resolve(ThemeMode mode) =>
        mode == ThemeMode.Dark || (mode == ThemeMode.System && SystemIsDark()) ? Palette.DarkPalette : Palette.LightPalette;

    /// <summary>Windows 11's icon font, or Windows 10's equivalent (same glyph codes).</summary>
    public static readonly string IconFontName =
        FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    public static Font IconFont(float points) => new(IconFontName, points);

    /// <summary>Dark window border and system menu to match the app (Windows 10 20H1+ / 11).</summary>
    public static void SetDarkFrame(IntPtr hwnd, bool dark)
    {
        int v = dark ? 1 : 0;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int));
    }

    public static void SetScrollbarTheme(Control c, bool dark)
    {
        if (c.IsHandleCreated) Native.SetWindowTheme(c.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
    }

    /// <summary>Colors a dialog or panel's standard controls.</summary>
    public static void Apply(Control root, Palette p)
    {
        root.BackColor = p.Chrome;
        root.ForeColor = p.Text;
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case TextBox or NumericUpDown:
                    c.BackColor = p.InputBack;
                    c.ForeColor = p.Text;
                    break;
                case CheckBox { Appearance: Appearance.Button } toggle:
                    toggle.FlatStyle = FlatStyle.Flat;
                    toggle.FlatAppearance.BorderSize = 0;
                    toggle.FlatAppearance.CheckedBackColor = p.Pressed;
                    toggle.FlatAppearance.MouseOverBackColor = p.Hover;
                    toggle.FlatAppearance.MouseDownBackColor = p.Pressed;
                    toggle.BackColor = p.Chrome;
                    toggle.ForeColor = p.Text;
                    break;
                case Button b:
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderColor = p.Border;
                    b.FlatAppearance.MouseOverBackColor = p.Hover;
                    b.FlatAppearance.MouseDownBackColor = p.Pressed;
                    b.BackColor = p.Chrome;
                    b.ForeColor = p.Text;
                    break;
                default:
                    Apply(c, p);
                    break;
            }
        }
    }
}

/// <summary>Menu, status bar and context-menu drawing in the app's palette.</summary>
sealed class ThemeRenderer : ToolStripProfessionalRenderer
{
    readonly Palette p;

    public ThemeRenderer(Palette palette) : base(new ThemeColors(palette))
    {
        p = palette;
        RoundedEdges = false;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? p.Text : p.TextDim;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item?.Enabled != false ? p.Text : p.TextDim;
        base.OnRenderArrow(e);
    }

    // The stock check mark is a black bitmap, invisible on dark menus; draw the glyph instead.
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        using var font = Theme.IconFont(9f);
        TextRenderer.DrawText(e.Graphics, "", font, e.ImageRectangle, p.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    // No lines under the menu bar or around the status bar; drop-down menus keep their border.
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown) base.OnRenderToolStripBorder(e);
    }

    sealed class ThemeColors : ProfessionalColorTable
    {
        readonly Palette p;
        public ThemeColors(Palette palette) { p = palette; UseSystemColors = false; }

        public override Color MenuStripGradientBegin => p.Chrome;
        public override Color MenuStripGradientEnd => p.Chrome;
        public override Color StatusStripGradientBegin => p.Chrome;
        public override Color StatusStripGradientEnd => p.Chrome;
        public override Color ToolStripGradientBegin => p.Chrome;
        public override Color ToolStripGradientMiddle => p.Chrome;
        public override Color ToolStripGradientEnd => p.Chrome;
        public override Color ToolStripBorder => p.Chrome;
        public override Color ToolStripDropDownBackground => p.Chrome;
        public override Color ImageMarginGradientBegin => p.Chrome;
        public override Color ImageMarginGradientMiddle => p.Chrome;
        public override Color ImageMarginGradientEnd => p.Chrome;
        public override Color MenuBorder => p.Border;
        public override Color MenuItemBorder => p.Hover;
        public override Color MenuItemSelected => p.Hover;
        public override Color MenuItemSelectedGradientBegin => p.Hover;
        public override Color MenuItemSelectedGradientEnd => p.Hover;
        public override Color MenuItemPressedGradientBegin => p.Pressed;
        public override Color MenuItemPressedGradientMiddle => p.Pressed;
        public override Color MenuItemPressedGradientEnd => p.Pressed;
        public override Color SeparatorDark => p.Separator;
        public override Color SeparatorLight => p.Chrome;
        public override Color ButtonSelectedHighlight => p.Hover;
        public override Color ButtonSelectedHighlightBorder => p.Hover;
        public override Color ButtonSelectedBorder => p.Hover;
        public override Color ButtonSelectedGradientBegin => p.Hover;
        public override Color ButtonSelectedGradientMiddle => p.Hover;
        public override Color ButtonSelectedGradientEnd => p.Hover;
        public override Color ButtonPressedHighlight => p.Pressed;
        public override Color ButtonPressedBorder => p.Pressed;
        public override Color ButtonPressedGradientBegin => p.Pressed;
        public override Color ButtonPressedGradientMiddle => p.Pressed;
        public override Color ButtonPressedGradientEnd => p.Pressed;
        public override Color ButtonCheckedHighlight => p.Pressed;
        public override Color ButtonCheckedGradientBegin => p.Pressed;
        public override Color ButtonCheckedGradientMiddle => p.Pressed;
        public override Color ButtonCheckedGradientEnd => p.Pressed;
        public override Color CheckBackground => p.Chrome;
        public override Color CheckSelectedBackground => p.Hover;
        public override Color CheckPressedBackground => p.Pressed;
    }
}
