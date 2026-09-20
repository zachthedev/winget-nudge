using Markdig;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using WingetNudge.Services;

namespace WingetNudge.Views;

/// <summary>
/// Renders Markdown release notes through Markdig and WebView2. Raw HTML in the source is
/// dropped and a content security policy blocks scripts, since the notes come from third
/// parties.
/// </summary>
public sealed partial class MarkdownView : UserControl
{
    /// <summary>Longest source rendered; WebView2 rejects documents past 2 MB.</summary>
    public const int MaxMarkdownLength = 1_000_000;

    // Generic attributes would let a note set arbitrary HTML attributes, so that extension
    // stays out even though the rest of the advanced set is wanted.
    private static readonly MarkdownPipeline Pipeline = BuildPipeline();

    private string _markdown = "";
    private bool _started;

    /// <summary>Creates the view.</summary>
    public MarkdownView()
    {
        InitializeComponent();
        // Fully transparent, so the page behind shows through and the notes sit on the same
        // backdrop as the list. WebView2 takes alpha 0 or 255 and rejects anything between.
        Browser.DefaultBackgroundColor = Microsoft.UI.Colors.Transparent;
        Browser.CoreWebView2Initialized += OnCoreWebView2Initialized;
        ActualThemeChanged += OnActualThemeChanged;
    }

    /// <summary>
    /// Re-renders against the theme Windows just switched to. The XAML around this control
    /// re-resolves its brushes on its own; a document already handed to the browser does not.
    /// </summary>
    private void OnActualThemeChanged(FrameworkElement sender, object args) => Render();

    /// <summary>Markdown to render. Rendering happens in <see cref="RenderAsync"/>.</summary>
    public string Markdown
    {
        get => _markdown;
        set => _markdown = value;
    }

    /// <summary>
    /// Starts the browser and renders the Markdown. Call once the view is laid out inside a
    /// sized container; initializing earlier faults inside XAML.
    /// </summary>
    /// <returns>Completes when initialization was requested; the document appears afterwards.</returns>
    public async Task RenderAsync()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        string userData = Path.Combine(AppServices.Current.Paths.Directory, "WebView2");
        try
        {
            Directory.CreateDirectory(userData);
            Core.Storage.SafePath.EnsureNotReparsePoint(userData);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowFallback(exception.Message);
            return;
        }

        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userData);
        try
        {
            await Browser.EnsureCoreWebView2Async();
        }
        catch (Exception exception)
            when (exception is System.Runtime.InteropServices.COMException or FileNotFoundException)
        {
            ShowFallback(exception.Message);
        }
    }

    private void OnCoreWebView2Initialized(WebView2 sender, CoreWebView2InitializedEventArgs args)
    {
        if (args.Exception is not null || sender.CoreWebView2 is null)
        {
            ShowFallback(args.Exception?.Message ?? "WebView2 did not initialize.");
            return;
        }

        // Third-party content: no script, no host bridge, no messages, on top of the CSP.
        CoreWebView2Settings settings = sender.CoreWebView2.Settings;
        settings.IsScriptEnabled = false;
        settings.IsWebMessageEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        Render();
    }

    /// <summary>Hands the browser a document styled for the theme in force right now.</summary>
    private void Render()
    {
        if (Browser.CoreWebView2 is null)
        {
            return;
        }

        string source = _markdown.Length > MaxMarkdownLength ? _markdown[..MaxMarkdownLength] : _markdown;
        Browser.NavigateToString(ToHtml(source, ActualTheme == ElementTheme.Dark));
    }

    private static MarkdownPipeline BuildPipeline()
    {
        MarkdownPipelineBuilder builder = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml();
        builder.Extensions.TryRemove<Markdig.Extensions.GenericAttributes.GenericAttributesExtension>();
        return builder.Build();
    }

    private void ShowFallback(string reason)
    {
        Browser.Visibility = Visibility.Collapsed;
        Fallback.Text = $"{reason}\n\n{_markdown}";
        FallbackScroller.Visibility = Visibility.Visible;
    }

    /// <summary>Wraps rendered Markdown in a themed, script-free document.</summary>
    /// <param name="markdown">Markdown source.</param>
    /// <param name="dark">Whether to style for the dark theme.</param>
    /// <returns>A complete HTML document with a transparent background.</returns>
    public static string ToHtml(string markdown, bool dark)
    {
        string body = Markdig.Markdown.ToHtml(markdown, Pipeline);
        string foreground = dark ? "#e6e6e6" : "#1a1a1a";
        string muted = dark ? "#a0a0a0" : "#5c5c5c";
        string rule = dark ? "#3a3a3a" : "#d9d9d9";
        string code = dark ? "#2b2b2b" : "#f0f0f0";
        string link = dark ? "#8ab4f8" : "#0f6cbd";
        // GitHub's alert accents, which is where these blocks come from.
        string note = dark ? "#4493f8" : "#0969da";
        string tip = dark ? "#3fb950" : "#1a7f37";
        string important = dark ? "#ab7df8" : "#8250df";
        string warning = dark ? "#d29922" : "#9a6700";
        string caution = dark ? "#f85149" : "#d1242f";
        return $$"""
            <!doctype html>
            <html>
            <head>
            <meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'">
            <meta name="color-scheme" content="{{(dark ? "dark" : "light")}}">
            <style>
            html, body { margin: 0; padding: 4px 12px 12px 4px; background: transparent; color: {{foreground}}; font: 14px 'Segoe UI Variable Text', 'Segoe UI', sans-serif; line-height: 1.5; overflow-wrap: anywhere; }
            h1, h2, h3, h4 { font-weight: 600; margin: 1em 0 0.35em; }
            h1 { font-size: 1.25em; } h2 { font-size: 1.15em; } h3 { font-size: 1.05em; } h4 { font-size: 1em; }
            h1:first-child, h2:first-child, h3:first-child, p:first-child { margin-top: 0; }
            p, ul, ol { margin: 0.4em 0; }
            ul, ol { padding-left: 1.4em; }
            li { margin: 0.15em 0; }
            a { color: {{link}}; text-decoration: none; }
            code { font-family: 'Cascadia Code', Consolas, monospace; font-size: 0.92em; background: {{code}}; padding: 0 3px; border-radius: 3px; }
            pre { background: {{code}}; padding: 8px; border-radius: 6px; overflow-x: auto; }
            pre code { background: none; padding: 0; }
            hr { border: 0; border-top: 1px solid {{rule}}; margin: 0.8em 0; }
            blockquote { border-left: 3px solid {{rule}}; margin: 0.5em 0; padding-left: 0.8em; color: {{muted}}; }
            img { max-width: 100%; }
            table { border-collapse: collapse; } td, th { border: 1px solid {{rule}}; padding: 2px 6px; }
            .markdown-alert { border-left: 3px solid var(--alert); margin: 0.6em 0; padding: 0.1em 0 0.1em 0.8em; }
            .markdown-alert-title { display: flex; align-items: center; gap: 6px; font-weight: 600; color: var(--alert); margin: 0 0 0.2em; }
            .markdown-alert-title svg { fill: currentColor; flex: none; }
            .markdown-alert-note { --alert: {{note}}; }
            .markdown-alert-tip { --alert: {{tip}}; }
            .markdown-alert-important { --alert: {{important}}; }
            .markdown-alert-warning { --alert: {{warning}}; }
            .markdown-alert-caution { --alert: {{caution}}; }
            </style>
            </head>
            <body>{{body}}</body>
            </html>
            """;
    }

    private void OnNavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        // Only the injected document may load; links open in the user's browser.
        if (
            !args.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            && !args.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
        )
        {
            args.Cancel = true;
            if (Uri.TryCreate(args.Uri, UriKind.Absolute, out Uri? target) && target.Scheme is "https" or "http")
            {
                _ = Windows.System.Launcher.LaunchUriAsync(target);
            }
        }
    }
}
