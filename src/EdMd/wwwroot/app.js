// All of EdMd's front-end logic. Kept as an external file (rather than an inline
// <script> in index.html) so the page's Content-Security-Policy can be script-src 'self':
// with no inline script, any script injected via a malicious .md document is refused by
// the browser even if it slips past Toast's sanitizer. Loaded at the end of <body>, after
// the DOM and the vendored Toast bundle, so element/global lookups below are safe.

const dirtyDot  = document.getElementById('dirtyDot');
const statusMsg = document.getElementById('statusMsg');
const wordCount = document.getElementById('wordCount');
const filePath  = document.getElementById('filePath');
const tabList   = document.getElementById('tabList');
const editorHost = document.getElementById('editorHost');

// ---- Tab model --------------------------------------------------------------------------
// The document model lives here in JS: one record per open file/tab, each owning its own
// Toast editor instance (so undo history, scroll and cursor are preserved per tab). C# only
// mirrors an aggregate dirty flag and, for saved files, the encoding/newline/timestamp — it
// treats `tabId` as an opaque echo token on save results. Each record:
//   { id, name, path, dirty, editor, el, fileHandle }
// `fileHandle` is the browser build's File System Access handle (null in the desktop app).
const tabs = [];
let activeId = null;
let nextTabId = 1;

const tabById = (id) => tabs.find(t => t.id === id);
const activeTab = () => tabById(activeId);
const activeEditor = () => { const t = activeTab(); return t ? t.editor : null; };
const anyDirty = () => tabs.some(t => t.dirty);
const findTabByPath = (p) => (p ? tabs.find(t => t.path === p) : null);
// A pristine untitled tab can be reused when opening a file, so opening into a fresh window
// doesn't leave an empty tab behind (Notepad-style).
const isReusableEmpty = (t) => !!t && !t.path && !t.dirty && t.editor.getMarkdown().trim() === '';

function makeEditor(containerEl){
  return new toastui.Editor({
    el: containerEl,
    height: '100%',
    initialEditType: 'wysiwyg',   // inline, formatted editing — no separate preview pane
    previewStyle: 'tab',
    hideModeSwitch: true,          // lock to WYSIWYG; remove this line to allow a raw-markdown tab too
    usageStatistics: false,
    placeholder: 'Open a markdown file, or just start typing…'
  });
}

// Build a tab (editor + strip chip) but do not activate it — callers do that once it's filled.
function createEmptyTab(){
  const id = nextTabId++;
  const el = document.createElement('div');
  el.className = 'tabEditor inactive';
  editorHost.appendChild(el);
  const editor = makeEditor(el);

  const tab = { id, name: '', path: '', dirty: false, editor, el, fileHandle: null };
  editor.on('change', () => {
    setDirtyTab(tab, true);
    if(tab.id === activeId) updateWordCount();
  });
  tabs.push(tab);
  buildChip(tab);
  applyThemeToEditor(tab);
  applyModeToEditor(tab);
  return tab;
}

// Load content into a tab without marking it dirty. setMarkdown fires a synchronous `change`
// (which flips the tab dirty); we clear that immediately after — the same pattern the
// single-document build used.
function loadContent(tab, content){
  tab.editor.setMarkdown(content || '', false);
  setDirtyTab(tab, false);
  if(tab.id === activeId) updateWordCount();
}

// Open a file into a tab: focus it if that path is already open, else reuse a pristine empty
// tab, else make a new one. Used by desktop `fileOpened`, browser open, and the ?session handoff.
function openInTab(name, path, content, fileHandle){
  const existing = findTabByPath(path);
  if(existing){ activateTab(existing.id); setStatus('Already open: ' + name); return existing; }
  const tab = isReusableEmpty(activeTab()) ? activeTab() : createEmptyTab();
  tab.name = name || '';
  tab.path = path || '';
  tab.fileHandle = fileHandle || null;
  loadContent(tab, content);
  refreshChip(tab);
  activateTab(tab.id);
  if(name) setStatus('Opened ' + name); // untitled (e.g. a template) sets its own status
  return tab;
}

// Always create a brand-new empty tab (the New button and the + button).
function newTab(){
  const tab = createEmptyTab();
  loadContent(tab, '');
  refreshChip(tab);
  activateTab(tab.id);
  return tab;
}

function activateTab(id){
  const tab = tabById(id);
  if(!tab) return;
  activeId = id;
  for(const t of tabs){
    t.el.classList.toggle('inactive', t.id !== id);
    if(t.chip) t.chip.classList.toggle('active', t.id === id);
  }
  // Footer reflects the active tab.
  setFilePath(tab.path || tab.name || '');
  dirtyDot.classList.toggle('show', tab.dirty);
  updateWordCount();
  tab.editor.focus();
}

function closeTab(id){
  const tab = tabById(id);
  if(!tab) return;
  if(tab.dirty && !confirm('Discard unsaved changes to "' + (tab.name || 'untitled') + '"?')) return;

  const idx = tabs.indexOf(tab);
  tab.editor.destroy();
  tab.el.remove();
  if(tab.chip) tab.chip.remove();
  tabs.splice(idx, 1);

  // Never leave zero tabs open — keep one empty tab like Notepad.
  if(tabs.length === 0){ newTab(); }
  else if(activeId === id){ activateTab(tabs[Math.min(idx, tabs.length - 1)].id); }

  // Let C# drop this file's cached encoding/timestamp so _docs doesn't grow across a session.
  if(host && host.tabClosed && tab.path) host.tabClosed(tab.path);
  if(host && host.dirtyChanged) host.dirtyChanged(anyDirty());
}

// ---- Tab strip chips --------------------------------------------------------------------
function buildChip(tab){
  const chip = document.createElement('div');
  chip.className = 'tab';
  const dot = document.createElement('span'); dot.className = 'tab-dot';
  const name = document.createElement('span'); name.className = 'tab-name';
  const close = document.createElement('button'); close.className = 'tab-close'; close.textContent = '×'; close.title = 'Close tab';
  chip.append(dot, name, close);
  chip.addEventListener('click', () => activateTab(tab.id));
  chip.addEventListener('auxclick', (e) => { if(e.button === 1){ e.preventDefault(); closeTab(tab.id); } }); // middle-click closes
  close.addEventListener('click', (e) => { e.stopPropagation(); closeTab(tab.id); });
  tab.chip = chip; tab.chipDot = dot; tab.chipName = name;
  tabList.appendChild(chip);
  refreshChip(tab);
}
function refreshChip(tab){
  if(!tab.chip) return;
  tab.chipName.textContent = tab.name || 'untitled';
  tab.chipName.title = tab.path || tab.name || 'untitled';
  tab.chipDot.classList.toggle('show', tab.dirty);
  tab.chip.classList.toggle('active', tab.id === activeId);
}

// ---- Dirty / status / footer ------------------------------------------------------------
// Only fires on a real transition, so we don't spam the C# bridge on every keystroke.
function setDirtyTab(tab, v){
  v = !!v;
  if(v === tab.dirty) return;
  tab.dirty = v;
  if(tab.chipDot) tab.chipDot.classList.toggle('show', v);
  if(tab.id === activeId) dirtyDot.classList.toggle('show', v);
  if(host && host.dirtyChanged) host.dirtyChanged(anyDirty());
}

// Local, offline token estimate (~4 chars/token rule of thumb). Good enough for context
// budgeting when authoring prompts/skills; an exact count would need a model tokenizer.
function estimateTokens(text){ return Math.max(0, Math.round(text.length / 4)); }
function updateWordCount(){
  const ed = activeEditor();
  const t = ed ? ed.getMarkdown() : '';
  const words = t.trim() ? t.trim().split(/\s+/).length : 0;
  const lines = t ? t.split(/\r\n|\r|\n/).length : 0;
  // Lead with the token estimate — it's the number that matters for AI prompts.
  wordCount.textContent = `~${estimateTokens(t)} tokens · ${words} words · ${t.length} chars`;
  wordCount.title = `${lines} lines · token count is an estimate (~4 chars/token)`;
}
function setStatus(msg, ms=2200, isError=false){
  statusMsg.textContent = msg;
  statusMsg.style.color = isError ? '#ff6b6b' : 'var(--accent)';
  if(ms) setTimeout(()=>{ if(statusMsg.textContent===msg) statusMsg.textContent=''; }, ms);
}
// Footer shows the full path when the host can supply one (desktop app), else the name.
function setFilePath(p){ filePath.textContent = p || 'No file open'; filePath.title = p || ''; }

// Update a tab after a successful save (name/path may change on a Save As).
function applySavedToTab(tab, name, path){
  tab.name = name || tab.name;
  tab.path = path || '';
  setDirtyTab(tab, false);
  refreshChip(tab);
  if(tab.id === activeId) setFilePath(tab.path || tab.name || '');
  setStatus('Saved ' + (name || tab.name));
}

// ---- Host abstraction: WebView2 bridge (desktop) OR File System Access API (Chrome) ----
// The same UI runs in two places: inside the WPF app's WebView2 (where C# owns disk
// I/O over postMessage) and in a plain Chromium tab (where the File System Access API
// gives real local open/save). We detect which and route open/save accordingly.
const IS_DESKTOP = !!(window.chrome && window.chrome.webview);

// Saves are async round-trips. Each save request registers a resolver keyed by tab id; the
// matching `saved` (ok) or `saveResult{ok:false}` reply resolves it. This lets the close
// handshake await every dirty tab's save and abort if a Save As is cancelled.
const pendingSaves = new Map();
function resolvePending(tabId, ok){
  const r = pendingSaves.get(tabId);
  if(r){ pendingSaves.delete(tabId); r(ok); }
}

let host;
if(IS_DESKTOP){
  // Desktop: C# owns the file dialogs and disk I/O; we talk over postMessage.
  const send = (payload)=> window.chrome.webview.postMessage(JSON.stringify(payload));
  // Ask C# to save a specific tab; resolves true (written) / false (cancelled or failed).
  // If a save for this tab is already in flight (e.g. rapid double-save), settle the old
  // resolver first so its promise never hangs before we replace it.
  const requestSave = (type, tab)=> new Promise((resolve)=>{
    resolvePending(tab.id, false);
    pendingSaves.set(tab.id, resolve);
    send({ type, tabId: tab.id, path: tab.path || '', name: tab.name || '', content: tab.editor.getMarkdown() });
  });
  window.chrome.webview.addEventListener('message', (event)=>{
    const msg = event.data; // WebView2 auto-parses JSON posted from C#
    if(msg.type === 'fileOpened') openInTab(msg.name, msg.path, msg.content);
    else if(msg.type === 'saved'){
      const t = tabById(msg.tabId);
      if(t) applySavedToTab(t, msg.name, msg.path);
      resolvePending(msg.tabId, true);
    }
    else if(msg.type === 'saveResult'){ resolvePending(msg.tabId, !!msg.ok); if(!msg.ok) setStatus('Save cancelled'); }
    else if(msg.type === 'error') setStatus(msg.message, 6000, true);
    else if(msg.type === 'requestSaveForClose') saveAllForClose();
    else if(msg.type === 'browsers') populateBrowserMenu(msg.list); // installed browsers for the dropdown
  });
  // Save every dirty tab in turn for the window-close handshake; on any cancel, abort the
  // close (don't send readyToClose) so the user keeps editing.
  async function saveAllForClose(){
    for(const tab of tabs.filter(t => t.dirty)){
      activateTab(tab.id); // surface which file a Save As dialog is for
      const ok = await requestSave('save', tab);
      if(!ok) return;
    }
    send({ type: 'readyToClose' });
  }
  host = {
    open:   ()=> send({ type: 'open' }), // C# opens each chosen file into its own tab
    save:   ()=>{ const t = activeTab(); if(t) requestSave('save', t); },
    saveAs: ()=>{ const t = activeTab(); if(t) requestSave('saveAs', t); },
    // Desktop-only: open the full editor in a browser, pre-loaded with the active doc.
    // browserId (optional) is an Id from the C#-supplied list; omitted = let C# auto-pick.
    openInBrowser: (browserId)=>{
      const t = activeTab(); if(!t) return;
      send({ type: 'openInBrowser', markdown: t.editor.getMarkdown(), name: t.name || '', path: t.path || '', browserId: browserId || '' });
      setStatus('Opening in browser…');
    },
    dirtyChanged: (v)=> send({ type: 'dirty', value: !!v }),
    tabClosed: (path)=> send({ type: 'tabClosed', path }), // drop C#'s cached meta for the file
    themeChanged: (dark)=> send({ type: 'theme', dark: !!dark }), // dark-mode the native title bar
  };
} else {
  // Chromium (Chrome/Edge): real local open/save via the File System Access API.
  // Needs a secure context — serve the folder over http://localhost (see serve.ps1).
  const pickerTypes = [{ description:'Markdown', accept:{'text/markdown':['.md','.markdown']} }];
  const noApi = ()=> alert('Local file access needs Chrome or Edge (File System Access API).');
  async function writeTo(handle, tab){
    const w = await handle.createWritable();
    await w.write(tab.editor.getMarkdown());
    await w.close();
  }
  host = {
    async open(){
      if(!window.showOpenFilePicker) return noApi();
      try{
        const handles = await window.showOpenFilePicker({ types: pickerTypes, multiple: true });
        for(const handle of handles){
          const file = await handle.getFile();
          openInTab(file.name, '', await file.text(), handle);
        }
      }catch(e){ if(e.name !== 'AbortError') setStatus('Open failed', 6000, true); }
    },
    async save(){
      const t = activeTab(); if(!t) return;
      if(!t.fileHandle) return host.saveAs();
      try{ await writeTo(t.fileHandle, t); applySavedToTab(t, t.fileHandle.name, ''); }
      catch(e){ if(e.name !== 'AbortError') setStatus('Save failed', 6000, true); }
    },
    async saveAs(){
      if(!window.showSaveFilePicker) return noApi();
      const t = activeTab(); if(!t) return;
      try{
        const handle = await window.showSaveFilePicker({ suggestedName: t.name || 'untitled.md', types: pickerTypes });
        t.fileHandle = handle;
        await writeTo(handle, t);
        applySavedToTab(t, handle.name, '');
      }catch(e){ if(e.name !== 'AbortError') setStatus('Save failed', 6000, true); }
    },
    dirtyChanged: ()=>{}, // browser tracks dirty locally; nothing to mirror
  };
  // "Open in Browser" is meaningless when we're already in the browser — hide the whole control.
  const bb = document.getElementById('browserMenu');
  if(bb) bb.style.display = 'none';

  // Ctrl+T is reserved by the browser (opens a browser tab), so we can't bind it here — drop
  // the shortcut hint that only holds in the desktop WebView2 host.
  const nt = document.getElementById('btnNewTab');
  if(nt) nt.title = 'New tab';

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
        openInTab(d.name, '', d.content);
        setStatus('Loaded ' + d.name + ' — Save writes via a file picker');
      })
      .catch(()=> setStatus('Could not load handed-off document', 6000, true));
  }
}

// Warn before leaving with unsaved edits — browser build only. In the desktop app the
// C# Window.Closing guard owns this; a beforeunload prompt there would double up.
if(!IS_DESKTOP){
  window.addEventListener('beforeunload', (e)=>{
    if(anyDirty()){ e.preventDefault(); e.returnValue = ''; }
  });
}

document.getElementById('btnOpen').addEventListener('click', ()=> host.open());
document.getElementById('btnSave').addEventListener('click', ()=> host.save());
document.getElementById('btnSaveAs').addEventListener('click', ()=> host.saveAs());
document.getElementById('btnNewTab').addEventListener('click', ()=> newTab());
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

document.getElementById('btnNew').addEventListener('click', ()=> newTab());

window.addEventListener('keydown', (e)=>{
  if((e.ctrlKey||e.metaKey) && e.key.toLowerCase()==='s'){
    e.preventDefault();
    host.save();
  }
  if((e.ctrlKey||e.metaKey) && e.key.toLowerCase()==='t'){ e.preventDefault(); newTab(); }
  if((e.ctrlKey||e.metaKey) && e.key.toLowerCase()==='w'){ e.preventDefault(); if(activeId!=null) closeTab(activeId); }
  if((e.ctrlKey||e.metaKey) && e.key.toLowerCase()==='f'){ e.preventDefault(); openFind(false); }
  if((e.ctrlKey||e.metaKey) && e.key.toLowerCase()==='h'){ e.preventDefault(); openFind(true); }
  if(e.key==='Escape' && findBar.style.display!=='none'){ e.preventDefault(); closeFind(); }
  if((e.ctrlKey||e.metaKey) && (e.key==='='||e.key==='+')){ e.preventDefault(); setZoom(zoom+10); }
  if((e.ctrlKey||e.metaKey) && e.key==='-'){ e.preventDefault(); setZoom(zoom-10); }
  if((e.ctrlKey||e.metaKey) && e.key==='0'){ e.preventDefault(); setZoom(100); }
});

// ---- Zoom (whole editor content area via CSS `zoom`, not the app chrome) ----
// Zoom is a global preference applied via a CSS variable, so it covers every tab's editor.
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

// Ctrl+scroll to zoom, without triggering the browser's own page-zoom
editorHost.addEventListener('wheel', (e)=>{
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
let currentTheme = THEMES[0];
const themeSelect = document.getElementById('themeSelect');
for(const t of THEMES){
  const opt = document.createElement('option');
  opt.value = t.id; opt.textContent = t.name;
  themeSelect.appendChild(opt);
}
// Toast UI's editor internals need its own dark stylesheet class for dark palettes; apply it
// to a single tab's editor root (used when a new tab is created).
function applyThemeToEditor(tab){
  const editorRoot = tab.el.querySelector('.toastui-editor-defaultUI');
  if(editorRoot) editorRoot.classList.toggle('toastui-editor-dark', currentTheme.dark);
}
function applyTheme(id){
  currentTheme = THEMES.find(t => t.id === id) || THEMES[0];
  document.body.dataset.theme = currentTheme.id;
  themeSelect.value = currentTheme.id;
  for(const tab of tabs) applyThemeToEditor(tab); // every tab's editor tracks the theme
  // Let the desktop host darken/lighten the native window title bar to match.
  if(host && host.themeChanged) host.themeChanged(!!currentTheme.dark);
  localStorage.setItem('EdMd-theme', currentTheme.id);
}
themeSelect.addEventListener('change', ()=> applyTheme(themeSelect.value));

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
  const ed = activeEditor();
  const doc = new DOMParser().parseFromString(ed ? ed.getHTML() : '', 'text/html');
  return (doc.body.textContent || '').replace(/\n{3,}/g, '\n\n').trim();
}
async function doCopy(kind){
  const ed = activeEditor();
  const text = kind === 'text' ? markdownAsPlainText() : (ed ? ed.getMarkdown() : '');
  const ok = await copyToClipboard(text);
  setStatus(ok ? `Copied ${kind === 'text' ? 'text' : 'markdown'} (${estimateTokens(text)} tokens)` : 'Copy failed', 2600, !ok);
}
document.getElementById('btnCopy').addEventListener('click', ()=> doCopy('md'));
document.querySelectorAll('#copyMenu .menu-panel button').forEach(b =>
  b.addEventListener('click', ()=> doCopy(b.dataset.copy)));

// ---- Raw markdown view toggle (Toast's markdown mode) ----
// A global preference, applied to every tab's editor so switching tabs stays consistent.
const btnRaw = document.getElementById('btnRaw');
let editMode = localStorage.getItem('EdMd-mode') === 'markdown' ? 'markdown' : 'wysiwyg';
function applyModeToEditor(tab){ tab.editor.changeMode(editMode, true); } // true = don't steal focus
function applyMode(mode){
  editMode = mode === 'markdown' ? 'markdown' : 'wysiwyg';
  for(const tab of tabs) applyModeToEditor(tab);
  btnRaw.classList.toggle('active', editMode === 'markdown');
  localStorage.setItem('EdMd-mode', editMode);
}
btnRaw.addEventListener('click', ()=> applyMode(editMode === 'markdown' ? 'wysiwyg' : 'markdown'));

// ---- Find / Replace (operates on the active tab's markdown; works in both modes) ----
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
  const ed = activeEditor();
  const m = (ed ? ed.getMarkdown() : '').match(re);
  findCount.textContent = (m ? m.length : 0) + ' matches';
}
function replaceAll(){
  const re = findPattern();
  if(!re || re === 'error') return;
  const ed = activeEditor();
  if(!ed) return;
  const md = ed.getMarkdown();
  const out = md.replace(re, replaceInput.value);
  if(out !== md){ ed.setMarkdown(out, false); updateWordCount(); if(activeTab()) setDirtyTab(activeTab(), true); setStatus('Replaced all'); }
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
  // Open the template in a fresh tab (or reuse a pristine empty one), never clobbering an
  // existing document.
  openInTab('', '', tpl);
  setStatus('New from ' + key + ' template');
}
document.querySelectorAll('#tplMenu .menu-panel button').forEach(b =>
  b.addEventListener('click', ()=> loadTemplate(b.dataset.tpl)));

// ---- Boot: one empty tab, then apply the persisted preferences ----
newTab();
applyTheme(localStorage.getItem('EdMd-theme') || 'nord');
setZoom(zoom);
applyMode(editMode);
updateWordCount();
