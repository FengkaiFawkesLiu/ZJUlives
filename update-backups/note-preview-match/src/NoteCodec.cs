using System;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows.Controls;

namespace Shike;

// Versioned editor boundary: a later Markdown codec can be implemented without changing note IDs, links or libraries.
public interface INoteCodec
{
    string Format { get; }
    FlowDocument Decode(string body, string plainText);
    string Encode(FlowDocument document);
    string PlainText(FlowDocument document);
}
public sealed class RichNoteCodec : INoteCodec
{
    public string Format => "rtf-v2";
    public FlowDocument Decode(string body, string plainText)
    {
        var document = new FlowDocument { FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"), FontSize = 15, PagePadding = new Thickness(18) };
        if (body.Length == 0) { document.Blocks.Add(new Paragraph(new Run(plainText))); NoteTypography.Apply(document, new()); }
        else
        {
            NoteEnvelope? envelope = body.StartsWith('{') ? JsonSerializer.Deserialize<NoteEnvelope>(body) : null;
            using var stream = new MemoryStream(Convert.FromBase64String(envelope?.Rtf ?? body));
            new TextRange(document.ContentStart, document.ContentEnd).Load(stream, DataFormats.Rtf);
            if (envelope != null)
            {
                NoteTypography.Apply(document, envelope.Layout, preserveParagraphs: true);
                var paragraphs = NoteTypography.Paragraphs(document).ToList();
                for (int n = 0; n < Math.Min(paragraphs.Count, envelope.Paragraphs.Count); n++)
                {
                    var style = envelope.Paragraphs[n];
                    if (style.Margin.Length == 4) paragraphs[n].Margin = new Thickness(style.Margin[0] ?? double.NaN, style.Margin[1] ?? double.NaN, style.Margin[2] ?? double.NaN, style.Margin[3] ?? double.NaN);
                    paragraphs[n].LineHeight = style.LineHeight ?? double.NaN;
                }
                var images = Images(document).ToList();
                foreach (var embedded in envelope.Embeds)
                    if (embedded.Index >= 0 && embedded.Index < images.Count && embedded.Kind is "math" or "ink") { images[embedded.Index].Tag = embedded; if (embedded.Kind == "math" && embedded.NaturalWidth > 0) NoteFormatting.SetFormulaSize(images[embedded.Index], embedded.FontSize); if (embedded.Wrap) FloatingFormula.Wrap(images[embedded.Index], embedded.X, embedded.Y); images[embedded.Index].ToolTip = "双击编辑" + (embedded.Kind == "math" ? "公式" : "手写"); }
            }
        }
        return document;
    }
    public string Encode(FlowDocument document)
    {
        // RTF flattens Figures into additional paragraphs. Flatten a private WPF clone first,
        // then restore floating geometry from metadata on load; the live editor and undo history stay intact.
        var sourceImages = Images(document).ToList();
        FlowDocument serial = document;
        if (sourceImages.Any(i => i.Tag is NoteEmbed e && e.Wrap))
        {
            using var package = new MemoryStream(); new TextRange(document.ContentStart,document.ContentEnd).Save(package,DataFormats.XamlPackage);
            package.Position = 0; serial = new FlowDocument(); new TextRange(serial.ContentStart,serial.ContentEnd).Load(package,DataFormats.XamlPackage);
            var clones = Images(serial).ToList();
            if (clones.Count != sourceImages.Count) throw new InvalidDataException("公式复制不完整，已停止保存。");
            for (int i = 0; i < clones.Count; i++) if (sourceImages[i].Tag is NoteEmbed tag && tag.Wrap) FloatingFormula.Inline(clones[i]);
        }
        using var stream = new MemoryStream();
        new TextRange(serial.ContentStart, serial.ContentEnd).Save(stream, DataFormats.Rtf);
        if (stream.Length > 6 * 1024 * 1024) throw new InvalidDataException("正文内容过大，请将大图片作为附件保存。");
        var embeds = Images(document).Select((image, index) => image.Tag is NoteEmbed tag ? new NoteEmbed { Index = index, Kind = tag.Kind, Source = tag.Source, FontSize = tag.FontSize, NaturalWidth = tag.NaturalWidth, Wrap = tag.Wrap, X = tag.X, Y = tag.Y } : null).Where(e => e != null).Cast<NoteEmbed>().ToList();
        var styles = NoteTypography.Paragraphs(serial).Select(p => new ParagraphLayout { LineHeight = double.IsNaN(p.LineHeight) ? null : p.LineHeight, Margin = new[] { p.Margin.Left, p.Margin.Top, p.Margin.Right, p.Margin.Bottom }.Select(v => double.IsNaN(v) ? (double?)null : v).ToArray() }).ToList();
        var result = JsonSerializer.Serialize(new NoteEnvelope { Rtf = Convert.ToBase64String(stream.ToArray()), Layout = NoteTypography.Get(document), Embeds = embeds, Paragraphs = styles });
        if (result.Length > 8 * 1024 * 1024) throw new InvalidDataException("正文内容过大，请将大图片作为附件保存。");
        return result;
    }
    public string PlainText(FlowDocument document) => new TextRange(document.ContentStart, document.ContentEnd).Text.TrimEnd('\r', '\n');
    public static IEnumerable<Image> Images(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is Image image) yield return image;
            foreach (var nested in Images(child)) yield return nested;
        }
    }
}
public sealed class NoteEmbed { public bool Wrap { get; set; } public double X { get; set; } public double Y { get; set; } public double FontSize { get; set; } = 24; public double NaturalWidth { get; set; } public int Index { get; set; } public string Kind { get; set; } = ""; public string Source { get; set; } = ""; }
public sealed class ParagraphLayout { public double? LineHeight { get; set; } public double?[] Margin { get; set; } = new double?[4]; }
public sealed class NoteEnvelope { public string Rtf { get; set; } = ""; public NoteLayout Layout { get; set; } = new(); public List<NoteEmbed> Embeds { get; set; } = new(); public List<ParagraphLayout> Paragraphs { get; set; } = new(); }
public sealed class NoteLayout
{
    public double FontSize { get; set; } = 16;
    public double LineSpacing { get; set; } = 1.5;
    public double ParagraphSpacing { get; set; } = 10;
    public double PageMargin { get; set; } = 24;
    public double ColumnWidth { get; set; } = 580;
}
public static class NoteTypography
{
    public static NoteLayout Get(FlowDocument doc) => doc.Resources["note-layout"] as NoteLayout ?? new();
    public static void Apply(FlowDocument doc, NoteLayout layout, bool resizeText = false, bool preserveParagraphs = false)
    {
        if (!double.IsFinite(layout.FontSize) || layout.FontSize < 10 || layout.FontSize > 36 || !double.IsFinite(layout.LineSpacing) || layout.LineSpacing < 1 || layout.LineSpacing > 2.5 || !double.IsFinite(layout.ParagraphSpacing) || layout.ParagraphSpacing < 0 || layout.ParagraphSpacing > 36 || !double.IsFinite(layout.PageMargin) || layout.PageMargin < 8 || layout.PageMargin > 80 || !double.IsFinite(layout.ColumnWidth) || layout.ColumnWidth < 320 || layout.ColumnWidth > 1600) throw new InvalidDataException("笔记排版参数不合法。");
        if (resizeText) {
            double factor = layout.FontSize / Get(doc).FontSize;
            void Scale(DependencyObject root) { foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) { if (child is TextElement element && element.ReadLocalValue(TextElement.FontSizeProperty) is double local) element.FontSize = Math.Clamp(local * factor, 8, 100); Scale(child); } }
            Scale(doc);
        }
        doc.Resources["note-layout"] = layout; doc.FontSize = layout.FontSize; doc.LineHeight = layout.FontSize * layout.LineSpacing; doc.LineStackingStrategy = LineStackingStrategy.MaxHeight;
        doc.PagePadding = new Thickness(layout.PageMargin); doc.PageWidth = double.NaN; doc.ColumnWidth = layout.ColumnWidth; doc.PageWidth = layout.ColumnWidth + 2 * layout.PageMargin;
        if (!preserveParagraphs) foreach (var p in Paragraphs(doc)) { p.Margin = new Thickness(0, 0, 0, layout.ParagraphSpacing); p.LineHeight = layout.FontSize * layout.LineSpacing; p.LineStackingStrategy = LineStackingStrategy.MaxHeight; }
    }
    public static IEnumerable<Paragraph> Paragraphs(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) { if (child is Paragraph p) yield return p; foreach (var nested in Paragraphs(child)) yield return nested; }
    }
}



