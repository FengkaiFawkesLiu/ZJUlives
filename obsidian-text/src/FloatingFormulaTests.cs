using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Controls.Primitives;

namespace Shike;
public sealed partial class MainWindow
{
    void RunFloatingFormulaTests(string dir, Action<bool,string> check, Action<Action,Action<Window>> modal, Action<Window,string> click)
    {
        string original = string.Concat(Enumerable.Repeat("这是用于验证公式四周环绕的课堂笔记。拖动公式后，文字应避开公式的矩形区域，继续在两侧自然排版。", 16));
        Point savedPosition = default;
        modal(() => EditKnowledgeNote(null), window =>
        {
            Visuals<TextBox>(window).First(t=>t.MaxLength==300).Text="浮动公式与四周环绕";
            var editor=Visuals<RichTextBox>(window).Single(); editor.Document.Blocks.Clear(); var p=new Paragraph(new Run(original)); editor.Document.Blocks.Add(p); editor.CaretPosition=p.ContentStart;
            InsertNoteImage(editor,Rasterize(MathFormula.Render(@"x=\frac{-b+\sqrt{b^2-4ac}}{2a}")),new(){Kind="math",Source=@"x=\frac{-b+\sqrt{b^2-4ac}}{2a}"});
            var image=RichNoteCodec.Images(editor.Document).Single(); NoteFormatting.SetFormulaSize(image,20);
            var anchor=editor.Document.Blocks.OfType<Paragraph>().Last(); FloatingFormula.Wrap(image,140,30,anchor); window.UpdateLayout();
            var figure=FloatingFormula.FigureOf(image)!;
            check(figure.WrapDirection is WrapDirection.Left or WrapDirection.Right && figure.HorizontalOffset==140, "Formula floats with rectangular exclusion on the wider side");
            var bounds=new Rect(image.TranslatePoint(new Point(),editor),new Size(image.ActualWidth,image.ActualHeight));
            bool left=false,right=false; int overlap=0;
            for(var pointer=anchor.ContentStart; pointer!=null && pointer.CompareTo(anchor.ContentEnd)<0; pointer=pointer.GetNextInsertionPosition(LogicalDirection.Forward))
            {
                if (pointer.Paragraph != anchor) continue;
                var rect=pointer.GetCharacterRect(LogicalDirection.Forward);
                if(rect.Top>=bounds.Top && rect.Bottom<=bounds.Bottom && rect.Height>0) { if(rect.Left<bounds.Left)left=true; if(rect.Left>bounds.Right)right=true; if(rect.Left>bounds.Left+2 && rect.Left<bounds.Right-2)overlap++; }
            }
            Theme.Screenshot((FrameworkElement)window.Content,Path.Combine(dir,"环绕测量.png"));
            check((left || right) && overlap==0,"Actual text layout wraps around formula without overlap");
            check(bounds.Left > 100, "Floating position affects the rendered image, not just metadata");
            var adorner=new FormulaAdorner(image,editor,()=>{}); adorner.Measure(new Size(400,300)); adorner.Arrange(new Rect(0,0,400,300));
            var handle=Visuals<Thumb>(adorner).Single(); double width=image.Width; handle.RaiseEvent(new DragDeltaEventArgs(40,0){RoutedEvent=Thumb.DragDeltaEvent});
            check(image.Width>width && Math.Abs(figure.Width.Value-image.Width)<0.01,"Resize handle changes formula and text exclusion width together");
            FloatingFormula.Place(editor,image,new Point(75,200)); window.UpdateLayout();
            check(((NoteEmbed)image.Tag).X!=140,"Dropping at another paper location changes floating position");
            FloatingFormula.Wrap(image,130,25,anchor); window.UpdateLayout();
            editor.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left){RoutedEvent=UIElement.PreviewMouseLeftButtonDownEvent,Source=image});
            editor.ReleaseMouseCapture(); window.UpdateLayout();
            check(AdornerLayer.GetAdornerLayer(image)?.GetAdorners(image)?.OfType<FormulaAdorner>().Any()==true,"Selecting formula exposes a visible resize handle");
            Theme.Screenshot((FrameworkElement)window.Content,Path.Combine(dir,"公式四周环绕.png"));
            savedPosition = image.TranslatePoint(new Point(),editor);
            File.WriteAllLines(Path.Combine(dir,"before-layout.txt"),NoteTypography.Paragraphs(editor.Document).Select(p=>$"{p.FontSize} {p.FontFamily} {p.LineHeight} {p.Margin} {p.Padding} {p.ContentStart.GetCharacterRect(LogicalDirection.Forward)}"));
            check(new RichNoteCodec().PlainText(editor.Document).Replace("\r","").Replace("\n","").Contains(original),"Floating and resizing preserve all surrounding text");
            click(window,"保存");
        });
        var saved=store.Data.Items.Single(i=>i.Title=="浮动公式与四周环绕");
        modal(()=>EditKnowledgeNote(saved),window=>
        {
            var editor=Visuals<RichTextBox>(window).Single(); var image=RichNoteCodec.Images(editor.Document).Single(); var tag=(NoteEmbed)image.Tag;
            check(tag.Wrap && FloatingFormula.FigureOf(image) is Figure f && Math.Abs(f.HorizontalOffset-130)<0.01 && Math.Abs(f.VerticalOffset-25)<0.01,"Floating geometry and wrap mode survive save and reopen");
            check(tag.FontSize>20 && image.Width>0,"Resized formula dimensions survive reopen");
            window.UpdateLayout(); var restoredPosition = image.TranslatePoint(new Point(),editor);
            File.WriteAllLines(Path.Combine(dir,"after-layout.txt"),NoteTypography.Paragraphs(editor.Document).Select(p=>$"{p.FontSize} {p.FontFamily} {p.LineHeight} {p.Margin} {p.Padding} {p.ContentStart.GetCharacterRect(LogicalDirection.Forward)}"));
            Theme.Screenshot((FrameworkElement)window.Content,Path.Combine(dir,"环绕重新打开.png"));
            check((restoredPosition-savedPosition).Length<2,"Rendered formula position survives reopening: " + savedPosition + " / " + restoredPosition);
            FloatingFormula.Inline(image); check(FloatingFormula.FigureOf(image)==null && image.Parent is InlineUIContainer,"Floating formula can return to inline mode"); click(window,"保存");
        });
    }
}


