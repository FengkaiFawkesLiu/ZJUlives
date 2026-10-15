using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
namespace Shike;
public static class FormulaEditorTests
{
    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir); var log = new List<string>(); bool failed = false;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; Theme.Install(app);
        var window = new FormulaEditorWindow(null, "", false, Path.Combine(Path.GetTempPath(), "Shike-math-test-" + Guid.NewGuid().ToString("N")));
        async Task Check(string label, string js) { var actual = await window.Script(js); if (actual != "true") throw new Exception(label + ": " + actual); log.Add("PASS " + label); File.WriteAllLines(Path.Combine(dir,"formula-test-results.txt"),log); }
        window.Loaded += async (_, _) =>
        {
            try
            {
                if (!await window.Ready.Task.WaitAsync(TimeSpan.FromSeconds(30))) throw new Exception("Editor initialization failed");
                await Check("All bundled fonts loaded locally", "document.fonts.status === 'loaded'");
                await Check("Selected expression becomes numerator and denominator is editable", @"(()=>{mf.setValue('a+b');mf.select();editor.insertTemplate('\\frac{#0}{#?}');return mf.value.includes('a+b') && mf.value.includes('placeholder');})()");
                await window.CaptureWindow(Path.Combine(dir,"分式占位框.png"));
                await Check("Typing into fraction placeholder completes formula", @"(()=>{mf.insert('c');return !mf.value.includes('placeholder') && mf.value.includes('c');})()");
                await Check("Nested root wraps selected fraction", @"(()=>{mf.select();editor.insertTemplate('\\sqrt{#0}');return mf.value.includes('sqrt') && mf.value.includes('frac');})()");
                await Check("Visual undo and redo preserve structure", @"(()=>{const s=mf.value;mf.executeCommand('undo');const changed=mf.value!==s;mf.executeCommand('redo');return changed && mf.value===s;})()");
                await Check("LaTeX source and visual mode roundtrip", @"(()=>{const s=mf.value;editor.switchMode('source');return editor.value()===s && editor.switchMode('visual') && mf.value===s;})()");
                await Check("Unsupported source preserved rather than silently lost", @"(()=>{editor.setSource('\\unknowncommand{x}');const s=editor.value();return !editor.switchMode('visual') && editor.value()===s;})()");
                await Check("Every template category has valid LaTeX structures", @"(()=>Object.values(groups).flat().every(t=>MathLive.validateLatex(t[1].replaceAll('#0','x').replaceAll('#?','a')).length===0))()");
                await Check("Matrix cell navigation and row/column insertion", @"(()=>{editor.setSource('\\begin{pmatrix}a&b\\\\c&d\\end{pmatrix}');editor.switchMode('visual');mf.position=2;return mf.executeCommand('addRowAfter') && mf.executeCommand('addColumnAfter');})()");
                await Check("Matrix row and column removal", "mf.executeCommand('removeRow') && mf.executeCommand('removeColumn')");
                await window.Script(@"editor.setSource('\\begin{aligned}\\lim_{x\\to0}\\frac{\\sin x}{x}&=1\\\\\\int_0^1 x^2\\,dx&=\\frac{1}{3}\\end{aligned}');editor.switchMode('visual');editor.selectGroup('高等数学')");
                await window.CaptureWindow(Path.Combine(dir,"可视化公式编辑器.png"));
                var image = await window.CaptureFormula(); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using (var f = File.Create(Path.Combine(dir,"插入公式.png"))) encoder.Save(f);
                if(image.PixelWidth < 100 || image.PixelHeight < 30) throw new Exception("Empty formula capture"); log.Add("PASS Local formula capture has expected dimensions");
                await window.Script("document.getElementById('save').click()");
            }
            catch(Exception ex) { failed = true; log.Add("FAIL " + window.Stage + " " + ex); try { log.Add(await window.Script("JSON.stringify({errors:window._errors,html:document.body.innerText,editor:typeof editor,math:typeof MathLive})")); await window.CaptureWindow(Path.Combine(dir,"failure.png")); } catch(Exception diagnostic) { log.Add(diagnostic.Message); } File.WriteAllLines(Path.Combine(dir,"formula-test-results.txt"),log); window.CloseTest(); }
        };
        window.ShowDialog();
        if(!failed)
        {
            try {
                if(window.ResultImage == null || !window.ResultLatex.Contains("int")) throw new Exception("Save did not produce formula");
                var rich = new RichTextBox(); MainWindow.InsertNoteImage(rich,window.ResultImage,new(){Kind="math",Source=window.ResultLatex});
                var codec = new RichNoteCodec(); var restored = codec.Decode(codec.Encode(rich.Document), ""); var embed = RichNoteCodec.Images(restored).Single().Tag as NoteEmbed;
                if(embed?.Source!=window.ResultLatex) throw new Exception("Source lost after save");
                log.Add("PASS Save button exports image and LaTeX source through note codec");
            } catch(Exception ex) {failed=true;log.Add("FAIL "+ex);}
        }
        File.WriteAllLines(Path.Combine(dir,"formula-test-results.txt"),log); app.Shutdown();return failed?1:0;
    }
}
