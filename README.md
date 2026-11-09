# MarkView

A Notepad-style viewer and editor for Windows that shows Markdown and JSON formatted.

Double-click a `.md` or `.json` file and you get a clean, readable page instead of raw text.
Press **Ctrl+E** to switch to the raw text and edit it, then press it again to see the result.

## Features

- **Formatted view** for Markdown (rendered with [Markdig](https://github.com/xoofx/markdig)) and JSON.
- **Raw editor** with multi-level undo and a Markdown formatting toolbar.
- **Tabs** in the title bar, like Notepad on Windows 11. Opening another file adds a tab to the
  window that's already open.
- **Find and replace**, Go to line, Time/Date, and printing.
- **Light, dark or system theme.**
- Registers in **Open with** and **Default apps** for `.md`, `.markdown`, `.json`, `.txt` and similar files.

## Install

Download `MarkView-Setup-<version>.exe` from the [Releases](../../releases) page and run it.

The installer isn't code-signed, so Windows SmartScreen may show "Windows protected your PC".
Click **More info**, then **Run anyway**.

Requirements: Windows 10 or 11 (x64). The installer adds the Microsoft Edge WebView2 Runtime if
it's missing (it's included with Windows 11).

## Build from source

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet run --project MarkView.csproj
```

Build output goes to `%TEMP%\MarkView.Build` rather than the source folder.

To build the installer, also install [Inno Setup 6](https://jrsoftware.org/isinfo.php)
(`winget install JRSoftware.InnoSetup`), then run:

```powershell
.\build-installer.ps1
```

The installer lands in `dist\`.

## License

MarkView is released under the [MIT License](LICENSE).

It uses [Markdig](https://github.com/xoofx/markdig) (BSD 2-Clause) and the
[Microsoft Edge WebView2](https://developer.microsoft.com/microsoft-edge/webview2/) SDK and runtime,
which have their own licenses.
