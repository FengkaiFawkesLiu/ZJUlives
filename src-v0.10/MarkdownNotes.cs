using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Markdig;
using Markdig.Syntax;
using Markdig.Parsers;
using Markdig.Helpers;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.TaskLists;
using MT = Markdig.Extensions.Tables;
using static Shike.Theme;

namespace Shike;

// Parse into native WPF elements: Markdown cannot execute HTML or fetch remote resources.
public static class MarkdownNotes
{
    public const string Format = "markdown-v1";
    static readonly MarkdownPipeline Pipeline = CreatePipeline();
    static MarkdownPipeline CreatePipeline()
    {
        var builder = new MarkdownPipelineBuilder().UsePipeTables().UseTaskLists().UseEmojiAndSmiley().UseMathematics().UseEmphasisExtras().DisableHtml();
        builder.InlineParsers.Insert(0, new AdjacentMathParser());
        return builder.Build();
    }
    sealed class AdjacentMathParser : InlineParser
    {
        public AdjacentMathParser() => OpeningCharacters = new[] { '$' };
        public override bool Match(InlineProcessor processor, ref StringSlice slice)
        {
            int start = slice.Start;
            string text = slice.Text;
            if (slice.PeekChar() == '$' || start > 0 && text[start - 1] == '$' || char.IsWhiteSpace(slice.PeekChar())) return false;
            for (int i = start + 1; i <= slice.End; i++)
            {
                char c = text[i];
                if (c is '\r' or '\n') break;
                if (c == '\\' && i < slice.End && text[i + 1] is '\\' or '$') { i++; continue; }
                if (c != '$') continue;
                if (i == start + 1 || char.IsWhiteSpace(text[i - 1]) || i < slice.End && text[i + 1] == '$') return false;
                bool ChineseAt(int index) => index >= 0 && index < text.Length && text[index] is >= '\u3400' and <= '\u9fff';
                if (!ChineseAt(start - 1) && !ChineseAt(i + 1)) return false;
                processor.Inline = new MathInline { Content = new StringSlice(text, start + 1, i - 1) };
                slice.Start = i + 1;
                return true;
            }
            return false;
        }
    }
    public static string Plain(string source) => Markdown.ToPlainText(source, Pipeline);
    public static FlowDocument Render(string source, Func<string,string?>? imagePath = null, Action<int,bool>? toggle = null)
    {
        var doc = new FlowDocument { FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 16, Foreground = Ink, PagePadding = new Thickness(22), ColumnWidth = double.PositiveInfinity, LineHeight = 26 };
        Blocks(Markdown.Parse(source, Pipeline), doc.Blocks, imagePath, toggle);
        return doc;
    }
    static FrameworkElement Formula(string text)
    {
        try { return MathFormula.Render(text, 18); }
        catch (Exception) { return new TextBlock { Text = text + "（公式暂无法排版）", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkGoldenrod }; }
    }
    static void Blocks(ContainerBlock source, BlockCollection target, Func<string,string?>? imagePath, Action<int,bool>? toggle)
    {
        foreach (var block in source)
        {
            if (block is MathBlock math) { target.Add(new BlockUIContainer(Formula(math.Lines.ToString()))); continue; }
            if (block is CodeBlock code) { var paragraph = new Paragraph { FontFamily = new FontFamily("Consolas"), FontSize = 14, Background = Brush("#F1F4F1"), Padding = new Thickness(12), LineHeight = 21 }; HighlightCode(code, paragraph); target.Add(paragraph); continue; }
            if (block is ListBlock list)
            {
                var result = new System.Windows.Documents.List { MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Padding = new Thickness(24, 0, 0, 0) };
                if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start) && start > 0) result.StartIndex = start;
                foreach (ListItemBlock item in list) { var li = new ListItem(); Blocks(item, li.Blocks, imagePath, toggle); result.ListItems.Add(li); }
                target.Add(result); continue;
            }
            if (block is MT.Table table)
            {
                var result = new System.Windows.Documents.Table { CellSpacing = 0 }; var rows = new TableRowGroup(); result.RowGroups.Add(rows);
                foreach (MT.TableRow row in table)
                {
                    var tr = new System.Windows.Documents.TableRow(); rows.Rows.Add(tr);
                    foreach (MT.TableCell cell in row) { var tc = new System.Windows.Documents.TableCell { BorderBrush = Line, BorderThickness = new Thickness(0.5), Padding = new Thickness(8), FontWeight = row.IsHeader ? FontWeights.Bold : FontWeights.Normal }; Blocks(cell, tc.Blocks, imagePath, toggle); tr.Cells.Add(tc); }
                }
                target.Add(result); continue;
            }
            if (block is ThematicBreakBlock) { target.Add(new Paragraph { BorderBrush = Muted, BorderThickness = new Thickness(0, 0, 0, 1), FontSize = 1 }); continue; }
            if (block is ContainerBlock container)
            {
                var section = new Section(); if (block is QuoteBlock) { section.BorderBrush = Brush("#387565"); section.BorderThickness = new Thickness(3, 0, 0, 0); section.Padding = new Thickness(12, 4, 4, 4); section.Foreground = Muted; }
                Blocks(container, section.Blocks, imagePath, toggle); target.Add(section); continue;
            }
            if (block is LeafBlock leaf)
            {
                var p = new Paragraph { Margin = new Thickness(0, 0, 0, 12) };
                if (block is HeadingBlock h) { p.FontSize = Math.Max(17, 30 - h.Level * 2); p.FontWeight = FontWeights.Bold; p.Margin = new Thickness(0, 14, 0, 12); }
                if (leaf.Inline != null) Inlines(leaf.Inline, p.Inlines, imagePath, toggle); else p.Inlines.Add(new Run(leaf.Lines.ToString())); target.Add(p);
            }
        }
    }
    static void HighlightCode(CodeBlock block, Paragraph paragraph)
    {
        string source = block.Lines.ToString();
        string? language = (block as FencedCodeBlock)?.Info?.ToLowerInvariant();
        string keywords = language switch
        {
            "py" or "python" => "def|import|from|as|class|return|if|else|elif|while|for|in|None|True|False|with|async|await",
            "cs" or "csharp" => "class|namespace|using|public|private|void|new|var|return|if|else|for|foreach|in|string|int|bool|null|true|false|async|await",
            "js" or "javascript" or "ts" or "typescript" => "const|let|var|function|return|if|else|for|while|new|class|import|export|from|null|true|false|async|await",
            _ => ""
        };
        if (keywords.Length == 0) { paragraph.Inlines.Add(new Run(source)); return; }
        string comment = language is "py" or "python" ? @"#[^\r\n]*" : @"//[^\r\n]*|/\*[\s\S]*?(?:\*/|$)";
        string pattern = "(?<comment>" + comment + ")|(?<str>\"(?:\\\\.|[^\"\\\\\\r\\n])*\"|'(?:\\\\.|[^'\\\\\\r\\n])*')|\\b(?:" + keywords + ")\\b";
        int pos = 0;
        foreach (System.Text.RegularExpressions.Match token in System.Text.RegularExpressions.Regex.Matches(source, pattern))
        {
            if (token.Index > pos) paragraph.Inlines.Add(new Run(source[pos..token.Index]));
            paragraph.Inlines.Add(new Run(token.Value) { Foreground = token.Groups["comment"].Success ? Brushes.SeaGreen : token.Groups["str"].Success ? Brushes.SaddleBrown : Brushes.DarkBlue });
            pos = token.Index + token.Length;
        }
        if (pos < source.Length) paragraph.Inlines.Add(new Run(source[pos..]));
    }
    static void Inlines(ContainerInline source, InlineCollection target, Func<string,string?>? imagePath, Action<int,bool>? toggle)
    {
        foreach (var inline in source)
        {
            switch (inline)
            {
                case LiteralInline literal: target.Add(new Run(literal.Content.ToString())); break;
                case LineBreakInline: target.Add(new LineBreak()); break;
                case CodeInline code: target.Add(new Run(code.Content) { FontFamily = new FontFamily("Consolas"), Background = Brush("#EEF2EE") }); break;
                case MathInline math: target.Add(new InlineUIContainer(Formula(math.Content.ToString())) { BaselineAlignment = BaselineAlignment.Center }); break;
                case TaskList task:
                    if (toggle == null) target.Add(new Run(task.Checked ? "☑ " : "☐ "));
                    else
                    {
                        var box = new CheckBox { IsChecked = task.Checked, ToolTip = "切换待办状态" };
                        box.Click += (_, _) => toggle(task.Span.Start, box.IsChecked == true);
                        target.Add(new InlineUIContainer(box));
                    }
                    break;
                case EmphasisInline emphasis:
                    var span = new Span(); if (emphasis.DelimiterChar == '~') span.TextDecorations = TextDecorations.Strikethrough;
                    else if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeights.Bold; else span.FontStyle = FontStyles.Italic;
                    Inlines(emphasis, span.Inlines, imagePath, toggle); target.Add(span); break;
                case LinkInline link:
                    // Images stay descriptive; attachments are opened through the existing local attachment panel.
                    if (link.IsImage) {
                        string? path = imagePath?.Invoke(link.Url ?? "");
                        if (path != null && File.Exists(path))
                        {
                            try
                            {
                                using var stream = File.OpenRead(path);
                                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                                bitmap.BeginInit();
                                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                                bitmap.DecodePixelWidth = 800;
                                bitmap.StreamSource = stream;
                                bitmap.EndInit();
                                bitmap.Freeze();
                                target.Add(new InlineUIContainer(new Image { Source = bitmap, MaxWidth = 480, MaxHeight = 360, StretchDirection = StretchDirection.DownOnly }));
                                break;
                            }
                            catch (Exception) { /* Keep the description for unreadable attachments. */ }
                        }
                        target.Add(new Run("[图片：")); Inlines(link, target, imagePath, toggle); target.Add(new Run("]")); break; }
                    if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
                    {
                        var hyperlink = new Hyperlink { NavigateUri = uri, Foreground = Brush("#387565"), ToolTip = uri.AbsoluteUri }; Inlines(link, hyperlink.Inlines, imagePath, toggle);
                        hyperlink.RequestNavigate += (_, e) => { e.Handled = true; try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch (Exception ex) { UiMessage.Show("无法打开链接：" + ex.Message); } }; target.Add(hyperlink);
                    }
                    else Inlines(link, target, imagePath, toggle);
                    break;
                case ContainerInline child: Inlines(child, target, imagePath, toggle); break;
            }
        }
    }
}

public sealed class MarkdownEditor : DockPanel
{
    public TextBox Source { get; } = new() { Name = "MarkdownSource", VerticalContentAlignment = VerticalAlignment.Top, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"), FontSize = 15, Padding = new Thickness(16), MaxLength = 1000000 };
    public FlowDocumentScrollViewer Preview { get; } = new() { Name = "MarkdownPreview", VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.White };
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    readonly TextBlock hint = Text("Markdown · 所有内容保存在本地", 11, Muted);
    readonly Func<string,string?>? imagePath;
    public MarkdownEditor(string source, Action changed, Func<string,string?>? imagePath = null, Func<string?>? pickImage = null)
    {
        this.imagePath = imagePath;
        var tools = new WrapPanel { Margin = new Thickness(10,8,10,8) }; var surface=new Border{Background=NoteEditorUi.Toolbar,Child=tools};SetDock(surface, Dock.Top); Children.Add(surface);
        var more=new WrapPanel{Visibility=Visibility.Collapsed,Margin=new Thickness(10,0,10,8)};SetDock(more,Dock.Top);Children.Add(more);
        void Tool(string label, string before, string after = "", string placeholder = "文字") { var b = NoteEditorUi.Action(label, () => Insert(before, after, placeholder)); b.Focusable = false; (label is "引用"?more:tools).Children.Add(b); }
        Tool("标题", "\n## ", "\n", "标题"); Tool("粗体", "**", "**"); Tool("斜体", "*", "*"); Tool("列表", "\n- ", "\n"); Tool("待办", "\n- [ ] ", "\n", "待办事项"); Tool("引用", "\n> ", "\n");  Tool("链接", "[", "](https://example.com)", "链接名称");
        void AddAction(string label, Action action, string hintText)
        {
            var button = NoteEditorUi.Action(label, action);
            button.Focusable = false;
            button.ToolTip = hintText;
            tools.Children.Add(button);
        }
        Tool("有序列表", "\n1. ", "\n");
        Tool("行内代码", new string((char)96, 1), new string((char)96, 1), "代码");
        AddAction("分隔线", () => Source.SelectedText = "\n\n---\n\n", "插入水平分隔线");
        if (pickImage != null) AddAction("图片", () =>
        {
            var url = pickImage();
            if (url != null) Insert("![", "](" + url + ")", "图片说明");
        }, "插入本地图片附件");
        var emojiMenu = new ContextMenu();
        var emojiButton = NoteEditorUi.Action("表情", () => emojiMenu.IsOpen = true);
        emojiButton.Focusable = false;
        emojiMenu.PlacementTarget = emojiButton;
        emojiMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        foreach (string symbol in new[] { "😀", "👍", "❤️", "🎉", "✅", "📌", "💡", "⭐" })
        {
            var item = new MenuItem { Header = symbol };
            item.Click += (_, _) => { Source.SelectedText = symbol; Source.Focus(); };
            emojiMenu.Items.Add(item);
        }
        tools.Children.Add(emojiButton);
        AddAction("表格", InsertTable, "选中以制表符分列的文本可直接转为表格，首行为表头");
        AddAction("代码块", InsertCodeBlock, "将选区包成围栏代码块");
        AddAction("切换待办", ToggleTask, "切换当前行的完成状态，普通文本行会转为待办");
        tools.Children.Add(FormulaButton(false));
        tools.Children.Add(FormulaButton(true));
        tools.Children.Add(NoteEditorUi.Disclosure("更多",more));
        var mode = Combo(new[] { "分栏预览", "仅编辑", "仅预览" }, "仅编辑");NoteEditorUi.AnimateMenu(mode); tools.Children.Add(mode);
        SetDock(hint, Dock.Bottom); hint.Margin = new Thickness(0, 8, 0, 0); Children.Add(hint);
        var panes = new Grid(); panes.ColumnDefinitions.Add(new()); panes.ColumnDefinitions.Add(new() { Width = new GridLength(8) }); panes.ColumnDefinitions.Add(new()); Children.Add(panes);
        panes.Children.Add(Source); Grid.SetColumn(Preview, 2); panes.Children.Add(Preview);
        var splitter = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Center, Background = Line }; Grid.SetColumn(splitter, 1); panes.Children.Add(splitter);
        mode.SelectionChanged += (_, _) => { int n = mode.SelectedIndex; Source.Visibility = n == 2 ? Visibility.Collapsed : Visibility.Visible; Preview.Visibility = n == 1 ? Visibility.Collapsed : Visibility.Visible; splitter.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed; panes.ColumnDefinitions[0].Width = new GridLength(n == 2 ? 0 : 1, GridUnitType.Star); panes.ColumnDefinitions[1].Width = new GridLength(n == 0 ? 8 : 0); panes.ColumnDefinitions[2].Width = new GridLength(n == 1 ? 0 : 1, GridUnitType.Star); if (n != 1) Refresh(); };
        Source.Background=NoteEditorUi.Paper;Source.BorderThickness=new Thickness(0);Source.FocusVisualStyle=null;Source.Padding=new Thickness(12);Preview.Background=NoteEditorUi.Paper;
        var paperTone=Combo(new[]{"柔白","米白"},"柔白");more.Children.Add(NoteEditorUi.Setting("纸面",paperTone));paperTone.SelectionChanged+=(_,_)=>{Source.Background=Preview.Background=paperTone.SelectedIndex==1?Brush("#F5F0E4"):NoteEditorUi.Paper;};
        Preview.Visibility=Visibility.Collapsed;splitter.Visibility=Visibility.Collapsed;panes.ColumnDefinitions[1].Width=new GridLength(0);panes.ColumnDefinitions[2].Width=new GridLength(0);
        Source.Text = source; Refresh();
        Source.TextChanged += (_, _) => { changed(); timer.Stop(); timer.Start(); };
        timer.Tick += (_, _) => { timer.Stop(); Refresh(); }; Unloaded += (_, _) => timer.Stop();
    }
    public void Insert(string before, string after, string placeholder)
    {
        int start = Source.SelectionStart; string selected = Source.SelectionLength > 0 ? Source.SelectedText : placeholder;
        Source.SelectedText = before + selected + after; Source.Select(start + before.Length, selected.Length); Source.Focus();
    }
    ContextMenu blockFormulas = null!;
    public void OpenFormulaMenu() => blockFormulas.IsOpen = true;

    Button FormulaButton(bool inline)
    {
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var button = NoteEditorUi.Action(inline ? "行内公式" : "公式块", () => menu.IsOpen = true);
        button.Focusable = false;
        menu.PlacementTarget = button;
        if (!inline) blockFormulas = menu;
        string before = inline ? "$" : "\n\n$$\n";
        string after = inline ? "$" : "\n$$\n\n";
        void Category(string name, params (string Label, string Latex)[] formulas)
        {
            var group = new MenuItem { Header = name };
            menu.Items.Add(group);
            foreach (var formula in formulas)
            {
                var item = new MenuItem { Header = formula.Label, ToolTip = formula.Latex };
                item.Click += (_, _) =>
                {
                    int start = Source.SelectionStart;
                    Source.SelectedText = before + formula.Latex + after;
                    Source.Focus();
                    Source.Select(start + before.Length, formula.Latex.Length);
                };
                group.Items.Add(item);
            }
        }
        Category("基础运算", ("分数", @"\frac{a}{b}"), ("平方根", @"\sqrt{x}"), ("幂", "x^{n}"), ("下标", "a_{n}"));
        Category("代数", ("二次方程求根", @"x=\frac{-b\pm\sqrt{b^{2}-4ac}}{2a}"), ("平方差", "a^{2}-b^{2}=(a-b)(a+b)"));
        Category("微积分", ("极限", @"\lim_{x\to 0}\frac{\sin x}{x}"), ("导数", @"\frac{dy}{dx}"), ("定积分", @"\int_{a}^{b}f(x)\,dx"));
        Category("数列与级数", ("求和", @"\sum_{i=1}^{n}i"), ("连乘", @"\prod_{i=1}^{n}a_i"));
        Category("线性代数", ("矩阵", @"\begin{pmatrix}a & b \\ c & d\end{pmatrix}"), ("向量", @"\vec{v}"));
        Category("三角函数", ("平方和", @"\sin^{2}{x}+\cos^{2}{x}=1"), ("正弦和角", @"\sin(a+b)=\sin a\cos b+\cos a\sin b"));
        var custom = new MenuItem { Header = "自定义公式（可使用选区）" };
        custom.Click += (_, _) => Insert(before, after, "x");
        menu.Items.Add(custom);
        return button;
    }
    void InsertTable()
    {
        if (Source.SelectionLength == 0)
        {
            Insert("\n\n| 项目 | 内容 |\n| --- | --- |\n| ", " | 内容 |\n\n", "项目");
            return;
        }
        var rows = Source.SelectedText.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n')
            .Select(line => line.Split('\t')).ToArray();
        int width = rows.Max(row => row.Length);
        string Row(string[] cells) => "| " + string.Join(" | ", Enumerable.Range(0, width)
            .Select(i => i < cells.Length ? cells[i].Replace("\\", "\\\\").Replace("|", "\\|") : "")) + " |";
        var lines = rows.Select(Row).ToList();
        lines.Insert(1, Row(Enumerable.Repeat("---", width).ToArray()));
        Source.SelectedText = "\n\n" + string.Join("\n", lines) + "\n\n";
        Source.Focus();
    }
    void InsertCodeBlock()
    {
        int longest = System.Text.RegularExpressions.Regex.Matches(Source.SelectedText, "`+")
            .Select(match => match.Length).DefaultIfEmpty().Max();
        string fence = new('`', Math.Max(3, longest + 1));
        Insert("\n\n" + fence + "\n", "\n" + fence + "\n\n", "代码");
    }
    void ToggleTask()
    {
        string text = Source.Text;
        int caret = Source.SelectionStart;
        int start = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
        var match = System.Text.RegularExpressions.Regex.Match(text.Substring(start), @"^([ \t]*)([-+*] )?(?:\[([ xX])\] )?");
        string prefix = match.Groups[1].Value + (match.Groups[2].Success ? match.Groups[2].Value : "- ")
            + (match.Groups[3].Value == " " ? "[x] " : "[ ] ");
        Source.Select(start, match.Length);
        Source.SelectedText = prefix;
        Source.CaretIndex = Math.Max(start + prefix.Length, caret + prefix.Length - match.Length);
        Source.Focus();
    }
    public void Refresh()
    {
        try { if (Source.Text.Length > 150000) { hint.Text = "长笔记已暂停预览，正文仍可编辑和保存（预览上限 15 万字符）。"; Preview.Document = new FlowDocument(); return; } string snapshot = Source.Text;
            Preview.Document = MarkdownNotes.Render(snapshot, imagePath, (start, done) =>
            {
                if (Source.Text != snapshot || start < 0 || start + 2 >= snapshot.Length || snapshot[start] != '[') { Refresh(); return; }
                Source.Select(start + 1, 1);
                Source.SelectedText = done ? "x" : " ";
                Refresh();
            }); hint.Text = $"{Source.Text.Length:N0} 字符 · Ctrl+S 保存 · 图片请通过右侧本地附件管理"; }
        catch (Exception) { hint.Text = "当前片段暂无法预览；源码仍可编辑和保存。"; }
    }
}

