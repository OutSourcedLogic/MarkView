using System.Text;

namespace MarkView;

/// <summary>One open tab: a file (or untitled text), its own raw-text editor, and its view state.</summary>
sealed class Document : IDisposable
{
    static int nextId;

    public required PlainEditor Editor { get; init; }
    public readonly int Id = ++nextId;

    public string? FilePath;               // full path, null for an Untitled tab
    public DocKind Kind = DocKind.Markdown;
    public string Text = "";               // document text, with the file's own line endings
    public Encoding Encoding = new UTF8Encoding(false);
    public string Eol = "\r\n";
    public string Html = Renderer.Welcome();
    public int LineCount;
    public bool Dirty;                     // unsaved edits
    public bool EditorChanged;             // editor text differs from what Html was built from
    public bool RawMode;                   // showing the raw editor instead of the formatted view
    public bool Loading;                   // suppresses TextChanged while the app fills the editor
    public double Scroll;                  // formatted-view scroll position, restored on tab switch

    public FileSystemWatcher? Watcher;
    public readonly System.Windows.Forms.Timer ReloadTimer = new() { Interval = 300 };
    public DateTime IgnoreChangesUntil;

    public string DisplayName => FilePath is null ? "Untitled" : Path.GetFileName(FilePath);

    /// <summary>An empty Untitled tab that opening a file can take over, like Notepad does.</summary>
    public bool IsPristine => FilePath is null && !Dirty && Editor.TextLength == 0;

    public void Dispose()
    {
        Watcher?.Dispose();
        ReloadTimer.Dispose();
        Editor.Dispose();
    }
}
