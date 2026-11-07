/* gameOptions.sdf editor: the settings chosen when the savegame was created, with the game's own names and value lists
   (gameoptions.json is generated from the game's XUi windows, SandboxOptionManager and Localization). */
(function () {
  let DEFS = null;
  const loadDefs = async () => DEFS ||= await (await fetch('/gameoptions.json')).json();

  const eq = (a, b) => typeof a === 'number' && typeof b === 'number' ? Math.abs(a - b) < 1e-6 : a === b;
  const indexOfValue = (set, v) => set.values.findIndex(x => eq(x.v, v));
  const A = n => String.fromCharCode(65 + n);

  // sandbox code: 'A' + (two letters option index, one letter value index) …
  function decode(code, byIndex) {
    const st = { ok: !!code && code[0] === 'A' && (code.length - 1) % 3 === 0, items: [] };   // items: {index, vi, known}
    if (!st.ok) return st;
    for (let i = 1; i + 2 < code.length + 0; i += 3) {
      const t = code.substr(i, 3); const idx = (t.charCodeAt(0) - 65) * 26 + (t.charCodeAt(1) - 65), vi = t.charCodeAt(2) - 65;
      st.items.push({ index: idx, vi, known: !!byIndex[idx] });
    }
    return st;
  }
  const encode = items => 'A' + items.map(t => A(Math.floor(t.index / 26)) + A(t.index % 26) + A(t.vi)).join('');

  async function gameOptionsEditor(el, M, a) {
    const D = await loadDefs();
    const prefs = Object.fromEntries(D.prefs.map(p => [p.name, p]));
    const byIndex = Object.fromEntries(D.options.map(o => [o.index, o]));
    const entry = n => M.entries.find(e => e.name === n);
    const touch = () => { markDirty(); };

    el.append(h('div', { class: 'card muted' }, 'These are the settings the world was created with (the game reads them again whenever the save is loaded). Names and value lists come from the game itself; a value the game does not offer is shown as "custom" and kept as it is.'));

    // ───────── general settings
    const gen = h('div', { class: 'card' }, h('h3', { style: 'margin-top:0' }, 'Game settings'));
    const tabNames = { xuiGameSettingsServer: 'Server', xuiGameSettingsAdvanced: 'Advanced', xuiGameSettingsMultiplayer: 'Multiplayer / land claims' };
    const grid = h('div', { class: 'grid2' }); gen.append(grid);
    const known = [], unknown = [];
    for (const e of M.entries) { if (e.name === 'SandboxCode' || e.name === 'SandboxPreset') continue; (prefs[e.name] ? known : unknown).push(e); }
    for (const e of known) {
      const p = prefs[e.name];
      let ctl;
      if (p.type === 'bool') ctl = h('select', { onchange: ev => { e.value = ev.target.value === 'true'; touch(); } }, p.values.map(v => h('option', { value: String(v.v), selected: e.value === v.v }, v.label)));
      else if (p.values.length && !p.text) {
        const have = p.values.findIndex(v => eq(v.v, e.value));
        ctl = h('select', { onchange: ev => { const v = p.values[Number(ev.target.value)]; if (v) { e.value = v.v; touch(); } } },
          have < 0 ? h('option', { value: -1, selected: true }, 'custom: ' + e.value) : '', p.values.map((v, i) => h('option', { value: i, selected: i === have }, v.label)));
      } else if (p.password) ctl = h('input', { type: 'text', value: e.value ?? '', onchange: ev => { e.value = ev.target.value; touch(); } });
      else if (e.type === 'int' || e.type === 'float') ctl = h('input', { type: 'number', value: e.value, onchange: ev => { e.value = Number(ev.target.value); touch(); } });
      else ctl = h('input', { type: 'text', value: e.value ?? '', onchange: ev => { e.value = ev.target.value; touch(); } });
      grid.append(h('div', { class: 'field' }, h('label', {}, p.label + (p.password ? ' (stored in clear text)' : ''), h('div', { class: 'muted mono', style: 'font-size:11px' }, e.name)), ctl));
    }
    el.append(gen);
    if (unknown.length) {
      const c = h('div', { class: 'card' }, h('h3', { style: 'margin-top:0' }, 'Other stored options'), h('div', { class: 'muted' }, 'Not known to this version of SaveDoctor (new, removed or mod options) – shown raw.'));
      for (const e of unknown) c.append(h('div', { class: 'row' }, h('span', { class: 'mono' }, e.name), h('span', { class: 'muted' }, e.type),
        e.type === 'bool' ? h('input', { type: 'checkbox', checked: !!e.value, onchange: ev => { e.value = ev.target.checked; touch(); } }) : h('input', { type: e.type === 'int' || e.type === 'float' ? 'number' : 'text', value: e.value ?? '', onchange: ev => { e.value = e.type === 'int' || e.type === 'float' ? Number(ev.target.value) : ev.target.value; touch(); } })));
      el.append(c);
    }

    // ───────── sandbox options
    let codeEntry = entry('SandboxCode'), presetEntry = entry('SandboxPreset');
    if (!codeEntry) { el.append(h('div', { class: 'card muted' }, 'This save stores no sandbox options (all values are the defaults).')); return; }
    const state = decode(codeEntry.value || '', byIndex);
    const cur = new Map();                         // option index -> value index (explicitly stored)
    const order = [];
    for (const t of state.items) { cur.set(t.index, t.vi); order.push(t.index); }
    const sandbox = h('div', { class: 'card' });
    const codeBox = h('input', { type: 'text', readonly: true, style: 'width:260px', class: 'mono' });
    const filter = h('input', { type: 'text', placeholder: 'filter options…', style: 'width:200px' });
    const onlyChanged = h('input', { type: 'checkbox' });
    const rows = h('div', {});

    const save = () => {
      const items = [];
      for (const idx of order) if (cur.has(idx)) items.push({ index: idx, vi: cur.get(idx) });
      codeEntry.value = encode(items); codeBox.value = codeEntry.value;
      if (presetEntry && presetEntry.value !== 'Custom') presetEntry.value = 'Custom';       // the game only reads the code when the preset is "Custom" or unknown
      touch();
    };
    function setValue(o, vi) {
      const defIdx = indexOfValue(D.sets[o.set], o.def);
      if (!order.includes(o.index) && vi !== defIdx) order.push(o.index);
      if (vi === defIdx && !state.items.some(t => t.index === o.index)) { cur.delete(o.index); const k = order.indexOf(o.index); if (k >= 0) order.splice(k, 1); } else cur.set(o.index, vi);
      save(); draw();
    }
    function draw() {
      rows.replaceChildren();
      const q = filter.value.trim().toLowerCase();
      for (const cat of D.categories) {
        const list = D.options.filter(o => o.category === cat.name && (!q || o.label.toLowerCase().includes(q) || o.id.toLowerCase().includes(q) || (o.desc || '').toLowerCase().includes(q)));
        const shown = [];
        for (const o of list) {
          const set = D.sets[o.set]; const defIdx = indexOfValue(set, o.def);
          const vi = cur.has(o.index) ? cur.get(o.index) : defIdx;
          const valid = vi >= 0 && vi < set.values.length;
          const changed = valid && vi !== defIdx;
          if (onlyChanged.checked && !changed && valid) continue;
          shown.push(h('div', { class: 'row', style: 'padding:3px 0;border-bottom:1px solid var(--line);' + (changed || !valid ? 'background:rgba(255,214,10,.10)' : '') },
            h('div', { style: 'width:260px' }, h('b', {}, o.label), h('div', { class: 'muted', style: 'font-size:11px' }, o.desc || o.id)),
            h('select', { onchange: ev => setValue(o, Number(ev.target.value)) },
              valid ? '' : h('option', { value: vi, selected: true }, 'invalid (' + vi + ')'),
              set.values.map((v, i) => h('option', { value: i, selected: valid && i === vi }, v.label + (i === defIdx ? '  (default)' : '')))),
            changed ? h('button', { class: 'small', title: 'Back to the default', onclick: () => setValue(o, defIdx) }, 'reset') : ''));
        }
        if (shown.length) rows.append(h('h4', { style: 'margin:14px 0 4px' }, cat.label), ...shown);
      }
      for (const t of state.items) if (!t.known) rows.append(h('div', { class: 'row warn' }, `Sandbox option #${t.index} (value ${t.vi}) is not known to this version – kept as it is.`));
    }
    filter.oninput = draw; onlyChanged.onchange = draw;
    codeBox.value = codeEntry.value || '';
    sandbox.append(h('h3', { style: 'margin-top:0' }, 'Sandbox options'),
      h('div', { class: 'muted' }, 'The game stores only the options that differ from its defaults, as one code. Preset: ' + (presetEntry ? presetEntry.value : '–') + '. Changing an option here switches the preset to "Custom" (the game then reads exactly this code).'),
      state.ok ? '' : h('div', { class: 'warn' }, 'The stored code is not in the game\'s format ("A" followed by groups of three letters) – saving a change here replaces it.'),
      h('div', { class: 'row' }, 'Code', codeBox, filter, h('label', { class: 'muted' }, onlyChanged, ' only changed'), h('span', { class: 'muted' }, cur.size + ' stored')),
      rows);
    el.append(sandbox);
    draw();
  }

  window.WT = window.WT || {};
  WT.gameOptionsEditor = gameOptionsEditor;
})();
