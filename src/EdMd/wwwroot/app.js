// All of EdMd's front-end logic. Kept as an external file (rather than an inline
// <script> in index.html) so the page's Content-Security-Policy can be script-src 'self':
// with no inline script, any script injected via a malicious .md document is refused by
// the browser even if it slips past Toast's sanitizer. Loaded at the end of <body>, after
// the DOM and the vendored Toast bundle, so element/global lookups below are safe.
let currentFileName = ''; // tracked for Save As suggestions; the name is shown in the footer path
const dirtyDot = document.getElementById('dirtyDot');
const statusMsg = document.getElementById('statusMsg');
const wordCount = document.getElementById('wordCount');
const filePath = document.getElementById('filePath');
let isDirty = false;

const editor = new toastui.Editor({
  el: document.querySelector('#editorHost'),
  height: '100%',
  initialEditType: 'wysiwyg',   // inline, formatted editing — no separate preview pane
  previewStyle: 'tab',
  hideModeSwitch: true,          // lock to WYSIWYG; remove this line to allow a raw-markdown tab too
  usageStatistics: false,
  placeholder: 'Open a markdown file, or just start typing…'
});

// Local, offline token estimate (~4 chars/token rule of thumb). Good enough for context
// budgeting when authoring prompts/skills; an exact count would need a model tokenizer.
function estimateTokens(text){ return Math.max(0, Math.round(text.length / 4)); }
function updateWordCount(){
  const t = editor.getMarkdown();
  const words = t.trim() ? t.trim().split(/\s+/).length : 0;
  const lines = t ? t.split(/\r\n|\r|\n/).length : 0;
  // Lead with the token estimate — it's the number that matters for AI prompts.
  wordCount.textContent = `~${estimateTokens(t)} tokens · ${words} words · ${t.length} chars`;
  wordCount.title = `${lines} lines · token count is an estimate (~4 chars/token)`;
}
// Only fire on a real transition, so we don't spam the C# bridge on every keystroke.
function setDirty(v){
  v = !!v;
  if(v === isDirty) return;
  isDirty = v;
  dirtyDot.classList.toggle('show', v);
  if(host && host.dirtyChanged) host.dirtyChanged(v);
}
function setStatus(msg, ms=2200, isError=false){
  statusMsg.textContent = msg;
  statusMsg.style.color = isError ? '#ff6b6b' : 'var(--accent)';
  if(ms) setTimeout(()=>{ if(statusMsg.textContent===msg) statusMsg.textContent=''; }, ms);
}
function setFileName(name){ currentFileName = name || ''; }
// Footer shows the full path when the host can supply one (desktop app), else the name.
function setFilePath(p){ filePath.textContent = p || 'No file open'; filePath.title = p || ''; }

editor.on('change', ()=>{ updateWordCount(); setDirty(true); });

// ---- Host abstraction: WebView2 bridge (desktop) OR File System Access API (Chrome) ----
// The same UI runs in two places: inside the WPF app's WebView2 (where C# owns disk
// I/O over postMessage) and in a plain Chromium tab (where the File System Access API
// gives real local open/save). We detect which and route open/save accordingly.
const IS_DESKTOP = !!(window.chrome && window.chrome.webview);

// Shared UI updates, driven by whichever host loaded/saved the file.
function applyOpenedFile(name, content, path){
  editor.setMarkdown(content, false);
  updateWordCount();
  setFileName(name); setFilePath(path || name); setDirty(false);
  setStatus('Opened ' + name);
}
function applySaved(name, path){ setDirty(false); setFileName(name); setFilePath(path || name); setStatus('Saved ' + name); }

let host;
if(IS_DESKTOP){
  // Desktop: C# owns the file dialogs and disk I/O; we talk over postMessage.
  const send = (payload)=> window.chrome.webview.postMessage(JSON.stringify(payload));
  window.chrome.webview.addEventListener('message', (event)=>{
    const msg = event.data; // WebView2 auto-parses JSON posted from C#
    if(msg.type === 'fileOpened') applyOpenedFile(msg.name, msg.content, msg.path);
    else if(msg.type === 'saved') applySaved(msg.name, msg.path);
    else if(msg.type === 'error') setStatus(msg.message, 6000, true);
    else if(msg.type === 'requestSaveForClose') host.save(); // C# closes once the save round-trips
    else if(msg.type === 'browsers') populateBrowserMenu(msg.list); // installed browsers for the dropdown
  });
  host = {
    open:   ()=>{ if(isDirty && !confirm('Discard unsaved changes and open another file?')) return; send({type:'open'}); },
    save:   ()=> send({type:'save',   content: editor.getMarkdown()}),
    saveAs: ()=> send({type:'saveAs', content: editor.getMarkdown()}),
    // Desktop-only: open the full editor in a browser, pre-loaded with the current doc.
    // browserId (optional) is an Id from the C#-supplied list; omitted = let C# auto-pick.
    openInBrowser: (browserId)=>{ send({type:'openInBrowser', markdown: editor.getMarkdown(), browserId: browserId || ''}); setStatus('Opening in browser…'); },
    dirtyChanged: (v)=> send({type:'dirty', value: !!v}),
    reset: ()=>{},
  };
} else {
  // Chromium (Chrome/Edge): real local open/save via the File System Access API.
  // Needs a secure context — serve the folder over http://localhost (see serve.ps1).
  const pickerTypes = [{ description:'Markdown', accept:{'text/markdown':['.md','.markdown']} }];
  const noApi = ()=> alert('Local file access needs Chrome or Edge (File System Access API).');
  let fileHandle = null;
  const suggestName = ()=> currentFileName || 'untitled.md';
  async function writeTo(handle){
    const w = await handle.createWritable();
    await w.write(editor.getMarkdown());
    await w.close();
  }
  host = {
    async open(){
      if(!window.showOpenFilePicker) return noApi();
      if(isDirty && !confirm('Discard unsaved changes and open another file?')) return;
      try{
        const [handle] = await window.showOpenFilePicker({ types: pickerTypes });
        const file = await handle.getFile();
        fileHandle = handle;
        applyOpenedFile(file.name, await file.text());
      }catch(e){ if(e.name !== 'AbortError') setStatus('Open failed', 6000, true); }
    },
    async save(){
      if(!fileHandle) return host.saveAs();
      try{ await writeTo(fileHandle); applySaved(fileHandle.name); }
      catch(e){ if(e.name !== 'AbortError') setStatus('Save failed', 6000, true); }
    },
    async saveAs(){
      if(!window.showSaveFilePicker) return noApi();
      try{
        const handle = await window.showSaveFilePicker({ suggestedName: suggestName(), types: pickerTypes });
        fileHandle = handle;
        await writeTo(handle);
        applySaved(handle.name);
      }catch(e){ if(e.name !== 'AbortError') setStatus('Save failed', 6000, true); }
    },
    dirtyChanged: ()=>{}, // browser tracks dirty locally; nothing to mirror
    reset: ()=>{ fileHandle = null; },
  };
  // "Open in Browser" is meaningless when we're already in the browser — hide the whole control.
  const bb = document.getElementById('browserMenu');
  if(bb) bb.style.display = 'none';

  // Launched from the desktop app's "Open in Browser": pull the handed-off document
  // from the local server and load it. It's not a real file handle, so the first Save
  // goes through Save As (the browser must prompt for a location to write to).
  const params = new URLSearchParams(location.search);
  if(params.has('session')){
    const token = params.get('token') || '';
    // Strip the token from the address bar/history before anything in the loaded document
    // can run: it would otherwise sit in a document.location a remote <img>/link could leak
    // via the Referer header (and linger in browser history). The fetch below captured it first.
    history.replaceState(null, '', location.pathname);
    fetch('/__session?token=' + encodeURIComponent(token))
      .then(r => r.json())
      .then(d => {
        applyOpenedFile(d.name, d.content, d.path);
        setStatus('Loaded ' + d.name + ' — Save writes via a file picker');
      })
      .catch(()=> setStatus('Could not load handed-off document', 6000, true));
  }
}

// Warn before leaving with unsaved edits — browser build only. In the desktop app the
// C# Window.Closing guard owns this; a beforeunload prompt there would double up.
if(!IS_DESKTOP){
  window.addEventListener('beforeunload', (e)=>{
    if(isDirty){ e.preventDefault(); e.returnValue = ''; }
  });
}

document.getElementById('btnOpen').addEventListener('click', ()=> host.open());
document.getElementById('btnSave').addEventListener('click', ()=> host.save());
document.getElementById('btnSaveAs').addEventListener('click', ()=> host.saveAs());
// Main button = auto-pick (C# chooses the preferred browser); caret = choose a specific one.
document.getElementById('btnBrowser').addEventListener('click', ()=>{ if(host.openInBrowser) host.openInBrowser(); });
const btnBrowserMenu = document.getElementById('btnBrowserMenu');
if(btnBrowserMenu) btnBrowserMenu.addEventListener('click', (e)=>{ e.stopPropagation(); toggleMenu(document.getElementById('browserMenu')); });

// Fill the "Open in Browser" dropdown from the list C# discovered on this machine. The caret
// stays hidden until there's at least one browser to choose (the main button still auto-picks).
function populateBrowserMenu(list){
  const panel = document.getElementById('browserMenuPanel');
  const caret = document.getElementById('btnBrowserMenu');
  if(!panel || !caret) return;
  panel.textContent = '';
  if(!Array.isArray(list) || list.length === 0){ caret.style.display = 'none'; return; }
  for(const b of list){
    const item = document.createElement('button');
    item.textContent = b.name;               // C#-supplied friendly name; set as text, not HTML
    item.addEventListener('click', ()=>{ closeAllMenus(); host.openInBrowser(b.id); });
    panel.appendChild(item);
  }
  caret.style.display = '';
}

document.getElementById('btnNew').addEventListener('click', ()=>{
  if(isDirty && !confirm('Discard unsaved changes and start a new file?')) return;
  editor.setMarkdown('', false);
  updateWordCount(); setFileName(null); setFilePath(null); setDirty(false);
  host.reset();
  setStatus('New file');
});

window.addEventListener('keydown', (e)=>{
  if((e.ctrlKey||e.metaKey) && e.key.toLowerCase()==='s'){
    e.preventDefault();
    host.save();
  }
  if((e.ctrlKey||e.metaKey) && e.key.toLowerCase()==='f'){ e.preventDefault(); openFind(false); }
  if((e.ctrlKey||e.metaKey) && e.key.toLowerCase()==='h'){ e.preventDefault(); openFind(true); }
  if(e.key==='Escape' && findBar.style.display!=='none'){ e.preventDefault(); closeFind(); }
  if((e.ctrlKey||e.metaKey) && (e.key==='='||e.key==='+')){ e.preventDefault(); setZoom(zoom+10); }
  if((e.ctrlKey||e.metaKey) && e.key==='-'){ e.preventDefault(); setZoom(zoom-10); }
  if((e.ctrlKey||e.metaKey) && e.key==='0'){ e.preventDefault(); setZoom(100); }
});

// ---- Zoom (whole editor content area via CSS `zoom`, not the app chrome) ----
const zoomLabel = document.getElementById('zoomLabel');
let zoom = parseInt(localStorage.getItem('EdMd-zoom') || '100', 10);

function setZoom(v){
  zoom = Math.min(200, Math.max(60, v));
  document.documentElement.style.setProperty('--zoom', zoom/100);
  zoomLabel.textContent = zoom + '%';
  localStorage.setItem('EdMd-zoom', zoom);
}
document.getElementById('zoomIn').addEventListener('click', ()=> setZoom(zoom+10));
document.getElementById('zoomOut').addEventListener('click', ()=> setZoom(zoom-10));
zoomLabel.addEventListener('dblclick', ()=> setZoom(100)); // quick reset
setZoom(zoom);

// Ctrl+scroll to zoom, without triggering the browser's own page-zoom
document.getElementById('editorHost').addEventListener('wheel', (e)=>{
  if(!e.ctrlKey) return;
  e.preventDefault();
  setZoom(zoom + (e.deltaY < 0 ? 10 : -10));
}, { passive:false });

// ---- Themes ----
// Each entry maps to a body[data-theme="id"] palette in the CSS above. `dark` picks
// which Toast editor skin (its dark stylesheet class) to apply for good contrast.
const THEMES = [
  { id:'dark',      name:'🌙 Dark',            dark:true  },
  { id:'light',     name:'☀️ Light',           dark:false },
  { id:'nord',      name:'🧊 Nord',            dark:true  },
  { id:'dracula',   name:'🧛 Dracula',         dark:true  },
  { id:'rose',      name:'🌹 Rosé Pine',       dark:true  },
  { id:'gruvbox',   name:'🍂 Gruvbox',         dark:true  },
  { id:'solarized', name:'🌞 Solarized Light', dark:false },
  { id:'prism',     name:'🎨 Prism',           dark:true  },
];
const themeSelect = document.getElementById('themeSelect');
for(const t of THEMES){
  const opt = document.createElement('option');
  opt.value = t.id; opt.textContent = t.name;
  themeSelect.appendChild(opt);
}
function applyTheme(id){
  const theme = THEMES.find(t => t.id === id) || THEMES[0];
  document.body.dataset.theme = theme.id;
  themeSelect.value = theme.id;
  // Toast UI's editor internals need its own dark stylesheet class for dark palettes.
  const editorRoot = document.querySelector('.toastui-editor-defaultUI');
  if(editorRoot) editorRoot.classList.toggle('toastui-editor-dark', theme.dark);
  localStorage.setItem('EdMd-theme', theme.id);
}
themeSelect.addEventListener('change', ()=> applyTheme(themeSelect.value));
applyTheme(localStorage.getItem('EdMd-theme') || 'nord');

// ---- Dropdown menus (Templates, Copy) ----
function closeAllMenus(){ document.querySelectorAll('.menu.open').forEach(m=>m.classList.remove('open')); }
function toggleMenu(menuEl){
  const open = menuEl.classList.contains('open');
  closeAllMenus();
  if(!open) menuEl.classList.add('open');
}
document.addEventListener('click', closeAllMenus);
document.getElementById('btnTemplates').addEventListener('click', (e)=>{ e.stopPropagation(); toggleMenu(document.getElementById('tplMenu')); });
document.getElementById('btnCopyMenu').addEventListener('click', (e)=>{ e.stopPropagation(); toggleMenu(document.getElementById('copyMenu')); });

// ---- Copy for AI ----
async function copyToClipboard(text){
  try{
    if(navigator.clipboard && navigator.clipboard.writeText){ await navigator.clipboard.writeText(text); }
    else { // fallback for non-secure contexts
      const ta = document.createElement('textarea'); ta.value = text;
      ta.style.position='fixed'; ta.style.opacity='0'; document.body.appendChild(ta);
      ta.select(); document.execCommand('copy'); ta.remove();
    }
    return true;
  }catch(e){ return false; }
}
function markdownAsPlainText(){
  // Parse the editor's HTML inertly to strip tags: a DOMParser document doesn't run
  // scripts or load subresources, so there's no <img onerror>/<script> side effect —
  // unlike assigning to innerHTML on a live element. We only read its text back.
  const doc = new DOMParser().parseFromString(editor.getHTML(), 'text/html');
  return (doc.body.textContent || '').replace(/\n{3,}/g, '\n\n').trim();
}
async function doCopy(kind){
  const text = kind === 'text' ? markdownAsPlainText() : editor.getMarkdown();
  const ok = await copyToClipboard(text);
  setStatus(ok ? `Copied ${kind === 'text' ? 'text' : 'markdown'} (${estimateTokens(text)} tokens)` : 'Copy failed', 2600, !ok);
}
document.getElementById('btnCopy').addEventListener('click', ()=> doCopy('md'));
document.querySelectorAll('#copyMenu .menu-panel button').forEach(b =>
  b.addEventListener('click', ()=> doCopy(b.dataset.copy)));

// ---- Raw markdown view toggle (Toast's markdown mode) ----
const btnRaw = document.getElementById('btnRaw');
let editMode = localStorage.getItem('EdMd-mode') === 'markdown' ? 'markdown' : 'wysiwyg';
function applyMode(mode){
  editMode = mode === 'markdown' ? 'markdown' : 'wysiwyg';
  editor.changeMode(editMode, true); // true = don't steal focus
  btnRaw.classList.toggle('active', editMode === 'markdown');
  localStorage.setItem('EdMd-mode', editMode);
}
btnRaw.addEventListener('click', ()=> applyMode(editMode === 'markdown' ? 'wysiwyg' : 'markdown'));
applyMode(editMode);

// ---- Find / Replace (operates on the markdown source; works in both modes) ----
const findBar      = document.getElementById('findBar');
const findInput    = document.getElementById('findInput');
const replaceInput = document.getElementById('replaceInput');
const findCount    = document.getElementById('findCount');
let findCase = false, findRegex = false;

function findPattern(){
  const q = findInput.value;
  if(!q) return null;
  let flags = 'g'; if(!findCase) flags += 'i';
  try{
    const src = findRegex ? q : q.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    return new RegExp(src, flags);
  }catch(e){ return 'error'; }
}
function updateFindCount(){
  const re = findPattern();
  if(re === 'error'){ findCount.textContent = 'bad regex'; return; }
  if(!re){ findCount.textContent = ''; return; }
  const m = editor.getMarkdown().match(re);
  findCount.textContent = (m ? m.length : 0) + ' matches';
}
function replaceAll(){
  const re = findPattern();
  if(!re || re === 'error') return;
  const md = editor.getMarkdown();
  const out = md.replace(re, replaceInput.value);
  if(out !== md){ editor.setMarkdown(out, false); updateWordCount(); setDirty(true); setStatus('Replaced all'); }
  else setStatus('No matches');
  updateFindCount();
}
function openFind(withReplace){
  findBar.style.display = 'flex';
  replaceInput.style.display = withReplace ? '' : 'none';
  document.getElementById('findReplaceAll').style.display = withReplace ? '' : 'none';
  findInput.focus(); findInput.select();
  updateFindCount();
}
function closeFind(){ findBar.style.display = 'none'; }
findInput.addEventListener('input', updateFindCount);
findInput.addEventListener('keydown', (e)=>{ if(e.key==='Enter'){ e.preventDefault(); if(replaceInput.style.display!=='none') replaceAll(); } });
replaceInput.addEventListener('keydown', (e)=>{ if(e.key==='Enter'){ e.preventDefault(); replaceAll(); } });
document.getElementById('findReplaceAll').addEventListener('click', replaceAll);
document.getElementById('findClose').addEventListener('click', closeFind);
document.getElementById('btnFind').addEventListener('click', ()=> openFind(true));
document.getElementById('findCase').addEventListener('click', (e)=>{ findCase = !findCase; e.currentTarget.classList.toggle('active', findCase); updateFindCount(); });
document.getElementById('findRegex').addEventListener('click', (e)=>{ findRegex = !findRegex; e.currentTarget.classList.toggle('active', findRegex); updateFindCount(); });

// ---- Templates (offline scaffolds) ----
const TEMPLATES = {
  skill:
`---
name: my-skill
description: One-line summary of when this skill should be used (be specific — it drives matching).
---

# My Skill

## Instructions

1. Step one.
2. Step two.

## Examples

- ...
`,
  claudemd:
`## Section title

Short description of what this covers.

\`\`\`
example command or snippet
\`\`\`
`,
  prompt:
`## System

You are a <role>. <constraints and tone>.

## User

<the task, with concrete inputs and the expected output format>
`,
  pr:
`## Summary

<what changed and why, in one or two sentences>

## Changes

-

## Testing

-
`,
  spec:
`# Title

## Context

## Goals

## Non-goals

## Design

## Open questions
`,
};
function loadTemplate(key){
  const tpl = TEMPLATES[key];
  if(tpl == null) return;
  if(isDirty && !confirm('Discard unsaved changes and start from this template?')) return;
  editor.setMarkdown(tpl, false);
  updateWordCount(); setFileName(null); setFilePath(null); setDirty(false);
  host.reset();
  setStatus('New from ' + key + ' template');
}
document.querySelectorAll('#tplMenu .menu-panel button').forEach(b =>
  b.addEventListener('click', ()=> loadTemplate(b.dataset.tpl)));

updateWordCount();
