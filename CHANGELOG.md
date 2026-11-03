# Changelog

Newest first. The current version is in `LocalDiskBrowser.csproj` and at the top of `README.md`.

### 2.1.7
Fix: commenting / uncommenting a selection in the SQL editor (Ctrl+/, Ctrl+K C / U) left out the last selected line.

### 2.1.6
Fix: SQL editor comment / uncomment chords now also work with Ctrl held for both keys (Ctrl+K, Ctrl+C / Ctrl+K, Ctrl+U), as in SSMS. Before, only Ctrl+K then a plain C / U worked.

### 2.1.5
SQL page: the database drop-down has the same height and look as the buttons next to it.

### 2.1.4
SQL page: the button line (database drop-down, Add database, Update, Forget, Run, Cancel, Excel, CSV) is right aligned.

### 2.1.3
SQL page: the row limits are now shown to users: a permanent "Limits" note in the hint bar (grid rows / Excel-CSV rows, from `SqlEditor:DisplayRows` / `ExportRows`), and the note under a truncated result now says which limit applied and how to get the rest.

### 2.1.2
SQL page: the database drop-down and the Add database / Update / Forget / Run / Cancel / Excel / CSV buttons now sit on their own line under the server info.

### 2.1.1
Fix: Ctrl+K in the SQL editor no longer opens the browser's address-bar search (it is the start of Ctrl+K C / U).

### 2.1.0
New: SQL editor shortcut **Ctrl+/** toggles comments on the selected lines (or the current line): uncomments when all are already commented, otherwise comments. Ctrl+K C / U still work.

### 2.0.0
Breaking: **SQL Server connections are entered in the page, not in `appsettings.json`.** *Add database* takes a connection string, encrypts it in the browser (AES-GCM + RSA, so Cloudflare / proxies / logs never see it), the server tests it and stores it encrypted at rest (`App_Data/sqlconn`, Data Protection + DPAPI) under a random key. The browser keeps only that key and sends it with every call; several databases can be saved and switched in a drop-down, *Update* replaces a string, *Forget* deletes it. Each connection is tied to the user that saved it; a changed client IP only shows a warning. Replay protection (timestamp + one-time nonce), failed-key rate limit, `AttachDbFilename` rejected, optional `SqlEditor:AllowedServers`. `SqlEditor:ConnectionString` is no longer read: remove it from `appsettings.json` and add the database again in the SQL page. Keep `App_Data` when deploying.

### 1.5.2
Fix: running a query in the SQL editor while the results section is hidden (Ctrl+R) now shows it again.

### 1.5.1
SQL editor: autocomplete now pops up while typing (after a dot, or once a word has 2+ characters; not inside comments or strings). Ctrl+Space still opens it manually. (Also includes the 1.4.1 column-name fix below.)

### 1.5.0
New: **Pin in Recent**. Hover a recent folder or file and click the pin icon to keep it at the top of the Recent list. Pinned items are never trimmed by the 20-item limit and survive "Clear"; click the pin-off icon to unpin.

### 1.4.1
Fix: SQL editor autocomplete (Ctrl-Space, and after typing a dot) now suggests column names. It reads the tables named after FROM / JOIN / UPDATE / INTO in the query, loads their columns once (cached), and offers them for `alias.` / `table.` and unqualified.

### 1.4.0
New: **SQL Server editor**. The SQL Server logo button after Refresh opens its own page: server / IP / database shown at the top, query editor with F5, session tabs, GO batches, result sets with copy, Excel and CSV download, and explorer tabs for Tables, Stored Procedures, Views, Table Types, Functions and SQL Agent Jobs. All statements are allowed (risky ones ask first). The connection string is read from `SqlEditor:ConnectionString` in `appsettings.json` and never leaves the server. Every run is logged to `logs/sql-audit.log`. New package: Microsoft.Data.SqlClient.
Repo: `appsettings.json` is no longer in git (it now holds the connection string); `appsettings.sample.json` is the template. `appsettings.Development.json` removed (it only repeated the logging levels).

### 1.3.7
The changelog moved from `README.md` to this file (`CHANGELOG.md`).

### 1.3.6
Docs: the example server name in `AGENTS.md` is now a placeholder (`your-server.example.com`).

### 1.3.5
The login-page ASCII heading is also shown at the top of this README.

### 1.3.4
Fixed the login-page ASCII heading: a literal `<` in the art (`</ `) was read as HTML and swallowed part of it. It is now written as `&lt;`.

### 1.3.3
Login page: the "Local Disk Browser" heading is now ASCII art (plain text in a `<pre>`, no image).

### 1.3.2
Icons switched from Bootstrap Icons to [Lucide](https://lucide.dev/) (ISC licence, local font copy in `wwwroot/lib/lucide`, no internet needed). Same colours and same places; Lucide has no PDF/Word glyph, so PDF, Word and text files share the file-text icon and differ by colour.

### 1.3.1
Colourful icons: every toolbar / right-click action has its own colour (`UI_COLORS` in `browser.js`), as do the sidebar headers and the editor buttons. File icons were already coloured by type.

### 1.3.0
Icons are now [Bootstrap Icons](https://icons.getbootstrap.com/) (MIT, local copy in `wwwroot/lib/bootstrap-icons`, no internet needed) instead of emoji: toolbar, right-click menu, file list (coloured by type), sidebar, Recent list, editor buttons. Looks the same on every computer and browser.

### 1.2.3
The yellow status-bar highlight now covers every long-running operation and its result: search, zip, extract, download (`Preparing zip...`, `Download started`) and upload, not only upload progress. Errors stay red; routine messages (item counts, copy/cut) stay plain.

### 1.2.2
Upload progress in the status bar is highlighted (yellow background, bold blue) and now reads `Uploading 1/1 - 45% - file name`: count and percentage come first, so a long file name can no longer push them out of view.

### 1.2.1
When Cloudflare blocks a request (its HTML block page, `Server: cloudflare`), the error now says "Blocked by Cloudflare. Connect to the VPN and try again." and a permanent yellow note after the Refresh button tells users to connect to the VPN if actions are blocked by Cloudflare.

### 1.2.0
New: one *Recent* list (folders and files together) in the sidebar, saved in the browser's localStorage (max 20, Clear link).

### 1.1.4
Logout icon changed from the door (🚪) to a lock (🔒).

### 1.1.3
Toolbar icons are at full opacity and fade on hover (instead of the reverse). The browser's native title tooltip is replaced by a custom dark tooltip (action name and shortcut) drawn by CSS from `data-tip`.

### 1.1.2
Toolbar buttons are now icon only, with no button box. Hovering raises the icon's opacity and shows a tooltip with the action name and its shortcut. Disabled buttons are faded. Dialog buttons keep their text.

### 1.1.1
Buttons (toolbar, dialogs, login) restyled: white background, thin light-grey border, rounded corners, blue hover highlight, greyed-out when disabled. CSS only (`wwwroot/css/browser.css`).

### 1.1.0
"Remember me" checkbox on the login page. When ticked, the login cookie is persistent (1 day); otherwise it is a session cookie as before.

### 1.0.7
Removed the "upload or paste?" question added in 1.0.6. Pasting files copied on the user's computer always uploads, exactly as before, whatever was copied inside the app earlier. In-app Copy/Cut still replace the computer's clipboard (https), so Ctrl+V after an in-app Copy pastes on the server instead of uploading stale files.

### 1.0.6
Fixed Ctrl+V uploading old files by mistake. If files had been copied earlier on the user's computer (Explorer), pressing Ctrl+V after an in-app Copy/Cut uploaded those files instead of pasting the selection. In-app Copy/Cut now replace the computer's clipboard content (needs https), and Ctrl+V pastes the in-app selection. If both exist and the browser could not replace the clipboard (plain http), the user is asked whether to upload or paste. The status bar says that in-app Copy/Cut is server-side and uploads nothing.

### 1.0.5
Icons on the toolbar buttons, the right-click menu, the Drives header, the Close button and the editor's Save / Word wrap buttons, in the same emoji style as the file-type icons. The icon of an action is the same everywhere it appears.

### 1.0.4
Only one "Delete" item (menu and toolbar), no "(permanent)" label and no separate "Delete permanently". Where there is no Recycle Bin (IIS) the confirmation still says the delete is permanent. Shift+Delete still deletes permanently when a Recycle Bin exists.

### 1.0.3
When there is no Recycle Bin (IIS), "Delete" and "Delete permanently" were identical. Now only one item is shown, named "Delete (permanent)" (menu and toolbar button).

### 1.0.2
Loading indicator: while any request is running the status bar shows a spinner, what is happening (Loading folder, Deleting, Copying, Zipping, Extracting, ...) and the elapsed seconds, with a "server or network is slow" note after 10 s; the mouse cursor shows "busy". Fast requests (under 0.3 s) do not flash it. Uploads keep their own percentage display.

### 1.0.1
- Errors are always shown in a pop-up with the full message **and** as red text in the status bar (hover for the full text). Previously the list refresh that follows an action overwrote the red message within a moment, so permission errors seemed to show nothing; the refresh now keeps the message.
- **Delete fix for IIS:** deletes no longer go through the Windows shell when the app runs as a service (IIS), where the shell could show an "administrator permission" prompt on the server's screen and fail silently. Under IIS deletes are now permanent (the Recycle Bin is only used when running interactively with `FileOperations:UseRecycleBin` = true); the confirmation text says so. Read-only items are deleted after confirmation.
- A delete is only reported as successful if the item is really gone. Failures show the exception type, Windows error code, and the account the app runs as (to diagnose permission problems).

### 1.0.0
First versioned release. Version number added to the login page. Includes everything listed under Features.
