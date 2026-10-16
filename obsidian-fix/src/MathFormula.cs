using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfMath.Parsers;
using WpfMath.Rendering;
using XamlMath;

namespace Shike;

// Embedded fonts and parser: formula source never leaves this process.
public static class MathFormula
{
    public static FrameworkElement Render(string source, double size = 27)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new FormatException("请输入 LaTeX 公式。");
        if (source.Length > 1500) throw new FormatException("公式最多输入 1500 个字符。");
        string latex = source.Trim();
        foreach (var pair in new[] { ("$$", "$$"), (@"\[", @"\]"), (@"\(", @"\)"), ("$", "$") })
            if (latex.Length >= pair.Item1.Length + pair.Item2.Length && latex.StartsWith(pair.Item1) && latex.EndsWith(pair.Item2)) { latex = latex[pair.Item1.Length..^pair.Item2.Length]; break; }
        int depth = 0;
        for (int n = 0; n < latex.Length; n++)
        {
            if (latex[n] == '\\') { n++; continue; }
            if (latex[n] == '{' && ++depth > 20) throw new FormatException("公式嵌套过深。");
            if (latex[n] == '}' && --depth < 0) throw new FormatException("花括号不匹配。");
        }
        if (depth != 0) throw new FormatException("花括号不匹配。");
        try
        {
            var formula = WpfTeXFormulaParser.Instance.Parse(latex);
            // Render at twice the display size for crisp embedded images.
            var bitmap = formula.RenderToBitmap(WpfTeXEnvironment.Create(TexStyle.Display, size * 2, "Cambria Math"), scale: size * 2);
            if (bitmap.PixelWidth > 12000 || bitmap.PixelHeight > 6000) throw new FormatException("公式尺寸过大，请拆分插入。");
            bitmap.Freeze();
            return new Border { Background = Brushes.White, Padding = new Thickness(12), Child = new Image { Source = bitmap, Width = bitmap.PixelWidth / 2.0, Height = bitmap.PixelHeight / 2.0, Stretch = Stretch.Uniform } };
        }
        catch (FormatException) { throw; }
        catch (Exception ex) { throw new FormatException("LaTeX 排版失败，请检查命令和括号：" + ex.Message, ex); }
    }
}
