(() => {
    const $ = id => document.getElementById(id);
    // App base path (e.g. "/" or "/LocalDiskBrowser/" when hosted as an IIS sub-application).
    const BASE = document.querySelector('meta[name=app-base]').content;
    const RECYCLE = document.querySelector('meta[name=use-recycle]').content === 'true';    const rowsEl = $('rows'), statusL = $('stLeft'), statusC = $('stClip'), statusBar = $('status');

    let cur = null;            // current folder path; null = drive list
    let items = [];            // current listing
    let view = [];             // sorted listing
    let sel = new Set();       // selected paths
    let anchor = -1;
    let clip = null;           // { mode: 'copy'|'cut', paths: [] }
    let hist = [], histPos = -1;
    let sort = { k: 'name', dir: 1 };
    let searchQ = null;        // non-null while showing search results

    // ---------- helpers ----------
    const join = (a, b) => a.endsWith('\\') ? a + b : a + '\\' + b;
    const fmtSize = n => {
        if (n < 1024) return n + ' B';
        const u = ['KB', 'MB', 'GB', 'TB']; let i = -1;
        do { n /= 1024; i++; } while (n >= 1024 && i < u.length - 1);
        return n.toFixed(n < 10 ? 2 : 1) + ' ' + u[i];
    };
    const fmtDate = s => { const d = new Date(s); return isNaN(d) ? '' : d.toLocaleDateString() + ' ' + d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }); };
    const esc = s => String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
    // hl = highlight (yellow) for operations the user is waiting for or just finished: search, zip, extract, download, upload.
    const status = (msg, isErr, hl) => { statusL.textContent = msg; statusL.title = msg; statusBar.classList.toggle('err', !!isErr); statusBar.classList.toggle('up', !!hl && !isErr); };

    // ---------- busy indicator: spinner + elapsed time in the status bar while any API call is running ----------
    const BUSY_LABELS = {
        drives: 'Loading drives', list: 'Loading folder', search: 'Searching', delete: 'Deleting', copy: 'Copying', move: 'Moving',
        rename: 'Renaming', mkdir: 'Creating folder', newfile: 'Creating file', zip: 'Zipping', extract: 'Extracting',
        props: 'Reading properties', attrs: 'Saving attributes', zipdl: 'Preparing download'
    };
    let pending = [], busyTimer = null;
    function paintBusy() {
        const el = $('busy'); if (!pending.length) return;
        const oldest = Math.min(...pending.map(p => p.start)), secs = Math.floor((Date.now() - oldest) / 1000);
        const label = pending[pending.length - 1].label;
        el.textContent = `${label}... ${secs}s` + (secs >= 10 ? ' (server or network is slow)' : '');
        el.hidden = Date.now() - oldest < 300; // do not flash for fast calls
    }
    function busyOn(url) {
        const key = url.split('?')[0].split('/').pop();
        const job = { start: Date.now(), label: BUSY_LABELS[key] || 'Working' };
        pending.push(job);
        document.body.classList.add('busy');
        if (!busyTimer) busyTimer = setInterval(paintBusy, 250);
        return job;
    }
    function busyOff(job) {
        pending = pending.filter(p => p !== job);
        if (pending.length) return;
        clearInterval(busyTimer); busyTimer = null;
        $('busy').hidden = true; document.body.classList.remove('busy');
    }

    // Cloudflare in front of the server sometimes blocks requests from certain networks (answers with its own HTML page).
    // Connecting to the VPN avoids it; the permanent hint next to Refresh (#cfHint) already says so, the error repeats it.
    const CF_HINT = 'Blocked by Cloudflare. Connect to the VPN and try again.';
    function cfBlocked(httpStatus) {
        return `HTTP ${httpStatus}: ${CF_HINT}`;
    }

    async function api(method, url, body) {
        const opt = { method, headers: { 'X-Requested-With': 'LocalDiskBrowser' } };
        if (body !== undefined) { opt.headers['Content-Type'] = 'application/json'; opt.body = JSON.stringify(body); }
        const job = busyOn(url);
        try {
            const r = await fetch(BASE + url.replace(/^\//, ''), opt);
            if (r.status === 401) { location.href = BASE + 'Account/Login'; throw new Error('Session expired'); }
            let data = null;
            try { data = await r.json(); } catch { }
            if (!r.ok) {
                let msg = data && (data.error || (data.errors && data.errors.join('\n'))) || ('HTTP ' + r.status);
                if (!data && /cloudflare/i.test(r.headers.get('Server') || '')) msg = cfBlocked(r.status);
                const e = new Error(msg); e.partial = r.status === 207; e.status = r.status; throw e;
            }
            return data;
        } finally { busyOff(job); }
    }
    const q = (p) => encodeURIComponent(p);

    async function run(fn, okMsg) {
        try { const r = await fn(); if (okMsg) status(okMsg); return r; }
        catch (e) {
            status(e.message.split('\n')[0], true);
            if (e.message !== 'Session expired') alert(e.message); // always show the full error, the status bar line is easy to miss
            if (e.partial) await reload();
        }
    }

    // ---------- navigation ----------
    async function loadDrives() {
        const drives = await api('GET', '/api/fs/drives');
        $('drives').innerHTML = drives.map(d => {
            const used = d.total - d.free, pct = d.total ? Math.round(used * 100 / d.total) : 0;
            const label = (d.label ? d.label + ' ' : '') + '(' + d.name.replace('\\', '') + ')';
            return `<div class="drive ${d.ready ? '' : 'na'}" data-p="${esc(d.name)}">
                <div>${esc(label)} <span class="sub">${esc(d.type)}</span></div>
                ${d.ready ? `<div class="bar"><i class="${pct > 90 ? 'full' : ''}" style="width:${pct}%"></i></div>
                <div class="sub">${fmtSize(d.free)} free of ${fmtSize(d.total)} &middot; ${esc(d.format)}</div>` : '<div class="sub">Not ready</div>'}
            </div>`;
        }).join('');
        return drives;
    }

    // ---------- recent folders and files: one list, kept in this browser only (localStorage), newest first ----------
    // Entries are { k: 'dirs'|'files', p: path }.
    const RECENT_MAX = 20;
    function loadRecent() {
        try {
            const r = JSON.parse(localStorage.getItem('ldb.recent'));
            return Array.isArray(r) ? r.filter(x => x && typeof x.p === 'string') : [];
        } catch { return []; }
    }
    function saveRecent(r) { try { localStorage.setItem('ldb.recent', JSON.stringify(r)); } catch { } }
    const sameRecent = (x, kind, path) => x.k === kind && x.p.toLowerCase() === path.toLowerCase();
    function addRecent(kind, path) {
        const r = [{ k: kind, p: path }, ...loadRecent().filter(x => !sameRecent(x, kind, path))].slice(0, RECENT_MAX);
        saveRecent(r); renderRecent();
    }
    function removeRecent(kind, path) {
        saveRecent(loadRecent().filter(x => !sameRecent(x, kind, path))); renderRecent();
    }
    function renderRecent() {
        const r = loadRecent();
        const row = ({ k, p }) => {
            const cut = p.replace(/\\+$/, '').lastIndexOf('\\');
            const name = cut < 0 ? p : p.slice(cut + 1), parent = cut < 0 ? '' : p.slice(0, cut + 1);
            const ext = k === 'files' ? (p.match(/\.[^.\\]+$/) || [''])[0].toLowerCase() : '';
            return `<div class="rec" data-k="${k}" data-p="${esc(p)}" title="${esc(p)}">${k === 'dirs' ? FOLDER_ICON : (ICONS[ext] || FILE_ICON)} ${esc(name)}<div class="sub">${esc(parent)}</div></div>`;
        };
        $('recent').innerHTML = r.length ? r.map(row).join('') : '<div class="sub none">None yet</div>';
    }

    async function nav(path, push = true) {
        searchQ = null;
        if (path === null) { // "This PC"
            cur = null; items = driveItems; sel.clear(); anchor = -1;
            if (push) pushHist(null);
            $('addr').value = ''; render(); status(items.length + ' drive(s)'); return true;
        }
        const r = await run(() => api('GET', `/api/fs/list?path=${q(path)}&hidden=${$('chkHidden').checked}`));
        if (!r) return false;
        cur = r.path; items = r.items; sel.clear(); anchor = -1;
        if (push) { pushHist(cur); addRecent('dirs', cur); }
        $('addr').value = cur; render(); status(`${items.length} item(s)`);
        document.querySelectorAll('.drive').forEach(el => el.classList.toggle('cur', cur.toUpperCase().startsWith(el.dataset.p.toUpperCase())));
        return true;
    }
    function pushHist(p) { hist = hist.slice(0, histPos + 1); hist.push(p); histPos = hist.length - 1; }

    async function reload() {
        const keep = new Set(sel);
        // Refreshing the list must not wipe the result of the action that triggered it (e.g. a red "Access denied").
        const msg = statusL.textContent, wasErr = statusBar.classList.contains('err'), wasHl = statusBar.classList.contains('up');
        if (searchQ !== null) await doSearch(searchQ); else await nav(cur, false);
        sel = new Set([...keep].filter(p => items.some(i => i.path === p))); render();
        if (msg) status(msg, wasErr, wasHl);
    }

    let driveItems = [];
    function setDriveItems(drives) {
        driveItems = drives.map(d => ({ name: d.name, path: d.name, isDir: true, size: d.free, modified: null, ext: d.type, hidden: false, drive: true }));
    }

    async function doSearch(text) {
        if (cur === null) return status('Open a folder first.', true);
        status('Searching...', false, true);
        const r = await run(() => api('GET', `/api/fs/search?path=${q(cur)}&q=${q(text)}`));
        if (!r) return;
        searchQ = text; items = r.items; sel.clear(); anchor = -1; render();
        status(`${items.length} result(s)${r.truncated ? ' (truncated at 1000)' : ''} for "${text}" in ${cur}`, false, true);
    }

    // ---------- icons: Lucide icon font (local copy in wwwroot/lib/lucide, no CDN); names: lucide.dev/icons ----------
    const ic = (name, color) => `<i class="icon-${name}"${color ? ` style="color:${color}"` : ''}></i>`;
    const FOLDER_ICON = ic('folder', '#e0a800'), DRIVE_ICON = ic('hard-drive', '#555'), FILE_ICON = ic('file', '#777');
    const ICONS = {};
    const addIcons = (icon, exts) => exts.split(' ').forEach(x => ICONS['.' + x] = icon);
    addIcons(ic('file-image', '#1a9b6c'), 'jpg jpeg png gif bmp webp svg ico tif tiff heic');
    addIcons(ic('file-music', '#8e44ad'), 'mp3 wav flac aac ogg wma m4a');
    addIcons(ic('file-video', '#c0392b'), 'mp4 mkv avi mov wmv flv webm mpg mpeg');
    addIcons(ic('file-archive', '#b8860b'), 'zip rar 7z tar gz tgz bz2 cab iso');
    addIcons(ic('file-text', '#d9363e'), 'pdf');
    addIcons(ic('file-text', '#2b5fb4'), 'doc docx rtf odt');
    addIcons(ic('file-spreadsheet', '#1e7e45'), 'xls xlsx csv ods');
    addIcons(ic('presentation', '#d2691e'), 'ppt pptx odp');
    addIcons(ic('file-text', '#666'), 'txt md log ini cfg conf');
    addIcons(ic('file-cog', '#555'), 'exe msi bat cmd com ps1 sh dll sys');
    addIcons(ic('file-code', '#3a7bd5'), 'cs js ts jsx tsx py java cpp c h hpp go rs php rb vb sln csproj cshtml razor');
    addIcons(ic('globe', '#1a8fb3'), 'html htm css xml json yml yaml');
    addIcons(ic('database', '#7a5c3a'), 'db sqlite mdb accdb sql bak mdf ldf');
    addIcons(ic('type', '#777'), 'ttf otf woff woff2 fon');
    addIcons(ic('link', '#3a7bd5'), 'lnk url');
    const iconFor = it => it.drive ? DRIVE_ICON : it.isDir ? FOLDER_ICON : (ICONS[it.ext] || FILE_ICON);

    // ---------- rendering ----------
    function render() {
        const k = sort.k, d = sort.dir;
        view = items.slice().sort((a, b) => {
            if (a.isDir !== b.isDir) return a.isDir ? -1 : 1;
            let x = a[k], y = b[k];
            if (k === 'name' || k === 'ext') { x = String(x).toLowerCase(); y = String(y).toLowerCase(); }
            return (x < y ? -1 : x > y ? 1 : a.name.toLowerCase().localeCompare(b.name.toLowerCase())) * d;
        });
        rowsEl.innerHTML = view.map((it, i) => {
            const nameCell = searchQ !== null ? `${esc(it.name)} <span style="color:#888">— ${esc(it.path.slice(0, it.path.length - it.name.length - 1))}</span>` : esc(it.name);
            const type = it.drive ? it.ext : (it.isDir ? 'Folder' : (it.ext ? it.ext.slice(1) : 'File'));
            const size = it.drive ? fmtSize(it.size) + ' free' : (it.isDir ? '' : fmtSize(it.size));
            const cls = [it.isDir ? 'dir' : '', it.hidden ? 'hid' : '', sel.has(it.path) ? 'sel' : '', clip && clip.mode === 'cut' && clip.paths.includes(it.path) ? 'cut' : ''].join(' ');
            return `<tr data-i="${i}" class="${cls}" title="${esc(it.path)}"><td><span class="ico">${iconFor(it)}</span>${nameCell}</td><td>${it.modified ? fmtDate(it.modified) : ''}</td><td>${esc(type)}</td><td class="r">${size}</td></tr>`;
        }).join('');
        $('empty').hidden = view.length > 0;
        $('bBack').disabled = histPos <= 0;
        $('bFwd').disabled = histPos >= hist.length - 1;
        $('bUp').disabled = cur === null;
        updateStatusRight();
    }
    // Update highlighting in place. Re-rendering the rows on click would detach the clicked <tr>
    // before the browser fires "dblclick", so double-click to open would never reach us.
    function refreshSel() {
        for (let i = 0; i < rowsEl.children.length; i++)
            rowsEl.children[i].classList.toggle('sel', sel.has(view[i].path));
        updateStatusRight();
    }
    function updateStatusRight() {
        const s = [...sel];
        let t = '';
        if (s.length) {
            const files = view.filter(v => sel.has(v.path) && !v.isDir);
            t = `${s.length} selected` + (files.length ? ` (${fmtSize(files.reduce((a, f) => a + f.size, 0))})` : '');
        }
        if (clip) t += `${t ? ' | ' : ''}Clipboard: ${clip.paths.length} to ${clip.mode}`;
        statusC.textContent = t;
    }
    const selItems = () => view.filter(v => sel.has(v.path));
    const selPaths = () => [...sel];

    // ---------- selection ----------
    rowsEl.addEventListener('click', e => {
        const tr = e.target.closest('tr'); if (!tr) return;
        const i = +tr.dataset.i, it = view[i];
        if (e.shiftKey && anchor >= 0) {
            const [a, b] = [Math.min(anchor, i), Math.max(anchor, i)];
            if (!e.ctrlKey) sel.clear();
            for (let j = a; j <= b; j++) sel.add(view[j].path);
        } else if (e.ctrlKey) {
            sel.has(it.path) ? sel.delete(it.path) : sel.add(it.path); anchor = i;
        } else { sel = new Set([it.path]); anchor = i; }
        refreshSel();
    });
    rowsEl.addEventListener('dblclick', e => { const tr = e.target.closest('tr'); if (tr) openItem(view[+tr.dataset.i]); });
    $('content').addEventListener('click', e => { if (e.target.id === 'content' || e.target.id === 'empty') { sel.clear(); refreshSel(); } });
    $('content').addEventListener('contextmenu', e => {
        e.preventDefault();
        const tr = e.target.closest('tr');
        if (tr) { const it = view[+tr.dataset.i]; if (!sel.has(it.path)) { sel = new Set([it.path]); anchor = +tr.dataset.i; refreshSel(); } }
        else if (e.target.closest('thead') === null) { sel.clear(); refreshSel(); }
        showCtx(e.clientX, e.clientY);
    });
    document.querySelectorAll('#grid th').forEach(th => th.addEventListener('click', () => {
        const k = th.dataset.k; sort = { k, dir: sort.k === k ? -sort.dir : 1 }; render();
    }));

    // ---------- icons for buttons and menus (single source; files use ICONS/iconFor above) ----------
    const UI_ICONS = {
        back: 'arrow-left', fwd: 'arrow-right', up: 'arrow-up', go: 'circle-arrow-right', search: 'search', logout: 'log-out',
        newFolder: 'folder-plus', newFile: 'file-plus', copy: 'copy', cut: 'scissors', paste: 'clipboard-paste', rename: 'pencil', delete: 'trash-2',
        zip: 'file-archive', extract: 'folder-open', open: 'eye', edit: 'square-pen', download: 'download', upload: 'upload', uploadDir: 'folder-up',
        props: 'info', refresh: 'refresh-cw', close: 'x'
    };
    // Same keys as UI_ICONS: every action has its own colour so the toolbar is easy to scan.
    const UI_COLORS = {
        back: '#3a7bd5', fwd: '#3a7bd5', up: '#3a7bd5', go: '#1e9e5a', search: '#7a4fd0', logout: '#c0392b',
        newFolder: '#e0a800', newFile: '#3a7bd5', copy: '#3a7bd5', cut: '#d9363e', paste: '#2b9e8f', rename: '#d2691e', delete: '#d9363e',
        zip: '#b8860b', extract: '#e0a800', open: '#1a8fb3', edit: '#2b5fb4', download: '#1e9e5a', upload: '#2b5fb4', uploadDir: '#7a4fd0',
        props: '#1a8fb3', refresh: '#1e9e5a', close: '#888'
    };
    const uiIcon = key => UI_ICONS[key] ? ic(UI_ICONS[key], UI_COLORS[key]) : '';
    document.querySelectorAll('[data-ico]').forEach(b => {
        const s = document.createElement('span');
        s.className = 'bico'; s.innerHTML = uiIcon(b.dataset.ico);
        // Toolbar buttons show the icon only: label + the shortcut from title become a styled tooltip
        // (data-tip, drawn by CSS) instead of the browser's native title tooltip.
        if (b.closest('#toolbar, #actions')) {
            const label = b.textContent.trim();
            const hint = b.title;
            b.removeAttribute('title');
            b.dataset.tip = label ? (hint ? label + ' (' + hint + ')' : label) : hint;
            if (label) {
                b.textContent = '';
                const l = document.createElement('span');
                l.className = 'blabel'; l.textContent = label;
                b.append(l);
            }
        }
        b.prepend(s);
    });

    // ---------- context menu ----------
    function showCtx(x, y) {
        const n = sel.size, one = n === 1, it = one ? selItems()[0] : null, inDrives = cur === null;
        // [label, action, enabled, icon key]
        const entries = [
            ['Open / View', openSel, n === 1, 'open'],
            ['Edit', editSel, one && !it.isDir, 'edit'],
            ['Download' + (one && !it.isDir ? '' : ' (zip)'), download, n > 0 && !inDrives, 'download'],
            ['Upload files here...', () => pickAndUpload(false), !inDrives, 'upload'],
            ['Upload folder here...', () => pickAndUpload(true), !inDrives, 'uploadDir'],
            ['-'],
            ['Cut', () => setClip('cut'), n > 0 && !inDrives, 'cut'],
            ['Copy', () => setClip('copy'), n > 0 && !inDrives, 'copy'],
            ['Paste', paste, !!clip && !inDrives, 'paste'],
            ['-'],
            ['Rename', rename, one && !inDrives, 'rename'],
            // One Delete item. With a Recycle Bin it uses it (Shift+Delete = permanent); without one (IIS) del() makes it permanent and says so in the confirmation.
            ['Delete', () => del(false), n > 0 && !inDrives, 'delete'],
            ['-'],
            ['Zip', zip, n > 0 && !inDrives, 'zip'],
            ['Extract here', () => extract(false), one && !it.isDir && ARCHIVE_RE.test(it.name) && !inDrives, 'extract'],
            ['Extract to...', () => extract(true), one && !it.isDir && ARCHIVE_RE.test(it.name) && !inDrives, 'extract'],
            ['-'],
            ['New Folder', newFolder, !inDrives, 'newFolder'],
            ['New File', newFile, !inDrives, 'newFile'],
            ['Properties', props, n === 1 || (n === 0 && !inDrives), 'props']
        ];
        const m = $('ctx');
        m.innerHTML = entries.map((e, i) => e[0] === '-' ? '<hr>'
            : `<div data-i="${i}" class="${e[2] ? '' : 'dis'}"><span class="bico">${uiIcon(e[3])}</span>${esc(e[0])}</div>`).join('');
        m.hidden = false;
        m.style.left = Math.min(x, innerWidth - m.offsetWidth - 4) + 'px';
        m.style.top = Math.min(y, innerHeight - m.offsetHeight - 4) + 'px';
        m.onclick = ev => { const d = ev.target.closest('div[data-i]'); if (d) { m.hidden = true; entries[+d.dataset.i][1](); } };
    }
    document.addEventListener('click', () => { $('ctx').hidden = true; });
    document.addEventListener('keydown', e => { if (e.key === 'Escape') $('ctx').hidden = true; });

    // ---------- actions ----------
    async function openItem(it) {
        if (it.isDir) return nav(it.path);
        addRecent('files', it.path);
        // Views in a new tab if the browser can show it (image/pdf/audio/video/text); otherwise the server sends it as a download.
        window.open(BASE + 'api/fs/download?inline=true&path=' + q(it.path), '_blank');
    }
    function editSel() {
        const s = selItems(); if (s.length !== 1 || s[0].isDir) return status('Select one file to edit.', true);
        addRecent('files', s[0].path);
        window.open(BASE + 'Home/Editor?path=' + q(s[0].path), '_blank');
    }
    function openSel() { const s = selItems(); if (s.length === 1) openItem(s[0]); }
    // One file downloads directly; folders or several items are zipped by the server first.
    async function download() {
        const s = selItems(); if (!s.length || cur === null) return status('Select something to download.', true);
        if (s.length === 1 && !s[0].isDir) { addRecent('files', s[0].path); location.href = BASE + 'api/fs/download?path=' + q(s[0].path); return; }
        status('Preparing zip...', false, true);
        const r = await run(() => api('POST', '/api/fs/zipdl', { paths: s.map(x => x.path), dest: null, permanent: false }));
        if (r) { location.href = BASE + 'api/fs/zipdl?t=' + r.token; status('Download started (large folders may take a moment to start).', false, true); }
    }

    // ---------- upload: your PC -> server ----------
    // Items are { file, rel } where rel keeps folder structure ("photos/a.jpg").
    const CHUNK = 4 * 1024 * 1024; // small requests stay under IIS / proxy / firewall size limits
    function sendChunk(url, blob, onProgress) {
        return new Promise((resolve, reject) => {
            const x = new XMLHttpRequest();
            x.open('POST', url);
            x.setRequestHeader('X-Requested-With', 'LocalDiskBrowser');
            x.setRequestHeader('Content-Type', 'application/octet-stream');
            x.upload.onprogress = e => { if (e.lengthComputable) onProgress(e.loaded); };
            x.onload = () => {
                if (x.status === 401) { location.href = BASE + 'Account/Login'; return reject(new Error('Session expired')); }
                if (x.status >= 200 && x.status < 300) return resolve();
                let msg = 'HTTP ' + x.status;
                try { msg = JSON.parse(x.responseText).error || msg; }
                catch {
                    // Not our JSON error: IIS / proxy / firewall page. Show what it says and who sent it.
                    const text = (x.responseText || '').replace(/<style[\s\S]*?<\/style>|<[^>]+>/g, ' ').replace(/\s+/g, ' ').trim().slice(0, 200);
                    const srv = x.getResponseHeader('Server');
                    msg += (text ? ' - ' + text : '') + (srv ? ` [Server: ${srv}]` : '');
                    if (/cloudflare/i.test(srv || '')) msg = cfBlocked(x.status);
                }
                reject(new Error(msg));
            };
            x.onerror = () => reject(new Error('Network error'));
            x.send(blob);
        });
    }
    async function xhrUpload(item, dest) {
        const id = [...crypto.getRandomValues(new Uint8Array(16))].map(b => b.toString(16).padStart(2, '0')).join('');
        const size = item.file.size;
        const url = (offset, last, abort) => BASE + `api/fs/upload-chunk?dest=${q(dest)}&rel=${q(item.rel)}&id=${id}&offset=${offset}&last=${last}&abort=${!!abort}`;
        let offset = 0;
        try {
            do { // do-while so an empty file still sends one (empty) chunk
                const end = Math.min(offset + CHUNK, size);
                const base = offset;
                await sendChunk(url(base, end >= size), item.file.slice(base, end), loaded => item.onProgress(Math.round((base + loaded) * 100 / Math.max(size, 1))));
                offset = end;
            } while (offset < size);
        } catch (e) {
            try { await sendChunk(url(0, false, true), new Blob([]), () => { }); } catch { } // remove the partial file
            throw new Error(e.message + ` [${(size / 1048576).toFixed(1)} MB]`);
        }
    }
    let uploading = false;
    async function uploadItems(list) {
        if (cur === null) return status('Open a folder first, then upload.', true);
        if (!list.length) return;
        if (uploading) return status('An upload is already running.', true);
        uploading = true;
        const dest = cur, errors = [];
        let done = 0;
        for (const it of list) {
            // Count and percentage come first: a long file name is cut off by the status bar and would hide them. status() clears the highlight.
            it.onProgress = p => status(`Uploading ${done + 1}/${list.length} - ${p}% - ${it.rel}`, false, true);
            it.onProgress(0);
            try { await xhrUpload(it, dest); } catch (e) { errors.push(`${it.rel}: ${e.message}`); }
            done++;
        }
        uploading = false;
        if (errors.length) { status(`Uploaded ${done - errors.length}/${list.length}; ${errors.length} failed.`, true); alert(errors.join('\n')); }
        else status(`Uploaded ${done} file(s) to ${dest}`, false, true);
        if (cur === dest) await reload();
    }

    // Walks dropped/pasted items (files and folders). Entries must be grabbed synchronously, before any await.
    function grabEntries(dt) {
        const out = [];
        if (dt.items && dt.items.length) {
            for (const it of dt.items) {
                if (it.kind !== 'file') continue;
                const entry = it.webkitGetAsEntry ? it.webkitGetAsEntry() : null;
                out.push(entry ? { entry } : { file: it.getAsFile() });
            }
        } else for (const f of dt.files) out.push({ file: f });
        return out;
    }
    async function expandEntries(grabbed) {
        const list = [];
        const walk = async (entry, prefix) => {
            if (entry.isFile) {
                const file = await new Promise((res, rej) => entry.file(res, rej));
                list.push({ file, rel: prefix + entry.name });
            } else if (entry.isDirectory) {
                const reader = entry.createReader();
                let batch;
                do {
                    batch = await new Promise((res, rej) => reader.readEntries(res, rej));
                    for (const e of batch) await walk(e, prefix + entry.name + '/');
                } while (batch.length);
            }
        };
        for (const g of grabbed) {
            if (g.entry) await walk(g.entry, '');
            else if (g.file) list.push({ file: g.file, rel: g.file.name });
        }
        return list;
    }
    async function uploadFromTransfer(dt) {
        const grabbed = grabEntries(dt);
        try { await uploadItems(await expandEntries(grabbed)); }
        catch (e) { status('Could not read dropped items: ' + e.message, true); }
    }
    function pickAndUpload(directory) {
        const inp = document.createElement('input');
        inp.type = 'file'; inp.multiple = true; if (directory) inp.webkitdirectory = true;
        inp.onchange = () => uploadItems([...inp.files].map(f => ({ file: f, rel: f.webkitRelativePath || f.name })));
        inp.click();
    }

    // Ctrl+V: files copied from the user's own computer are uploaded; otherwise paste the server-side clipboard.
    document.addEventListener('paste', e => {
        if (e.target.tagName === 'INPUT' || $('dlg').open) return;
        const dt = e.clipboardData;
        e.preventDefault();
        // Our own Copy/Cut put CLIP_MARKER on the computer's clipboard, replacing any files copied earlier in Explorer.
        if (dt && dt.getData('text/plain') === CLIP_MARKER && clip) return paste();
        // Files on the computer's clipboard (copied in Explorer) are always uploaded, no questions asked.
        if (dt && dt.files && dt.files.length) uploadFromTransfer(dt);
        else paste();
    });
    const dropZone = $('content');
    ['dragenter', 'dragover'].forEach(ev => dropZone.addEventListener(ev, e => {
        if (!e.dataTransfer || ![...e.dataTransfer.types].includes('Files')) return;
        e.preventDefault(); dropZone.style.outline = '2px dashed #3a7bd5';
    }));
    dropZone.addEventListener('dragleave', e => { if (e.target === dropZone) dropZone.style.outline = ''; });
    dropZone.addEventListener('drop', e => {
        dropZone.style.outline = '';
        if (!e.dataTransfer || !e.dataTransfer.types.includes('Files')) return;
        e.preventDefault(); uploadFromTransfer(e.dataTransfer);
    });
    // Stop the browser from navigating to a file dropped outside the list.
    window.addEventListener('dragover', e => e.preventDefault());
    window.addEventListener('drop', e => e.preventDefault());

    const CLIP_MARKER = 'LocalDiskBrowser:internal-clipboard';
    function setClip(mode) {
        if (!sel.size) return;
        clip = { mode, paths: selPaths() }; render(); status(`${clip.paths.length} item(s) ${mode === 'cut' ? 'cut' : 'copied'} (server-side, nothing is uploaded)`);
        // Replace whatever the user copied earlier on their computer (e.g. files from Explorer), so Ctrl+V
        // pastes this selection instead of uploading those old files. Needs https; harmless if unavailable.
        try { navigator.clipboard.writeText(CLIP_MARKER).catch(() => { }); } catch { }
    }
    async function paste() {
        if (!clip || cur === null) return;
        const c = clip;
        await run(() => api('POST', '/api/fs/' + (c.mode === 'cut' ? 'move' : 'copy'), { paths: c.paths, dest: cur, permanent: false }),
            `${c.mode === 'cut' ? 'Moved' : 'Copied'} ${c.paths.length} item(s)`);
        if (c.mode === 'cut') clip = null;
        await reload();
    }
    async function del(permanent) {
        const s = selItems(); if (!s.length || cur === null) return;
        const what = s.length === 1 ? `"${s[0].name}"` : `${s.length} items`;
        if (!RECYCLE) permanent = true; // server has the Recycle Bin turned off (see FileOperations:UseRecycleBin)
        const msg = permanent ? `PERMANENTLY delete ${what}? This cannot be undone.` : `Move ${what} to the Recycle Bin?`;
        if (!confirm(msg)) return;
        await run(() => api('POST', '/api/fs/delete', { paths: s.map(x => x.path), permanent }), `Deleted ${s.length} item(s)`);
        await reload();
    }
    async function rename() {
        const s = selItems(); if (s.length !== 1) return;
        const name = prompt('New name:', s[0].name);
        if (!name || name === s[0].name) return;
        await run(() => api('POST', '/api/fs/rename', { path: s[0].path, newName: name }), 'Renamed');
        await reload();
    }
    async function newFolder() {
        if (cur === null) return;
        const name = prompt('Folder name:', 'New folder'); if (!name) return;
        await run(() => api('POST', '/api/fs/mkdir', { path: cur, name }), 'Folder created'); await reload();
    }
    async function newFile() {
        if (cur === null) return;
        const name = prompt('File name:', 'New file.txt'); if (!name) return;
        await run(() => api('POST', '/api/fs/newfile', { path: cur, name }), 'File created'); await reload();
    }
    async function zip() {
        const s = selItems(); if (!s.length || cur === null) return;
        status('Zipping...', false, true);
        const r = await run(() => api('POST', '/api/fs/zip', { paths: s.map(x => x.path), dest: cur, permanent: false }));
        if (r) status('Created ' + r.created, false, true);
        await reload();
    }
    const ARCHIVE_RE = /\.(zip|rar|7z|tar|tar\.gz|tgz|gz)$/i;
    // ask=false ("Extract here"): files go straight into the archive's own folder.
    // ask=true  ("Extract to..."): the user picks the folder (blank = this folder too).
    async function extract(ask) {
        const s = selItems(); if (s.length !== 1 || s[0].isDir) return status('Select one archive file.', true);
        if (!ARCHIVE_RE.test(s[0].name)) return status('Not a supported archive (.zip, .rar, .7z, .tar, .tar.gz, .tgz, .gz).', true);
        let dest = null;
        if (ask) {
            const def = s[0].name.replace(/\.(tar\.gz|tgz|zip|rar|7z|tar|gz)$/i, '');
            dest = prompt('Extract to folder (relative to this folder, or a full path; leave empty for this folder):', def);
            if (dest === null) return;
        }
        const send = overwrite => api('POST', '/api/fs/extract', { path: s[0].path, dest: dest || null, overwrite });
        status('Extracting...', false, true);
        try {
            let r;
            try { r = await send(false); }
            catch (e) {
                if (e.status !== 409 || !confirm('Some files already exist in the destination.\n\nReplace the existing files?')) throw e;
                status('Extracting (replacing existing files)...', false, true);
                r = await send(true);
            }
            status('Extracted to ' + r.created, false, true);
        } catch (e) { status(e.message.split('\n')[0], true); if (e.message.length > 120) alert(e.message); }
        await reload();
    }
    async function props() {
        const s = selItems();
        const path = s.length === 1 ? s[0].path : (s.length === 0 ? cur : null);
        if (!path) return;
        if (cur === null && s[0] && s[0].drive) { /* drive root is a normal directory */ }
        $('dlgBody').innerHTML = 'Loading...'; $('dlg').showModal();
        try {
            const p = await api('GET', '/api/fs/props?path=' + q(path));
            const row = (k, v) => `<tr><td>${k}</td><td>${esc(v)}</td></tr>`;
            $('dlgBody').innerHTML = `<h3 style="margin-top:0">${esc(p.name)}</h3><table>` +
                row('Type', p.type) + row('Location', p.location || '') + row('Full path', p.path) +
                row('Size', p.size.toLocaleString() + ' bytes (' + fmtSize(p.size) + ')') +
                (p.type === 'Folder' ? row('Contains', `${p.files.toLocaleString()} file(s), ${p.folders.toLocaleString()} folder(s)`) : '') +
                row('Created', fmtDate(p.created)) + row('Modified', fmtDate(p.modified)) + row('Accessed', fmtDate(p.accessed)) +
                row('Attributes', p.attrs) + (p.target ? row('Link target', p.target) : '') + '</table>' +
                `<label><input type="checkbox" id="pRO" ${p.readOnly ? 'checked' : ''}> Read-only</label> ` +
                `<label><input type="checkbox" id="pHid" ${p.hidden ? 'checked' : ''}> Hidden</label> ` +
                `<button id="pApply">Apply</button>`;
            $('pApply').onclick = async () => {
                try { await api('POST', '/api/fs/attrs', { path: p.path, readOnly: $('pRO').checked, hidden: $('pHid').checked }); $('dlg').close(); await reload(); }
                catch (e) { alert(e.message); }
            };
        } catch (e) { $('dlgBody').textContent = e.message; }
    }

    // ---------- wiring ----------
    $('bBack').onclick = () => { if (histPos > 0) { histPos--; nav(hist[histPos], false); } };
    $('bFwd').onclick = () => { if (histPos < hist.length - 1) { histPos++; nav(hist[histPos], false); } };
    $('bUp').onclick = up;
    function up() {
        if (cur === null) return;
        const p = cur.replace(/[\\/]+$/, '');
        const i = p.lastIndexOf('\\');
        if (i < 0) return nav(null);                       // "C:" -> drive list
        const parent = p.slice(0, i);
        nav(/^[A-Za-z]:$/.test(parent) ? parent + '\\' : parent);
    }
    $('bGo').onclick = () => { const v = $('addr').value.trim(); nav(v === '' ? null : v); };
    $('addr').onkeydown = e => { if (e.key === 'Enter') $('bGo').click(); };
    $('bSearch').onclick = () => { const v = $('search').value.trim(); v ? doSearch(v) : (searchQ !== null && reload()); };
    $('search').onkeydown = e => { if (e.key === 'Enter') $('bSearch').click(); if (e.key === 'Escape') { $('search').value = ''; if (searchQ !== null) nav(cur, false); } };
    $('bNewFolder').onclick = newFolder; $('bNewFile').onclick = newFile;
    $('bCopy').onclick = () => setClip('copy'); $('bCut').onclick = () => setClip('cut'); $('bPaste').onclick = paste;
    $('bRename').onclick = rename; $('bDelete').onclick = () => del(false);
    $('bZip').onclick = zip; $('bExtract').onclick = () => extract(true);
    $('bOpen').onclick = openSel; $('bEdit').onclick = editSel;$('bDownload').onclick = download;
    $('bUpload').onclick = () => pickAndUpload(false); $('bUploadDir').onclick = () => pickAndUpload(true);
    $('bProps').onclick = props;
    $('bRefresh').onclick = async () => { await loadDrivesAndItems(); await reload(); };
    $('chkHidden').onchange = () => reload();
    // Recent lists: a folder that no longer opens is dropped from the list; "Clear" empties one list.
    $('sidebar').addEventListener('click', async e => {
        const c = e.target.closest('.clr');
        if (c) { saveRecent([]); return renderRecent(); }
        const el = e.target.closest('.rec'); if (!el) return;
        const p = el.dataset.p;
        if (el.dataset.k === 'dirs') { if (!(await nav(p))) removeRecent('dirs', p); }
        else openItem({ isDir: false, path: p });
    });
    renderRecent();
    $('drives').addEventListener('click', e => { const d = e.target.closest('.drive'); if (d && !d.classList.contains('na')) nav(d.dataset.p); });

    document.addEventListener('keydown', e => {
        if (e.target.tagName === 'INPUT' || $('dlg').open) return;
        const k = e.key;
        if (e.ctrlKey && k.toLowerCase() === 'a') { e.preventDefault(); sel = new Set(view.map(v => v.path)); refreshSel(); }
        else if (e.ctrlKey && k.toLowerCase() === 'c') { e.preventDefault(); setClip('copy'); }
        else if (e.ctrlKey && k.toLowerCase() === 'x') { e.preventDefault(); setClip('cut'); }
        else if (k === 'Delete') { e.preventDefault(); del(e.shiftKey); }
        else if (k === 'F2') { e.preventDefault(); rename(); }
        else if (k === 'F4') { e.preventDefault(); editSel(); }
        else if (k === 'F5') { e.preventDefault(); $('bRefresh').click(); }
        else if (k === 'Enter' && e.altKey) { e.preventDefault(); props(); }
        else if (k === 'Enter') { openSel(); }
        else if (k === 'Backspace') { e.preventDefault(); up(); }
        else if (e.altKey && k === 'ArrowLeft') $('bBack').click();
        else if (e.altKey && k === 'ArrowRight') $('bFwd').click();
        else if (k === 'ArrowDown' || k === 'ArrowUp') {
            e.preventDefault();
            if (!view.length) return;
            let i = Math.min(Math.max(anchor + (k === 'ArrowDown' ? 1 : -1), 0), view.length - 1);
            if (anchor < 0) i = 0;
            anchor = i; sel = new Set([view[i].path]); refreshSel();
            rowsEl.children[i]?.scrollIntoView({ block: 'nearest' });
        }
    });

    async function loadDrivesAndItems() {
        const drives = await loadDrives(); setDriveItems(drives);
        if (cur === null) items = driveItems;
    }

    (async () => {
        try { await loadDrivesAndItems(); } catch (e) { status(e.message, true); }
        await nav(null);
    })();
})();
