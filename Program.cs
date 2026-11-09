namespace MarkView;

static class Program
{
    // Fixed names that Setup checks (AppMutex) so it won't install or uninstall while MarkView runs.
    const string RunningMutex = "MarkView.Running";

    [STAThread]
    static void Main(string[] args)
    {
        using var running = new Mutex(false, RunningMutex);
        using var runningGlobal = TryGlobalMutex();

        // Resolve paths here: the window that receives them may have a different working folder.
        string[] files = args
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => { try { return Path.GetFullPath(a); } catch { return a; } })
            .ToArray();

        // Launched with files while MarkView is open → they become tabs in that window.
        // Launched without files (Start menu, File > New Window) → always a new window.
        using var instance = new Mutex(true, SingleInstance.MutexName, out bool firstInstance);
        if (!firstInstance && files.Length > 0 && SingleInstance.TrySend(files)) return;

        ApplicationConfiguration.Initialize();
        var settings = Settings.Load();
#pragma warning disable WFO5001 // Dark mode is experimental in .NET 9; MarkView draws its own chrome and uses it for dialogs.
        Application.SetColorMode(Theme.Resolve(settings.Theme).Dark ? SystemColorMode.Dark : SystemColorMode.Classic);
#pragma warning restore WFO5001
        // A failure in one command (e.g. a busy clipboard) shouldn't take the open tabs with it.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}", "MarkView",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);

        var form = new MainForm(files, settings);
        if (firstInstance) SingleInstance.Listen(form, form.OpenFromOtherInstance);
        Application.Run(form);
    }

    static Mutex? TryGlobalMutex()
    {
        try { return new Mutex(false, @"Global\" + RunningMutex); }
        catch { return null; }  // not fatal: Setup still sees the session-local one
    }
}
