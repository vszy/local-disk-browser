# AGENTS.md

Instructions for AI coding agents working on this repository. Read this first.

## What this is

Local Disk Browser: an ASP.NET Core 8 MVC web file manager for the disks of the machine it runs on. Plain UI, function over looks.
Deployed to IIS (e.g. `https://your-server.example.com/Deployer/`, hosted as a **sub-application**). See `README.md` for features and layout.

## Mandatory rules for EVERY change

1. **Bump the version** in `LocalDiskBrowser.csproj` (`<Version>`): patch (x.y.Z) for fixes, minor (x.Y.0) for new features, major for breaking changes. The login page shows it automatically (`Models/AppInfo.cs`).
2. **Update `README.md`**: the "Current version" line and the Features / Configuration sections if behaviour changed. **Add an entry at the top of `CHANGELOG.md`** (newest first) describing the change.
3. **Update this file** if you add a convention, a gotcha, or change the structure.
4. Build (`dotnet build`) and, where possible, test against the running app before saying a change is done. Say plainly what was and was not tested (UI behaviour cannot be clicked through from the terminal).

## Build / run / test

- Run: `dotnet run` (http://localhost:5195, login `Admin` / `Admin@123`).
- Publish: `dotnet publish -c Release`.
- Check JS syntax: `node --check wwwroot/js/browser.js` (and `editor.js`).
- API tests: start the app, log in with a cookie jar (the login form needs the antiforgery token from `GET /Account/Login`), then call `/api/fs/*` with header `X-Requested-With: LocalDiskBrowser`. Use scratch folders only. Wait until the server answers before testing (startup takes a few seconds). Stop the `LocalDiskBrowser` process afterwards.
- In Git Bash, `MSYS_NO_PATHCONV=1` is needed when passing values that start with `/` (e.g. `PathBase=/ldb`) to programs.

## Architecture notes

- **Backend:** `Controllers/FsController.cs` is the whole API (`/api/fs/...`): drives, list, search, delete, copy, move, rename, mkdir, newfile, zip, extract, props, attrs, download, text/save (editor), upload-chunk, zipdl (prepare + get). Errors are returned as JSON `{ error }`; multi-item operations return 207 with `errors[]`.
- **Frontend:** plain JavaScript, no build step. `browser.js` (file list) and `editor.js` (CodeMirror 5, local copy in `wwwroot/lib/codemirror`). No CDN: the target server may have no internet.
- **Auth:** cookie auth, fallback policy requires login for everything except `[AllowAnonymous]` (login). Credentials in `appsettings.json` (`Auth:*`). API calls get 401 (not a redirect); the JS then goes to the login page.
- **Request guard** (`Program.cs`): Host header must be localhost or listed in `Security:AllowedHostNames` (`*` = any); non-GET `/api` calls must carry `X-Requested-With: LocalDiskBrowser` (CSRF defence). Every new JS call to the API must send that header (`api()` in `browser.js` does).

## Gotchas learned (do not repeat these mistakes)

- **Sub-application hosting:** the app runs under `/Deployer`. Never use root-absolute URLs like `/api/...` in JS or `Redirect("/")` in C#. JS uses `BASE` (from `<meta name="app-base">`, set with `Url.Content("~/")`); C# uses `Url.Content("~/")`. Razor tag helpers (`asp-*`, `~/`) already handle it.
- **Double-click:** do not re-render the whole table on a single click; the clicked `<tr>` gets detached and `dblclick` is lost. Selection changes use `refreshSel()` (class toggling only).
- **Server vs client:** the app runs on the server, the user is remote. Never launch programs / open Explorer on the server (`Process.Start`): it is useless remotely and a remote-code-execution risk. "Open" = view in the user's browser tab (`download?inline=true`) or download.
- **Serving user files:** files can be hostile. Inline responses send `nosniff` and a `sandbox` CSP (except PDF); `.html/.xml` are served as `text/plain`, never rendered.
- **Deleting:** never go through the Windows shell / `Microsoft.VisualBasic.FileIO` under IIS: it cannot show prompts to a service and fails silently. Permanent delete uses plain `System.IO` (`DeletePermanently`); the Recycle Bin is only for interactive runs. A delete must verify the item is really gone before reporting success. Errors include exception type, HRESULT and the account the app runs as, because most failures are app-pool permissions (see README, IIS section).
- **Icons:** Lucide icon font (local copy in `wwwroot/lib/lucide`, linked in `Index.cshtml` and `Editor.cshtml`; no CDN). No emoji and no image files, except the SQL Server logo button (`wwwroot/img/sqlserver.jpg`, `#bSql`, a plain `<img>`). Use the `ic(name, color)` helper in `browser.js` (name = Lucide icon name without the `icon-` prefix, see lucide.dev/icons). File-type icons: `ICONS` / `iconFor()` (`FOLDER_ICON`, `DRIVE_ICON`, `FILE_ICON`). Action icons: `UI_ICONS` (icon names) and `UI_COLORS` (same keys, one colour each) in `browser.js`, rendered with `uiIcon(key)`; the single source for toolbar buttons (`data-ico="key"` in `Index.cshtml`) and the right-click menu. A new button or menu item must get an icon from `UI_ICONS`, and the same action uses the same icon everywhere. Static text icons in views are `<i class="icon-NAME"></i>`. Toolbar buttons (`#toolbar`, `#actions`) are icon only: the loop that adds the icon moves the button's text into a hidden `.blabel` span and into `data-tip` (custom CSS tooltip; `title` is removed, so no native tooltip), so give every new toolbar button a text label and, if it has a shortcut, a `title="Key"`.
- **Recent (folders + files):** client-only, one list in `localStorage` key `ldb.recent` (array of `{k:'dirs'|'files', p:path, pin?:true}`, newest first, max 20 unpinned; pinned entries are shown first, never trimmed, and kept by Clear, see `togglePin()`), see `addRecent()` / `renderRecent()` in `browser.js`. Folders are recorded in `nav()` only when `push` is true (so Back/Forward/Refresh do not reorder); files in `openItem()`, `editSel()` and the single-file `download()`. Any new "open a file" path should call `addRecent('files', path)`. `nav()` returns true/false so a dead recent folder can be removed.
- **Cloudflare block:** the site sits behind Cloudflare, which blocks some networks (HTML page, header `Server: cloudflare`, not our JSON). `api()` and `sendChunk()` detect it via `cfBlocked()`, which builds the error text; the permanent `#cfHint` note after the Refresh button (always visible, "connect to the VPN") is plain HTML. Any new raw `fetch`/XHR to the API should do the same check.
- **SQL Server editor:** `/Sql` page (`Views/Sql/Index.cshtml`, `sql.js`, `sql.css`) + `Controllers/SqlApiController.cs` (`/api/sql/*`). Connections are entered by the user: the browser encrypts the string (WebCrypto: AES-256-GCM, key wrapped with the server's RSA public key from `GET api/sql/pubkey`, login name as authenticated data, timestamp + one-time nonce against replay) and POSTs it to `api/sql/save` ONLY; the server tests it and stores it encrypted (Data Protection) in `App_Data/sqlconn/<sha256(key)>.json` (`Services/SqlConnStore.cs`). The browser keeps only the random key (`localStorage` `ldb.sql.conns`) and sends `conn: key` with every SQL call (`api()` in `sql.js` adds it). The connection string is never returned, logged or audited; keep it that way. A new SQL endpoint must call `Use(req.Conn)` first (key + logged-in user must match). The client IP (`CF-Connecting-IP` / `X-Forwarded-For`, header can be forged) is stored with the connection and only produces a warning, never a block. `App_Data` is git-ignored and excluded from publish. It runs arbitrary SQL by design (user decision), so keep: every run audited to `logs/sql-audit.log`, row caps, per-batch timeout, the confirm prompt for UPDATE/DELETE without WHERE / TRUNCATE / DROP. Each run opens a new connection (no session state between runs, `USE db` only lasts one run). The last result is cached in `IMemoryCache` for the Excel/CSV download (`export?token=`); the xlsx is written by hand in `SqlApiController.Xlsx`, no library. Explorer queries use the `sys.*` catalog (SQL 2012+ compatible, no STRING_AGG).
- **SQL profiler:** the editor's Profiler button opens `/Sql/Profiler` in a new tab (`window.open`, connection key in the URL `#hash` so it never reaches a server log; the page sends it only in request bodies). `Services/SqlProfilerService.cs` creates one Extended Events session per tab (`ldb_prof_<guid>`, `sql_batch_completed` + `rpc_completed`, ring buffer, SQL 2012+, not Azure SQL DB) and `api/sql/prof/start|poll|stop` (in `SqlApiController`, each calls `Use()`) drive it; the page polls every 2 s. Cleanup layers, keep all of them: Stop button, `pagehide` -> `fetch` with `keepalive:true` (NOT `sendBeacon`: it cannot send the `X-Requested-With` header), a 120 s lease (background tabs are throttled to one timer tick per minute) checked every 15 s by the hosted service, drop at app shutdown, and at next start via protected markers in `App_Data/profiler`. Starting again for the same user + connection replaces the old session (reload-safe). Captured SQL is returned only to the user who started it and is never logged or audited (audit has start/stop only). Profiler connections use `ApplicationName=LDB-Profiler` so the session ignores its own queries.
- **Settings file:** `appsettings.json` is git-ignored (real passwords and the SQL connection string). Any new setting must also go into `appsettings.sample.json` (placeholders only, never real values). Never print or log the connection string.
- **Clipboard:** in-app Copy/Cut/Paste is server-side (paths only, nothing uploaded). Ctrl+V uploads whenever the computer's clipboard holds files, with no confirmation (pasting from the user's own computer must never be blocked or questioned). `setClip()` overwrites the system clipboard with `CLIP_MARKER` so stale Explorer files are never uploaded by mistake; keep that behaviour when touching paste/copy code.
- **Busy indicator:** every API call must go through `api()` in `browser.js`, which drives the status-bar spinner (`busyOn/busyOff`). Add a label for new endpoints in `BUSY_LABELS`. Uploads use XHR chunks with their own progress text.
- **Status bar:** `status(msg, isErr, hl)`: pass `hl = true` for messages about operations the user waits for or just finished (yellow highlight, class `up`); errors are red and win over `hl`. `reload()` restores the previous status text (and its highlight) afterwards. Do not let a list refresh overwrite an error message.
- **Large uploads:** do not use one big multipart request (IIS/proxy limits). Uploads use 4 MB chunks (`upload-chunk`). `web.config` sets the IIS request limit.
- **Archives:** `.zip/.tar/.gz` use .NET; `.rar/.7z` use SharpCompress. Every entry path is checked to stay inside the target folder; existing files are not overwritten unless the user confirms (`overwrite`).
- **Editor:** content is sent with `\n`; the server restores the file's original line endings and encoding (BOM / UTF-16 / Windows-1252). Saves check a version (last-write ticks) to detect concurrent changes.
- **IIS deploy:** the DLL is locked while the app pool runs; stop the pool (or use `app_offline.htm`) before copying. Browsers cache JS, so users need Ctrl+F5.
- Do not commit real passwords; the defaults in `appsettings.json` must be changed on the server.

## Server permissions (IIS app pool `Deployer`)

The app can only delete / write what the app pool account may. Run on the **server** in an **administrator** Command Prompt:

```
icacls "D:\" /grant "IIS AppPool\Deployer:(OI)(CI)M" /C /Q
```

Gives Modify on the whole D: drive, inherited by everything below. No `/T`, so it is fast; `/C` continues past errors, `/Q` is quiet.
Only if some folders still fail (their inheritance is turned off), run once, off-hours, because it is slow and makes the server lag on big drives:

```
icacls "D:\" /grant "IIS AppPool\Deployer:(OI)(CI)M" /T /C /Q
```

For a folder owned by another user: `takeown /F "D:\Folder" /R /D Y`, then repeat the `icacls` for that folder.
"Access is denied" lines for `System Volume Information` and `$RECYCLE.BIN` are normal.
Granting Modify on the whole drive is a deliberate decision: anyone who logs in can change anything on D:, so keep HTTPS on and change the default password.

## Style

- Match the existing code: small functions, short comments explaining *why*, no frameworks added to the frontend, no fancy styling.
- Prefer the simplest thing that works; this tool is about function.
