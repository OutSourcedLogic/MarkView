using System.Text.RegularExpressions;

namespace MarkView;

/// <summary>
/// The raw-text editor: a RichEdit control locked into plain-text mode. Unlike TextBox it has
/// multi-level undo/redo (like Notepad). Its rich-text hotkeys (bold, alignment, line spacing...)
/// are blocked, and pastes always arrive as plain text.
/// </summary>
sealed class PlainEditor : RichTextBox
{
    public PlainEditor()
    {
        Dock = DockStyle.Fill;
        BorderStyle = BorderStyle.None;
        Multiline = true;
        AcceptsTab = true;
        DetectUrls = false;
        HideSelection = false;
        ScrollBars = RichTextBoxScrollBars.Both;
        MaxLength = int.MaxValue;
        LanguageOption = RichTextBoxLanguageOptions.UIFonts;
        AllowDrop = true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        // Must happen while the native control is still empty, i.e. before the base restores text.
        Native.SendMessage(Handle, Native.EM_SETTEXTMODE,
            Native.TM_PLAINTEXT | Native.TM_MULTILEVELUNDO | Native.TM_MULTICODEPAGE, IntPtr.Zero);
        base.OnHandleCreated(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);  // the app's own handlers run first
        if (e.Handled || e.SuppressKeyPress) return;

        if (e.KeyData is (Keys.Control | Keys.V) or (Keys.Shift | Keys.Insert))
        {
            PastePlain();
            e.SuppressKeyPress = true;
        }
        else if (e.KeyData == (Keys.Control | Keys.Shift | Keys.Z))
        {
            Redo();
            e.SuppressKeyPress = true;
        }
        else if (e.Control && !AllowedWithCtrl(e.KeyCode))
        {
            e.SuppressKeyPress = true;  // RichEdit formatting hotkeys mean nothing in plain text
        }
    }

    static bool AllowedWithCtrl(Keys k) => k is
        Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown or
        Keys.Back or Keys.Delete or Keys.Insert or Keys.Z or Keys.Y or Keys.X or Keys.C or Keys.A or
        Keys.ControlKey or Keys.ShiftKey or Keys.Menu;

    public void PastePlain()
    {
        if (Clipboard.ContainsText()) SelectedText = Clipboard.GetText().ReplaceLineEndings("\n");
    }

    public void DeleteSelectionOrNext()
    {
        if (SelectionLength == 0 && SelectionStart < TextLength) Select(SelectionStart, 1);
        SelectedText = "";
    }

    /// <summary>Caret line and column, counting real lines (not word-wrapped ones), like Notepad.</summary>
    public (int Line, int Column) Caret()
    {
        int pos = SelectionStart;
        if (!WordWrap)
        {
            int l = GetLineFromCharIndex(pos);
            return (l + 1, pos - GetFirstCharIndexFromLine(l) + 1);
        }
        string t = Text;
        pos = Math.Min(pos, t.Length);
        int lineStart = t.LastIndexOf('\n', Math.Max(0, pos - 1)) + 1;
        if (pos == 0) lineStart = 0;
        return (t.AsSpan(0, pos).Count('\n') + 1, pos - lineStart + 1);
    }

    public int LineCount() => Text.AsSpan().Count('\n') + 1;

    public void GoToLine(int line)
    {
        string t = Text;
        int idx = 0;
        for (int l = 1; l < line && idx >= 0; l++)
        {
            int next = t.IndexOf('\n', idx);
            if (next < 0) break;
            idx = next + 1;
        }
        Select(idx, 0);
        ScrollToCaret();
    }
}

/// <summary>Markdown formatting commands for the raw editor, each a single undo step.</summary>
static class MarkdownFormat
{
    /// <summary>Wraps the selection in markers (or removes them if already there).</summary>
    public static void Wrap(TextBoxBase ed, string open, string close, string placeholder)
    {
        int start = ed.SelectionStart, len = ed.SelectionLength;
        string text = ed.Text;

        // Toggle off: markers just outside the selection...
        if (start >= open.Length && start + len + close.Length <= text.Length &&
            string.CompareOrdinal(text, start - open.Length, open, 0, open.Length) == 0 &&
            string.CompareOrdinal(text, start + len, close, 0, close.Length) == 0)
        {
            ed.Select(start - open.Length, len + open.Length + close.Length);
            ed.SelectedText = text.Substring(start, len);
            ed.Select(start - open.Length, len);
            return;
        }
        // ...or included in it.
        string sel = ed.SelectedText;
        if (len >= open.Length + close.Length && sel.StartsWith(open, StringComparison.Ordinal) && sel.EndsWith(close, StringComparison.Ordinal))
        {
            ed.SelectedText = sel[open.Length..^close.Length];
            ed.Select(start, len - open.Length - close.Length);
            return;
        }

        if (len == 0)
        {
            ed.SelectedText = open + placeholder + close;
            ed.Select(start + open.Length, placeholder.Length);
            return;
        }
        ed.SelectedText = open + sel + close;
        ed.Select(start + open.Length, len);
    }

    public static void Code(TextBoxBase ed)
    {
        if (ed.SelectedText.Contains('\n')) Wrap(ed, "```\n", "\n```", "code");
        else Wrap(ed, "`", "`", "code");
    }

    public static void Link(TextBoxBase ed)
    {
        int start = ed.SelectionStart;
        string sel = ed.SelectedText.Trim();
        if (Uri.TryCreate(sel, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            ed.SelectedText = $"[link text]({sel})";
            ed.Select(start + 1, "link text".Length);
        }
        else if (sel.Length > 0)
        {
            ed.SelectedText = $"[{sel}](https://)";
            ed.Select(start + sel.Length + 3, "https://".Length);
        }
        else
        {
            ed.SelectedText = "[link text](https://)";
            ed.Select(start + 1, "link text".Length);
        }
    }

    static readonly Regex HeadingMark = new(@"^#{1,6}\s+");
    static readonly Regex ListMark = new(@"^(\s*)(?:[-*+]\s+(?:\[[ xX]\]\s+)?|\d+[.)]\s+)");
    static readonly Regex BulletMark = new(@"^\s*[-*+]\s+(?!\[[ xX]\])");
    static readonly Regex NumberMark = new(@"^\s*\d+[.)]\s+");
    static readonly Regex TaskMark = new(@"^\s*[-*+]\s+\[[ xX]\]\s+");
    static readonly Regex QuoteMark = new(@"^>\s?");

    /// <summary>Heading level 1-6 on every selected line; 0 makes them normal text.</summary>
    public static void Heading(TextBoxBase ed, int level) =>
        EditLines(ed, (line, _) =>
        {
            string body = HeadingMark.Replace(line, "");
            return level == 0 || body.Length == 0 && line.Length == 0 ? body : new string('#', level) + " " + body;
        });

    public static void Bullets(TextBoxBase ed) => ToggleList(ed, BulletMark, _ => "- ");
    public static void Numbers(TextBoxBase ed) => ToggleList(ed, NumberMark, i => $"{i + 1}. ");
    public static void Tasks(TextBoxBase ed) => ToggleList(ed, TaskMark, _ => "- [ ] ");

    public static void Quote(TextBoxBase ed)
    {
        bool all = SelectedLines(ed).Where(l => l.Trim().Length > 0).All(l => QuoteMark.IsMatch(l));
        EditLines(ed, (line, _) => all ? QuoteMark.Replace(line, "") : "> " + line);
    }

    // Lists: if every non-empty line already has this kind of marker, remove it; otherwise
    // replace whatever list marker is there (so bullets can become numbers or tasks).
    static void ToggleList(TextBoxBase ed, Regex mark, Func<int, string> prefix)
    {
        bool all = SelectedLines(ed).Where(l => l.Trim().Length > 0).All(l => mark.IsMatch(l));
        int n = 0;
        EditLines(ed, (line, _) =>
        {
            if (line.Trim().Length == 0) return line;
            var m = ListMark.Match(line);
            string indent = m.Success ? m.Groups[1].Value : "";
            string body = m.Success ? line[m.Length..] : line.TrimStart();
            if (!m.Success) indent = line[..(line.Length - line.TrimStart().Length)];
            return all ? indent + body : indent + prefix(n++) + body;
        });
    }

    static (int Start, int End) LineSpan(TextBoxBase ed, string text)
    {
        int start = ed.SelectionStart, end = start + ed.SelectionLength;
        if (end > start && end <= text.Length && text[end - 1] == '\n') end--;  // a selection ending at a line start
        int lineStart = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
        int lineEnd = text.IndexOf('\n', Math.Min(end, text.Length));
        return (lineStart, lineEnd < 0 ? text.Length : lineEnd);
    }

    static string[] SelectedLines(TextBoxBase ed)
    {
        string text = ed.Text;
        var (s, e) = LineSpan(ed, text);
        return text[s..e].Split('\n');
    }

    static void EditLines(TextBoxBase ed, Func<string, int, string> edit)
    {
        string text = ed.Text;
        var (s, e) = LineSpan(ed, text);
        string[] lines = text[s..e].Split('\n');
        string result = string.Join('\n', lines.Select((l, i) => edit(l, i)));
        ed.Select(s, e - s);
        ed.SelectedText = result;
        ed.Select(s, result.Length);
    }
}
