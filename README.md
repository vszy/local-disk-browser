# Local Disk Browser

```
 _                 _   ___  _    _
| |   ___  __ __ _| | |   \(_)__| |__
| |__/ _ \/ _/ _` | | | |) | (_-< / /
|____\___/\__\__,_|_| |___/|_/__/_\_\
 ___
| _ )_ _ _____ __ _____ ___ _ _
| _ \ '_/ _ \ V  V (_-</ -_) '_|
|___/_| \___/\_/\_//__/\___|_|
```

A plain, functional web-based file manager for the disks of the machine it runs on (ASP.NET Core 8 MVC).
Browse drives and folders, copy / move / delete, zip / extract, edit text files, upload and download, view properties.
No fancy design on purpose. Protected by a login.

**Current version: 2.4.0** (shown on the login page; defined in `LocalDiskBrowser.csproj`)

## Features

- **Drives:** every drive with label, type, filesystem and free/total space.
- **Browse:** address bar, Back/Forward/Up, sortable columns, multi-select (Ctrl/Shift), keyboard navigation, right-click menu, show/hide hidden items, recursive search in the current folder (`*` and `?` allowed). Double-click opens a folder. File icons depend on the extension.
- **Recent:** one list under *Drives* in the sidebar with the folders and files you used last (newest first, max 20, with *Clear*). Hover an item and click the pin icon to **pin** it: pinned items stay at the top, are not trimmed and survive *Clear*; click the pin-off icon to unpin. Folders are added when you open them, files when you open/view, edit or download them. Click to go back there; a folder that no longer opens is dropped. Stored in this browser only (`localStorage`), so it is per browser and per computer.
- **Copy / Cut / Paste (server side):** name clashes become `name - Copy.ext`. Folder-into-itself is blocked. Works across drives.
- **Delete:** Recycle Bin by default; Shift+Delete / "Delete permanently" removes for good (with confirmation).
- **Rename, New Folder, New File.**
- **Zip** the selection into the current folder.
- **Extract** (`.zip .rar .7z .tar .tar.gz .tgz .gz`): *Extract here* puts files straight into the archive's folder (no wrapper folder); *Extract to...* asks for a folder. Existing files trigger a "Replace?" prompt. Password-protected archives are refused.
- **Open / View** (double-click on a file, Enter): shows images, PDF, audio, video and text/code in a new browser tab; other types download.
- **Edit** (button, right-click, F4): CodeMirror 5 editor (bundled locally, no internet needed) with syntax highlighting, search, Ctrl+S. Keeps encoding (UTF-8, UTF-8 BOM, UTF-16, ANSI) and line endings; warns if the file changed on disk; refuses binary files and files over 5 MB.
- **Upload (your PC -> server):** Ctrl+V of files copied on your computer, drag-and-drop (files and folders), *Upload Files* / *Upload Folder*. Sent in 4 MB chunks so proxy / IIS request-size limits do not apply. Never overwrites (adds `- Copy`).
- **Download (server -> your PC):** one file downloads directly; folders or several items download as one `.zip`.
- **Properties:** size (folders are counted recursively), counts, dates, attributes; toggle Read-only / Hidden.
- **SQL Server editor:** the SQL Server logo button after *Refresh* opens a separate page. It shows the server, its IP and the database (never the password), then offers: a query editor (F5 runs the selection or everything; `GO` splits batches; all statements are allowed, risky ones - UPDATE/DELETE without WHERE, TRUNCATE, DROP - ask first), several session tabs, result sets with copy cell/row/column, **Excel and CSV download**, and explorer tabs for **Tables, Stored Procedures, Views, Table Types, Functions and SQL Agent Jobs** (definitions, CREATE script, columns/keys/indexes). Shortcuts: F5, Ctrl+3 / Ctrl+4 (top 500 / top 1 of the selected table), Ctrl+5 (column info), Alt+F1 / Ctrl+F1 (open the selected table / SP-view-function), Ctrl+R (toggle results), Ctrl+/ (toggle comment), Ctrl+K C / U (comment / uncomment), Ctrl+Space (autocomplete; it also pops up while typing). **Databases:** click *Add database* and paste a connection string (encrypted in the browser, tested, saved encrypted on the server); pick between saved databases in the drop-down, *Update* replaces the string, *Forget* deletes it. A warning shows when a saved database is opened from a different client IP than last time (warning only).
- **Login:** username + password from `appsettings.json`. "Remember me" keeps you logged in for 1 day, even after closing the browser; without it the login ends when the browser closes (or after 8 h idle).

Not possible in a browser: putting server files on your computer's clipboard (use Download instead).

## Run locally

```
dotnet run
```
then open http://localhost:5195 and log in (default `Admin` / `Admin@123`).

`appsettings.json` is **not in git** (it holds passwords and the SQL connection string). On a fresh clone copy `appsettings.sample.json` to `appsettings.json` and edit it.

## Configuration (`appsettings.json`)

| Key | Meaning |
|---|---|
| `Auth:Username`, `Auth:Password` | Login credentials (plain text). **Change before publishing.** Restart the app after changing. |
| `FileOperations:UseRecycleBin` | `true` (default): "Delete" uses the Recycle Bin when the app runs interactively. Under IIS (a service) the Recycle Bin is not used; deletes are permanent. |
| `Security:AllowedHostNames` | Hostnames the app answers to. `["*"]` = any host. Localhost is always allowed. |
| *(no connection string here)* | SQL Server connection strings are no longer in `appsettings.json`. Users add a database in the SQL page (**Add database**): the string is encrypted in the browser, tested, and stored encrypted on the server in `App_Data/sqlconn` (Data Protection keys in `App_Data/keys`, DPAPI-protected). The browser keeps only a random key per database. Back up / keep `App_Data` when deploying; if the app-pool account changes, saved databases must be added again. |
| `SqlEditor:AllowedServers` | Optional list of server host names users may connect to (`["*"]` or empty = any). |
| `SqlEditor:CommandTimeoutSeconds` | Per-batch timeout, default 120. |
| `SqlEditor:DisplayRows` / `SqlEditor:ExportRows` | Rows shown in the grid (default 5000) / rows kept for the Excel and CSV download (default 200000). |
| `PathBase` | Optional. Only for hosting outside IIS under a sub-path. IIS sets this itself. |

## Publish to IIS

1. Install the **.NET 8 Hosting Bundle** on the server. Use an app pool with **No Managed Code**.
2. `dotnet publish -c Release`. Stop the app pool (or add `app_offline.htm`), copy **all** files, start it again.
3. The project `web.config` raises the IIS request limit; the app works as a site or as a sub-application (e.g. `/Deployer`).
4. Give `IIS AppPool\<pool name>` Read & Execute on the app folder, and the access you want it to have on the disks it manages (write access for copy/move/delete/upload/edit).
5. Hard-refresh the browser (Ctrl+F5) after each deploy so the cached JavaScript is replaced.

**The SQL Server editor runs any SQL the configured login may run**, for everyone who logs in to this app (every run is written to `logs/sql-audit.log`: time, user@IP, server/database, SQL text; never the connection string). Give the connection string a login with limited rights, keep HTTPS on and change the default password.

**Use HTTPS.** Anyone who logs in can read and change every file the app pool account can access. Keep that account's permissions narrow.

## Project layout

```
Controllers/FsController.cs      all file operations (REST API under /api/fs)
Controllers/AccountController.cs login / logout (cookie auth)
Controllers/HomeController.cs    main page and editor page
Controllers/SqlController.cs     SQL Server editor page
Controllers/SqlApiController.cs  SQL Server API (/api/sql: pubkey, save, forget, connect, query, objects, definition, export)
Services/SqlConnStore.cs         saved SQL connections (encrypted files in App_Data) + RSA key for the browser envelope
Views/Home/Index.cshtml          file browser page
Views/Home/Editor.cshtml         text editor page
Views/Sql/Index.cshtml           SQL Server editor page (wwwroot/js/sql.js, wwwroot/css/sql.css)
appsettings.sample.json          template for appsettings.json (which is git-ignored)
Views/Account/Login.cshtml       login page (shows the version)
wwwroot/js/browser.js            browser UI logic
wwwroot/js/editor.js             editor logic
wwwroot/css/browser.css          styles
wwwroot/lib/codemirror/          bundled CodeMirror 5
Program.cs                       auth, host check, CSRF header check, pipeline
web.config                       IIS settings (request size)
CHANGELOG.md                      version history (newest first)
```

## Changelog

See [CHANGELOG.md](CHANGELOG.md).
