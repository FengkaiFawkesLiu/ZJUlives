using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Markdig;
using Markdig.Syntax;
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
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().UseTaskLists().UseMathematics().UseEmphasisExtras().DisableHtml().Build();
    public static string Plain(string source) => Markdown.ToPlainText(source, Pipeline);
    public static FlowDocument Render(string source)
    {
        var doc = new FlowDocument { FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 16, Foreground = Ink, PagePadding = new Thickness(22), ColumnWidth = double.PositiveInfinity, LineHeight = 26 };
        Blocks(Markdown.Parse(source, Pipeline), doc.Blocks);
        return doc;
    }
    static FrameworkElement Formula(string text)
    {
        try { return MathFormula.Render(text, 18); }
        catch (Exception) { return new TextBlock { Text = text + "（公式暂无法排版）", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkGoldenrod }; }
    }
    static void Blocks(ContainerBlock source, BlockCollection target)
    {
        foreach (var block in source)
        {
            if (block is MathBlock math) { target.Add(new BlockUIContainer(Formula(math.Lines.ToString()))); continue; }
            if (block is CodeBlock code) { target.Add(new Paragraph(new Run(code.Lines.ToString())) { FontFamily = new FontFamily("Consolas"), FontSize = 14, Background = Brush("#F1F4F1"), Padding = new Thickness(12), LineHeight = 21 }); continue; }
            if (block is ListBlock list)
            {
                var result = new System.Windows.Documents.List { MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Padding = new Thickness(24, 0, 0, 0) };
                if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start) && start > 0) result.StartIndex = start;
                foreach (ListItemBlock item in list) { var li = new ListItem(); Blocks(item, li.Blocks); result.ListItems.Add(li); }
                target.Add(result); continue;
            }
            if (block is MT.Table table)
            {
                var result = new System.Windows.Documents.Table { CellSpacing = 0 }; var rows = new TableRowGroup(); result.RowGroups.Add(rows);
                foreach (MT.TableRow row in table)
                {
                    var tr = new System.Windows.Documents.TableRow(); rows.Rows.Add(tr);
                    foreach (MT.TableCell cell in row) { var tc = new System.Windows.Documents.TableCell { BorderBrush = Line, BorderThickness = new Thickness(0.5), Padding = new Thickness(8), FontWeight = row.IsHeader ? FontWeights.Bold : FontWeights.Normal }; Blocks(cell, tc.Blocks); tr.Cells.Add(tc); }
                }
                target.Add(result); continue;
            }
            if (block is ThematicBreakBlock) { target.Add(new Paragraph(new Run("────────────────")) { Foreground = Muted }); continue; }
            if (block is ContainerBlock container)
            {
                var section = new Section(); if (block is QuoteBlock) { section.BorderBrush = Brush("#387565"); section.BorderThickness = new Thickness(3, 0, 0, 0); section.Padding = new Thickness(12, 4, 4, 4); section.Foreground = Muted; }
                Blocks(container, section.Blocks); target.Add(section); continue;
            }
            if (block is LeafBlock leaf)
            {
                var p = new Paragraph { Margin = new Thickness(0, 0, 0, 12) };
                if (block is HeadingBlock h) { p.FontSize = Math.Max(17, 30 - h.Level * 2); p.FontWeight = FontWeights.Bold; p.Margin = new Thickness(0, 14, 0, 12); }
                if (leaf.Inline != null) Inlines(leaf.Inline, p.Inlines); else p.Inlines.Add(new Run(leaf.Lines.ToString())); target.Add(p);
            }
        }
    }
    static void Inlines(ContainerInline source, InlineCollection target)
    {
        foreach (var inline in source)
        {
            switch (inline)
            {
                case LiteralInline literal: target.Add(new Run(literal.Content.ToString())); break;
                case LineBreakInline: target.Add(new LineBreak()); break;
                case CodeInline code: target.Add(new Run(code.Content) { FontFamily = new FontFamily("Consolas"), Background = Brush("#EEF2EE") }); break;
                case MathInline math: target.Add(new InlineUIContainer(Formula(math.Content.ToString())) { BaselineAlignment = BaselineAlignment.Center }); break;
                case TaskList task: target.Add(new Run(task.Checked ? "☑ " : "☐ ")); break;
                case EmphasisInline emphasis:
                    var span = new Span(); if (emphasis.DelimiterChar == '~') span.TextDecorations = TextDecorations.Strikethrough;
                    else if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeights.Bold; else span.FontStyle = FontStyles.Italic;
                    Inlines(emphasis, span.Inlines); target.Add(span); break;
                case LinkInline link:
                    // Images stay descriptive; attachments are opened through the existing local attachment panel.
                    if (link.IsImage) { target.Add(new Run("[图片：")); Inlines(link, target); target.Add(new Run("]")); break; }
                    if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
                    {
                        var hyperlink = new Hyperlink { NavigateUri = uri, Foreground = Brush("#387565"), ToolTip = uri.AbsoluteUri }; Inlines(link, hyperlink.Inlines);
                        hyperlink.RequestNavigate += (_, e) => { e.Handled = true; try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show("无法打开链接：" + ex.Message); } }; target.Add(hyperlink);
                    }
                    else Inlines(link, target);
                    break;
                case ContainerInline child: Inlines(child, target); break;
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
    public MarkdownEditor(string source, Action changed)
    {
        var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) }; SetDock(tools, Dock.Top); Children.Add(tools);
        void Tool(string label, string before, string after = "", string placeholder = "文字") { var b = Button(label, () => Insert(before, after, placeholder)); b.Focusable = false; b.Padding = new Thickness(8, 6, 8, 6); tools.Children.Add(b); }
        Tool("标题", "\n## ", "\n", "标题"); Tool("粗体", "**", "**"); Tool("斜体", "*", "*"); Tool("列表", "\n- ", "\n"); Tool("待办", "\n- [ ] ", "\n", "待办事项"); Tool("引用", "\n> ", "\n"); Tool("代码", "\n```\n", "\n```\n", "代码"); Tool("链接", "[", "](https://example.com)", "链接名称"); Tool("表格", "\n| 项目 | 内容 |\n| --- | --- |\n| ", " | 内容 |\n", "项目"); Tool("数学公式", "\n$$\n", "\n$$\n", @"\frac{a}{b}");
        var mode = Combo(new[] { "分栏预览", "仅编辑", "仅预览" }, "分栏预览"); tools.Children.Add(mode);
        SetDock(hint, Dock.Bottom); hint.Margin = new Thickness(0, 8, 0, 0); Children.Add(hint);
        var panes = new Grid(); panes.ColumnDefinitions.Add(new()); panes.ColumnDefinitions.Add(new() { Width = new GridLength(8) }); panes.ColumnDefinitions.Add(new()); Children.Add(panes);
        panes.Children.Add(Source); Grid.SetColumn(Preview, 2); panes.Children.Add(Preview);
        var splitter = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Center, Background = Line }; Grid.SetColumn(splitter, 1); panes.Children.Add(splitter);
        mode.SelectionChanged += (_, _) => { int n = mode.SelectedIndex; Source.Visibility = n == 2 ? Visibility.Collapsed : Visibility.Visible; Preview.Visibility = n == 1 ? Visibility.Collapsed : Visibility.Visible; splitter.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed; panes.ColumnDefinitions[0].Width = new GridLength(n == 2 ? 0 : 1, GridUnitType.Star); panes.ColumnDefinitions[1].Width = new GridLength(n == 0 ? 8 : 0); panes.ColumnDefinitions[2].Width = new GridLength(n == 1 ? 0 : 1, GridUnitType.Star); if (n != 1) Refresh(); };
        Source.Text = source; Refresh();
        Source.TextChanged += (_, _) => { changed(); timer.Stop(); timer.Start(); };
        timer.Tick += (_, _) => { timer.Stop(); Refresh(); }; Unloaded += (_, _) => timer.Stop();
    }
    public void Insert(string before, string after, string placeholder)
    {
        int start = Source.SelectionStart; string selected = Source.SelectionLength > 0 ? Source.SelectedText : placeholder;
        Source.SelectedText = before + selected + after; Source.Select(start + before.Length, selected.Length); Source.Focus();
    }
    public void Refresh()
    {
        try { if (Source.Text.Length > 150000) { hint.Text = "长笔记已暂停预览，正文仍可编辑和保存（预览上限 15 万字符）。"; Preview.Document = new FlowDocument(); return; } Preview.Document = MarkdownNotes.Render(Source.Text); hint.Text = $"{Source.Text.Length:N0} 字符 · Ctrl+S 保存 · 图片请通过右侧本地附件管理"; }
        catch (Exception) { hint.Text = "当前片段暂无法预览；源码仍可编辑和保存。"; }
    }
}

