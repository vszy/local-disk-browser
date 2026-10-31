(() => {
    const $ = id => document.getElementById(id);
    const BASE = document.querySelector('meta[name=app-base]').content;
    const path = new URLSearchParams(location.search).get('path');

    const MODES = {
        js: 'javascript', mjs: 'javascript', ts: 'text/typescript', jsx: 'javascript', tsx: 'text/typescript',
        json: 'application/json', html: 'htmlmixed', htm: 'htmlmixed', cshtml: 'htmlmixed', razor: 'htmlmixed', xml: 'xml', config: 'xml',
        csproj: 'xml', props: 'xml', targets: 'xml', svg: 'xml', css: 'css',
        cs: 'text/x-csharp', java: 'text/x-java', c: 'text/x-csrc', h: 'text/x-csrc', cpp: 'text/x-c++src', hpp: 'text/x-c++src',
        py: 'python', sql: 'text/x-sql', md: 'markdown', sh: 'shell', bat: 'shell', cmd: 'shell', ps1: 'powershell',
        yml: 'yaml', yaml: 'yaml', ini: 'properties', cfg: 'properties', conf: 'properties', properties: 'properties',
        go: 'go', php: 'application/x-httpd-php', rb: 'ruby'
    };
    const ext = (path || '').split('.').pop().toLowerCase();

    const cm = CodeMirror($('edBox'), {
        value: '', mode: MODES[ext] || null, lineNumbers: true, indentUnit: 4, tabSize: 4, indentWithTabs: false,
        matchBrackets: true, autoCloseBrackets: true, styleActiveLine: true, lineWrapping: false,
        extraKeys: {
            'Ctrl-S': () => save(), 'Cmd-S': () => save(),
            Tab: c => c.somethingSelected() ? c.indentSelection('add') : c.replaceSelection('    ', 'end'),
            'Shift-Tab': c => c.indentSelection('subtract')
        }
    });

    let meta = null, clean = 0, saving = false;
    const status = (m, err) => { $('stLeft').textContent = m; $('stLeft').style.color = err ? '#b00' : ''; };
    const dirty = () => meta && !cm.isClean(clean);
    function refreshState() {
        const d = dirty();
        $('edState').textContent = d ? 'Modified (unsaved)' : '';
        $('edState').classList.toggle('dirty', d);
        $('bSave').disabled = !d || meta.readOnly;
        document.title = (d ? '* ' : '') + (meta ? meta.name : 'Editor');
    }
    function cursorInfo() {
        const c = cm.getCursor();
        $('stRight').textContent = `Ln ${c.line + 1}, Col ${c.ch + 1} | ${meta ? meta.encoding + ' | ' + meta.eol.toUpperCase() : ''}`;
    }

    async function api(method, url, body) {
        const opt = { method, headers: { 'X-Requested-With': 'LocalDiskBrowser' } };
        if (body !== undefined) { opt.headers['Content-Type'] = 'application/json'; opt.body = JSON.stringify(body); }
        const r = await fetch(BASE + url, opt);
        if (r.status === 401) { location.href = BASE + 'Account/Login'; throw new Error('Session expired'); }
        let data = null; try { data = await r.json(); } catch { }
        if (!r.ok) { const e = new Error((data && data.error) || 'HTTP ' + r.status); e.status = r.status; throw e; }
        return data;
    }

    async function load() {
        if (!path) return status('No file specified.', true);
        try {
            meta = await api('GET', 'api/fs/text?path=' + encodeURIComponent(path));
            $('edName').textContent = meta.name; $('edPath').textContent = path;
            cm.setValue(meta.content); cm.clearHistory(); clean = cm.changeGeneration(true);
            if (meta.readOnly) { cm.setOption('readOnly', true); status('File is read-only.', true); } else status('Loaded.');
            refreshState(); cursorInfo(); cm.focus();
        } catch (e) { status(e.message, true); cm.setOption('readOnly', true); }
    }

    async function save(force) {
        if (!meta || saving || meta.readOnly || (!dirty() && !force)) return;
        saving = true; status('Saving...');
        const gen = cm.changeGeneration(true);
        try {
            const r = await api('POST', 'api/fs/save', {
                path, content: cm.getValue(), encoding: meta.encoding, eol: meta.eol, version: meta.version, force: !!force
            });
            meta.version = r.version; clean = gen; status('Saved ' + new Date().toLocaleTimeString());
        } catch (e) {
            if (e.status === 409 && confirm('This file was changed on disk by someone else after you opened it.\n\nOverwrite it with your version?')) {
                saving = false; return save(true);
            }
            status(e.message, true);
        }
        saving = false; refreshState();
    }

    $('bSave').onclick = () => save();
    $('bWrap').onclick = () => { const w = !cm.getOption('lineWrapping'); cm.setOption('lineWrapping', w); $('bWrap').textContent = '↩️ Word wrap: ' + (w ? 'on' : 'off'); };
    cm.on('change', refreshState);
    cm.on('cursorActivity', cursorInfo);
    window.addEventListener('beforeunload', e => { if (dirty()) { e.preventDefault(); e.returnValue = ''; } });
    window.addEventListener('resize', () => cm.refresh());

    load();
})();
