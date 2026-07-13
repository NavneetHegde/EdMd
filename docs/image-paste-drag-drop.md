# EdMd — Image Paste & Drag-and-Drop (design)

Status: **proposed** (not yet implemented). This document specifies the feature end-to-end
so it can be built in one pass, following the same "wire both sides of the bridge" contract
as the existing open/save/export actions.

## Context

EdMd edits Markdown in WYSIWYG mode (Toast UI Editor) but has no way to get an **image**
into a document. Today a user who copies a screenshot or drags a PNG onto the editor gets
nothing useful. This is the single biggest gap for note-taking and authoring: pasting a
screenshot and having it "just work" is the headline feature of every modern Markdown app.

**Goal:** paste an image from the clipboard (Ctrl+V) or drag-and-drop an image file onto the
editor, have EdMd persist the bytes and insert a Markdown image reference at the caret — with
no dialog in the common case. Match the app's existing dual-mode (desktop WebView2 / browser)
and single-file-per-tab model.

**Non-goals (v1):** image resizing/compression, alt-text prompts, re-linking images when a
document is renamed/moved, pasting images *out* of EdMd, remote-URL image download, and an
image gallery/manager. All are follow-ups.

---

## Where the bytes go

The core decision is **where a pasted image is written**, and it depends on whether the tab
has a path yet (mirroring how `save` vs. Save As already branch on an empty `path`).

| Tab state | Storage | Inserted reference |
| --- | --- | --- |
| **Saved** (has a `path`) | a sibling `assets/` folder next to the `.md` file | relative: `![](assets/img-20260712-153000-ab12.png)` |
| **Untitled** (no `path`) | inline **base64 data URI** (nothing to write next to) | `![](data:image/png;base64,…)` |

Rationale:

- **Relative `assets/` links** keep the document portable: the `.md` plus its `assets/`
  folder move/commit together, and the links render on GitHub, in the export
  (`docs/` sibling paths resolve), and in other editors. This is the de-facto convention.
- **Data URIs for untitled tabs** avoid writing stray files to a temp dir the user never
  chose. The image travels *inside* the buffer, so it also survives
  **[[session restore / crash recovery]]** (the snapshot already carries full tab content) and
  the **Copy** action. The cost is a larger buffer; acceptable for the untitled case, and the
  user can externalise later (see "First save of an untitled tab" below).

The `assets/` folder name is fixed for v1 (not per-document `#<name>.assets`), which is the
least surprising and matches common tooling. Collisions across sibling `.md` files sharing one
`assets/` folder are fine — filenames are content-addressed (below).

### Filenames

`img-<yyyyMMdd-HHmmss>-<8 hex of SHA-256(bytes)>.<ext>`, e.g. `img-20260712-153000-1a2b3c4d.png`.

- The **content hash** de-duplicates: pasting the same screenshot twice reuses one file, and
  the hash makes accidental overwrite of an unrelated file impossible.
- The **timestamp** keeps the folder human-sortable.
- `<ext>` is derived from the blob's MIME type via a fixed allowlist (below), never from any
  caller-supplied name.

---

## Capture: Toast's `addImageBlobHook`

Toast UI Editor already intercepts both **paste** and **drag-drop** of images through a single
option: `addImageBlobHook(blob, callback, source)`. When present, Toast calls it with the
image `Blob` and expects `callback(url, altText)` to insert `![altText](url)`. This is the one
integration point — no manual `paste`/`drop` listeners, no ProseMirror surgery.

In `makeEditor` (`app.js`):

```js
function makeEditor(containerEl){
  return new toastui.Editor({
    // …existing options…
    hooks: {
      addImageBlobHook: (blob, callback) => {
        // Route through the active tab so we know its path (assets dir vs. data URI).
        insertImage(activeTab(), blob, callback);
      },
    },
  });
}
```

`insertImage` is `async`: it resolves the blob to a URL (via the host), then calls
`callback(url, defaultAlt)`. If persistence fails it calls `setStatus(..., isError)` and does
**not** invoke the callback, so no broken `![]()` is inserted. Inserting via the callback is a
normal editor edit, so it flips the tab dirty, is undoable, and schedules a session snapshot —
all for free.

---

## Bridge messages

Two new messages, following the existing promise-based round-trip pattern (`pendingSaves`
keyed by `tabId`; see the save handshake). Image requests are keyed by a per-request id since a
tab can have several in flight.

### JS → C# (`saveImage`)

```
{ type: 'saveImage', reqId, docPath, ext, dataBase64 }
```

- `reqId` — a JS-generated integer; echoed back so the resolver map can route the reply.
- `docPath` — the active tab's on-disk path (**empty ⇒ untitled**; C# returns an error and JS
  falls back to a data URI, so untitled never hits disk).
- `ext` — the allowlisted extension JS derived from `blob.type` (C# re-validates).
- `dataBase64` — the image bytes, base64. (WebView2 `postMessage` is JSON/UTF-16; base64 keeps
  it a clean string. Size cap enforced on both sides — see Security.)

### C# → JS (`imageSaved`)

```
{ type: 'imageSaved', reqId, ok, relPath }     // ok:true  → insert ![](relPath)
{ type: 'imageSaved', reqId, ok:false, message } // ok:false → JS falls back to data URI (or shows the error)
```

`relPath` is the **relative** link to embed (e.g. `assets/img-….png`), computed by C# from the
document's directory — JS never sees or builds an absolute path.

The desktop `host` gains one method, and the JS `message` listener one branch, exactly like
`save`/`saved`:

```js
// desktop host
saveImage: (reqId, docPath, ext, dataBase64) =>
  send({ type: 'saveImage', reqId, docPath, ext, dataBase64 }),
```

```js
// message listener
else if(msg.type === 'imageSaved') resolveImage(msg.reqId, msg);
```

---

## Dual-mode behaviour

Per the CLAUDE.md rule, **any new file action is implemented on both host objects**.

- **Desktop (WebView2):** `saveImage` → C# writes to the `assets/` folder and returns the
  relative path. Untitled tab (`docPath` empty) → JS skips the bridge and embeds a data URI.
- **Browser (File System Access):** there is no C# and no arbitrary folder write. Two tiers:
  1. If the tab was opened via a **directory** handle (future enhancement), write into an
     `assets/` sub-handle. *Out of scope for v1.*
  2. Otherwise embed a **data URI** — identical to the untitled desktop path.

  So the browser build always uses a data URI in v1, which keeps behaviour correct (the image
  renders and saves inside the `.md`) without needing a directory grant.

Both paths funnel through the shared `insertImage(tab, blob, callback)` helper, so the caret
insertion and dirty/undo behaviour are identical — the same pattern as `openInTab` /
`applySavedToTab`.

---

## C# side (`MainWindow` + a testable `ImageStore`)

Keep the **pure, security-sensitive logic** in a separate `ImageStore.cs` so it is unit-tested
like `AtomicFile` / `SessionStore`, and keep only the disk write and bridge glue in
`MainWindow`.

`ImageStore` (pure, no I/O):

- `string? ExtensionForMime(string mime)` — the allowlist map (`image/png`→`png`,
  `image/jpeg`→`jpg`, `image/gif`→`gif`, `image/webp`→`webp`; **not** `svg` in v1, see Security).
  Returns null for anything else.
- `string BuildFileName(byte[] bytes, string ext, DateTime nowUtc)` — the
  `img-<ts>-<hash8>.<ext>` name.
- `string AssetsDirFor(string docPath)` — `Path.Combine(dir-of-docPath, "assets")`.
- `string RelativeLink(string assetsDir, string fileName)` — always `assets/<file>` with
  forward slashes (Markdown links use `/`).

`MainWindow.SaveImage(JsonElement msg)`:

1. Validate `docPath` non-empty and known (in `_docs`); else reply `ok:false` (→ data URI).
2. `ext = ImageStore.ExtensionForMime(mime)`; reject unknown → `ok:false`.
3. Decode base64; enforce the **size cap** (e.g. 25 MB) before allocating/writing.
4. `dir = ImageStore.AssetsDirFor(docPath)`; `Directory.CreateDirectory(dir)`.
5. `name = ImageStore.BuildFileName(bytes, ext, DateTime.UtcNow)`; if that file already exists
   with identical length, **reuse it** (content-addressed dedupe) — else
   `AtomicFile.WriteAllBytes(Path.Combine(dir, name), bytes)` (new binary sibling of
   `WriteAllText`, same temp-file+atomic-replace).
6. Reply `{ imageSaved, reqId, ok:true, relPath = ImageStore.RelativeLink(dir, name) }`.

All wrapped in try/catch → `ReportError` + `ok:false`, so any I/O failure degrades to a data
URI or a red status message, never a crash (the `async void` handler contract).

---

## First save of an untitled tab (follow-up, noted here)

When an untitled tab that contains **data-URI images** is saved to a real path for the first
time (`SaveAs`), those inline blobs could be *externalised* to the new document's `assets/`
folder and the data URIs rewritten to relative links, shrinking the `.md`. This is a nice
polish but **not required for v1** — a data URI in a saved file is valid Markdown and renders
everywhere. Flagged so the v1 implementation leaves a clean seam (do the rewrite in the JS
`applySavedToTab`, calling `saveImage` for each inline blob) rather than precluding it.

---

## Security

The WebView2 bridge can write to disk, and a `.md` can be untrusted, so binary writes get the
same scrutiny as the text path:

- **C# derives the target path** from the document's own directory (`_docs` key) — it never
  writes to a path supplied by JS. Same principle as "C# maps a `browserId` to a path from its
  own list, never launches a JS-supplied path."
- **Extension allowlist**, matched from the MIME type, not a filename. **SVG is excluded in
  v1**: an `.svg` can carry `<script>`; although an `<img src>` won't execute it, writing
  attacker-controlled SVG into the user's folder is needless risk. Revisit with sanitisation
  later.
- **Size cap** (e.g. 25 MB) enforced *before* the base64 decode allocates, to bound a hostile
  or accidental huge paste.
- **Content-addressed filenames** (hash) mean a write can't clobber an unrelated existing file,
  and can't be steered to a chosen name.
- **CSP already allows** `img-src … data: blob:`, so both the data-URI and the
  `EdMd.local`-served relative image render without a CSP change. No new origin is introduced.
- **`assets/` stays inside the document's directory** — no `..`, no absolute paths in the
  emitted link.

---

## Files to modify (summary)

- `src/EdMd/wwwroot/app.js` — `addImageBlobHook` in `makeEditor`; shared `insertImage(tab,
  blob, callback)`; base64 helper; `pendingImages` map + `resolveImage`; desktop `host.saveImage`
  and browser data-URI path; `imageSaved` branch in the `message` listener.
- `src/EdMd/MainWindow.xaml.cs` — `saveImage` case in the `OnWebMessageReceived` switch;
  `SaveImage(...)` method; `imageSaved` reply.
- `src/EdMd/ImageStore.cs` *(new)* — MIME→ext allowlist, filename builder, assets-dir + relative
  link helpers (pure, unit-tested).
- `src/EdMd/AtomicFile.cs` — add `WriteAllBytes(path, bytes)` (temp-file + atomic replace, the
  binary sibling of the existing text write).
- `src/EdMd.Tests/ImageStoreTests.cs` *(new)* — MIME allowlist (incl. SVG rejected), filename
  format + hash dedupe, relative-link slashes, unknown-MIME rejection.
- `CLAUDE.md` — document the `saveImage` / `imageSaved` bridge messages and this flow.

Reuse existing: the `pendingSaves`/`resolvePending` promise pattern, `openInTab` /
`applySavedToTab` insertion helpers, `AtomicFile` write discipline, `_docs` for the known path,
and the `ReportError` degrade-don't-crash path.

---

## Verification

1. **Build/tests**: `dotnet build` (0 warnings) and `dotnet test` (new `ImageStore` tests pass);
   `node --check app.js`.
2. **Paste into a saved doc**: open a `.md`, copy a screenshot, Ctrl+V → an `assets/img-….png`
   appears next to the file and `![](assets/…)` renders inline; the tab goes dirty; Save writes
   only the `.md` (the image is already on disk).
3. **Drag-and-drop**: drag a PNG from Explorer onto the editor → same result.
4. **Paste into an untitled tab**: Ctrl+V → a data-URI image renders; Save As writes a valid
   `.md` that still shows the image.
5. **Dedupe**: paste the same image twice → one file in `assets/`, two references.
6. **Unsupported type**: paste an `.svg` or a non-image → no file written, a clear status
   message, no crash.
7. **Oversize**: paste a >25 MB image → rejected with a status message, no partial file.
8. **Undo**: after a paste, Ctrl+Z removes the image reference cleanly.
9. **Browser mode** (`serve.ps1`): paste → data-URI image renders and saves inside the `.md`.
10. **Session restore**: paste into an untitled tab, kill the app, relaunch → the image is still
    there (it lived in the buffer snapshot).
