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
// Clear the editor's undo history so the just-loaded document is the baseline. Without
// this, editor.setMarkdown() records the load as one undoable transaction on top of the
// empty starting doc, so the user's first Ctrl+Z wipes the whole file back to blank.
// Recreating the EditorState reinitialises all plugin state (incl. prosemirror-history)
// while keeping the current doc/plugins/selection — the standard ProseMirror recipe.
function resetUndoHistory(){
  try{
    const view = editor.getCurrentModeEditor().view;
    view.updateState(view.state.constructor.create({
      doc: view.state.doc,
      plugins: view.state.plugins,
      selection: view.state.selection,
    }));
  }catch(_){ /* Toast internals moved; skip (undo just isn't reset) */ }
}
function applyOpenedFile(name, content, path){
  editor.setMarkdown(content, false);
  resetUndoHistory();
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
    reset: ()=> send({type:'reset'}),
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

// "New" starts a fresh, untitled document: it blanks the editor AND detaches from the
// currently-open file (clearing the name/path and the file handle). This is what stops a
// later Save from overwriting the last-opened file — with no handle, Save falls through
// to Save As and asks where to write the new file.
document.getElementById('btnNew').addEventListener('click', ()=>{
  if(isDirty && !confirm('Discard unsaved changes and start a new file?')) return;
  editor.setMarkdown('', false);
  resetUndoHistory();
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

// ---- Reading width (centered column via the --read-col CSS var; browser-side only) ----
// Constrains line length for readability. 'full' = 100% collapses back to full-width.
const WIDTHS = [
  { id:'narrow',  name:'📖 Narrow',      col:'38rem' },
  { id:'comfort', name:'📗 Comfortable', col:'46rem' },  // default (~72ch)
  { id:'wide',    name:'📚 Wide',        col:'58rem' },
  { id:'full',    name:'🖥️ Full width',  col:'100%'  },
];
const widthSelect = document.getElementById('widthSelect');
for(const w of WIDTHS){
  const o = document.createElement('option');
  o.value = w.id; o.textContent = w.name;
  widthSelect.appendChild(o);
}
function applyWidth(id){
  const w = WIDTHS.find(x => x.id === id) || WIDTHS[1];
  document.documentElement.style.setProperty('--read-col', w.col);
  widthSelect.value = w.id;
  localStorage.setItem('EdMd-width', w.id);
}
widthSelect.addEventListener('change', ()=> applyWidth(widthSelect.value));
applyWidth(localStorage.getItem('EdMd-width') || 'comfort');

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
  { id:'aurora',    name:'🌌 Aurora',          dark:true  },
  { id:'sunset',    name:'🌇 Sunset',          dark:true  },
  { id:'blossom',   name:'🌸 Blossom',         dark:false },
  { id:'citrus',    name:'🍋 Citrus',          dark:false },
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

// ---- Find / Replace ----
// Matches are highlighted directly in the visible editing surface via the CSS Custom
// Highlight API (Chromium-only, which is all WebView2/Chrome/Edge). It paints Ranges
// without mutating the DOM, so it never disturbs Toast/ProseMirror. Highlighting reads
// the rendered text (WYSIWYG shows prose, raw mode shows the markdown source), and
// Replace all edits those same rendered matches so it stays consistent with the count.
const findBar      = document.getElementById('findBar');
const findInput    = document.getElementById('findInput');
const replaceInput = document.getElementById('replaceInput');
const findCount    = document.getElementById('findCount');
let findCase = false, findRegex = false;

const HL_OK = !!(window.CSS && CSS.highlights && window.Highlight);
const matchHi   = HL_OK ? new Highlight() : null;
const currentHi = HL_OK ? new Highlight() : null;
if(HL_OK){
  CSS.highlights.set('find-match', matchHi);
  CSS.highlights.set('find-current', currentHi);
  currentHi.priority = 1;            // the active match wins where the two overlap
}
let matchRanges = [];
let currentMatch = -1;
let suppressFindRefresh = false;  // set during a batch replace so each edit doesn't re-scan

// The regex source for the current query (query escaped unless regex mode is on).
// null = empty query, 'error' = invalid regex.
function findSource(){
  const q = findInput.value;
  if(!q) return null;
  const src = findRegex ? q : q.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  try{ new RegExp(src); return src; }catch(e){ return 'error'; }
}
function findPattern(){
  const src = findSource();
  if(!src || src === 'error') return src;
  return new RegExp(src, findCase ? 'g' : 'gi');
}
// The editable surface of whichever mode (WYSIWYG / raw) is currently shown.
function visibleEditorRoot(){
  const roots = document.querySelectorAll('#editorHost .ProseMirror');
  for(const r of roots){ if(r.offsetParent !== null || r.getClientRects().length) return r; }
  return roots[0] || null;
}
function locateOffset(map, offset){
  let lo = 0, hi = map.length - 1, res = 0;
  while(lo <= hi){ const mid = (lo + hi) >> 1; if(map[mid].start <= offset){ res = mid; lo = mid + 1; } else hi = mid - 1; }
  return { node: map[res].node, offset: offset - map[res].start };
}
// Flatten a root's text nodes into one string plus a {node,start} map so a match offset
// in the string can be mapped back to a DOM position (see locateOffset). Used for both
// highlighting and DOM-range replace, so the two always see the same text.
function domTextAndMap(root){
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, null);
  let text = ''; const map = []; let n;
  while((n = walker.nextNode())){ map.push({ node: n, start: text.length }); text += n.data; }
  return { text, map };
}
function rangeFromOffsets(map, s, e){
  const a = locateOffset(map, s), b = locateOffset(map, e);
  const r = document.createRange();
  r.setStart(a.node, a.offset); r.setEnd(b.node, b.offset);
  return r;
}
function clearHighlights(){
  if(HL_OK){ matchHi.clear(); currentHi.clear(); }
  matchRanges = []; currentMatch = -1;
}
// Rebuild the set of match ranges from the live DOM. keepIndex keeps the caret on the
// same match number where possible (e.g. after toggling case), else jumps to the first.
function refreshMatches(keepIndex){
  const prev = currentMatch;
  clearHighlights();
  const re = findPattern();
  if(!HL_OK || !re || re === 'error'){ updateFindCount(re); return; }
  const root = visibleEditorRoot();
  if(!root){ updateFindCount(re); return; }
  const { text, map } = domTextAndMap(root);
  if(!map.length){ updateFindCount(re); return; }
  re.lastIndex = 0;
  let m;
  while((m = re.exec(text))){
    if(m[0].length === 0){ re.lastIndex++; continue; }   // guard against zero-width matches
    try{
      const r = rangeFromOffsets(map, m.index, m.index + m[0].length);
      matchRanges.push(r); matchHi.add(r);
    }catch(_){ /* skip a range the DOM won't accept */ }
  }
  if(matchRanges.length){
    const idx = keepIndex && prev >= 0 ? Math.min(prev, matchRanges.length - 1) : 0;
    setCurrent(idx, false);
  } else {
    updateFindCount(re);
  }
}
function setCurrent(idx, scroll){
  if(!HL_OK) return;
  currentHi.clear();
  if(idx < 0 || idx >= matchRanges.length){ currentMatch = -1; updateFindCount(); return; }
  currentMatch = idx;
  const r = matchRanges[idx];
  currentHi.add(r);
  if(scroll){ const host = r.startContainer.parentElement; if(host) host.scrollIntoView({ block: 'center' }); }
  updateFindCount();
}
function stepMatch(dir){
  if(!matchRanges.length){ refreshMatches(true); if(!matchRanges.length) return; }
  let idx = currentMatch + dir;
  if(idx < 0) idx = matchRanges.length - 1;
  if(idx >= matchRanges.length) idx = 0;
  setCurrent(idx, true);
}
function updateFindCount(re){
  if(re === undefined) re = findPattern();
  if(re === 'error'){ findCount.textContent = 'bad regex'; return; }
  if(!re){ findCount.textContent = ''; return; }
  if(!HL_OK){
    // No CSS Custom Highlight API (non-Chromium): we can't paint/navigate ranges, but
    // still report a count so Find stays useful. Count over the markdown source.
    const m = editor.getMarkdown().match(re);
    findCount.textContent = (m ? m.length : 0) + ' matches';
    return;
  }
  findCount.textContent = matchRanges.length
    ? (currentMatch + 1) + ' of ' + matchRanges.length
    : 'No results';
}
// Replace all matches. On Chromium we edit the same DOM-derived matches that are
// highlighted/counted, so what gets replaced always equals what the user sees. Each
// match is written through execCommand('insertText'), which ProseMirror handles as a
// normal edit (keeping its model + the markdown in sync). Matches are re-derived from
// the live DOM after every edit — ProseMirror re-renders can detach earlier ranges — and
// a text-offset cursor advances past each insertion so a replacement that itself contains
// the query can't loop forever. Non-Chromium (no highlight ranges) falls back to a plain
// markdown-source replace.
function replaceAll(){
  const src = findSource();
  if(!src || src === 'error') return;
  if(!HL_OK){
    const re = new RegExp(src, findCase ? 'g' : 'gi');
    const md = editor.getMarkdown();
    const out = md.replace(re, replaceInput.value);
    if(out !== md){ editor.setMarkdown(out, false); updateWordCount(); setDirty(true); setStatus('Replaced all'); }
    else setStatus('No matches');
    updateFindCount();
    return;
  }
  const editRoot = visibleEditorRoot();
  if(!editRoot){ setStatus('No matches'); return; }
  if(editRoot.focus) editRoot.focus();            // execCommand targets the focused editable
  const flags = findCase ? '' : 'i';
  const reOne = new RegExp(src, flags);           // non-global: expands $& / $1 per match
  const sel = window.getSelection();
  let cursor = 0, replaced = 0, guard = 100000;
  suppressFindRefresh = true;
  try{
    while(guard-- > 0){
      const root = visibleEditorRoot();
      if(!root) break;
      const { text, map } = domTextAndMap(root);
      const reG = new RegExp(src, 'g' + flags);
      reG.lastIndex = cursor;
      const m = reG.exec(text);
      if(!m) break;
      if(m[0].length === 0){ cursor = m.index + 1; continue; }   // skip zero-width matches
      let range;
      try{ range = rangeFromOffsets(map, m.index, m.index + m[0].length); }
      catch(_){ cursor = m.index + m[0].length; continue; }
      const replacement = m[0].replace(reOne, replaceInput.value);
      sel.removeAllRanges(); sel.addRange(range);
      if(!document.execCommand('insertText', false, replacement)) break;
      replaced++;
      cursor = m.index + replacement.length;       // step past the inserted text
    }
  } finally { suppressFindRefresh = false; }
  if(replaced){ updateWordCount(); setDirty(true); setStatus('Replaced ' + replaced); }
  else setStatus('No matches');
  findInput.focus();
  refreshMatches(false);
}
function openFind(withReplace){
  findBar.style.display = 'flex';
  replaceInput.style.display = withReplace ? '' : 'none';
  document.getElementById('findReplaceAll').style.display = withReplace ? '' : 'none';
  findInput.focus(); findInput.select();
  refreshMatches(false);
}
function closeFind(){ findBar.style.display = 'none'; clearHighlights(); }
findInput.addEventListener('input', ()=> refreshMatches(false));
findInput.addEventListener('keydown', (e)=>{
  if(e.key === 'Enter'){ e.preventDefault(); stepMatch(e.shiftKey ? -1 : 1); }
});
replaceInput.addEventListener('keydown', (e)=>{ if(e.key==='Enter'){ e.preventDefault(); replaceAll(); } });
document.getElementById('findReplaceAll').addEventListener('click', replaceAll);
document.getElementById('findClose').addEventListener('click', closeFind);
document.getElementById('findPrev').addEventListener('click', ()=> stepMatch(-1));
document.getElementById('findNext').addEventListener('click', ()=> stepMatch(1));
document.getElementById('btnFind').addEventListener('click', ()=> openFind(true));
document.getElementById('findCase').addEventListener('click', (e)=>{ findCase = !findCase; e.currentTarget.classList.toggle('active', findCase); refreshMatches(true); });
document.getElementById('findRegex').addEventListener('click', (e)=>{ findRegex = !findRegex; e.currentTarget.classList.toggle('active', findRegex); refreshMatches(true); });
// Keep highlights in sync while the doc changes with the find bar open.
editor.on('change', ()=>{ if(!suppressFindRefresh && findBar.style.display !== 'none') refreshMatches(true); });

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
  resetUndoHistory();
  updateWordCount(); setFileName(null); setFilePath(null); setDirty(false);
  host.reset();
  setStatus('New from ' + key + ' template');
}
document.querySelectorAll('#tplMenu .menu-panel button').forEach(b =>
  b.addEventListener('click', ()=> loadTemplate(b.dataset.tpl)));

updateWordCount();
