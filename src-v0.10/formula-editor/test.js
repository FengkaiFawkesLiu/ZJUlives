(async()=>{await customElements.whenDefined('math-field');await document.fonts.ready;await new Promise(r=>setTimeout(r,300));const logs=[];function check(name,fn){try{if(!fn())throw Error('assertion failed');logs.push('PASS '+name);}catch(e){logs.push('FAIL '+name+' '+e.message);}}
check('Selected a+b wraps into numerator',()=>{mf.setValue('a+b');mf.select();editor.insertTemplate('\\frac{#0}{#?}');return mf.value.includes('a+b')&&mf.value.includes('placeholder');});
check('Fill denominator placeholder',()=>{mf.insert('c');return !mf.value.includes('placeholder')&&mf.value.includes('c');});
check('Nested root wraps fraction',()=>{mf.select();editor.insertTemplate('\\sqrt{#0}');return mf.value.includes('sqrt')&&mf.value.includes('frac');});
check('Undo and redo preserve structure',()=>{const s=mf.value;mf.executeCommand('undo');const changed=s!==mf.value;mf.executeCommand('redo');return changed&&s===mf.value;});
check('Visual and source roundtrip',()=>{const s=mf.value;editor.switchMode('source');return editor.value()===s&&editor.switchMode('visual')&&mf.value===s;});
check('Unsupported source retained',()=>{editor.setSource('\\unknowncommand{x}');return !editor.switchMode('visual')&&editor.value()==='\\unknowncommand{x}';});
for(const [name,templates] of Object.entries(groups))for(const [label,s] of templates)check(name+' / '+label,()=>MathLive.validateLatex(s.replaceAll('#0','x').replaceAll('#?','a')).length===0);
check('Insert matrix row and column',()=>{editor.setSource('\\begin{pmatrix}a&b\\\\c&d\\end{pmatrix}');editor.switchMode('visual');mf.position=2;return mf.executeCommand('addRowAfter')&&mf.executeCommand('addColumnAfter');});
check('Remove matrix row and column',()=>mf.executeCommand('removeRow')&&mf.executeCommand('removeColumn'));
check('Tab commands navigate placeholders',()=>{mf.setValue('');editor.insertTemplate('\\frac{#0}{#?}');const before=mf.position;mf.executeCommand('moveToNextPlaceholder');return mf.position!==before;});
editor.setSource('\\begin{aligned}\\lim_{x\\to0}\\frac{\\sin x}{x}&=1\\\\\\int_0^1 x^2\\,dx&=\\frac{1}{3}\\end{aligned}');editor.switchMode('visual');editor.selectGroup('高等数学');
check('Export is sized within large editor',()=>{editor.prepareExport();const b=editor.exportBounds();editor.finishExport();return b.width>100&&b.height>30&&b.width<innerWidth;});
const pre=document.createElement('pre');pre.id='test-results';pre.textContent=logs.join('\n');pre.style.cssText='background:white;padding:20px;white-space:pre-wrap';document.body.append(pre);
})().catch(e=>{const p=document.createElement('pre');p.textContent='FAIL '+e.stack;document.body.append(p);});