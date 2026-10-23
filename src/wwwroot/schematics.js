'use strict';
/* World tools → Schematics: copy a box of the world into a library file, paste it anywhere (also into another save). */
(function () {
  const { num, fld, fmtN } = WT;

  WT.pendingSel = null;
  window.openSchematicDialog = sel => {
    WT.pendingSel = sel; WT.tab = 'schem';
    document.querySelectorAll('div[style*="z-index:70"]').forEach(e => e.remove());   // close the 3D viewer
    S.sel = '@world'; S.analysis = null; renderSide(true); renderMain();
  };

  WT.register('schem', '🏗 Copy & paste builds', async el => {
    const box = Object.assign({ x1: 0, y1: 60, z1: 0, x2: 31, y2: 100, z2: 31, name: '' }, WT.pendingSel || {});
    const hadSel = !!WT.pendingSel; WT.pendingSel = null;
    const players = await api('/api/world/players').catch(() => []);
    el.append(h('div', { class: 'card muted' }, 'Copy any box of the world (blocks, containers with their contents, lamps, …) into a schematic and paste it somewhere else – in this save or, after opening another save, in a different one. Blocks are stored by name, so the two saves only need to know the same blocks. Rotation is not supported (block orientations are shape specific). Close the game first.'));

    // ── copy form
    const form = h('div', { class: 'card' }, h('h3', { style: 'margin-top:0' }, 'Copy from this world'));
    if (hadSel) form.append(h('div', { class: 'ok', style: 'margin-bottom:6px' }, '✓ Corners taken from your selection in the 3D viewer.'));
    const nameIn = h('input', { type: 'text', placeholder: 'name, e.g. my-base', style: 'width:220px', value: box.name || '' });
    const sizeTxt = h('span', { class: 'muted' });
    const upd = () => { const w = Math.abs(box.x2 - box.x1) + 1, hh = Math.abs(box.y2 - box.y1) + 1, d = Math.abs(box.z2 - box.z1) + 1; sizeTxt.textContent = `${w} × ${hh} × ${d} = ${fmtN(w * hh * d)} blocks`; };
    const n = k => num(box, k, { step: 1, style: 'width:90px', on: upd });
    const plSel = h('select', { onchange: () => { const p = players[Number(plSel.value)]; if (!p) return; box.x1 = p.x - 16; box.x2 = p.x + 16; box.z1 = p.z - 16; box.z2 = p.z + 16; box.y1 = Math.max(0, p.y - 10); box.y2 = Math.min(255, p.y + 40); WT.pendingSel = { ...box, name: nameIn.value }; WT.render($('#main')); } },
      h('option', { value: '' }, '… around a player'), players.map((p, i) => h('option', { value: i }, p.name + ' (' + p.x + ', ' + p.y + ', ' + p.z + ')')));
    form.append(h('div', { class: 'row' }, fld('X from', n('x1')), fld('Y from', n('y1')), fld('Z from', n('z1')), fld('X to', n('x2')), fld('Y to', n('y2')), fld('Z to', n('z2'))),
      h('div', { class: 'row' }, sizeTxt, plSel), h('div', { class: 'row' }, nameIn,
        h('button', { class: 'primary', onclick: () => guarded(async () => {
          const name = nameIn.value.trim(); if (!name) { toast('Give the schematic a name.', 'err'); return; }
          const r = await api('/api/schem/copy', { body: { ...box, name } });
          toast(r.message + (r.notes && r.notes.length ? '\n' + r.notes.join('\n') : ''), 'ok', 12000);
          WT.pendingSel = null; WT.render($('#main'));
        }) }, 'Copy into library')),
      h('div', { class: 'muted', style: 'font-size:12px' }, 'Tip: in the 3D viewer pick the tool "Select area", click two opposite corners, then "Copy selection → schematic…".'));
    upd(); el.append(form);

    // ── library
    const lib = await api('/api/schem/list');
    el.append(h('h2', {}, 'Library'), h('div', { class: 'muted mono' }, lib.dir));
    if (!lib.items.length) el.append(h('div', { class: 'card muted' }, 'No schematics yet.'));
    else el.append(h('table', {}, h('tr', {}, ['Name', 'Size', 'Blocks', 'Tile entities', 'From', 'Created', ''].map(x => h('th', {}, x))),
      lib.items.map(it => it.error ? h('tr', {}, h('td', {}, it.name), h('td', { colspan: 5, class: 'err' }, it.error), h('td', {}, h('button', { class: 'small danger', onclick: () => delIt(it.name) }, 'Delete'))) :
        h('tr', {}, h('td', {}, h('b', {}, it.name)), h('td', {}, `${it.w}×${it.h}×${it.d}`), h('td', {}, fmtN(it.blocks)), h('td', {}, it.tes), h('td', { class: 'muted' }, it.source), h('td', { class: 'muted' }, it.created),
          h('td', { style: 'white-space:nowrap' }, h('button', { class: 'small primary', onclick: () => pasteDialog(it, players) }, 'Paste…'), ' ',
            h('a', { class: 'small', href: url('/api/schem/download', { name: it.name }), download: it.name + '.sdschem' }, h('button', { class: 'small' }, '⬇')), ' ',
            h('button', { class: 'small danger', onclick: () => delIt(it.name) }, '✕'))))));
    const up = h('input', { type: 'file', accept: '.sdschem', style: 'display:none', onchange: () => guarded(async () => {
      const f = up.files[0]; if (!f) return;
      const r = await api('/api/schem/upload?' + qs({ name: f.name.replace(/\.sdschem$/i, '') }), { raw: await f.arrayBuffer() });
      toast(r.message); WT.render($('#main'));
    }) });
    el.append(h('div', { class: 'row', style: 'margin-top:8px' }, h('button', { onclick: () => up.click() }, '⬆ Import .sdschem file…'), up));
    async function delIt(name) { if (await confirmBox('Delete schematic "' + name + '"?', 'The file is removed from the library.', 'Delete', true)) guarded(async () => { await api('/api/schem/delete', { body: { name } }); WT.render($('#main')); }); }
  }, 20);

  function pasteDialog(it, players) {
    const t = { x: 0, y: 70, z: 0, replaceAir: false };
    const out = h('div', { class: 'muted', style: 'min-height:40px;margin:8px 0;white-space:pre-wrap' }, 'Choose the position of the schematic\'s lowest north-west corner (x, y, z).');
    const plSel = h('select', { onchange: () => { const p = players[Number(plSel.value)]; if (p) { t.x = p.x; t.y = p.y; t.z = p.z; render(); } } }, h('option', { value: '' }, '… at a player'), players.map((p, i) => h('option', { value: i }, p.name + ' (' + p.x + ', ' + p.y + ', ' + p.z + ')')));
    const body = h('div', {});
    const call = dry => api('/api/schem/paste', { body: { name: it.name, x: t.x, y: t.y, z: t.z, replaceAir: t.replaceAir, dryRun: dry } });
    const render = () => {
      body.replaceChildren(h('div', { class: 'row' }, fld('X', num(t, 'x', { step: 1 })), fld('Y (height)', num(t, 'y', { step: 1 })), fld('Z', num(t, 'z', { step: 1 })), plSel),
        h('label', { style: 'display:flex;gap:6px;align-items:center' }, h('input', { type: 'checkbox', checked: t.replaceAir, onchange: e => { t.replaceAir = e.target.checked; } }), 'Empty spaces of the schematic also remove existing blocks (cuts the shape out of the terrain)'),
        out,
        h('div', { class: 'row', style: 'justify-content:flex-end' }, h('button', { onclick: closeModal }, 'Close'),
          h('button', { onclick: () => guarded(async () => { const r = await call(true); out.textContent = r.message + (r.skipped ? `\n${r.skipped} position(s) cannot be written.` : '') + (r.notes.length ? '\n' + r.notes.join('\n') : ''); }) }, 'Preview'),
          h('button', { class: 'primary', onclick: () => guarded(async () => {
            if (!WT.needWrite()) return;
            if (!(await confirmBox('Paste "' + it.name + '" at ' + t.x + ', ' + t.y + ', ' + t.z + '?', 'Existing blocks in the target area are replaced. Every changed region file is backed up first (Backups dialog).', 'Paste', true))) return;
            const r = await call(false); closeModal(); toast(r.message + (r.notes.length ? '\n' + r.notes.join('\n') : ''), 'ok', 12000);
          }) }, 'Paste now')));
    };
    render();
    modal(h('div', {}, h('h2', {}, 'Paste "' + it.name + '"'), h('div', { class: 'muted' }, `${it.w} × ${it.h} × ${it.d} blocks, ${fmtN(it.blocks)} placed, ${it.tes} tile entities.`), body), null, false);
  }
})();
