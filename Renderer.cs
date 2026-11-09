using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Markdig;

namespace MarkView;

public enum DocKind { Markdown, Json, Text }

/// <summary>Turns a document's text into a complete, self-contained HTML page.</summary>
public static class Renderer
{
    const int JsonTreeLimit = 15 * 1024 * 1024; // above this, JSON is shown as plain text
    const int ExpandAllLimit = 64 * 1024;       // smaller JSON files open fully expanded

    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()   // tables, task lists, footnotes, auto-ids for #anchors, etc.
        .UseYamlFrontMatter()      // hide front matter instead of rendering it as text
        .Build();

    public static DocKind KindFor(string? path) => Path.GetExtension(path ?? "").ToLowerInvariant() switch
    {
        ".md" or ".markdown" or ".mdown" or ".mkd" or ".mkdn" => DocKind.Markdown,
        ".json" or ".jsonc" or ".geojson" or ".webmanifest" => DocKind.Json,
        "" when path is null => DocKind.Markdown,
        _ => DocKind.Text,
    };

    static readonly System.Text.RegularExpressions.Regex MarkdownHint = new(
        @"^(#{1,6}\s|\s*[-*+]\s|\s*\d+[.)]\s|>|```|\|.*\|)|\*\*[^*\n]+\*\*|\[[^\]\n]+\]\([^)\n]+\)",
        System.Text.RegularExpressions.RegexOptions.Multiline);

    /// <summary>Best guess at what an Untitled tab holds: valid JSON, Markdown syntax, or plain text.</summary>
    public static DocKind Guess(string text)
    {
        string t = text.TrimStart();
        if (t.Length > 0 && t[0] is '{' or '[')
        {
            try
            {
                using var _ = JsonDocument.Parse(text, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 512,
                });
                return DocKind.Json;
            }
            catch (JsonException) { /* not JSON; maybe Markdown */ }
        }
        return MarkdownHint.IsMatch(text) ? DocKind.Markdown : DocKind.Text;
    }

    public static string Extension(DocKind kind) => kind switch
    {
        DocKind.Markdown => ".md",
        DocKind.Json => ".json",
        _ => ".txt",
    };

    public static string KindName(DocKind kind) => kind switch
    {
        DocKind.Markdown => "Markdown",
        DocKind.Json => "JSON",
        _ => "Plain text",
    };

    public static string Render(string text, DocKind kind, string title)
    {
        string body = kind switch
        {
            DocKind.Markdown => $"<article class=\"md\">{Markdown.ToHtml(text, Pipeline)}</article>",
            DocKind.Json => RenderJson(text),
            _ => Plain(text),
        };
        return Page(title, body);
    }

    public static string Welcome() => Page("MarkView", """
        <div class="welcome">
          <h1>MarkView</h1>
          <p>Open a Markdown or JSON file with <kbd>Ctrl</kbd>+<kbd>O</kbd>, or drop one on this window.</p>
          <p>Press <kbd>Ctrl</kbd>+<kbd>E</kbd> any time to switch between the formatted view and the raw text.</p>
        </div>
        """);

    // ---------------------------------------------------------------- JSON

    static readonly JsonSerializerOptions KeyEncoding = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static string RenderJson(string text)
    {
        if (text.Length > JsonTreeLimit)
            return Banner("info", "This file is very large, so it's shown as plain text.") + Plain(text);
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 512,
            });
            var sb = new StringBuilder(text.Length * 3 + 256);
            sb.Append("<div class=\"json\">");
            Node(sb, doc.RootElement, key: null, comma: false, depth: 0, expandAll: text.Length <= ExpandAllLimit);
            sb.Append("</div>");
            return sb.ToString();
        }
        catch (JsonException ex)
        {
            string where = ex.LineNumber is long line
                ? $" (line {line + 1}, column {ex.BytePositionInLine + 1})"
                : "";
            return Banner("error", $"This file isn't valid JSON{where}. Showing the raw text instead.") + Plain(text);
        }
    }

    static void Node(StringBuilder sb, JsonElement e, string? key, bool comma, int depth, bool expandAll)
    {
        string label = key is null ? "" : $"<span class=\"k\">{Enc(JsonSerializer.Serialize(key, KeyEncoding))}</span><span class=\"p\">: </span>";
        string tail = comma ? "<span class=\"p\">,</span>" : "";

        if (e.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            bool isObject = e.ValueKind == JsonValueKind.Object;
            int count = isObject ? e.EnumerateObject().Count() : e.GetArrayLength();
            char open = isObject ? '{' : '[', close = isObject ? '}' : ']';

            if (count == 0)
            {
                sb.Append($"<div class=\"row\">{label}<span class=\"p\">{open}{close}</span>{tail}</div>");
                return;
            }

            string noun = isObject ? (count == 1 ? "key" : "keys") : (count == 1 ? "item" : "items");
            sb.Append(expandAll || depth < 2 ? "<details open>" : "<details>");
            sb.Append($"<summary>{label}<span class=\"p\">{open}</span><span class=\"hint\"> … {close}{(comma ? "," : "")}<i>{count:N0} {noun}</i></span></summary>");
            sb.Append("<div class=\"children\">");
            int i = 0;
            if (isObject)
                foreach (var p in e.EnumerateObject())
                    Node(sb, p.Value, p.Name, ++i < count, depth + 1, expandAll);
            else
                foreach (var item in e.EnumerateArray())
                    Node(sb, item, null, ++i < count, depth + 1, expandAll);
            sb.Append($"</div><div class=\"close\"><span class=\"p\">{close}</span>{tail}</div></details>");
            return;
        }

        string value = e.ValueKind switch
        {
            JsonValueKind.String => StringValue(e),
            JsonValueKind.Number => $"<span class=\"n\">{Enc(e.GetRawText())}</span>",
            JsonValueKind.True or JsonValueKind.False => $"<span class=\"b\">{e.GetRawText()}</span>",
            _ => "<span class=\"z\">null</span>",
        };
        sb.Append($"<div class=\"row\">{label}{value}{tail}</div>");
    }

    static string StringValue(JsonElement e)
    {
        string raw = Enc(e.GetRawText());
        string? s = e.GetString();
        if (s is not null && (s.StartsWith("http://") || s.StartsWith("https://")) && Uri.IsWellFormedUriString(s, UriKind.Absolute))
            return $"<span class=\"s\"><a href=\"{Enc(s)}\">{raw}</a></span>";
        return $"<span class=\"s\">{raw}</span>";
    }

    // ---------------------------------------------------------------- page

    static string Plain(string text) => $"<pre class=\"plain\">{Enc(text)}</pre>";
    static string Banner(string kind, string message) => $"<div class=\"banner {kind}\">{Enc(message)}</div>";
    static string Enc(string s) => WebUtility.HtmlEncode(s);

    // Page scripts are disabled in the WebView as well; the CSP is a second lock on the door.
    static string Page(string title, string body) => $$"""
        <!doctype html>
        <html><head><meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy" content="default-src 'self' https: data: blob:; img-src * data: blob:; script-src 'none'; object-src 'none'; style-src 'self' 'unsafe-inline' https:">
        <meta name="color-scheme" content="light dark">
        <title>{{Enc(title)}}</title>
        <style>{{Css}}</style>
        </head><body>{{body}}</body></html>
        """;

    const string Css = """
        :root{--bg:#fff;--fg:#1f2328;--muted:#656d76;--border:#d8dee4;--subtle:#f6f8fa;--link:#0969da;
          --key:#0550ae;--str:#0a3069;--num:#953800;--bool:#cf222e;--null:#8250df;--info-bg:#fff8c5;--err-bg:#ffebe9;--err:#cf222e}
        @media (prefers-color-scheme:dark){:root{--bg:#272727;--fg:#e6e6e6;--muted:#9da5ae;--border:#444;--subtle:#323232;--link:#58a6ff;
          --key:#79c0ff;--str:#a5d6ff;--num:#ffa657;--bool:#ff7b72;--null:#d2a8ff;--info-bg:#3b3214;--err-bg:#3d1a1a;--err:#ff7b72}}
        *{box-sizing:border-box}
        html{background:var(--bg);color:var(--fg)}
        body{margin:0;padding:28px 40px 64px;font:15px/1.6 "Segoe UI Variable Text","Segoe UI",system-ui,sans-serif;overflow-wrap:break-word}
        a{color:var(--link);text-decoration:none} a:hover{text-decoration:underline}
        code,pre,kbd,.json,.plain{font-family:"Cascadia Mono",Consolas,monospace}
        kbd{font-size:.8em;padding:.1em .45em;border:1px solid var(--border);border-bottom-width:2px;border-radius:5px;background:var(--subtle)}

        .md{max-width:900px;margin:0 auto}
        .md>:first-child{margin-top:0}
        .md h1,.md h2,.md h3,.md h4,.md h5,.md h6{margin:1.5em 0 .6em;font-weight:600;line-height:1.25}
        .md h1{font-size:2em;padding-bottom:.3em;border-bottom:1px solid var(--border)}
        .md h2{font-size:1.5em;padding-bottom:.3em;border-bottom:1px solid var(--border)}
        .md h3{font-size:1.25em} .md h4{font-size:1em} .md h5{font-size:.875em} .md h6{font-size:.85em;color:var(--muted)}
        .md p,.md ul,.md ol,.md blockquote,.md pre,.md table,.md dl{margin:0 0 1em}
        .md ul,.md ol{padding-left:2em} .md li+li{margin-top:.25em}
        .md li.task-list-item{list-style:none} .md li.task-list-item input{margin:0 .45em 0 -1.5em;vertical-align:middle}
        .md code{font-size:.875em;background:var(--subtle);padding:.15em .4em;border-radius:6px}
        .md pre{background:var(--subtle);padding:14px 16px;border-radius:6px;overflow:auto;line-height:1.45}
        .md pre code{background:none;padding:0}
        .md blockquote{margin-left:0;padding:0 1em;color:var(--muted);border-left:.25em solid var(--border)}
        .md table{border-collapse:collapse;display:block;overflow:auto;max-width:100%}
        .md th,.md td{border:1px solid var(--border);padding:6px 13px}
        .md th{font-weight:600;background:var(--subtle)}
        .md img{max-width:100%}
        .md hr{border:0;height:1px;background:var(--border);margin:1.5em 0}

        .json{font-size:13.5px;line-height:1.6}
        .json summary{list-style:none;cursor:pointer;border-radius:4px}
        .json summary::-webkit-details-marker{display:none}
        .json summary:hover{background:var(--subtle)}
        .json summary::before{content:"\25B8";display:inline-block;width:1.1em;color:var(--muted);transition:transform .1s}
        .json details[open]>summary::before{transform:rotate(90deg)}
        .json details[open]>summary .hint{display:none}
        .json .hint{color:var(--muted)}
        .json .hint i{font:normal .85em "Segoe UI",sans-serif;margin-left:.6em}
        .json .children{margin-left:.5em;padding-left:.6em;border-left:1px solid var(--border)}
        .json .row,.json .close{padding-left:1.1em;overflow-wrap:anywhere}
        .k{color:var(--key)} .s{color:var(--str)} .n{color:var(--num)} .b{color:var(--bool)} .z{color:var(--null)} .p{color:var(--muted)}
        .s a{color:inherit;text-decoration:underline;text-decoration-color:var(--border)}

        .plain{white-space:pre-wrap;font-size:14px;margin:0;overflow-wrap:anywhere}
        .banner{padding:10px 14px;border-radius:6px;margin:0 0 16px;font-size:14px}
        .banner.error{background:var(--err-bg);color:var(--err)} .banner.info{background:var(--info-bg)}
        .welcome{max-width:520px;margin:18vh auto 0;text-align:center;color:var(--muted)}
        .welcome h1{color:var(--fg);font-weight:600;font-size:28px;margin:0 0 12px}
        """;
}
