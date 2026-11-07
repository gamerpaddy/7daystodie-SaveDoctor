'use strict';
/* World tools → Objects: vending machines, traders, workstations, signs (written + drawn), locks, items / bags lying on the floor.
   Also the block inspector (block state, tile entity, entities nearby) used by the Objects tab and the 3D viewer. */
(function () {
  const { num, fld, fmtN } = WT;
  const txt = (obj, key, o = {}) => h('input', { type: 'text', value: obj[key] ?? '', style: o.style || 'width:180px', onchange: e => { obj[key] = e.target.value; o.on && o.on(); } });
  const chk = (obj, key, label, on) => h('label', { style: 'display:flex;gap:6px;align-items:center' }, h('input', { type: 'checkbox', checked: !!obj[key], onchange: e => { obj[key] = e.target.checked; on && on(); } }), label);
  // the game stores lock passwords as the 32-bit "stable string hash" in hex (ILockable.GetHashForPassword) – never the password itself
  const stableHash = s => { let h1 = 5381, h2 = 5381; for (let i = 0; i < s.length && s[i] !== '\0'; i += 2) { h1 = (Math.imul(h1, 33) ^ s.charCodeAt(i)) | 0; if (i === s.length - 1 || s[i + 1] === '\0') break; h2 = (Math.imul(h2, 33) ^ s.charCodeAt(i + 1)) | 0; } return ((h1 + Math.imul(h2, 1566083941)) | 0) >>> 0; };
  WT.passwordHash = s => s.length ? stableHash(s).toString(16).toUpperCase().padStart(8, '0') : '';
  const emptyItem = () => ({ type: 0, useTimes: 0, quality: 0, meta: -1, metadata: [], stats: null, mods: [], cosmetics: [], flags: 0, ammo: 0, seed: 0, texture: null });
  const EMPTY = () => ({ count: 0, item: emptyItem() });

  /** Table editor for a list of item stacks. opts: { fixedSize, extra: [{head, cell(entry, i)}], wrap(i) → the object that holds the stack ({item: Stack}) for trader entries } */
  function stackTable(M, list, opts = {}) {
    const box = h('div', {});
    const st = s => (opts.get ? opts.get(s) : s);
    const draw = () => {
      box.replaceChildren();
      const t = h('table', {}, h('tr', {}, h('th', {}, '#'), h('th', {}, 'Item'), h('th', {}, 'Count'), h('th', {}, 'Quality'), h('th', {}, 'Wear'), h('th', {}, 'Meta'), (opts.extra || []).map(x => h('th', {}, x.head)), h('th', {}, '')));
      list.forEach((entry, i) => {
        const s = st(entry); const empty = !(s.count > 0 && s.item.type);
        t.append(h('tr', {},
          h('td', { class: 'muted' }, i + 1),
          h('td', {}, empty ? itemPicker(it => { Object.assign(s, newItem(it, 1)); M.names[it.id] = it.name; draw(); }) : h('span', { style: 'display:inline-flex;gap:6px;align-items:center' }, itemIcon(M, s.item.type, 26), h('b', {}, M.names[s.item.type] || '#' + s.item.type))),
          h('td', {}, empty ? '' : num(s, 'count', { step: 1, min: 0, max: 65535, style: 'width:80px' })),
          h('td', {}, empty ? '' : num(s.item, 'quality', { step: 1, min: 0, max: 600, style: 'width:64px' })),
          h('td', {}, empty ? '' : num(s.item, 'useTimes', { step: 1, min: 0, style: 'width:70px' })),
          h('td', {}, empty ? '' : num(s.item, 'meta', { step: 1, min: -1, style: 'width:70px' })),
          (opts.extra || []).map(x => h('td', {}, empty ? '' : x.cell(entry, i))),
          h('td', {}, h('button', { class: 'small danger', title: opts.fixedSize ? 'Empty this slot' : 'Remove', onclick: () => { if (opts.fixedSize) { s.count = 0; s.item = emptyItem(); } else list.splice(i, 1); draw(); } }, '✕'))));
      });
      box.append(t);
      if (!opts.fixedSize) box.append(h('div', { class: 'row' }, h('span', { class: 'muted' }, 'add:'), itemPicker(it => { const e = opts.make ? opts.make(newItem(it, 1)) : newItem(it, 1); list.push(e); M.names[it.id] = it.name; draw(); })));
    };
    draw();
    return box;
  }
  WT.stackTable = stackTable;

  // player names for user ids (players.xml and the player files), so owners / authors show a name instead of an EOS id
  let namesCache = null, namesAt = 0;
  WT.playerNames = async () => {
    if (namesCache && Date.now() - namesAt < 30000) return namesCache;
    const m = {};
    try { const r = await api('/api/players/list'); for (const p of r.players || []) { if (p.name) { if (p.userId) m[p.userId.toLowerCase()] = p.name; if (p.nativeUserId) m[p.nativeUserId.toLowerCase()] ||= p.name; } } } catch { }
    try { const r = await api('/api/world/players'); for (const p of r || []) if (p.name && p.userId) m[p.userId.toLowerCase()] ||= p.name; } catch { }
    namesCache = m; namesAt = Date.now(); return m;
  };
  /** A small label that shows the player name for a user id (filled in asynchronously). */
  WT.nameLabel = (id, o = {}) => {
    const s = h('span', { class: 'muted', style: 'align-self:center' }, '');
    s.set = async v => { if (!v) { s.textContent = ''; return; } const m = await WT.playerNames(); const n = m[String(v).toLowerCase()]; s.textContent = o.plain ? (n || String(v).slice(0, 14) + '…') : n ? '→ ' + n : o.quiet ? '' : '→ unknown player'; s.style.color = n ? 'var(--ok, #4caf50)' : ''; };
    s.set(id); return s;
  };
  const plat = (obj, key) => {
    const o = obj[key] || { platform: 'EOS', userId: '' };
    const nm = WT.nameLabel(o.userId);
    return h('span', { style: 'display:inline-flex;gap:4px' },
      h('input', { type: 'text', value: o.platform, style: 'width:60px', placeholder: 'EOS', onchange: e => { o.platform = e.target.value; obj[key] = o.userId || o.platform ? o : null; } }),
      h('input', { type: 'text', value: o.userId, style: 'width:260px', placeholder: 'user id (empty = nobody)', onchange: e => { o.userId = e.target.value; obj[key] = o.userId ? { flag: 1, ...o } : null; nm.set(o.userId); } }), nm);
  };

  // ───────── trader / vending stock editor
  function traderEditor(M, tr) {
    const wrap = h('div', {});
    wrap.append(h('div', { class: 'row' }, fld('Trader id', num(tr, 'traderId', { step: 1 })), fld('Coins the trader holds', num(tr, 'availableMoney', { step: 1 })), fld('Last restock (world ticks)', num(tr, 'lastInventoryUpdate', { step: 1, style: 'width:140px' })),
      h('span', { class: 'muted', style: 'align-self:end' }, 'Last restock 0 = the game rolls a fresh stock when it is next opened.')));
    wrap.append(h('h4', { style: 'margin:10px 0 4px' }, `Stock (${tr.primary.length} entries) – price = base value × (1 + 0.2 × markup); positive markup is shown red, negative green`),
      stackTable(M, tr.primary, { get: e => e.item, make: s => ({ item: s, markup: 0, addedByPlayer: true }),
        extra: [{ head: 'Markup', cell: e => num(e, 'markup', { step: 1, min: -4, max: 100, style: 'width:64px', on: () => modalRefresh && modalRefresh() }) }, { head: 'Price (coins, base)', cell: e => { const ec = M.econ && M.econ[e.item.item.type]; if (!ec) return h('span', { class: 'muted' }, '–'); const mult = 1 + e.markup * 0.2; return h('span', { title: 'base value ' + ec[0] + ' × (1 + 0.2 × markup) for a bundle of ' + ec[1] + ' – quality, wear and bartering change it in the game' }, '≈ ' + Math.ceil(Math.floor(ec[0] * mult * (e.item.count / ec[1])))); } }, { head: 'Player added', cell: e => chk(e, 'addedByPlayer', '') }] }));
    if (tr.tiers && tr.tiers.length) wrap.append(h('div', { class: 'muted', style: 'margin-top:6px' }, `Tier groups (stock pools): ${tr.tiers.map(t => t.length).join(', ')} stacks – kept unchanged.`));
    wrap.append(h('div', { class: 'row' }, h('button', { class: 'small danger', onclick: () => { tr.primary.length = 0; tr.lastInventoryUpdate = 0; modalRefresh && modalRefresh(); } }, 'Clear stock (re-roll on open)')));
    return wrap;
  }
  let modalRefresh = null;

  // ───────── block inspector
  async function inspect(x, y, z) {
    const r = await api('/api/world/inspect?' + qs({ x, y, z }));
    const M = { names: r.names || {}, econ: r.econ || {} };
    const b = r.block;
    const body = h('div', {});
    modalRefresh = () => render();
    const bs = { rotation: b.rotation, meta: b.meta, meta2: b.meta2, meta3: b.meta3, damage: b.damage, stability: b.stability, textures: [...b.textures], decal: b.decal, fields: {} };
    for (const f of b.fields) bs.fields[f.key] = f.value;
    const te = r.te;
    const teEdit = te ? { owner: te.owner, features: {}, legacy: te.legacy ? JSON.parse(JSON.stringify(te.legacy)) : null } : null;
    if (te && te.features) for (const f of te.features) if (f.editable && f.kind !== 'storage' && f.kind !== 'marker') teEdit.features[f.hash] = JSON.parse(JSON.stringify(f.data));
    const FACES = ['Top', 'Bottom', 'North', 'South', 'West', 'East'];
    const storageEdit = {};

    function render() {
      body.replaceChildren();
      body.append(h('div', { class: 'muted mono' }, `x ${x}  y ${y}  z ${z}  ·  chunk ${r.cx}, ${r.cz}  ·  `, r.redirectedFrom ? h('span', { class: 'warn' }, `part of a multi-part block – showing its main block (you picked ${r.redirectedFrom.x}, ${r.redirectedFrom.y}, ${r.redirectedFrom.z})  ·  `) : '', h('a', { href: '#', onclick: e => { e.preventDefault(); closeModal(); guarded(() => WT.gotoBlock(x, z)); } }, 'show in region view')));
      if (!b.id) { body.append(h('div', { class: 'card' }, 'There is no block at this position.')); }
      else {
        // block state
        const fieldsEl = b.fields.map(f => f.kind === 'bool' ? chk(bs.fields, f.key, f.label) : fld(f.label, num(bs.fields, f.key, { step: 1, min: f.min, max: f.max, style: 'width:70px' })));
        body.append(h('div', { class: 'card' }, h('h3', { style: 'margin-top:0' }, b.name, h('span', { class: 'muted', style: 'font-weight:400;font-size:12px' }, `  id ${b.id}${b.def ? ' · class ' + (b.def.class || '–') + (b.def.group ? ' · ' + b.def.group : '') : ''}`)),
          fieldsEl.length ? h('div', { class: 'row' }, fieldsEl) : h('div', { class: 'muted' }, 'This block class has no named state bits.'),
          h('div', { class: 'row' }, fld('Rotation (0–31)', num(bs, 'rotation', { step: 1, min: 0, max: 31, style: 'width:70px' })), fld('Meta 1 (0–15)', num(bs, 'meta', { step: 1, min: 0, max: 15, style: 'width:70px' })), fld('Meta 2 (0–15)', num(bs, 'meta2', { step: 1, min: 0, max: 15, style: 'width:70px' })), chk(bs, 'meta3', 'Meta 3'), chk(bs, 'decal', 'Has decal'),
            fld('Damage', num(bs, 'damage', { step: 1, min: 0, style: 'width:80px' })), fld('Stability (0–15)', num(bs, 'stability', { step: 1, min: 0, max: 15, style: 'width:70px' }))),
          h('div', { class: 'row' }, h('span', { class: 'muted' }, 'Paint per face:'), FACES.map((n, i) => h('span', { title: b.textureNames[i] || 'unpainted' }, n + ' ', num(bs.textures, i, { step: 1, min: 0, max: 255, style: 'width:60px' })))),
          h('div', { class: 'muted', style: 'font-size:12px' }, `raw ${b.raw >>> 0}${b.child ? ' · child part of a multi-block (its meta bits hold the parent offset – leave them alone)' : ''} · light ${b.light} · density ${b.density} · water ${b.water}${b.def && b.def.dim && (b.def.dim[0] * b.def.dim[1] * b.def.dim[2] > 1) ? ' · multi-part block ' + b.def.dim.join('×') : ''}`),
          h('div', { class: 'row' }, h('button', { class: 'primary', onclick: () => guarded(async () => {
            if (!WT.needWrite()) return;
            const body2 = { x, y, z, rotation: bs.rotation, damage: bs.damage, stability: bs.stability, textures: bs.textures, decal: bs.decal, meta3: bs.meta3 };
            if (b.fields.length) body2.fields = bs.fields; else { body2.meta = bs.meta; body2.meta2 = bs.meta2; }
            if (b.fields.length && (bs.meta !== b.meta || bs.meta2 !== b.meta2)) { body2.meta = bs.meta; body2.meta2 = bs.meta2; delete body2.fields; }
            const res = await api('/api/world/block-state', { body: body2 }); toast(res.message);
          }) }, 'Save block state'))));
      }
      // tile entity
      if (te) {
        const card = h('div', { class: 'card' }, h('h3', { style: 'margin-top:0' }, 'Tile entity – ' + te.typeName));
        if (!r.tailParsed) card.append(h('div', { class: 'warn' }, 'This chunk\'s tile entities cannot be edited safely: ' + (r.tailNote || '')));
        if (te.composite) card.append(h('div', { class: 'row' }, h('span', { class: 'muted' }, 'Owner:'), plat(teEdit, 'owner')));
        for (const f of te.features || []) {
          const d = teEdit.features[f.hash]; const sec = h('div', { style: 'border-top:1px solid var(--line);padding:6px 0' }, h('b', {}, f.name));
          if (f.kind === 'storage' && f.data) {
            const sd = storageEdit[f.hash] ||= JSON.parse(JSON.stringify(f.data)); const g = sd.grid;
            const unopened = !g.playerOwned && !g.timeTouched && g.items.every(s => !(s.count > 0));
            sec.append(h('span', { class: 'muted' }, ` ${g.width}×${g.height} slots · loot list "${sd.lootList || '–'}" · ` + (g.playerOwned ? 'placed by a player' : g.timeTouched ? 'opened before' : 'never opened')),
              unopened ? h('div', { class: 'muted' }, 'Not opened yet: the game rolls this container\'s loot from its loot list the first time somebody opens it, so it holds nothing now. Putting items in here makes it count as opened.') : '',
              stackTable(M, g.items, { fixedSize: true }),
              h('div', { class: 'row' }, fld('Loot list', txt(sd, 'lootList', { style: 'width:200px' })), h('button', { class: 'primary', onclick: () => guarded(async () => { if (!WT.needWrite()) return; const res = await api('/api/world/container', { body: { x, y, z, grid: g, loot: sd.lootList || '' } }); toast(res.message); }) }, 'Save contents'),
                !g.playerOwned && sd.lootList ? h('button', { class: 'small', title: 'Empty it and mark it unopened – new loot is rolled when somebody opens it', onclick: () => guarded(async () => { if (!WT.needWrite()) return; const res = await api('/api/world/container', { body: { x, y, z, grid: g, resetLoot: true } }); toast(res.message); closeModal(); }) }, 'Reset loot (re-roll)') : null));
          }
          else if (f.kind === 'storage') sec.append(h('span', { class: 'muted' }, ' – unknown storage layout, kept unchanged'));
          else if (f.kind === 'door' && d) sec.append(h('div', {}, chk(d, 'isOpen', 'Door is open')));
          else if (f.kind === 'landclaim' && d) sec.append(h('div', {}, chk(d, 'showBounds', 'Show claim bounds')));
          else if (f.kind === 'lockable' && d) {
            const pw = h('input', { type: 'text', placeholder: 'new password', style: 'width:150px' });
            sec.append(h('div', { class: 'row' }, chk(d, 'locked', 'Locked'), fld('Stored password hash (empty = no password)', txt(d, 'passwordHash', { style: 'width:130px' })), h('button', { class: 'small', onclick: () => { d.locked = false; d.passwordHash = ''; d.allowed = []; render(); } }, 'Clear lock & password')),
              h('div', { class: 'row' }, h('span', { class: 'muted' }, 'The game keeps only a 32-bit hash of the password, not the password itself. Type a new one to replace it:'), pw,
                h('button', { class: 'small', onclick: () => { if (!pw.value) return; d.passwordHash = WT.passwordHash(pw.value); d.locked = true; render(); } }, 'Set this password'),
                ));
            const al = h('div', {}); (d.allowed || []).forEach((a, i) => al.append(h('div', { class: 'row' }, plat(d.allowed, i), h('button', { class: 'small danger', onclick: () => { d.allowed.splice(i, 1); render(); } }, '✕'))));
            sec.append(h('div', { class: 'muted' }, 'Allowed users:'), al, h('button', { class: 'small', onclick: () => { (d.allowed ||= []).push({ flag: 1, platform: 'EOS', userId: '' }); render(); } }, '+ add user'));
          }
          else if (f.kind === 'signable' && d) {
            sec.append(h('div', { class: 'row' }, chk(d, 'hasText', 'Has text'), fld('Text', h('textarea', { rows: 3, style: 'width:320px', onchange: e => { d.text = e.target.value; d.hasText = !!d.text; } }, d.text || '')), fld('Author', plat(d, 'author'))));
          }
          else if (f.kind === 'canvas' && d) {
            const prev = h('div', { style: 'min-height:20px' }, '');
            const loadPrev = async () => { prev.textContent = 'loading…'; const s = await api('/api/world/sign?' + qs({ library: d.library, guid: d.guid })); prev.replaceChildren(s.found ? h('div', {}, h('div', { class: 'muted' }, `${s.name} · ${s.layers} layers · ${s.source}` + (s.texts.length ? ' · text: ' + s.texts.slice(0, 8).join(' | ') : '')), h('div', { html: '' }, (() => { const w = h('div', { style: 'width:400px;max-width:100%' }); w.innerHTML = s.svg; return w; })())) : h('div', { class: 'muted' }, 'This sign was not found in the default library, the POI libraries or a player store' + (s.source ? ' (' + s.source + ')' : '') + '.')); };
            sec.append(h('div', { class: 'row' }, fld('Library', txt(d, 'library', { style: 'width:200px' })), fld('Sign id', txt(d, 'guid', { style: 'width:290px' })),
              fld('Blend', h('select', { onchange: e => { d.blend = Number(e.target.value); } }, ['Cutout', 'Alpha blend'].map((n, i) => h('option', { value: i, selected: d.blend === i }, n)))),
              fld('Rotation', h('select', { onchange: e => { d.rotation = Number(e.target.value); } }, ['0°', '90°', '180°', '270°'].map((n, i) => h('option', { value: i, selected: d.rotation === i }, n)))), chk(d, 'showOnImposter', 'Show on imposter')),
              h('div', { class: 'row' }, h('button', { class: 'small', onclick: () => guarded(loadPrev) }, 'Preview sign'), h('button', { class: 'small', title: 'The sign shown on freshly placed canvas blocks', onclick: () => { d.library = '[D]'; d.guid = '00000000-0000-0000-0000-000000000000'; render(); } }, 'Use the default sign')), prev);
          }
          else sec.append(h('span', { class: 'muted' }, f.kind === 'marker' ? ' – no settings' : ` – ${f.payloadBytes} bytes, kept unchanged`));
          card.append(sec);
        }
        if (te.legacy && teEdit.legacy) {
          const m = teEdit.legacy;
          if (te.legacyKind === 'vending') {
            card.append(h('div', { class: 'row' }, chk(m, 'locked', 'Locked'), fld('Owner', plat(m, 'owner')), fld('Password hash (game stores only a hash)', txt(m, 'passwordHash', { style: 'width:130px' })), h('button', { class: 'small', onclick: () => { const p = prompt('New password for this vending machine (leave empty to remove the password):'); if (p === null) return; m.passwordHash = WT.passwordHash(p); if (p) m.locked = true; render(); } }, 'Set password…'), fld('Rental ends (day)', num(m, 'rentalEndDay', { step: 1, style: 'width:90px' })), m.rentable ? fld('Next auto-rent (ticks)', num(m, 'nextAutoBuy', { step: 1, style: 'width:120px' })) : null));
            const al = h('div', {}); (m.allowed || []).forEach((a, i) => al.append(h('div', { class: 'row' }, plat(m.allowed, i), h('button', { class: 'small danger', onclick: () => { m.allowed.splice(i, 1); render(); } }, '✕'))));
            card.append(h('div', { class: 'muted' }, 'Allowed users:'), al, h('button', { class: 'small', onclick: () => { m.allowed.push({ flag: 1, platform: 'EOS', userId: '' }); render(); } }, '+ add user'), traderEditor(M, m.trader));
          } else if (te.legacyKind === 'workstation') {
            card.append(h('div', { class: 'row' }, chk(m, 'isBurning', 'Burning'), fld('Burn time left', num(m, 'burnTimeLeft')), chk(m, 'playerPlaced', 'Placed by a player'), fld('Last tick', num(m, 'lastTick', { step: 1, style: 'width:120px' }))));
            for (const [k, label] of [['fuel', 'Fuel'], ['input', 'Input'], ['tools', 'Tools'], ['output', 'Output'], ['lastInput', 'Last input (recipe memory)']])
              card.append(h('h4', { style: 'margin:10px 0 4px' }, label), stackTable(M, m[k], { fixedSize: true }));
            card.append(h('div', { class: 'muted' }, `Crafting queue: ${m.queue.filter(q => q.hasRecipe).length} active recipe(s) – kept unchanged. `), h('button', { class: 'small danger', onclick: () => { for (const q of m.queue) { q.hasRecipe = false; q.isCrafting = false; q.multiplier = 0; } render(); } }, 'Cancel the queue'));
          } else if (te.legacyKind === 'forge') {
            card.append(h('div', { class: 'row' }, fld('Fuel left (ticks)', num(m, 'fuelInForgeTicks', { step: 1 })), fld('Metal in forge', num(m, 'metalInForge', { step: 1 })), fld('Molded so far', num(m, 'moldedMetalSoFar', { step: 1 }))),
              h('h4', { style: 'margin:10px 0 4px' }, 'Fuel'), stackTable(M, m.fuel, { fixedSize: true }), h('h4', { style: 'margin:10px 0 4px' }, 'Input'), stackTable(M, m.input, { fixedSize: true }),
              h('h4', { style: 'margin:10px 0 4px' }, 'Mold'), stackTable(M, [m.mold], { fixedSize: true }), h('h4', { style: 'margin:10px 0 4px' }, 'Output'), stackTable(M, [m.output], { fixedSize: true }));
          } else if (te.legacyKind === 'collector') {
            card.append(h('div', { class: 'row' }, chk(m, 'underwater', 'Underwater'), chk(m, 'blocked', 'Blocked')),
              h('h4', { style: 'margin:10px 0 4px' }, 'Items'), stackTable(M, m.items, { fixedSize: true }), h('h4', { style: 'margin:10px 0 4px' }, 'Mods'), stackTable(M, m.mods, { fixedSize: true }),
              h('h4', { style: 'margin:10px 0 4px' }, 'Fuel'), stackTable(M, m.fuelSlots, { fixedSize: true }), h('h4', { style: 'margin:10px 0 4px' }, 'Catalysts'), stackTable(M, m.catalysts, { fixedSize: true }));
          }
        } else if (!te.composite) card.append(h('div', { class: 'muted' }, te.legacyNote || 'No editable data in this tile entity type (lights, power blocks, sleeper markers keep their values).'));
        const canSave = r.tailParsed && ((te.legacy && teEdit.legacy) || (te.features || []).some(f => teEdit.features[f.hash]) || te.composite);
        if (canSave) card.append(h('div', { class: 'row' }, h('button', { class: 'primary', onclick: () => guarded(async () => {
          if (!WT.needWrite()) return;
          const body2 = { x, y, z };
          if (te.composite) body2.owner = teEdit.owner && teEdit.owner.userId ? { flag: 1, ...teEdit.owner } : null;
          if (Object.keys(teEdit.features).length) body2.features = teEdit.features;
          if (teEdit.legacy) body2.legacy = teEdit.legacy;
          const res = await api('/api/world/te', { body: body2 }); toast(res.message);
        }) }, 'Save tile entity')));
        body.append(card);
      }
      if (r.entities && r.entities.length) body.append(h('div', { class: 'card' }, h('h3', { style: 'margin-top:0' }, 'Entities here'), h('table', {}, r.entities.map(e => h('tr', {}, h('td', {}, e.name), h('td', { class: 'muted' }, e.kind), h('td', { class: 'mono' }, `${e.x.toFixed(1)}, ${e.y.toFixed(1)}, ${e.z.toFixed(1)}`), h('td', {}, h('button', { class: 'small', onclick: () => guarded(() => openEntity(r.cx, r.cz, e.index)) }, 'Open')))))));
    }
    render();
    modal(h('div', {}, h('h2', {}, 'Inspector'), body), null, true);
  }
  WT.inspect = inspect;

  /** What is stacked at one block column: surface, every block run from the top down, tile entities, entities nearby – with buttons to open each. */
  WT.columnPanel = async function (x, z, opts = {}) {
    const r = await api('/api/world/column?' + qs({ x, z }));
    const yIn = h('input', { type: 'number', min: 0, max: 255, value: opts.y ?? (r.top >= 0 ? r.top : 64), style: 'width:70px' });
    const run = y => r.runs.find(u => y <= u.y2 && y >= u.y1);
    return h('div', {},
      h('div', { class: 'muted' }, `chunk (${r.cx}, ${r.cz}) · local ${r.lx}, ${r.lz}` + (r.top >= 0 ? ` · surface y ${r.top}: ${r.runs.length ? r.runs[0].name : ''}` : ' · empty column')),
      h('div', { class: 'row' }, 'y', yIn, h('button', { class: 'primary', onclick: () => guarded(() => inspect(x, Number(yIn.value), z)) }, '🔍 Inspect this block'), r.top >= 0 ? h('button', { onclick: () => { yIn.value = r.top; guarded(() => inspect(x, r.top, z)); } }, 'Surface block') : '', ...(opts.extra || [])),
      r.tes.length ? h('div', {}, h('h4', { style: 'margin:8px 0 4px' }, 'Tile entities in this column'), r.tes.map(t => h('div', { class: 'row', style: 'margin:2px 0' }, h('span', { class: 'mono' }, 'y ' + t.y), h('span', {}, t.name), h('span', { class: 'muted' }, t.kind), h('button', { class: 'small', onclick: () => guarded(() => inspect(x, t.y, z)) }, 'Open')))) : '',
      r.entities.length ? h('div', {}, h('h4', { style: 'margin:8px 0 4px' }, 'Entities within 2 blocks'), r.entities.map(e => h('div', { class: 'row', style: 'margin:2px 0' }, h('span', {}, e.name), h('span', { class: 'muted mono' }, `${e.x.toFixed(1)}, ${e.y.toFixed(1)}, ${e.z.toFixed(1)}`), h('button', { class: 'small', onclick: () => guarded(() => openEntity(r.cx, r.cz, e.index)) }, 'Open')))) : '',
      h('h4', { style: 'margin:8px 0 4px' }, 'Blocks from the top down (also below ground)'),
      h('table', {}, r.runs.slice(0, 60).map(u => h('tr', { style: opts.y != null && run(opts.y) === u ? 'background:rgba(255,214,10,.15)' : '' }, h('td', { class: 'mono' }, u.y1 === u.y2 ? 'y ' + u.y1 : `y ${u.y2} … ${u.y1}`), h('td', {}, u.name), h('td', {}, h('button', { class: 'small', onclick: () => guarded(() => inspect(x, u.y2, z)) }, 'Inspect'))))));
  };

  // ───────── entity editor
  async function openEntity(cx, cz, index) {
    const r = await api('/api/world/entity?' + qs({ cx, cz, index }));
    const e = r.entity; const M = { names: r.names || {}, econ: r.econ || {} };
    const ed = { x: e.x, y: e.y, z: e.z, lifetime: e.lifetime };
    const inv = e.inventory ? JSON.parse(JSON.stringify(e.inventory)) : null;
    const item = e.droppedItem ? JSON.parse(JSON.stringify(e.droppedItem)) : null;
    const trader = e.trader ? JSON.parse(JSON.stringify(e.trader)) : null;
    const loot = { v: e.overrideLoot ?? '', n: e.overrideName ?? '' };
    modalRefresh = () => draw();
    const body = h('div', {});
    function draw() {
      body.replaceChildren();
      body.append(h('div', { class: 'muted' }, `${e.name} · ${e.kind || 'entity'} · id ${e.id}` + (e.refPlayerId != null ? ` · belongs to player entity ${e.refPlayerId}` : '') + (e.note ? ' · ' + e.note : '')),
        h('div', { class: 'row' }, fld('X', num(ed, 'x')), fld('Y', num(ed, 'y')), fld('Z', num(ed, 'z')), fld('Lifetime', num(ed, 'lifetime')), h('a', { href: '#', style: 'align-self:end', onclick: ev => { ev.preventDefault(); closeModal(); guarded(() => WT.gotoBlock(Math.floor(e.x), Math.floor(e.z))); } }, 'show in region view')));
      if (item) body.append(h('h4', {}, 'Item on the ground'), stackTable(M, [item], { fixedSize: true }));
      if (e.kind === 'EntityLootContainer' && e.hasMirror) body.append(h('div', { class: 'row' }, fld('Loot list', txt(loot, 'v', { style: 'width:200px' })), fld('Display name', txt(loot, 'n', { style: 'width:200px' }))));
      if (inv && !inv.timeTouched && !inv.items.some(s => s.count > 0)) body.append(h('div', { class: 'muted' }, 'Never opened: the game rolls the loot of airdrops and loot bags when somebody opens them for the first time, so nothing is stored yet. Adding items here marks it as opened.'));
      if (inv) body.append(h('h4', {}, `Inventory (${inv.width}×${inv.height} slots${e.hasMirror ? ', stored twice – both copies are kept in sync' : ''})`), stackTable(M, inv.items, { fixedSize: true }));
      if (trader) body.append(h('h4', {}, 'Trader stock'), traderEditor(M, trader));
      body.append(h('div', { class: 'row', style: 'justify-content:flex-end;margin-top:12px' },
        h('button', { class: 'danger', onclick: () => guarded(async () => { if (!WT.needWrite()) return; if (!(await confirmBox('Remove this entity?', 'It disappears from the chunk (backup first).' + (trader ? ' Removing a trader NPC may leave the trader area without its trader.' : ''), 'Remove', true))) return; const res = await api('/api/world/entity', { body: { cx, cz, index, delete: true } }); toast(res.message); closeModal(); }) }, 'Delete entity'),
        h('button', { onclick: closeModal }, 'Close'),
        h('button', { class: 'primary', disabled: !e.editable, title: e.editable ? '' : e.note, onclick: () => guarded(async () => {
          if (!WT.needWrite()) return;
          const edit = { x: ed.x, y: ed.y, z: ed.z, lifetime: ed.lifetime };
          if (item) edit.droppedItem = item; if (inv) edit.inventory = inv; if (trader) edit.trader = trader;
          if (e.kind === 'EntityLootContainer' && e.hasMirror && (loot.v !== (e.overrideLoot ?? '') || loot.n !== (e.overrideName ?? ''))) { edit.overrideLoot = loot.v; edit.overrideName = loot.n; }
          const res = await api('/api/world/entity', { body: { cx, cz, index, edit } }); toast(res.message); closeModal();
        }) }, 'Save entity')));
    }
    draw();
    modal(h('div', {}, h('h2', {}, 'Entity'), body), null, true);
  }
  WT.openEntity = openEntity;

  // ───────── Objects tab
  const KINDS = [['vending', '🏪 Vending machines'], ['trader', '🧑‍💼 Traders'], ['workstation', '🔨 Workstations'], ['forge', '🔥 Forges'], ['collector', '💧 Collectors'], ['sign', '🪧 Written signs'], ['canvas', '🖼 Drawn signs'], ['lock', '🔒 Locks & passwords'], ['claim', '🚩 Land claim blocks'], ['drop', '🎒 Items on the floor'], ['lootbag', '📦 Loot bags'], ['backpack', '🎒 Dropped backpacks'], ['airdrop', '🪂 Airdrops'], ['carrying', '🧟 Carrying loot']];
  WT.register('objects', '🧾 Objects', async el => {
    el.append(h('div', { class: 'card muted' }, 'Everything with its own data in the world: vending machines and trader NPCs (stock, prices, coins), workstations and forges, collectors, written and drawn signs, locked doors / containers, land claim blocks, and entities lying around (dropped items, loot bags, backpacks). Open one to inspect or edit it.'));
    await WT.waitIndex(el);
    const st = WT.objState ||= { kind: 'vending', q: '' };
    const out = h('div', {}); const chips = h('div', { class: 'row' });
    const q = h('input', { type: 'text', placeholder: 'filter by name, text or owner…', style: 'width:260px', value: st.q, onchange: e => { st.q = e.target.value; load(); } });
    el.append(chips, h('div', { class: 'row' }, q), out);
    async function load() {
      let r; for (;;) { r = await api('/api/world/objects?' + qs({ kind: st.kind, q: st.q })); if (!r.building) break; await new Promise(res => setTimeout(res, 400)); }
      chips.replaceChildren(...KINDS.map(([k, l]) => h('button', { class: st.kind === k ? 'primary' : '', onclick: () => { st.kind = k; load(); } }, `${l} (${r.counts[k] || 0})`)));
      out.replaceChildren();
      if (!r.results.length) { out.append(h('div', { class: 'card muted' }, 'Nothing of this kind in the world' + (st.q ? ' matches the filter.' : '.'))); return; }
      out.append(h('table', {}, h('tr', {}, ['Name', 'Details', 'Position', 'Owner', ''].map(x => h('th', {}, x))), r.results.map(o => {
        const entity = o.index >= 0;
        return h('tr', {}, h('td', {}, o.name), h('td', { style: 'max-width:420px;overflow:hidden;text-overflow:ellipsis' }, o.detail, o.flag ? h('span', { class: 'warn' }, entity ? '  (read-only)' : '') : ''), h('td', {}, WT.posLink(o.x, o.y, o.z)), h('td', { class: 'muted', title: o.owner || '' }, o.owner ? WT.nameLabel(o.owner, { plain: true }) : ''),
          h('td', {}, h('button', { class: 'small', onclick: () => guarded(() => entity ? openEntity(o.cx, o.cz, o.index) : inspect(o.x, o.y, o.z)) }, 'Open / edit')));
      })), r.results.length >= 1000 ? h('div', { class: 'muted' }, 'First 1000 shown – use the filter.') : null);
    }
    await load();
  }, 15);

  // ───────── drawn sign store (.signs file view)
  window.tabSigns = async function (el, a) {
    el.append(h('div', { class: 'muted' }, 'Drawn signs a player created in the game\'s sign editor. The preview is approximate (shapes, colours and text; warps and noise are not drawn). Deleting a sign here leaves canvas blocks that use it showing the default sign.'));
    const r = await api('/api/signs/list?' + qs({ rel: a.rel }));
    if (!r.signs.length) { el.append(h('div', { class: 'card' }, 'This store is empty.')); return; }
    for (const s of r.signs) {
      if (s.error) { el.append(h('div', { class: 'card err' }, 'Damaged sign: ' + s.error)); continue; }
      const prev = h('div', { style: 'width:300px;max-width:100%;flex:none' }); prev.innerHTML = s.svg;
      el.append(h('div', { class: 'card', style: 'display:flex;gap:14px;flex-wrap:wrap' }, prev, h('div', { style: 'flex:1;min-width:240px' }, h('h3', { style: 'margin-top:0' }, s.name), h('div', { class: 'muted mono' }, `${s.guid} · ${s.layers} layers · ${s.sharing} · modified ${s.modified}`), s.texts.length ? h('div', {}, 'Text: ' + s.texts.join(' | ')) : null,
        h('div', { class: 'row' }, h('button', { class: 'small', onclick: () => { const bl = new Blob([s.svg], { type: 'image/svg+xml' }); const u = document.createElement('a'); u.href = URL.createObjectURL(bl); u.download = (s.name || 'sign') + '.svg'; u.click(); } }, '⬇ SVG'),
          h('button', { class: 'small danger', onclick: () => guarded(async () => { if (!(await confirmBox('Delete this sign?', `"${s.name}" is removed from the store (backup first).`, 'Delete', true))) return; const res = await api('/api/signs/delete', { body: { rel: a.rel, guid: s.guid } }); toast(res.message); await selectFile(a.rel); }) }, 'Delete')))));
    }
  };
})();

// ───────── World consistency check tab
(function () {
  const { fmtN } = WT;
  const TITLES = {
    'te-orphan': ['Tile entity without a block', 'Left behind when a block was removed. Fix: delete the tile entity.'],
    'te-mismatch': ['Tile entity belongs to another block', 'The block here is not the one the tile entity was made for. Fix: delete the tile entity.'],
    'te-duplicate': ['Two tile entities at one position', 'Fix: keep the first.'],
    'te-out-of-chunk': ['Tile entity outside its chunk', 'Fix: delete it.'],
    'te-missing': ['Blocks that should have a tile entity but have none', 'Chests, doors, signs … without their data. Remove or replace the blocks (copy a working one with schematics).'],
    'unknown-item': ['Items that no longer exist', 'Removed mods. Fix: empty those slots / remove the stock entries.'],
    'unknown-block': ['Blocks that no longer exist', 'Removed mods. Use "replace block" in the region view to swap them.'],
    'vending-markup': ['Vending machine price out of range', 'Open the vending machine in the Objects tab.'],
    'storage-unreadable': ['Storage with unknown layout', 'Kept as is.'],
    'tail-unreadable': ['Chunks whose tile entities / entities cannot be read', 'The editors skip these chunks.'],
    'entity-duplicate-id': ['Entities sharing one id', 'Fix: remove the later copies.'],
    'entity-nan': ['Entities with an invalid position', 'Fix: remove them.'],
    'entity-void': ['Entities that fell out of the world', 'Fix: remove them.'],
    'entity-far': ['Entities far from their chunk', 'Informational.'],
  };
  const FIXABLE = new Set(['te-orphan', 'te-mismatch', 'te-duplicate', 'te-out-of-chunk', 'unknown-item', 'entity-duplicate-id', 'entity-nan', 'entity-void']);
  WT.register('check', '🩺 Consistency check', async el => {
    el.append(h('div', { class: 'card muted' }, 'Reads every chunk and cross-checks tile entities against blocks, items against the id tables, and entities against each other. These are things the game tolerates silently but that point at crashes, removed mods or bad edits. Nothing is changed until you press a Fix button (backup first).'));
    const out = h('div', {}); el.append(h('div', { class: 'row' }, h('button', { class: 'primary', onclick: () => guarded(() => run(out, true)) }, 'Check again')), out);
    await run(out, false);
  }, 17);

  async function run(out, force) {
    out.replaceChildren(h('div', { class: 'muted' }, 'Checking…'));
    let r; for (let i = 0; i < 4000; i++) { r = await api('/api/world/check' + (force && i === 0 ? '?force=1' : '')); if (!r.building) break; out.replaceChildren(h('div', { class: 'muted' }, `Checking… ${r.done}/${r.total} region files`)); await new Promise(res => setTimeout(res, 500)); if (!out.isConnected) return; }
    out.replaceChildren();
    if (r.error) { out.append(h('div', { class: 'card err' }, r.error)); return; }
    out.append(h('div', { class: 'muted' }, `${fmtN(r.chunks)} chunks checked in ${r.seconds.toFixed(1)} s – ${r.total === 0 ? 'no findings.' : fmtN(r.total) + ' finding(s).'}`));
    if (!r.groups.length) { out.append(h('div', { class: 'card' }, h('b', { class: 'ok' }, '✓ Everything is consistent.'))); return; }
    for (const g of r.groups) {
      const [title, help] = TITLES[g.code] || [g.code, ''];
      const sev = g.severity === 'error' ? 'err' : g.severity === 'warn' ? 'warn' : 'muted';
      const rows = h('div', { style: 'display:none' });
      rows.append(h('table', {}, g.sample.slice(0, 100).map(i => h('tr', {}, h('td', { class: 'muted' }, i.message), h('td', {}, WT.posLink(i.x, i.y, i.z)), h('td', {}, i.index >= 0 ? h('button', { class: 'small', onclick: () => guarded(() => WT.openEntity(i.cx, i.cz, i.index)) }, 'Entity') : (i.code === 'x' ? '' : h('button', { class: 'small', onclick: () => guarded(() => WT.inspect(i.x, i.y, i.z)) }, 'Inspect')))))));
      out.append(h('div', { class: 'card' }, h('div', { class: 'row', style: 'margin:0' }, h('b', { class: sev }, `${fmtN(g.count)} × ${title}`), h('span', { class: 'muted' }, help),
        FIXABLE.has(g.code) ? h('button', { class: 'small danger', onclick: () => guarded(async () => { if (!WT.needWrite()) return; if (!(await confirmBox('Fix "' + title + '"?', help + '\n\nAffects ' + g.count + ' finding(s). Every changed region file is backed up first.', 'Fix', true))) return; const res = await api('/api/world/check/fix', { body: { code: g.code } }); toast(res.message, 'ok', 10000); run(out, true); }) }, 'Fix all') : null,
        h('button', { class: 'small', onclick: () => { rows.style.display = rows.style.display === 'none' ? '' : 'none'; } }, 'Show list')), rows));
    }
  }
})();
