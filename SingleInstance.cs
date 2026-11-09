using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MarkView;

/// <summary>
/// Lets a second launch with files (e.g. a double-click in Explorer) hand them to the window
/// that's already open, which shows them as new tabs. The pipe only accepts the same user.
/// </summary>
static class SingleInstance
{
    static readonly string Id =
        $"MarkView.{Environment.UserDomainName}.{Environment.UserName}.{Process.GetCurrentProcess().SessionId}";

    public static string MutexName => @"Local\" + Id;

    public static bool TrySend(string[] files)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Id, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            // We were just launched by the user, so we may hand foreground rights to the open window.
            if (GetNamedPipeServerProcessId(client.SafePipeHandle, out uint pid)) AllowSetForegroundWindow(pid);
            client.Write(Encoding.UTF8.GetBytes(string.Join('\n', files)));
            client.Flush();
            return true;
        }
        catch
        {
            return false;  // no window answering: this launch opens its own
        }
    }

    public static void Listen(Form form, Action<string[]> onFiles)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(Id, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    server.WaitForConnection();
                    using var ms = new MemoryStream();
                    server.CopyTo(ms);
                    string[] files = Encoding.UTF8.GetString(ms.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < 50 && !form.IsHandleCreated; i++) Thread.Sleep(100);
                    if (files.Length > 0 && form.IsHandleCreated && !form.IsDisposed)
                        form.BeginInvoke(() => onFiles(files));
                }
                catch (ObjectDisposedException) { return; }
                catch (InvalidOperationException) { return; }  // form closed
                catch (IOException) { Thread.Sleep(200); }
            }
        })
        { IsBackground = true, Name = "MarkView single-instance pipe" };
        thread.Start();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(uint processId);
}
