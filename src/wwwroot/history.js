'use strict';
/* World tools → History: timeline of every backup SaveDoctor made, comparison with the current save, restore single files / chunks / players. */
(function () {
  const { fmtN } = WT;
  const when = t => { const d = new Date(t); return isNaN(d) || d.getFullYear() < 2000 ? '?' : d.toLocaleString(); };
  const REASON = { auto: 'automatic snapshot', manual: 'manual snapshot', 'edit-blocks': 'block editor', 'paste-schematic': 'schematic paste', 'trim-world': 'world trim', 'reset-loot': 'loot reset', 'edit-container': 'container edit', 'remove-damaged-chunks': 'removed damaged chunks', 'restore-history': 'history restore', 'restore-chunks-history': 'history restore (chunks)', 'restore-player-history': 'history restore (player)' };

  WT.register('history', '🕘 History & restore', async el => {
    const r = await api('/api/history/list');
    el.append(h('div', { class: 'card muted' }, 'Every change SaveDoctor makes first copies the old file here, and "Snapshot backup" zips the whole save. Pick any entry to see what is different from the current save and restore single files, single chunks or a single player\'s items / position.'));

    // auto snapshot settings
    const a = r.auto;
    const en = h('input', { type: 'checkbox', checked: a.enabled });
    const hrs = h('input', { type: 'number', value: a.minHours, min: 1, style: 'width:70px' }), keep = h('input', { type: 'number', value: a.keep, min: 1, style: 'width:70px' });
    el.append(h('div', { class: 'card' }, h('h3', { style: 'margin-top:0' }, 'Automatic snapshots'),
      h('div', { class: 'row' }, h('label', { style: 'display:flex;gap:6px;align-items:center' }, en, 'Zip the whole save when I open it, at most every'), hrs, 'hours – keep the newest', keep, h('button', { class: 'small', onclick: () => guarded(async () => { await api('/api/history/auto', { body: { enabled: en.checked, minHours: Number(hrs.value), keep: Number(keep.value) } }); toast('Saved.'); }) }, 'Save'),
        h('button', { class: 'small', onclick: () => guarded(async () => { const s = await api('/api/snapshot', { body: {} }); toast('Snapshot created (' + s.files + ' files, ' + fmtSize(s.size) + ').'); WT.render($('#main')); }) }, 'Snapshot now')),
      h('div', { class: 'muted', style: 'font-size:12px' }, 'No automatic snapshot is taken while the game runs (the files may be half written). Stored in ' + r.root)));

    if (!r.items.length) { el.append(h('div', { class: 'card muted' }, 'No backups yet.')); return; }
    const out = h('div', {});
    el.append(h('h2', {}, 'Timeline'), h('table', {}, h('tr', {}, ['When', 'Type', 'Reason', 'Files', 'Size', ''].map(x => h('th', {}, x))),
      r.items.map(it => h('tr', {}, h('td', {}, when(it.time)), h('td', {}, it.kind === 'zip' ? h('span', { class: 'pill' }, 'full snapshot') : h('span', { class: 'muted' }, 'changed files')),
        h('td', {}, REASON[it.reason] || it.reason, it.sample && it.sample.length ? h('div', { class: 'muted mono', style: 'font-size:11px' }, it.sample.join(', ')) : null), h('td', {}, fmtN(it.files)), h('td', {}, fmtSize(it.size)),
        h('td', {}, h('button', { class: 'small primary', onclick: () => guarded(() => compare(it, out)) }, 'Compare with now'))))), out);
  }, 50);

  async function compare(it, out) {
    out.replaceChildren(h('div', { class: 'muted' }, 'Comparing…'));
    const r = await api('/api/history/diff?' + qs({ a: it.id, b: 'live' }));
    out.replaceChildren(h('h2', {}, 'Differences: ' + (REASON[it.reason] || it.reason) + ' · ' + when(it.time) + ' → now'));
    if (!r.diff.length) { out.append(h('div', { class: 'card' }, h('b', { class: 'ok' }, 'Identical – nothing in the save differs from this backup.'))); out.scrollIntoView(); return; }
    const STX = { changed: ['changed', 'warn'], added: ['new since then', 'ok'], removed: ['deleted since then', 'err'] };
    out.append(h('table', {}, h('tr', {}, ['File', 'What', 'Then', 'Now', ''].map(x => h('th', {}, x))),
      r.diff.map(d => {
        const acts = [];
        if (d.kind === 'region' && d.status !== 'added') acts.push(h('button', { class: 'small', onclick: () => guarded(() => regionDiff(it, d.rel)) }, 'Chunks…'));
        if (d.kind === 'player' && d.status !== 'added' && /\.ttp$/.test(d.rel)) acts.push(h('button', { class: 'small', title: 'Put the toolbelt, backpack and equipment of that time back', onclick: () => guarded(() => restorePlayer(it, d, 'items')) }, 'Items'), h('button', { class: 'small', title: 'Move the player back to where he / she was', onclick: () => guarded(() => restorePlayer(it, d, 'position')) }, 'Position'));
        acts.push(h('button', { class: 'small danger', title: 'Replace the live file with this version', onclick: () => guarded(() => restoreFile(it, d)) }, d.status === 'added' ? 'Remove' : 'Restore file'));
        return h('tr', {}, h('td', { class: 'mono' }, d.label ? h('b', {}, d.label + ' ') : '', d.rel), h('td', { class: STX[d.status][1] }, STX[d.status][0]), h('td', {}, d.status === 'added' ? '–' : fmtSize(d.sizeA)), h('td', {}, d.status === 'removed' ? '–' : fmtSize(d.sizeB)), h('td', { style: 'white-space:nowrap' }, acts));
      })));
    out.scrollIntoView();
  }

  async function restoreFile(it, d) {
    if (!WT.needWrite()) return;
    const msg = d.status === 'added' ? 'This file did not exist in the backup. It will be removed (a backup of the current file is made first).' : 'The live file is replaced by the version from the backup (a backup of the current file is made first).';
    if (!(await confirmBox('Restore ' + d.rel + '?', msg, 'Restore', true))) return;
    const r = await api('/api/history/restore', { body: { a: it.id, rel: d.rel } });
    toast(r.message); await loadFiles();
  }

  async function restorePlayer(it, d, mode) {
    if (!WT.needWrite()) return;
    if (!(await confirmBox(mode === 'items' ? 'Restore items?' : 'Restore position?', (mode === 'items' ? 'Toolbelt, backpack and equipment' : 'The position') + ' of ' + (d.label || d.rel) + ' are replaced by the values from the backup. Everything else stays as it is. (Backup first.)', 'Restore', true))) return;
    const r = await api('/api/history/restore-player', { body: { a: it.id, rel: d.rel, mode } });
    toast(r.message, 'ok', 9000);
  }

  async function regionDiff(it, rel) {
    const r = await api('/api/history/regiondiff?' + qs({ a: it.id, b: 'live', rel }));
    const sel = new Set();
    const body = h('div', {});
    const ST = { changed: '#ffb43c', added: '#4ad66d', removed: '#ff5a5a' };
    const draw = () => {
      body.replaceChildren();
      const cv = h('canvas', { width: 32 * 14, height: 32 * 14, class: 'rg', style: 'image-rendering:pixelated;cursor:pointer' });
      const g = cv.getContext('2d'); g.fillStyle = '#0d1117'; g.fillRect(0, 0, cv.width, cv.height);
      const base = { x: r.rx * 32, z: r.rz * 32 };
      for (const c of r.chunks) {
        const lx = ((c.cx % 32) + 32) % 32, lz = ((c.cz % 32) + 32) % 32;
        g.fillStyle = ST[c.status]; g.fillRect(lx * 14, lz * 14, 13, 13);
        if (sel.has(c.slot)) { g.strokeStyle = '#fff'; g.lineWidth = 2; g.strokeRect(lx * 14 + 1, lz * 14 + 1, 11, 11); }
      }
      const byPos = new Map(r.chunks.map(c => [(((c.cx % 32) + 32) % 32) + ',' + (((c.cz % 32) + 32) % 32), c]));
      const tip = h('div', { class: 'mono muted', style: 'min-height:20px' }, 'click chunks to select them');
      cv.onmousemove = e => { const b = cv.getBoundingClientRect(); const x = Math.floor((e.clientX - b.left) / b.width * 32), z = Math.floor((e.clientY - b.top) / b.height * 32); const c = byPos.get(x + ',' + z); tip.textContent = c ? `chunk ${c.cx}, ${c.cz}: ${c.status}${c.blocks != null ? ' · ' + c.blocks + ' blocks differ' : ''}` : ''; };
      cv.onclick = e => { const b = cv.getBoundingClientRect(); const x = Math.floor((e.clientX - b.left) / b.width * 32), z = Math.floor((e.clientY - b.top) / b.height * 32); const c = byPos.get(x + ',' + z); if (!c) return; if (sel.has(c.slot)) sel.delete(c.slot); else sel.add(c.slot); draw(); };
      body.append(h('div', { class: 'legend' }, h('span', {}, h('i', { style: 'background:' + ST.changed }), 'changed'), h('span', {}, h('i', { style: 'background:' + ST.added }), 'new since then (restoring removes it)'), h('span', {}, h('i', { style: 'background:' + ST.removed }), 'deleted since then')), tip, cv,
        h('div', { class: 'row', style: 'margin-top:8px' }, h('button', { onclick: () => { for (const c of r.chunks) sel.add(c.slot); draw(); } }, 'Select all'), h('button', { onclick: () => { sel.clear(); draw(); } }, 'Select none'),
          h('button', { class: 'danger', disabled: !sel.size, onclick: () => guarded(async () => {
            if (!WT.needWrite()) return;
            if (!(await confirmBox('Restore ' + sel.size + ' chunk(s)?', 'The selected chunks are put back exactly as they were in the backup; everything else in the region file stays. (Backup of the region file first.)', 'Restore chunks', true))) return;
            const res = await api('/api/history/restore-chunks', { body: { a: it.id, rel, slots: [...sel] } }); toast(res.message, 'ok', 9000); closeModal();
          }) }, 'Restore ' + sel.size + ' selected chunk(s)')));
    };
    draw();
    modal(h('div', {}, h('h2', {}, rel), h('div', { class: 'muted' }, `${r.chunks.length} chunk(s) differ from ${it.id}. ${r.inA} chunks then, ${r.inB} now.`), body), null, true);
  }
})();
