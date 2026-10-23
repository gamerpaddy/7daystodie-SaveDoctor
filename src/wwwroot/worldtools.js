'use strict';
/* SaveDoctor world tools: search & statistics, heat maps, container editor, and the tab shell the other world tools plug into.
   Uses the helpers of index.html (h, api, qs, toast, guarded, confirmBox, modal, closeModal, itemIcon, itemPicker, newItem, fmtSize, S). */
(function () {
  const WT = window.WT = { tabs: [], tab: 'search', state: {} };
  WT.register = (id, label, render, order) => { WT.tabs.push({ id, label, render, order: order ?? 50 }); WT.tabs.sort((a, b) => a.order - b.order); };

  // ───────── small shared helpers
  const num = (obj, key, o = {}) => h('input', { type: 'number', value: obj[key], step: o.step ?? 'any', min: o.min, max: o.max, style: o.style || 'width:100px', onchange: e => { obj[key] = e.target.value === '' ? 0 : Number(e.target.value); o.on && o.on(); } });
  const fld = (label, ...inputs) => h('div', { class: 'field' }, h('label', {}, label), ...inputs);
  const fmtN = n => Number(n).toLocaleString();
  WT.num = num; WT.fld = fld; WT.fmtN = fmtN;
  WT.writeBlocked = () => S.state && S.state.serverRunning && !S.state.allowWhileRunning;
  WT.needWrite = () => { if (WT.writeBlocked()) { toast('The game / server is running – stop it first (or tick "allow writes while game runs").', 'err'); return false; } return true; };

  /** Opens SaveDoctor's region view for a world position (block coordinates). */
  WT.gotoBlock = async (x, z) => {
    const cx = Math.floor(x / 16), cz = Math.floor(z / 16);
    const rx = Math.floor(cx / 32), rz = Math.floor(cz / 32);
    const rel = `Region/r.${rx}.${rz}.7rg`;
    S.regionFocus = { x, z };
    await selectFile(rel);
  };

  WT.render = function (m) {
    m.replaceChildren();
    m.append(h('h1', {}, 'World tools'), h('div', { class: 'sub' }, 'Tools that work on the whole world: search, statistics, container editing, schematics, trimming, history and players/claims. Write operations make a backup of every file they touch first.'));
    const tabs = h('div', { class: 'tabs' }, WT.tabs.map(t => h('div', { class: 'tab' + (WT.tab === t.id ? ' sel' : ''), onclick: () => { WT.tab = t.id; WT.render($('#main')); } }, t.label)));
    const body = h('div', { id: 'wtbody' });
    m.append(tabs, body);
    const t = WT.tabs.find(x => x.id === WT.tab) || WT.tabs[0];
    guarded(async () => t.render(body));
  };

  // ───────── index status helper
  async function waitIndex(el, force) {
    const note = h('div', { class: 'card muted' }, 'Indexing the world…');
    el.append(note);
    for (let i = 0; i < 4000; i++) {
      const r = await api('/api/world/index' + (force && i === 0 ? '?force=1' : ''));
      if (r.error) { note.textContent = 'Indexing failed: ' + r.error; throw new Error(r.error); }
      if (!r.building) { note.remove(); return r; }
      note.textContent = `Indexing the world… ${r.done}/${r.total} region files`;
      await new Promise(res => setTimeout(res, 400));
      if (!el.isConnected) throw new Error('cancelled');
    }
  }
  WT.waitIndex = waitIndex;

  // ───────── search & statistics tab
  function posLink(x, y, z) {
    return h('a', { href: '#', class: 'mono', title: 'Show this region in the region view', onclick: e => { e.preventDefault(); guarded(() => WT.gotoBlock(x, z)); } }, `${x}, ${y ?? '?'}, ${z}`);
  }
  WT.posLink = posLink;

  WT.register('search', '🔍 Search & statistics', async el => {
    const info = await waitIndex(el);
    const st = await api('/api/world/stats');
    el.append(h('div', { class: 'cards' },
      h('div', { class: 'stat' }, h('b', {}, fmtN(st.chunks)), h('span', {}, 'chunks in ' + st.regions + ' regions')),
      h('div', { class: 'stat' }, h('b', {}, fmtN(st.solid)), h('span', {}, 'blocks placed')),
      h('div', { class: 'stat' }, h('b', {}, fmtN(st.containers)), h('span', {}, 'storage containers (' + fmtN(st.containersWithItems) + ' with items)')),
      h('div', { class: 'stat' }, h('b', {}, fmtN(st.tileEntities)), h('span', {}, 'tile entities')),
      h('div', { class: 'stat' }, h('b', {}, fmtN(st.entities)), h('span', {}, 'zombies / animals / loot bags in chunks'))));
    if (st.unsplit || st.bad) el.append(h('div', { class: 'muted', style: 'margin-bottom:8px' }, `${st.unsplit} chunk(s) contain tile entities that cannot be split (vending machines, generators…) – their containers are not listed. ${st.bad} damaged chunk(s) skipped. Indexed in ${info.seconds.toFixed(1)} s.`));

    // search form
    const kind = h('select', {}, [['item', 'an item (in containers, players, vehicles)'], ['block', 'a block type'], ['container', 'a container type / loot list'], ['entity', 'zombies / animals']].map(([v, l]) => h('option', { value: v }, l)));
    const q = h('input', { type: 'text', placeholder: 'part of a name, e.g. ammo9mm, gunPistol, cntSafe, terrStone, zombieBoe', style: 'width:min(420px,100%)' });
    const out = h('div', { style: 'margin-top:10px' });
    const go = () => guarded(async () => { out.replaceChildren(h('div', { class: 'muted' }, 'Searching…')); await runSearch(kind.value, q.value, out); });
    q.addEventListener('keydown', e => { if (e.key === 'Enter') go(); });
    el.append(h('h2', {}, 'Find anything'), h('div', { class: 'row' }, 'Find', kind, q, h('button', { class: 'primary', onclick: go }, 'Search'), h('button', { onclick: () => guarded(async () => { out.replaceChildren(); await heatCard(out, kind.value, q.value); }) }, '🌡 Show as heat map')), out);

    // heat map shortcuts
    el.append(h('h2', {}, 'Heat maps'), h('div', { class: 'row' }, [['solid', 'Where players built / terrain mass'], ['container', 'Containers'], ['tileentity', 'Tile entities'], ['entity', 'Creatures']].map(([k, l]) => h('button', { onclick: () => guarded(async () => { out.replaceChildren(); await heatCard(out, k, ''); out.scrollIntoView(); }) }, l))));

    // tops
    const tbl = (rows, cols) => h('table', {}, h('tr', {}, cols.map(c => h('th', {}, c[0]))), rows.map(r => h('tr', {}, cols.map(c => h('td', {}, c[1](r))))));
    el.append(h('h2', {}, 'Most common'),
      h('div', { style: 'display:flex;gap:18px;flex-wrap:wrap;align-items:flex-start' },
        h('div', {}, h('h3', {}, 'Blocks'), tbl(st.topBlocks.slice(0, 30), [['Block', r => r.name], ['Count', r => fmtN(r.count)], ['%', r => r.pct]])),
        h('div', {}, h('h3', {}, 'Tile entities'), tbl(st.topTileEntities.slice(0, 30), [['Block', r => r.name], ['Count', r => fmtN(r.count)]])),
        h('div', {}, h('h3', {}, 'Items inside containers'), tbl(st.topItems.slice(0, 30), [['Item', r => r.name], ['Total', r => fmtN(r.count)]]), h('h3', {}, 'Creatures'), tbl(st.topEntities.slice(0, 15), [['Class', r => r.name], ['Count', r => fmtN(r.count)]]))));
  }, 10);

  async function runSearch(kind, text, out) {
    if (!text.trim()) { out.replaceChildren(h('div', { class: 'muted' }, 'Type a name first.')); return; }
    let r; for (;;) { r = await api('/api/world/search?' + qs({ kind, q: text })); if (!r.building) break; await new Promise(res => setTimeout(res, 400)); }
    out.replaceChildren();
    const openC = (x, y, z) => h('button', { class: 'small', onclick: () => guarded(() => openContainer(x, y, z)) }, 'Open / edit');
    if (kind === 'item') {
      out.append(h('div', { class: 'muted' }, `${r.totalContainers} container(s) hold a matching item${r.truncated ? ' (first 500 shown)' : ''}; ${r.carriers.length} player / vehicle / drone inventories.`));
      if (r.carriers.length) out.append(h('h3', {}, 'Players, vehicles & drones'), h('table', {}, h('tr', {}, ['Owner', 'Where', 'Items'].map(x => h('th', {}, x))),
        r.carriers.map(c => h('tr', {}, h('td', {}, c.player || '?'), h('td', {}, c.where + (c.slotGroup ? ' – ' + c.slotGroup : '') + (c.x != null ? ' @ ' + Math.round(c.x) + ', ' + Math.round(c.z) : '')), h('td', {}, c.matches.map(m => m.name + ' ×' + fmtN(m.count) + (m.quality ? ' (q' + m.quality + ')' : '')).join(', '))))));
      if (r.results.length) out.append(h('h3', {}, 'Containers'), h('table', {}, h('tr', {}, ['Position', 'Container', 'Loot list', 'Matches', ''].map(x => h('th', {}, x))),
        r.results.map(c => h('tr', {}, h('td', {}, posLink(c.x, c.y, c.z)), h('td', {}, c.block), h('td', { class: 'muted' }, c.loot || ''), h('td', {}, c.matches.map(m => m.name + ' ×' + fmtN(m.count) + (m.quality ? ' (q' + m.quality + ')' : '')).join(', ')), h('td', {}, openC(c.x, c.y, c.z))))));
      if (!r.results.length && !r.carriers.length) out.append(h('div', { class: 'card' }, 'Nothing found.'));
    } else if (kind === 'block') {
      out.append(h('div', { class: 'muted' }, `${r.types.length} matching block type(s) in ${fmtN(r.totalChunks)} chunk(s).`),
        h('table', {}, h('tr', {}, ['Block', 'Count'].map(x => h('th', {}, x))), r.types.map(t => h('tr', {}, h('td', {}, t.name), h('td', {}, fmtN(t.count))))),
        h('h3', {}, 'Chunks with the most (centre of chunk)'), h('table', {}, h('tr', {}, ['Position', 'Chunk', 'Matching blocks'].map(x => h('th', {}, x))), r.chunks.slice(0, 100).map(c => h('tr', {}, h('td', {}, posLink(c.x, null, c.z)), h('td', { class: 'muted' }, c.cx + ', ' + c.cz), h('td', {}, fmtN(c.count))))));
    } else if (kind === 'container') {
      out.append(h('div', { class: 'muted' }, `${fmtN(r.total)} matching container(s)${r.total > r.results.length ? ' (first 500 shown)' : ''}.`),
        h('table', {}, h('tr', {}, ['Position', 'Block', 'Loot list', 'Filled', 'State', ''].map(x => h('th', {}, x))), r.results.map(c => h('tr', {}, h('td', {}, posLink(c.x, c.y, c.z)), h('td', {}, c.block), h('td', { class: 'muted' }, c.loot || ''), h('td', {}, c.used + '/' + c.slots),
          h('td', { class: 'muted' }, c.playerOwned ? 'player placed' : c.touched ? 'looted / touched' : 'untouched'), h('td', {}, openC(c.x, c.y, c.z))))));
    } else if (kind === 'entity') {
      out.append(h('div', { class: 'muted' }, `${fmtN(r.total)} creature(s) saved inside chunks (roaming zombies of the current horde/spawner are not stored there).`),
        h('div', { class: 'row' }, r.byName.map(b => h('span', { class: 'pill' }, b.name + ' ×' + b.count))),
        h('table', {}, h('tr', {}, ['Class', 'Position'].map(x => h('th', {}, x))), r.results.map(e => h('tr', {}, h('td', {}, e.name), h('td', {}, posLink(e.x, e.y, e.z))))));
    }
  }

  // ───────── heat map
  async function heatCard(out, kind, q) {
    let r; for (;;) { r = await api('/api/world/heat?' + qs({ kind, q })); if (!r.building) break; await new Promise(res => setTimeout(res, 400)); }
    const w = r.maxCx - r.minCx + 1, hh = r.maxCz - r.minCz + 1;
    const px = Math.max(2, Math.min(14, Math.floor(980 / Math.max(1, w)), Math.floor(700 / Math.max(1, hh))));
    const cv = h('canvas', { width: w * px, height: hh * px, class: 'rg', style: 'image-rendering:pixelated;cursor:crosshair' });
    const g = cv.getContext('2d');
    g.fillStyle = '#0d1117'; g.fillRect(0, 0, cv.width, cv.height);
    g.fillStyle = '#1c2430'; for (const [cx, cz] of r.existing) g.fillRect((cx - r.minCx) * px, (cz - r.minCz) * px, px, px);
    const ramp = t => { const stops = [[0, [30, 60, 130]], [0.25, [30, 160, 190]], [0.5, [90, 200, 90]], [0.75, [240, 210, 60]], [1, [240, 60, 40]]]; for (let i = 1; i < stops.length; i++) if (t <= stops[i][0]) { const [t0, c0] = stops[i - 1], [t1, c1] = stops[i]; const f = (t - t0) / (t1 - t0); return c0.map((v, k) => Math.round(v + (c1[k] - v) * f)); } return stops[4][1]; };
    const val = new Map();
    for (const [cx, cz, v] of r.cells) { val.set(cx + ',' + cz, v); const t = r.max > 0 ? Math.log(1 + v) / Math.log(1 + r.max) : 0; const c = ramp(t); g.fillStyle = `rgb(${c[0]},${c[1]},${c[2]})`; g.fillRect((cx - r.minCx) * px, (cz - r.minCz) * px, px, px); }
    const tip = h('div', { class: 'mono muted', style: 'min-height:20px' }, 'hover the map');
    cv.onmousemove = e => { const b = cv.getBoundingClientRect(); const cx = r.minCx + Math.floor((e.clientX - b.left) / b.width * w), cz = r.minCz + Math.floor((e.clientY - b.top) / b.height * hh); const v = val.get(cx + ',' + cz); tip.textContent = `chunk ${cx}, ${cz}  ·  blocks x ${cx * 16}…${cx * 16 + 15}, z ${cz * 16}…${cz * 16 + 15}  ·  ${v == null ? 'nothing' : fmtN(v)}`; };
    cv.onclick = e => { const b = cv.getBoundingClientRect(); const cx = r.minCx + Math.floor((e.clientX - b.left) / b.width * w), cz = r.minCz + Math.floor((e.clientY - b.top) / b.height * hh); guarded(() => WT.gotoBlock(cx * 16 + 8, cz * 16 + 8)); };
    out.append(h('h3', {}, `Heat map: ${{ solid: 'blocks per chunk', block: 'matching blocks', container: 'containers', tileentity: 'tile entities', entity: 'creatures', item: 'matching items in containers' }[kind]}${q ? ' "' + q + '"' : ''}`),
      h('div', { class: 'muted' }, `${fmtN(r.total)} in total · strongest chunk ${fmtN(r.max)} · logarithmic colour scale (blue = few, red = many) · click a chunk to open its region`), tip, cv);
  }

  // ───────── container editor
  async function openContainer(x, y, z) {
    const c = await api('/api/world/container?' + qs({ x, y, z }));
    const M = { names: c.names || {} };
    const grid = c.grid;
    let sel = grid.items.findIndex(s => s.count > 0); if (sel < 0) sel = 0;
    let loot = c.loot || '';
    const body = h('div', {});
    const render = () => {
      body.replaceChildren();
      const cols = Math.max(1, grid.width || 8);
      const cells = h('div', { class: 'igrid', style: `grid-template-columns:repeat(${cols},64px);max-width:${cols * 68}px` });
      grid.items.forEach((s, i) => {
        const empty = !(s.count > 0 && s.item.type);
        const q = !empty && s.item.quality > 0 ? QCOL[Math.min(6, s.item.quality)] : null;
        cells.append(h('div', { class: 'cell' + (i === sel ? ' sel' : ''), draggable: 'true', style: q ? `border-color:${q}` : '', title: empty ? 'empty' : `${M.names[s.item.type] || s.item.type} ×${s.count}`,
          onclick: () => { sel = i; render(); },
          ondragstart: e => e.dataTransfer.setData('text/plain', 'c:' + i), ondragover: e => e.preventDefault(),
          ondrop: e => { e.preventDefault(); const v = e.dataTransfer.getData('text/plain'); if (!v.startsWith('c:')) return; const j = Number(v.slice(2)); if (j !== i) { const a = grid.items[j], b = grid.items[i]; const t = { count: a.count, item: a.item }; a.count = b.count; a.item = b.item; b.count = t.count; b.item = t.item; sel = i; render(); } } },
          empty ? '' : [itemIcon(M, s.item.type, 48), s.count > 1 ? h('span', { class: 'cnt' }, s.count) : null]));
      });
      const s = grid.items[sel];
      const side = h('div', { class: 'card', style: 'flex:1;min-width:260px;align-self:flex-start' });
      if (s) {
        const empty = !(s.count > 0 && s.item.type);
        side.append(h('h3', { style: 'margin-top:0' }, 'Slot ' + (sel + 1)));
        if (empty) side.append(h('div', { class: 'muted', style: 'margin-bottom:6px' }, 'Empty – add an item:'), itemPicker(it => { Object.assign(s, newItem(it, 1)); M.names[it.id] = it.name; render(); }));
        else side.append(h('div', { class: 'row' }, itemIcon(M, s.item.type, 56), h('div', {}, h('b', {}, M.names[s.item.type] || '#' + s.item.type), h('div', { class: 'muted mono' }, 'id ' + s.item.type))),
          h('div', { class: 'grid2' }, fld('Count', num(s, 'count', { step: 1, min: 0, max: 65535, on: render })), fld('Quality', num(s.item, 'quality', { step: 1, min: 0, max: 600 })), fld('Wear', num(s.item, 'useTimes', { step: 1, min: 0 }))),
          h('div', { class: 'row' }, h('button', { class: 'small danger', onclick: () => { s.count = 0; s.item = { type: 0, useTimes: 0, quality: 0, meta: -1, metadata: [], stats: null, mods: [], cosmetics: [], flags: 0, ammo: 0, seed: 0, texture: null }; render(); } }, 'Empty slot')));
      }
      body.append(h('div', { class: 'muted', style: 'margin-bottom:8px' }, `${c.block} at ${x}, ${y}, ${z} · ${grid.width}×${grid.height} slots · ` + (c.playerOwned ? 'placed by a player' : c.touched ? 'loot already rolled / touched' : 'untouched loot container (the game rolls loot when first opened)')),
        h('div', { style: 'display:flex;gap:14px;flex-wrap:wrap' }, h('div', {}, cells), side),
        h('div', { class: 'row', style: 'margin-top:8px' }, fld('Loot list', h('input', { type: 'text', value: loot, style: 'width:200px', onchange: e => { loot = e.target.value; } }))),
        h('div', { class: 'row', style: 'justify-content:flex-end;margin-top:12px' },
          h('button', { onclick: () => guarded(() => openContainer(x, y, z)) }, 'Revert'),
          !c.playerOwned && c.loot ? h('button', { title: 'Empties the container and marks it untouched – new loot is rolled the next time somebody opens it', onclick: () => guarded(async () => { if (!WT.needWrite()) return; const r = await api('/api/world/container', { body: { x, y, z, grid, resetLoot: true } }); toast(r.message); closeModal(); }) }, 'Reset loot (re-roll)') : null,
          h('button', { onclick: closeModal }, 'Close'),
          h('button', { class: 'primary', onclick: () => guarded(async () => { if (!WT.needWrite()) return; const r = await api('/api/world/container', { body: { x, y, z, grid, loot } }); toast(r.message); closeModal(); }) }, 'Save container')));
    };
    render();
    modal(h('div', {}, h('h2', {}, 'Container editor'), body), null, true);
  }
  WT.openContainer = openContainer;

  window.afterWorldEdit = rel => { try { if (S.sel === rel) selectFile(rel); } catch { } };
})();
