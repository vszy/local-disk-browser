(() => {
    const $ = id => document.getElementById(id);
    // App base path (the app runs as an IIS sub-application, never use root-absolute URLs).
    const BASE = document.querySelector('meta[name=app-base]').content;
    const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
    const ic = (name, color) => `<i class="icon-${name}"${color ? ` style="color:${color}"` : ''}></i>`;

    // ---------- status bar ----------
    const statusL = $('stLeft'), statusBar = $('status');
    // hl = yellow highlight for work the user is waiting for or just finished.
    const status = (msg, isErr, hl) => {
        statusL.textContent = msg; statusL.title = msg;
        statusBar.classList.toggle('err', !!isErr); statusBar.classList.toggle('up', !!hl && !isErr);
    };

    // ---------- API (same rules as browser.js: CSRF header, 401 -> login, Cloudflare hint, busy spinner) ----------
    let CUR = null; // the selected saved connection { key, label, server, database }
    const CF_MSG = 'Blocked by Cloudflare. Connect to the VPN and try again.';
    let pending = 0, busyStart = 0, busyTimer = null;
    function busyOn() {
        if (pending++ === 0) {
            busyStart = Date.now(); document.body.classList.add('busy');
            busyTimer = setInterval(() => {
                const s = Math.floor((Date.now() - busyStart) / 1000);
                $('busy').textContent = `Working... ${s}s`; $('busy').hidden = Date.now() - busyStart < 300;
            }, 250);
        }
    }
    function busyOff() {
        if (--pending === 0) { clearInterval(busyTimer); $('busy').hidden = true; document.body.classList.remove('busy'); }
    }
    async function api(method, url, body, signal) {
        // Saved connection: only its key is sent (never the connection string).
        if (CUR && /^api\/sql\/(connect|query|objects|definition)$/.test(url)) body = { ...(body || {}), conn: CUR.key };
        if (CUR && url === 'api/sql/info') url += '?conn=' + encodeURIComponent(CUR.key);
        const opt = { method, signal, headers: { 'X-Requested-With': 'LocalDiskBrowser' } };
        if (body !== undefined) { opt.headers['Content-Type'] = 'application/json'; opt.body = JSON.stringify(body); }
        busyOn();
        try {
            const r = await fetch(BASE + url, opt);
            if (r.status === 401) { location.href = BASE + 'Account/Login'; throw new Error('Session expired'); }
            let data = null;
            try { data = await r.json(); } catch { }
            if (!r.ok) {
                let msg = (data && data.error) || ('HTTP ' + r.status);
                if (!data && /cloudflare/i.test(r.headers.get('Server') || '')) msg = `HTTP ${r.status}: ${CF_MSG}`;
                throw new Error(msg);
            }
            return data;
        } finally { busyOff(); }
    }

    // ---------- clipboard ----------
    async function copyText(t) {
        try { await navigator.clipboard.writeText(t); }
        catch {
            const ta = document.createElement('textarea'); ta.value = t; document.body.append(ta); ta.select();
            document.execCommand('copy'); ta.remove();
        }
        status('Copied to clipboard.', false, true);
    }

    // ---------- tabs ----------
    const TABS = [
        { id: 'editor', label: 'SQL Editor', icon: 'terminal', color: '#3a7bd5' },
        { id: 'tables', label: 'Tables', icon: 'table', color: '#e0a800', kind: 'tables' },
        { id: 'procs', label: 'Stored Procedures', icon: 'file-code', color: '#8e44ad', kind: 'procs' },
        { id: 'views', label: 'Views', icon: 'eye', color: '#1a8fb3', kind: 'views' },
        { id: 'tabletypes', label: 'Table Types', icon: 'table-properties', color: '#d2691e', kind: 'tabletypes' },
        { id: 'functions', label: 'Functions', icon: 'square-function', color: '#2b9e8f', kind: 'functions' },
        { id: 'jobs', label: 'Jobs', icon: 'calendar-clock', color: '#c0392b', kind: 'jobs' }
    ];
    const EXPL = {}; // per kind: { items, loaded, selected, filter }
    TABS.filter(t => t.kind).forEach(t => EXPL[t.kind] = { items: [], loaded: false, selected: null, filter: '' });
    let curTab = 'editor';

    function renderTabs() {
        $('sqlTabs').innerHTML = TABS.map(t =>
            `<div class="tab ${t.id === curTab ? 'on' : ''}" data-t="${t.id}">${ic(t.icon, t.color)} ${esc(t.label)}</div>`).join('');
    }
    $('sqlTabs').addEventListener('click', e => { const t = e.target.closest('.tab'); if (t) showTab(t.dataset.t); });

    function showTab(id) {
        curTab = id; renderTabs();
        const t = TABS.find(x => x.id === id);
        $('paneEditor').hidden = id !== 'editor';
        $('paneExplorer').hidden = id === 'editor';
        if (id === 'editor') { cm.refresh(); cm.focus(); updateExportButtons(); return; }
        $('listHead').textContent = t.label;
        $('listFilter').value = EXPL[t.kind].filter;
        updateExportButtons();
        if (!EXPL[t.kind].loaded) loadList(t.kind); else { renderList(); renderDetail(); }
    }

    // ---------- saved connections ----------
    // The browser keeps only { key, label, server, database } per saved database; the connection string lives encrypted on the server.
    let conns = [], configured = false;
    function loadConns() {
        try {
            const c = JSON.parse(localStorage.getItem('ldb.sql.conns'));
            if (c && Array.isArray(c.list)) { conns = c.list; CUR = conns.find(x => x.key === c.active) || conns[0] || null; }
        } catch { }
    }
    function saveConns() { try { localStorage.setItem('ldb.sql.conns', JSON.stringify({ active: CUR && CUR.key, list: conns })); } catch { } }
    function renderConnSel() {
        $('connSel').innerHTML = conns.map(c => `<option value="${esc(c.key)}" ${CUR && c.key === CUR.key ? 'selected' : ''}>${esc(c.label)}</option>`).join('');
        $('connSel').hidden = !conns.length; $('bConnUpd').hidden = $('bConnDel').hidden = !CUR;
    }
    function resetExplorer() {
        for (const k in EXPL) Object.assign(EXPL[k], { items: [], loaded: false, selected: null });
        for (const k in colCache) delete colCache[k];
        for (const k in defCache) delete defCache[k];
        $('detail').innerHTML = '<div class="muted pad">Select an item.</div>';
        if (curTab !== 'editor') showTab(curTab);
    }
    $('connSel').onchange = () => { CUR = conns.find(c => c.key === $('connSel').value); saveConns(); resetExplorer(); connect(); };

    const b64 = u8 => { let s = ''; u8.forEach(x => s += String.fromCharCode(x)); return btoa(s); };
    const unb64 = t => Uint8Array.from(atob(t), c => c.charCodeAt(0));
    // Hybrid encryption so the connection string is never readable in transit, even by a proxy that terminates TLS (Cloudflare):
    // AES-256-GCM for the payload, its key wrapped with the server's RSA public key. The login name is authenticated data.
    async function encryptEnvelope(payload, conn) {
        if (!(window.crypto && crypto.subtle)) throw new Error('Encryption needs HTTPS (or localhost). Open the site over https.');
        const pk = await api('GET', 'api/sql/pubkey');
        const enc = new TextEncoder();
        const rsa = await crypto.subtle.importKey('spki', unb64(pk.publicKey), { name: 'RSA-OAEP', hash: 'SHA-256' }, false, ['encrypt']);
        const aes = await crypto.subtle.generateKey({ name: 'AES-GCM', length: 256 }, true, ['encrypt']);
        const iv = crypto.getRandomValues(new Uint8Array(12));
        const ct = await crypto.subtle.encrypt({ name: 'AES-GCM', iv, additionalData: enc.encode(pk.user) }, aes, enc.encode(JSON.stringify(payload)));
        const wrapped = await crypto.subtle.encrypt({ name: 'RSA-OAEP' }, rsa, await crypto.subtle.exportKey('raw', aes));
        return { keyId: pk.keyId, wrappedKey: b64(new Uint8Array(wrapped)), iv: b64(iv), ciphertext: b64(new Uint8Array(ct)), conn, fingerprint: pk.fingerprint };
    }

    let panelUpdate = false;
    function openPanel(update) {
        panelUpdate = update;
        $('cpTitle').textContent = update ? 'Update database: ' + CUR.label : 'Add database';
        $('cpLabel').value = update ? CUR.label : ''; $('cpString').value = ''; $('cpMsg').textContent = '';
        $('cpCancel').hidden = !conns.length && !update;
        $('connPanel').hidden = false; (update ? $('cpString') : $('cpLabel')).focus();
    }
    const closePanel = () => { $('cpString').value = ''; $('connPanel').hidden = true; };
    $('bConnAdd').onclick = () => openPanel(false);
    $('bConnUpd').onclick = () => openPanel(true);
    $('cpCancel').onclick = closePanel;
    $('bConnDel').onclick = async () => {
        if (!CUR || !confirm(`Forget "${CUR.label}"? The saved connection string is deleted from the server.`)) return;
        try { await api('POST', 'api/sql/forget', { conn: CUR.key }); } catch (e) { status(e.message, true); return; }
        conns = conns.filter(c => c.key !== CUR.key); CUR = conns[0] || null; saveConns(); renderConnSel(); resetExplorer();
        if (CUR) connect(); else noConnection();
    };
    $('cpSave').onclick = async () => {
        const text = $('cpString').value.trim();
        if (!text) { $('cpMsg').textContent = 'Paste a connection string.'; return; }
        $('cpSave').disabled = true; $('cpMsg').textContent = 'Encrypting and testing the connection...';
        try {
            const nonce = b64(crypto.getRandomValues(new Uint8Array(18)));
            const env = await encryptEnvelope({ connectionString: text, label: $('cpLabel').value.trim(), ts: Date.now(), nonce }, panelUpdate ? CUR.key : null);
            $('cpFp').textContent = 'Server encryption key fingerprint: ' + env.fingerprint; delete env.fingerprint;
            const r = await api('POST', 'api/sql/save', env);
            const item = { key: r.key, label: r.label || r.database || r.server, server: r.server, database: r.database };
            const i = conns.findIndex(c => c.key === r.key);
            if (i >= 0) conns[i] = item; else conns.push(item);
            CUR = item; saveConns(); renderConnSel(); closePanel(); resetExplorer(); connect();
        } catch (e) { $('cpMsg').textContent = e.message; }
        finally { $('cpSave').disabled = false; }
    };

    function noConnection() {
        configured = false; $('bRun').disabled = true;
        $('connInfo').innerHTML = '<span class="bad">No database selected.</span>'; $('connState').textContent = '';
        openPanel(false);
    }

    // ---------- connect ----------
    async function connect() {
        if (!CUR) return noConnection();
        try {
            const i = await api('GET', 'api/sql/info');
            if (!i.configured) { // the server no longer knows this key (file removed, or another login)
                conns = conns.filter(c => c.key !== CUR.key); CUR = conns[0] || null; saveConns(); renderConnSel();
                status('That saved database is no longer on the server. Add it again.', true);
                if (CUR) return connect(); return noConnection();
            }
            configured = true; $('bRun').disabled = running;
            $('connInfo').innerHTML = `<span class="k">Server</span> ${esc(i.server)}${i.ip && i.ip !== i.server ? ` <span class="k">(IP ${esc(i.ip)})</span>` : ''}`
                + ` &nbsp; <span class="k">Database</span> ${esc(i.database || '(default)')} &nbsp; <span class="k">Login</span> ${esc(i.login)}`;
            $('connState').className = 'connState'; $('connState').textContent = 'Connecting...';
            try {
                const c = await api('POST', 'api/sql/connect');
                $('connState').className = 'connState ok';
                $('connState').textContent = 'Connected - SQL Server ' + c.version;
                if (c.ipChanged) status(`Warning: this saved connection was last used from IP ${c.previousIp || '?'}, now from ${c.clientIp}.`, true);
                else status('Connected.', false, true);
                loadList('tables', true); // table names for autocomplete
            } catch (e) {
                $('connState').className = 'connState bad';
                $('connState').textContent = 'Not connected';
                $('connState').title = e.message;
                status('Connection failed: ' + e.message, true);
                setMessages(['Connection failed: ' + e.message]);
            }
        } catch (e) { $('connInfo').textContent = e.message; status(e.message, true); }
    }
    function init() { loadConns(); renderConnSel(); connect(); }

    // ---------- editor + sessions ----------
    const cm = CodeMirror($('edBox'), {
        value: '', mode: 'text/x-mssql', lineNumbers: true, indentUnit: 4, tabSize: 4, indentWithTabs: false,
        matchBrackets: true, autoCloseBrackets: true, styleActiveLine: true,
        extraKeys: {
            F5: () => run(), 'Ctrl-3': () => topRows(500), 'Ctrl-4': () => topRows(1), 'Ctrl-5': () => columnInfo(),
            'Alt-F1': () => showObject(['tables', 'tabletypes']), 'Ctrl-F1': () => showObject(['procs', 'views', 'functions']),
            'Ctrl-R': () => toggleResults(), 'Ctrl-K C': () => commentSel(true), 'Ctrl-K U': () => commentSel(false),
            'Ctrl-Space': () => showHint(),
            Tab: c => c.somethingSelected() ? c.indentSelection('add') : c.replaceSelection('    ', 'end'),
            'Shift-Tab': c => c.indentSelection('subtract')
        }
    });
    cm.on('cursorActivity', () => { const c = cm.getCursor(); $('stRight').textContent = `Ln ${c.line + 1}, Col ${c.ch + 1}`; });
    window.addEventListener('keydown', e => { if (e.key === 'F5') { e.preventDefault(); if (curTab === 'editor') run(); } });

    // Column names are fetched (once per table) only for the tables the query mentions after FROM / JOIN / UPDATE / INTO.
    const colCache = {};
    async function showHint() {
        const names = EXPL.tables.items;
        const used = new Set();
        const re = /\b(?:from|join|update|into)\s+((?:\[[^\]]+\]|\w+)(?:\s*\.\s*(?:\[[^\]]+\]|\w+))*)/gi;
        let m;
        while ((m = re.exec(cm.getValue()))) {
            const w = m[1].replace(/[\[\]\s]/g, '').toLowerCase();
            const hit = names.find(n => { const l = n.toLowerCase(); return l === w || l === 'dbo.' + w || l.endsWith('.' + w); });
            if (hit) used.add(hit);
        }
        await Promise.all([...used].filter(n => !colCache[n]).map(async n => {
            try { colCache[n] = (await api('POST', 'api/sql/definition', { kind: 'tables', name: n })).columns.map(c => c.ColumnName); }
            catch { colCache[n] = []; }
        }));
        const tables = {}, all = new Set();
        for (const n of names) {
            const cols = colCache[n] || [];
            tables[n] = cols; if (n.startsWith('dbo.')) tables[n.slice(4)] = cols;
        }
        for (const n of used) colCache[n].forEach(c => all.add(c));
        tables.__cols = [...all]; // unqualified column names from every table in the query
        cm.showHint({ hint: CodeMirror.hint.sql, completeSingle: false, tables, defaultTable: '__cols' });
    }
    // Auto popup: after a dot, or once a word has 2+ characters (not in comments / strings, not while a popup is already open).
    let hintBusy = false;
    cm.on('inputRead', (c, ch) => {
        if (c.state.completionActive || hintBusy || ch.origin !== '+input') return;
        const t = ch.text[0];
        if (t !== '.' && !/^[\w@#]$/.test(t)) return;
        const cur = c.getCursor(), tok = c.getTokenAt(cur);
        if (/comment|string/.test(tok.type || '')) return;
        if (t !== '.' && tok.string.length < 2) return;
        hintBusy = true;
        showHint().finally(() => { hintBusy = false; });
    });

    function commentSel(on) {
        const from = cm.getCursor('from'), to = cm.getCursor('to');
        if (to.line > from.line && to.ch === 0) to.line--; // a selection ending at column 0 does not include that line
        if (on) cm.lineComment(from, { line: to.line, ch: 0 }, { lineComment: '--' });
        else cm.uncomment(from, { line: to.line, ch: 0 }, { lineComment: '--' });
    }

    // Sessions: several editor tabs, SQL text kept in this browser (never the connection string).
    let sessions = [], activeId = null;
    const newSession = () => ({ id: 's' + Date.now() + Math.floor(Math.random() * 1000), name: 'Query ' + (sessions.length + 1), sql: '', res: null });
    function loadSessions() {
        try {
            const s = JSON.parse(localStorage.getItem('ldb.sql.sessions'));
            if (s && Array.isArray(s.list) && s.list.length) { sessions = s.list.map(x => ({ id: x.id, name: x.name, sql: x.sql || '', res: null })); activeId = s.active; }
        } catch { }
        if (!sessions.length) { sessions = [newSession()]; }
        if (!sessions.some(s => s.id === activeId)) activeId = sessions[0].id;
    }
    let saveTimer = null;
    function saveSessions() {
        const a = active(); if (a) a.sql = cm.getValue();
        try { localStorage.setItem('ldb.sql.sessions', JSON.stringify({ active: activeId, list: sessions.map(s => ({ id: s.id, name: s.name, sql: s.sql })) })); } catch { }
    }
    cm.on('change', () => { clearTimeout(saveTimer); saveTimer = setTimeout(saveSessions, 500); });
    const active = () => sessions.find(s => s.id === activeId);
    function renderSessions() {
        $('sessionBar').innerHTML = sessions.map(s =>
            `<div class="sess ${s.id === activeId ? 'on' : ''}" data-id="${s.id}" title="Double-click to rename"><span>${esc(s.name)}</span><b class="x" data-x="${s.id}">&times;</b></div>`).join('')
            + '<button id="bAddSess" title="New session">+</button>';
    }
    function switchSession(id) {
        saveSessions(); activeId = id; renderSessions();
        cm.setValue(active().sql); cm.clearHistory(); renderResults(); cm.focus(); saveSessions();
    }
    $('sessionBar').addEventListener('click', e => {
        if (e.target.id === 'bAddSess') { saveSessions(); const s = newSession(); sessions.push(s); switchSession(s.id); return; }
        const x = e.target.closest('[data-x]');
        if (x) {
            if (sessions.length === 1) { sessions[0].sql = ''; sessions[0].res = null; cm.setValue(''); renderResults(); return; }
            const i = sessions.findIndex(s => s.id === x.dataset.x), wasActive = sessions[i].id === activeId;
            sessions.splice(i, 1);
            if (wasActive) switchSession(sessions[Math.max(0, i - 1)].id); else { renderSessions(); saveSessions(); }
            return;
        }
        const t = e.target.closest('.sess'); if (t && t.dataset.id !== activeId) switchSession(t.dataset.id);
    });
    $('sessionBar').addEventListener('dblclick', e => {
        const t = e.target.closest('.sess'); if (!t) return;
        const s = sessions.find(x => x.id === t.dataset.id), n = prompt('Session name:', s.name);
        if (n && n.trim()) { s.name = n.trim(); renderSessions(); saveSessions(); }
    });

    // ---------- splitter / toggle results ----------
    let edHeight = 260, resultsHidden = false;
    function applyLayout() {
        const wrap = $('edWrap');
        $('results').hidden = $('splitter').hidden = resultsHidden;
        wrap.style.height = resultsHidden ? '' : edHeight + 'px';
        wrap.style.flex = resultsHidden ? '1 1 auto' : '0 0 auto';
        cm.refresh();
    }
    function toggleResults() { resultsHidden = !resultsHidden; applyLayout(); }
    $('splitter').addEventListener('mousedown', e => {
        e.preventDefault();
        const startY = e.clientY, startH = edHeight;
        const move = ev => { edHeight = Math.max(60, Math.min(innerHeight - 220, startH + ev.clientY - startY)); applyLayout(); };
        const up = () => { removeEventListener('mousemove', move); removeEventListener('mouseup', up); };
        addEventListener('mousemove', move); addEventListener('mouseup', up);
    });

    // ---------- running SQL ----------
    let running = false, abortCtl = null;
    function setRunning(on) {
        running = on; $('bRun').disabled = on || !configured; $('bCancel').disabled = !on;
    }
    // Statements that can destroy a lot with one keystroke ask first (comments and strings are ignored when checking).
    function riskyKinds(sql) {
        const clean = sql.replace(/--.*$/gm, '').replace(/\/\*[\s\S]*?\*\//g, '').replace(/'(?:[^']|'')*'/g, "''");
        const kinds = new Set();
        for (const st of clean.split(/;|^\s*go\s*$/gim)) {
            const s = st.trim().toLowerCase(); if (!s) continue;
            let first = (s.match(/^\w+/) || [''])[0];
            if (first === 'with') first = (s.match(/\b(update|delete)\b/) || [''])[1] || first;
            if ((first === 'update' || first === 'delete') && !/\bwhere\b/.test(s)) kinds.add(first.toUpperCase() + ' without WHERE (affects ALL rows)');
            if (first === 'truncate' || first === 'drop') kinds.add(first.toUpperCase());
        }
        return [...kinds];
    }
    async function run(sqlOverride) {
        if (running || !configured) return;
        const sql = (sqlOverride ?? (cm.somethingSelected() ? cm.getSelection() : cm.getValue())).trim();
        if (!sql) return status('Nothing to run.', true);
        const risky = riskyKinds(sql);
        if (risky.length && !confirm('This statement is risky:\n\n - ' + risky.join('\n - ') + '\n\nRun it anyway?')) return;
        const sess = active();
        if (resultsHidden) { resultsHidden = false; applyLayout(); } // a run always reveals the results
        setRunning(true); abortCtl = new AbortController();
        status('Running query...', false, true);
        try {
            const r = await api('POST', 'api/sql/query', { sql }, abortCtl.signal);
            sess.res = { token: r.token, sets: r.sets, messages: r.messages, elapsedMs: r.elapsedMs, idx: r.sets.length ? 0 : -1 };
            if (sess.id === activeId) renderResults();
            const rows = r.sets.reduce((a, s) => a + s.total, 0);
            status(`Done in ${(r.elapsedMs / 1000).toFixed(2)} s - ${r.sets.length} result set(s), ${rows.toLocaleString()} row(s)`, false, true);
        } catch (e) {
            if (e.name === 'AbortError') { status('Cancelled.', false, true); sess.res = { token: null, sets: [], messages: ['Cancelled.'], idx: -1 }; }
            else { status(e.message.split('\n')[0], true); sess.res = { token: null, sets: [], messages: [e.message], idx: -1, error: true }; }
            if (sess.id === activeId) renderResults();
        } finally { setRunning(false); abortCtl = null; }
    }
    $('bRun').onclick = () => run();
    $('bCancel').onclick = () => { if (abortCtl) abortCtl.abort(); };

    // Shortcut queries. Names come from the selection in the editor.
    const selText = () => cm.getSelection().trim();
    function topRows(n) {
        const t = selText(); if (!t) return status('Select a table name first.', true);
        run(`SELECT TOP ${n} * FROM ${t}`);
    }
    function columnInfo() {
        const t = selText(); if (!t) return status('Select TableName or TableName,ColumnName first.', true);
        const [tbl, col] = t.split(',').map(x => x.trim().replace(/[\[\]]/g, '').split('.').pop());
        const q = s => "'" + s.replace(/'/g, "''") + "'";
        run(`SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE, IS_NULLABLE, COLUMN_DEFAULT\n`
            + `FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = ${q(tbl)}${col ? ` AND COLUMN_NAME LIKE ${q('%' + col + '%')}` : ''} ORDER BY ORDINAL_POSITION`);
    }
    // Alt+F1 / Ctrl+F1: open the selected name in the matching explorer tab.
    async function showObject(kinds) {
        const t = selText().replace(/[\[\]]/g, ''); if (!t) return status('Select a name first.', true);
        const want = t.toLowerCase(), bare = want.split('.').pop();
        for (const k of kinds) {
            const st = EXPL[k];
            if (!st.loaded) await loadList(k, true);
            const hit = st.items.find(n => n.toLowerCase() === want) || st.items.find(n => n.toLowerCase().split('.').pop() === bare);
            if (hit) { st.selected = hit; showTab(TABS.find(x => x.kind === k).id); loadDefinition(k, hit); return; }
        }
        status(`"${t}" not found in ${kinds.map(k => TABS.find(x => x.kind === k).label).join(' / ')}.`, true);
    }

    // ---------- results ----------
    const NUM = /^(int|bigint|smallint|tinyint|decimal|numeric|float|real|money|smallmoney)$/;
    let selRows = new Set(), lastRow = -1;
    const curRes = () => { const s = active(); return s && s.res; };
    function setMessages(msgs) { const s = active(); s.res = { token: null, sets: [], messages: msgs, idx: -1 }; renderResults(); }

    function renderResults() {
        const r = curRes(); selRows = new Set(); lastRow = -1;
        updateExportButtons();
        if (!r) { $('resTabs').innerHTML = ''; $('resBody').innerHTML = '<div class="muted pad">Run a query to see results.</div>'; return; }
        $('resTabs').innerHTML = r.sets.map((s, i) => `<div class="rtab ${i === r.idx ? 'on' : ''}" data-i="${i}">Result ${i + 1} <span class="muted">(${s.total.toLocaleString()})</span></div>`).join('')
            + `<div class="rtab ${r.idx === -1 ? 'on' : ''}" data-i="-1">Messages</div>`
            + (r.elapsedMs != null ? `<span class="grow"></span><span class="muted pad">${(r.elapsedMs / 1000).toFixed(2)} s</span>` : '');
        if (r.idx === -1) {
            $('resBody').innerHTML = `<pre class="msgs ${r.error ? 'bad' : ''}">${esc(r.messages.length ? r.messages.join('\n') : '(Commands completed successfully.)')}</pre>`;
            return;
        }
        const s = r.sets[r.idx];
        const head = s.columns.map((c, i) => `<th data-c="${i}" title="${esc(s.types[i])}">${esc(c)}</th>`).join('');
        const body = s.rows.map((row, ri) => `<tr data-r="${ri}"><td class="rn">${ri + 1}</td>${row.map((v, ci) =>
            v === null ? `<td class="null" data-c="${ci}">NULL</td>` : `<td class="${NUM.test(s.types[ci]) ? 'num' : ''}" data-c="${ci}">${esc(v)}</td>`).join('')}</tr>`).join('');
        const note = s.total > s.shown ? `<div class="note">Showing the first ${s.shown.toLocaleString()} of ${s.total.toLocaleString()} rows. Excel / CSV contain ${s.stored.toLocaleString()} rows.</div>` : '';
        $('resBody').innerHTML = note + `<table class="rs"><thead><tr><th class="rn">#</th>${head}</tr></thead><tbody>${body}</tbody></table>`;
    }
    $('resTabs').addEventListener('click', e => {
        const t = e.target.closest('.rtab'); const r = curRes(); if (!t || !r) return;
        r.idx = +t.dataset.i; renderResults();
    });
    function refreshRowSel() {
        $('resBody').querySelectorAll('tbody tr').forEach(tr => tr.classList.toggle('sel', selRows.has(+tr.dataset.r)));
    }
    $('resBody').addEventListener('click', e => {
        const tr = e.target.closest('tbody tr'); if (!tr) return;
        const i = +tr.dataset.r;
        if (e.shiftKey && lastRow >= 0) { if (!e.ctrlKey) selRows.clear(); for (let j = Math.min(i, lastRow); j <= Math.max(i, lastRow); j++) selRows.add(j); }
        else if (e.ctrlKey) { selRows.has(i) ? selRows.delete(i) : selRows.add(i); lastRow = i; }
        else { selRows = new Set([i]); lastRow = i; }
        refreshRowSel();
    });
    $('resBody').addEventListener('contextmenu', e => {
        const r = curRes(); if (!r || r.idx < 0) return;
        const td = e.target.closest('td, th'), tr = e.target.closest('tbody tr');
        if (!td) return;
        e.preventDefault();
        const col = td.dataset.c === undefined ? -1 : +td.dataset.c, s = r.sets[r.idx];
        if (tr && !selRows.has(+tr.dataset.r)) { selRows = new Set([+tr.dataset.r]); lastRow = +tr.dataset.r; refreshRowSel(); }
        const rowsTsv = withHead => {
            const idx = [...selRows].sort((a, b) => a - b);
            const lines = idx.map(i => s.rows[i].map(v => v === null ? '' : String(v)).join('\t'));
            return (withHead ? s.columns.join('\t') + '\n' : '') + lines.join('\n');
        };
        const colTsv = withHead => (withHead ? s.columns[col] + '\n' : '') + s.rows.map(row => row[col] === null ? '' : String(row[col])).join('\n');
        const items = [];
        if (tr && col >= 0) items.push(['Copy Cell', () => copyText(s.rows[+tr.dataset.r][col] === null ? '' : String(s.rows[+tr.dataset.r][col]))]);
        if (selRows.size) { items.push(['Copy Row', () => copyText(rowsTsv(false))]); items.push(['Copy Row with Headers', () => copyText(rowsTsv(true))]); }
        if (col >= 0) { items.push(['Copy Column', () => copyText(colTsv(false))]); items.push(['Copy Column with Header', () => copyText(colTsv(true))]); }
        showCtx(e.clientX, e.clientY, items);
    });
    function showCtx(x, y, items) {
        const m = $('ctx');
        m.innerHTML = items.map((it, i) => `<div data-i="${i}">${esc(it[0])}</div>`).join('');
        m.hidden = false;
        m.style.left = Math.min(x, innerWidth - m.offsetWidth - 4) + 'px';
        m.style.top = Math.min(y, innerHeight - m.offsetHeight - 4) + 'px';
        m.onclick = ev => { const d = ev.target.closest('div[data-i]'); if (d) { m.hidden = true; items[+d.dataset.i][1](); } };
    }
    document.addEventListener('click', () => { $('ctx').hidden = true; });
    document.addEventListener('keydown', e => { if (e.key === 'Escape') $('ctx').hidden = true; });

    // ---------- Excel / CSV ----------
    function updateExportButtons() {
        const r = curRes(), ok = curTab === 'editor' && r && r.token && r.idx >= 0;
        $('bXlsx').disabled = $('bCsv').disabled = !ok;
    }
    function exportSet(fmt) {
        const r = curRes(); if (!r || !r.token || r.idx < 0) return;
        location.href = `${BASE}api/sql/export?token=${r.token}&set=${r.idx}&format=${fmt}`;
        status('Download started.', false, true);
    }
    $('bXlsx').onclick = () => exportSet('xlsx');
    $('bCsv').onclick = () => exportSet('csv');

    // ---------- explorer (tables, procedures, views, table types, functions, jobs) ----------
    const kindOf = () => TABS.find(t => t.id === curTab).kind;
    async function loadList(kind, quiet) {
        const st = EXPL[kind];
        if (!quiet) { $('listItems').innerHTML = '<div class="muted pad">Loading...</div>'; }
        try {
            st.items = await api('POST', 'api/sql/objects', { kind });
            st.loaded = true;
        } catch (e) {
            if (!quiet) { $('listItems').innerHTML = `<div class="bad pad">${esc(e.message)}</div>`; }
            status(e.message.split('\n')[0], true); return;
        }
        if (!quiet && kindOf() === kind) { renderList(); renderDetail(); }
        else if (kindOf() === kind) renderList();
    }
    function renderList() {
        const kind = kindOf(), st = EXPL[kind], f = st.filter.toLowerCase();
        const shown = st.items.filter(n => n.toLowerCase().includes(f));
        $('listHead').textContent = `${TABS.find(t => t.kind === kind).label} (${st.items.length})`;
        $('listItems').innerHTML = shown.length
            ? shown.map(n => `<div class="item ${n === st.selected ? 'on' : ''}" data-n="${esc(n)}">${esc(n)}</div>`).join('')
            : `<div class="muted pad">${st.items.length ? 'No match.' : 'None found.'}</div>`;
    }
    $('listFilter').addEventListener('input', () => { EXPL[kindOf()].filter = $('listFilter').value; renderList(); });
    $('bListRefresh').onclick = () => { const k = kindOf(); EXPL[k].loaded = false; EXPL[k].selected = null; $('detail').innerHTML = '<div class="muted pad">Select an item.</div>'; loadList(k); };
    $('listItems').addEventListener('click', e => {
        const it = e.target.closest('.item'); if (!it) return;
        const kind = kindOf(); EXPL[kind].selected = it.dataset.n; renderList(); loadDefinition(kind, it.dataset.n);
    });

    const defCache = {};
    async function loadDefinition(kind, name) {
        $('detail').innerHTML = `<div class="muted pad">Loading ${esc(name)}...</div>`;
        try {
            const d = await api('POST', 'api/sql/definition', { kind, name });
            defCache[kind] = { name, d, showScript: false };
        } catch (e) { $('detail').innerHTML = `<div class="bad pad">${esc(e.message)}</div>`; return; }
        if (kindOf() === kind) renderDetail();
    }
    const table = (heads, rows, cells) => `<table class="def"><thead><tr>${heads.map(h => `<th>${esc(h)}</th>`).join('')}</tr></thead><tbody>`
        + rows.map(r => `<tr>${cells(r).map(c => `<td>${esc(c)}</td>`).join('')}</tr>`).join('') + '</tbody></table>';
    const yn = v => v ? 'Yes' : 'No';
    const quoteName = n => n.split('.').map(p => '[' + p.replace(/\]/g, ']]') + ']').join('.');
    const SIZED = /^(char|varchar|nchar|nvarchar|binary|varbinary|decimal|numeric|datetime2|time|datetimeoffset)$/i;
    function createScript(name, d, isType) {
        const lines = d.columns.map(c => `    [${c.ColumnName}] ${SIZED.test(c.DataType) && c.Size !== '' ? `${c.DataType}(${c.Size})` : c.DataType}`
            + `${c.IsIdentity ? ' IDENTITY(1,1)' : ''}${c.IsNullable ? ' NULL' : ' NOT NULL'}${c.DefaultValue ? ' DEFAULT ' + c.DefaultValue : ''}`);
        if (d.primaryKeys.length) lines.push(`    PRIMARY KEY (${d.primaryKeys.map(p => '[' + p.ColumnName + ']').join(', ')})`);
        return `CREATE ${isType ? 'TYPE ' + quoteName(name) + ' AS TABLE' : 'TABLE ' + quoteName(name)}\n(\n${lines.join(',\n')}\n)`;
    }
    function renderDetail() {
        const kind = kindOf(), c = defCache[kind], st = EXPL[kind];
        if (!st.selected || !c || c.name !== st.selected) { $('detail').innerHTML = '<div class="muted pad">Select an item.</div>'; return; }
        const d = c.d, name = c.name;
        let h = `<div class="dhead"><h3>${esc(name)}</h3><span class="grow"></span>`;
        if (kind === 'tables' || kind === 'tabletypes') h += `<button id="bScript">${c.showScript ? 'Hide CREATE Script' : 'Create Definition'}</button>`;
        if (kind === 'tables') h += `<button id="bTop">Select Top 500</button>`;
        h += '</div>';
        if (kind === 'tables' || kind === 'tabletypes') {
            if (c.showScript) h += `<div class="scriptBox"><button class="copy" data-copy="script">Copy</button><pre>${esc(createScript(name, d, kind === 'tabletypes'))}</pre></div>`;
            h += '<h4>Columns</h4>' + (kind === 'tables'
                ? table(['Column', 'Type', 'Size', 'Nullable', 'Identity', 'Default'], d.columns, r => [r.ColumnName, r.DataType, r.Size, yn(r.IsNullable), yn(r.IsIdentity), r.DefaultValue ?? ''])
                : table(['Column', 'Type', 'Size', 'Nullable'], d.columns, r => [r.ColumnName, r.DataType, r.Size, yn(r.IsNullable)]));
            if (d.primaryKeys.length) h += '<h4>Primary Key</h4>' + table(['Constraint', 'Column'], d.primaryKeys, r => [r.ConstraintName, r.ColumnName]);
            if (d.foreignKeys && d.foreignKeys.length) h += '<h4>Foreign Keys</h4>' + table(['Constraint', 'Column', 'References Table', 'References Column'], d.foreignKeys, r => [r.ConstraintName, r.ColumnName, r.ReferencedTable, r.ReferencedColumn]);
            if (d.indexes && d.indexes.length) h += '<h4>Indexes</h4>' + table(['Index', 'Type', 'Unique', 'Columns'], d.indexes, r => [r.IndexName, r.IndexType, yn(r.IsUnique), r.Columns]);
        } else if (kind === 'jobs') {
            h += d.info ? `<p><b>Enabled:</b> ${yn(d.info.Enabled)} &nbsp; <b>Description:</b> ${esc(d.info.Description || '-')}</p>` : '';
            h += '<h4>Steps</h4>' + table(['#', 'Step Name', 'Subsystem', 'Command'], d.steps, r => [r.StepId, r.StepName, r.Subsystem, r.Command]).replace('<td>', '<td>');
            if (d.schedules.length) h += '<h4>Schedules</h4>' + table(['Schedule', 'Enabled', 'Frequency', 'Start Time'], d.schedules, r => [r.ScheduleName, yn(r.Enabled), r.Frequency, r.StartTime]);
        } else {
            h += `<div class="scriptBox"><button class="copy" data-copy="text">Copy</button><pre>${esc(d.text)}</pre></div>`;
        }
        $('detail').innerHTML = h;
    }
    $('detail').addEventListener('click', e => {
        const kind = kindOf(), c = defCache[kind]; if (!c) return;
        if (e.target.id === 'bScript') { c.showScript = !c.showScript; renderDetail(); }
        else if (e.target.id === 'bTop') {
            cm.setValue(`SELECT TOP 500 * FROM ${quoteName(c.name)}`); showTab('editor'); run();
        } else if (e.target.dataset.copy) copyText(e.target.dataset.copy === 'script' ? createScript(c.name, c.d, kind === 'tabletypes') : c.d.text);
    });

    // ---------- start ----------
    loadSessions(); renderSessions(); renderTabs(); applyLayout();
    cm.setValue(active().sql);
    window.addEventListener('beforeunload', saveSessions);
    init();
})();
