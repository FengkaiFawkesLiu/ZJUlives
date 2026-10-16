using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Shike;
public sealed class FormulaEditorWindow : Window
{
    readonly WebView2 browser = new();
    readonly string initial, profile;
    readonly bool editing;
    bool dirty, accepted, closed, saving;
    public string CurrentSource { get; private set; } = "";
    public bool RequestFallback { get; private set; }
    public string Stage { get; private set; } = "constructed";
    public bool Failed { get; private set; }
    public BitmapSource? ResultImage { get; private set; }
    public string ResultLatex { get; private set; } = "";
    public TaskCompletionSource<bool> Ready { get; } = new();
    public FormulaEditorWindow(Window? owner, string source, bool isEditing, string dataRoot)
    {
        if (owner != null) Owner = owner;
        initial = source; CurrentSource = source; editing = isEditing; profile = Path.Combine(dataRoot, "formula-runtime");
        Title = "数学公式 · 可视化编辑"; Width = 1080; Height = 850; MinWidth = 780; MinHeight = 650;
        WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        Content = browser;
        Loaded += async (_, _) => { _ = WatchStartup(); await Initialize(); };
        Closing += (_, e) => { if (saving) { e.Cancel = true; return; } if (!accepted && dirty && MessageBox.Show(this, "放弃本次公式修改？", "公式尚未插入笔记", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) e.Cancel = true; };
        Closed += (_, _) => { closed = true; browser.Dispose(); Ready.TrySetResult(false); };
    }
    async Task Initialize()
    {
        try
        {
            Stage = "environment";
            string browserArguments = "--disable-background-networking --disable-sync --no-first-run";
#if FORMULA_TEST
            // Only in the isolated test build: nested Chromium sandbox cannot start under the test runner token.
            browserArguments += " --no-sandbox --disable-gpu";
#endif
            var env = await CoreWebView2Environment.CreateAsync(null, profile, new CoreWebView2EnvironmentOptions(browserArguments));
            if (closed) return;
            Stage = "ensure-browser";
            await browser.EnsureCoreWebView2Async(env);
            Stage = "browser-ready";
            if (closed) return;
            var core = browser.CoreWebView2;
            core.ProcessFailed += (_, e) => { Stage = "failed: " + e.ProcessFailedKind + " / " + e.Reason + " / " + e.ExitCode; ShowFailure("公式组件停止运行，请使用下方保留的源码继续编辑。"); };
            core.Settings.AreDefaultContextMenusEnabled = false; core.Settings.AreDevToolsEnabled = false; core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false; core.Settings.IsGeneralAutofillEnabled = false;
            core.SetVirtualHostNameToFolderMapping("formula.shike.local", Path.Combine(AppContext.BaseDirectory, "formula-editor"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) => { if (!e.Request.Uri.StartsWith("https://formula.shike.local/", StringComparison.Ordinal)) e.Response = env.CreateWebResourceResponse(new MemoryStream(), 403, "Local only", ""); };
            core.NavigationStarting += (_, e) => { if (e.Uri != "https://formula.shike.local/index.html") e.Cancel = true; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.WebMessageReceived += async (_, e) =>
            {
                if (e.Source != "https://formula.shike.local/index.html") return;
                try
                {
                    using var doc = JsonDocument.Parse(e.WebMessageAsJson); var m = doc.RootElement;
                    switch (m.GetProperty("type").GetString())
                    {
                        case "ready": await Script("editor.init(" + JsonSerializer.Serialize(initial) + "," + (editing ? "true" : "false") + ")"); Ready.TrySetResult(true); break;
                        case "dirty": dirty = m.GetProperty("dirty").GetBoolean(); CurrentSource = m.GetProperty("latex").GetString() ?? ""; break;
                        case "cancel": Close(); break;
                        case "save": await Save(m.GetProperty("latex").GetString() ?? ""); break;
                    }
                }
                catch (Exception ex) { if (!closed && !Failed) { try { await Script("editor.error(" + JsonSerializer.Serialize("未能插入：" + ex.Message) + ")"); } catch { ShowFailure("公式组件无法响应，源码已保留。"); } } }
            };
            await core.AddScriptToExecuteOnDocumentCreatedAsync("window._errors=[];window.addEventListener('error',e=>window._errors.push(e.message));window.addEventListener('unhandledrejection',e=>window._errors.push(String(e.reason)));");
            core.Navigate("https://formula.shike.local/index.html");
        }
        catch (Exception ex)
        {
            ShowFailure("请检查本机 WebView2 Runtime。可继续使用 LaTeX 源码编辑。\n" + ex.Message);
        }
    }
    async Task WatchStartup()
    {
        await Task.Delay(20000);
        if (!closed && !Ready.Task.IsCompleted) ShowFailure("公式组件启动超时，现有公式未更改。可继续使用源码编辑。");
    }
    void ShowFailure(string message)
    {
        if (closed || Failed) return;
        Failed = true; saving = false; Ready.TrySetResult(false);
        var retained = new TextBox { Text = CurrentSource, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, MaxLength = 1500 };
        retained.TextChanged += (_, _) => CurrentSource = retained.Text;
        Content = new Border { Padding = new Thickness(28), Child = Theme.Stack(Theme.Text("可视化公式组件暂不可用", 22), Theme.Space(), Theme.Text(message), Theme.Space(), retained, Theme.Space(), Theme.Row(Theme.Button("使用源码编辑", () => { RequestFallback = true; dirty = false; Close(); }), Theme.Button("返回笔记", () => Close()))) };
    }
    internal void CloseTest() { accepted = true; Close(); }
    public Task<string> Script(string js) => browser.CoreWebView2.ExecuteScriptAsync(js).WaitAsync(TimeSpan.FromSeconds(10));
    public async Task<BitmapSource> CaptureFormula()
    {
        await Script("editor.prepareExport()");
        await Task.Delay(180);
        using var boundsDoc = JsonDocument.Parse(await Script("editor.exportBounds()")); var b = boundsDoc.RootElement;
        double width = b.GetProperty("width").GetDouble(), height = b.GetProperty("height").GetDouble();
        double viewportWidth = b.GetProperty("viewportWidth").GetDouble(), viewportHeight = b.GetProperty("viewportHeight").GetDouble();
        if (width > viewportWidth || height > viewportHeight) { await Script("editor.finishExport()"); throw new InvalidOperationException("公式超出窗口，请扩大窗口或拆成多行后插入。"); }
        using var stream = new MemoryStream(); await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream); stream.Position = 0;
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var crop = new CroppedBitmap(frame, new Int32Rect(0, 0, Math.Min(frame.PixelWidth, (int)Math.Ceiling(width * frame.PixelWidth / viewportWidth)), Math.Min(frame.PixelHeight, (int)Math.Ceiling(height * frame.PixelHeight / viewportHeight)))); crop.Freeze();
        var pixels = new FormatConvertedBitmap(crop, PixelFormats.Pbgra32, null, 0); int stride = pixels.PixelWidth * 4; var buffer = new byte[stride * pixels.PixelHeight]; pixels.CopyPixels(buffer, stride, 0);
        var result = BitmapSource.Create(pixels.PixelWidth, pixels.PixelHeight, 96 * frame.PixelWidth / viewportWidth, 96 * frame.PixelHeight / viewportHeight, PixelFormats.Pbgra32, null, buffer, stride); result.Freeze();
        await Script("editor.finishExport()"); return result;
    }
    async Task Save(string latex)
    {
        if (saving || string.IsNullOrWhiteSpace(latex) || latex.Length > 1500) return;
        saving = true;
        try { ResultImage = await CaptureFormula(); ResultLatex = latex; accepted = true; saving = false; DialogResult = true; }
        catch { saving = false; throw; }
    }
    public async Task CaptureWindow(string path)
    {
        using var fs = File.Create(path); await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
    }
}
public sealed partial class MainWindow
{
    void EditFormula(Window owner, RichTextBox editor, Image? existing = null)
    {
        var dialog = new FormulaEditorWindow(owner, (existing?.Tag as NoteEmbed)?.Source ?? "", existing != null, store.Root);
        if (dialog.ShowDialog() == true && dialog.ResultImage != null)
            InsertNoteImage(editor, dialog.ResultImage, new() { Kind = "math", Source = dialog.ResultLatex }, existing);
        else if (dialog.RequestFallback) EditFormulaLegacy(owner, editor, existing, dialog.CurrentSource);
    }
}
