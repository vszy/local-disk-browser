# Local Disk Browser

A plain, functional web-based file manager for the disks of the machine it runs on (ASP.NET Core 8 MVC).
Browse drives and folders, copy / move / delete, zip / extract, edit text files, upload and download, view properties.
No fancy design on purpose. Protected by a login.

**Current version: 1.1.4** (shown on the login page; defined in `LocalDiskBrowser.csproj`)

## Features

- **Drives:** every drive with label, type, filesystem and free/total space.
- **Browse:** address bar, Back/Forward/Up, sortable columns, multi-select (Ctrl/Shift), keyboard navigation, right-click menu, show/hide hidden items, recursive search in the current folder (`*` and `?` allowed). Double-click opens a folder. File icons depend on the extension.
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
- **Login:** username + password from `appsettings.json`. "Remember me" keeps you logged in for 1 day, even after closing the browser; without it the login ends when the browser closes (or after 8 h idle).

Not possible in a browser: putting server files on your computer's clipboard (use Download instead).

## Run locally

```
dotnet run
```
then open http://localhost:5195 and log in (default `Admin` / `Admin@123`).

## Configuration (`appsettings.json`)

| Key | Meaning |
|---|---|
| `Auth:Username`, `Auth:Password` | Login credentials (plain text). **Change before publishing.** Restart the app after changing. |
| `FileOperations:UseRecycleBin` | `true` (default): "Delete" uses the Recycle Bin when the app runs interactively. Under IIS (a service) the Recycle Bin is not used; deletes are permanent. |
| `Security:AllowedHostNames` | Hostnames the app answers to. `["*"]` = any host. Localhost is always allowed. |
| `PathBase` | Optional. Only for hosting outside IIS under a sub-path. IIS sets this itself. |

## Publish to IIS

1. Install the **.NET 8 Hosting Bundle** on the server. Use an app pool with **No Managed Code**.
2. `dotnet publish -c Release`. Stop the app pool (or add `app_offline.htm`), copy **all** files, start it again.
3. The project `web.config` raises the IIS request limit; the app works as a site or as a sub-application (e.g. `/Deployer`).
4. Give `IIS AppPool\<pool name>` Read & Execute on the app folder, and the access you want it to have on the disks it manages (write access for copy/move/delete/upload/edit).
5. Hard-refresh the browser (Ctrl+F5) after each deploy so the cached JavaScript is replaced.

**Use HTTPS.** Anyone who logs in can read and change every file the app pool account can access. Keep that account's permissions narrow.

## Project layout

```
Controllers/FsController.cs      all file operations (REST API under /api/fs)
Controllers/AccountController.cs login / logout (cookie auth)
Controllers/HomeController.cs    main page and editor page
Views/Home/Index.cshtml          file browser page
Views/Home/Editor.cshtml         text editor page
Views/Account/Login.cshtml       login page (shows the version)
wwwroot/js/browser.js            browser UI logic
wwwroot/js/editor.js             editor logic
wwwroot/css/browser.css          styles
wwwroot/lib/codemirror/          bundled CodeMirror 5
Program.cs                       auth, host check, CSRF header check, pipeline
web.config                       IIS settings (request size)
```

## Changelog

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
