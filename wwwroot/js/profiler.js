(() => {
    const $ = id => document.getElementById(id);
    // App base path (the app runs as an IIS sub-application, never use root-absolute URLs).
    const BASE = document.querySelector('meta[name=app-base]').content;
    const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
    const CF_MSG = 'Blocked by Cloudflare. Connect to the VPN and try again.';
    const MAX_ROWS = 2000;
    const PAUSE_HTML = '<i class="icon-pause" style="color:#e0a800"></i> Pause';
    const RESUME_HTML = '<i class="icon-play" style="color:#1e9e5a"></i> Resume';

    const statusL = $('stLeft'), statusBar = $('status');
    const status = (msg, isErr, hl) => {
        statusL.textContent = msg; statusL.title = msg;
        statusBar.classList.toggle('err', !!isErr); statusBar.classList.toggle('up', !!hl && !isErr);
    };

    // The connection key comes in the URL #hash (never sent to the server in a URL); the page only sends it in the request body.
    const KEY = (location.hash.match(/conn=([A-Za-z0-9_-]+)/) || [])[1] || '';
    let LABEL = '';
    try {
        const c = JSON.parse(localStorage.getItem('ldb.sql.conns'));
        const hit = c && Array.isArray(c.list) && c.list.find(x => x.key === KEY);
        if (hit) LABEL = hit.label + (hit.server ? ' - ' + hit.server : '');
    } catch { }
    $('pfConn').textContent = LABEL;
    document.title = 'SQL Profiler' + (LABEL ? ' - ' + LABEL : '');

    // Same rules as the other pages: CSRF header, 401 -> login, Cloudflare hint. Polling is quiet (no spinner every 2 s).
    async function api(url, body, keepalive) {
        const opt = { method: 'POST', keepalive: !!keepalive, headers: { 'X-Requested-With': 'LocalDiskBrowser', 'Content-Type': 'application/json' }, body: JSON.stringify({ ...body, conn: KEY }) };
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
    }

    let id = null, running = false, paused = false, timer = null, fails = 0, rows = [], sel = null, dirty = false;

    function setState() {
        $('bStart').disabled = running || !KEY; $('bStop').disabled = !running; $('bPause').disabled = !running;
        $('optThisDb').disabled = $('optMin').disabled = running;
        const st = $('pfState');
        st.className = 'pfState ' + (running ? 'run' : 'off');
        st.textContent = running ? (paused ? 'Running (display paused)' : 'Running') : 'Stopped';
        $('pfEmpty').hidden = rows.length > 0;
        if (!rows.length) $('pfEmpty').textContent = running ? 'Waiting for queries...' : (KEY ? 'Not running.' : 'Open the profiler from the SQL editor (Profiler button).');
    }

    // ---------- list ----------
    const shown = () => {
        const f = $('pfFilter').value.trim().toLowerCase();
        return f ? rows.filter(r => (r.sql + ' ' + r.database + ' ' + r.login + ' ' + r.host + ' ' + r.app).toLowerCase().includes(f)) : rows;
    };
    function render() {
        dirty = false;
        const box = $('pfList');
        const atBottom = box.scrollHeight - box.scrollTop - box.clientHeight < 40;
        $('pfRows').innerHTML = shown().map(r =>
            `<tr data-s="${r.seq}" class="${r.seq === sel ? 'sel' : ''}${r.durationMs >= 1000 ? ' slow' : ''}"><td>${r.time}</td><td>${r.type}</td><td class="n">${r.durationMs}</td><td class="n">${r.cpuMs}</td><td class="n">${r.reads}</td><td class="n">${r.rows}</td>`
            + `<td>${esc(r.database)}</td><td>${esc(r.login)}</td><td>${esc(r.host)}</td><td>${esc(r.app)}</td><td class="sqlc" title="${esc(r.sql.slice(0, 300))}">${esc(r.sql.replace(/\s+/g, ' '))}</td></tr>`).join('');
        $('pfEmpty').hidden = rows.length > 0;
        if (atBottom) box.scrollTop = box.scrollHeight;
    }
    $('pfRows').addEventListener('click', e => {
        const tr = e.target.closest('tr'); if (!tr) return;
        sel = +tr.dataset.s;
        // Class toggling only: do not re-render the table on a click.
        $('pfRows').querySelectorAll('tr.sel').forEach(x => x.classList.remove('sel')); tr.classList.add('sel');
        const r = rows.find(x => x.seq === sel); if (!r) return;
        $('pfDetail').innerHTML = `<div class="meta">${r.time} | ${r.type} | ${r.durationMs} ms (CPU ${r.cpuMs} ms) | ${r.reads} reads | ${r.rows} rows | ${esc(r.database)} | ${esc(r.login)} | ${esc(r.host)} | ${esc(r.app)}</div>`
            + `<button id="bCopySql"><i class="icon-copy" style="color:#3a7bd5"></i> Copy SQL</button><pre>${esc(r.sql)}</pre>`;
        $('bCopySql').onclick = async () => {
            try { await navigator.clipboard.writeText(r.sql); }
            catch { const ta = document.createElement('textarea'); ta.value = r.sql; document.body.append(ta); ta.select(); document.execCommand('copy'); ta.remove(); }
            status('SQL copied to clipboard.', false, true);
        };
    });
    $('pfFilter').oninput = () => render();
    $('bClear').onclick = () => {
        rows = []; sel = null; render(); setState();
        $('pfDetail').innerHTML = '<div class="muted pad">Click a row to see its SQL.</div>';
    };

    // ---------- polling ----------
    async function poll() {
        timer = null;
        if (!running) return;
        let again = false;
        try {
            const r = await api('api/sql/prof/poll', { id });
            if (!running) return; // stopped while the request was in flight
            fails = 0;
            if (r.gone) { running = false; id = null; setState(); status('The profiler was stopped (expired, or started in another tab).', true); return; }
            if (r.events.length) {
                rows.push(...r.events);
                if (rows.length > MAX_ROWS) rows = rows.slice(rows.length - MAX_ROWS);
                dirty = true;
                if (!paused) render();
                status(`Running - ${rows.length} statement(s) captured`, false, true);
            }
            again = r.more;
        } catch (e) {
            if (!running) return;
            // A few failures in a row (network, VPN, Cloudflare): stop polling. The server drops the session by itself within 2 minutes.
            if (++fails >= 3) { running = false; setState(); status(e.message.split('\n')[0] + ' - profiler stopped here; the server drops it within 2 minutes.', true); return; }
            status('Poll failed (' + e.message.split('\n')[0] + '), retrying...', true);
        }
        if (running) timer = setTimeout(poll, again ? 0 : 2000);
    }

    async function start() {
        if (!KEY || running) return;
        $('bStart').disabled = true; status('Starting profiler...', false, true);
        try {
            const r = await api('api/sql/prof/start', { thisDb: $('optThisDb').checked, minMs: Math.max(0, parseInt($('optMin').value, 10) || 0) });
            id = r.id; running = true; paused = false; fails = 0; $('bPause').innerHTML = PAUSE_HTML;
            setState();
            status('Profiler running' + (r.database ? ' on database ' + r.database : ' on all databases'), false, true);
            timer = setTimeout(poll, 500);
        } catch (e) { running = false; setState(); status(e.message.split('\n')[0], true); }
    }

    async function stop() {
        if (!running) return;
        running = false; clearTimeout(timer); timer = null;
        const old = id; id = null;
        $('bStop').disabled = true;
        try { await api('api/sql/prof/stop', { id: old }); status('Profiler stopped.', false, true); }
        catch (e) { status('Could not stop: ' + e.message.split('\n')[0], true); }
        setState();
    }

    $('bStart').onclick = start;
    $('bStop').onclick = stop;
    $('bPause').onclick = () => {
        paused = !paused;
        $('bPause').innerHTML = paused ? RESUME_HTML : PAUSE_HTML;
        if (!paused && dirty) render();
        setState();
    };

    // Closing the tab stops the profiler. fetch + keepalive (sendBeacon cannot send the required header). If this never arrives
    // (crash, no network) the server drops the session when polling stops for 2 minutes.
    addEventListener('pagehide', () => {
        if (running && id) { try { api('api/sql/prof/stop', { id }, true).catch(() => { }); } catch { } }
    });

    // Splitter between the list and the SQL detail.
    $('pfSplit').onmousedown = e => {
        e.preventDefault();
        const det = $('pfDetail'), startY = e.clientY, startH = det.offsetHeight;
        const mv = ev => { det.style.flexBasis = Math.max(40, startH + (startY - ev.clientY)) + 'px'; };
        const up = () => { removeEventListener('mousemove', mv); removeEventListener('mouseup', up); };
        addEventListener('mousemove', mv); addEventListener('mouseup', up);
    };

    setState();
    if (KEY) start();
})();
