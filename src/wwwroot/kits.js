'use strict';
/* Kit export: turns inventory / container contents into files a modder can use (lossless JSON kit, loot.xml group, ItemsOnEnterGame loadout). */
(function () {
  const KIT = window.KIT = {};
  const xe = s => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
  const cm = s => String(s).replace(/--/g, '- -');                                    // safe inside an XML comment
  const safe = s => (s || 'kit').replace(/[^A-Za-z0-9_\-]+/g, '_').replace(/^_+|_+$/g, '') || 'kit';

  const CRC = (() => { const t = new Uint32Array(256); for (let n = 0; n < 256; n++) { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xEDB88320 ^ (c >>> 1) : c >>> 1; t[n] = c >>> 0; } return t; })();
  const crc32 = d => { let c = 0xFFFFFFFF; for (let i = 0; i < d.length; i++) c = CRC[(c ^ d[i]) & 255] ^ (c >>> 8); return (c ^ 0xFFFFFFFF) >>> 0; };
  function zip(files) {
    const enc = new TextEncoder(), parts = [], central = []; let off = 0;
    for (const f of files) {
      const nm = enc.encode(f.name), data = enc.encode(f.text), crc = crc32(data), hd = new DataView(new ArrayBuffer(30));
      hd.setUint32(0, 0x04034b50, true); hd.setUint16(4, 20, true); hd.setUint16(6, 0x0800, true); hd.setUint32(14, crc, true); hd.setUint32(18, data.length, true); hd.setUint32(22, data.length, true); hd.setUint16(26, nm.length, true);
      parts.push(hd.buffer, nm, data);
      const ch = new DataView(new ArrayBuffer(46));
      ch.setUint32(0, 0x02014b50, true); ch.setUint16(4, 20, true); ch.setUint16(6, 20, true); ch.setUint16(8, 0x0800, true); ch.setUint32(16, crc, true); ch.setUint32(20, data.length, true); ch.setUint32(24, data.length, true); ch.setUint16(28, nm.length, true); ch.setUint32(42, off, true);
      central.push(ch.buffer, nm); off += 30 + nm.length + data.length;
    }
    let csz = 0; for (const c of central) csz += c.byteLength;
    const end = new DataView(new ArrayBuffer(22)); end.setUint32(0, 0x06054b50, true); end.setUint16(8, files.length, true); end.setUint16(10, files.length, true); end.setUint32(12, csz, true); end.setUint32(16, off, true);
    return new Blob([...parts, ...central, end.buffer], { type: 'application/zip' });
  }
  function save(blob, name) { const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = name; document.body.append(a); a.click(); setTimeout(() => { URL.revokeObjectURL(a.href); a.remove(); }, 2000); }

  /** Collect the stacks of the chosen groups with everything the game stores about each item. */
  function collect(groups, names, picked) {
    const nm = t => (names && names[t]) || null;
    const out = [], missing = new Set();
    const nameOrMissing = t => { const n = nm(t); if (!n || /^(item|block)#/.test(n)) { missing.add(t); return null; } return n; };
    for (const [group, spec] of Object.entries(groups)) {
      if (!picked.has(group)) continue;
      spec.items.forEach((s, i) => {
        if (!(s.count > 0 && s.item && s.item.type)) return;
        const it = s.item, name = nameOrMissing(it.type); if (!name) return;
        const rec = { group, slot: i + 1, item: name, count: s.count, quality: it.quality || 0 };
        if (spec.labels && spec.labels[i]) rec.slotName = spec.labels[i];
        if (it.useTimes) rec.wear = it.useTimes;                          // how worn the item is (0 = new)
        if (it.meta != null && it.meta >= 0) rec.meta = it.meta;          // rounds in the magazine (guns) / fuel or charge (tools, cans) as the game stores it
        if (it.ammo) rec.ammoType = it.ammo;                              // selected ammo type index of the weapon
        const mods = (it.mods || []).map(m => m && m.type ? nameOrMissing(m.type) : null);
        if (mods.some(Boolean)) rec.mods = mods.filter(Boolean);
        if (it.mods && it.mods.length) rec.modSlots = it.mods.length;
        const cos = (it.cosmetics || []).map(m => m && m.type ? nameOrMissing(m.type) : null).filter(Boolean);
        if (cos.length) rec.cosmetics = cos;
        if (it.metadata && it.metadata.length) rec.metadata = it.metadata.map(m => ({ key: m.key, type: ['none', 'float', 'int', 'string'][m.tag] || m.tag, value: m.tag === 1 ? m.f : m.tag === 2 ? m.i : m.tag === 3 ? m.s : null }));
        if (it.stats && it.stats.length) rec.stats = it.stats;
        if (it.flags) rec.flags = it.flags;
        if (it.seed) rec.seed = it.seed;
        if (it.texture) rec.texture = it.texture;
        out.push(rec);
      });
    }
    return { recs: out, missing };
  }

  function build(kitName, recs) {
    const id = safe(kitName);
    const json = JSON.stringify({
      format: 'SaveDoctorKit', version: 1, name: kitName, created: new Date().toISOString().slice(0, 10), game: '7 Days to Die',
      notes: {
        quality: 'item tier / quality level', wear: 'UseTimes – how worn out the item is (0 = like new)', meta: 'raw item meta: rounds loaded in the magazine for guns, fuel / charge for tools and cans',
        ammoType: 'index of the selected ammo type', mods: 'installed mod item names', metadata: 'extra key/value data stored on the item',
      },
      items: recs,
    }, null, 2);

    const n = recs.length, rows = Math.max(1, Math.ceil(n / 8));
    const lines = [`<!-- SaveDoctor kit "${cm(kitName)}" – ${n} stack(s).`,
      `     Put this inside <lootcontainers> of Config/loot.xml (or a modlet: <append xpath="/lootcontainers"> … </append>).`,
      `     Use the group in any container with <item group="kit_${id}"/> or open the container "kit_${id}" (a crate with every item).`,
      `     loot.xml can express item, count, quality and installed mods. Magazine ammo, fuel and wear are NOT part of loot.xml: loot items start unloaded and with random wear unless random_durability="false" – the exact values are listed in the comments and in the .json kit. -->`,
      `<lootgroup name="kit_${id}" count="all">`];
    for (const r of recs) {
      const a = [`name="${xe(r.item)}"`, `count="${r.count}"`];
      if (r.quality) a.push(`quality="${r.quality}"`);
      if (r.mods && r.mods.length) a.push(`mods="${xe(r.mods.join(','))}"`, `mod_chance="1"`);
      a.push(`random_durability="false"`);
      const info = [r.wear ? 'wear ' + r.wear : '', r.meta ? 'meta ' + r.meta : '', r.ammoType ? 'ammo type ' + r.ammoType : '', r.cosmetics ? 'cosmetics ' + r.cosmetics.join(',') : ''].filter(Boolean).join(', ');
      lines.push(`\t<item ${a.join(' ')}/>` + (info ? ` <!-- ${cm(info)} -->` : ''));
    }
    lines.push('</lootgroup>', '', `<lootcontainer name="kit_${id}" count="0" size="8,${rows}" sound_open="UseActions/crate_wood_open" sound_close="UseActions/crate_wood_close" loot_quality_template="qualBaseTemplate">`, `\t<item group="kit_${id}"/>`, '</lootcontainer>');
    const loot = lines.join('\n') + '\n';

    // ItemsOnEnterGame (player spawn loadout): the game only reads name=count there
    const counts = new Map(); for (const r of recs) counts.set(r.item, (counts.get(r.item) || 0) + r.count);
    const list = [...counts].map(([k, v]) => v > 1 ? `${k}=${v}` : k).join(',');
    const skipped = recs.filter(r => r.quality > 1 || (r.mods && r.mods.length)).length;
    const loadout = `<!-- SaveDoctor kit "${cm(kitName)}" as a spawn loadout.\n     Replace the existing <property class="ItemsOnEnterGame"> of the player entity class in Config/entityclasses.xml (modlet: <set xpath="/entity_classes/entity_class[@name='playerMale']/property[@class='ItemsOnEnterGame']/property[@name='GameModeSurvival']/@value">…</set>).\n     The game reads only "name=count" here – quality, installed mods, ammo and wear cannot be set in a vanilla loadout${skipped ? ` (${skipped} stack(s) of this kit have quality or mods that get lost this way)` : ''}.\n     Use the loot.xml group or a script / mod that reads the .json kit if you need them. The first entries go into the toolbelt slots 2, 3, … -->\n<property class="ItemsOnEnterGame">\n\t<property name="GameModeSurvival" value="${xe(list)}"/>\n\t<property name="GameModeSurvivalSP" value="${xe(list)}"/>\n\t<property name="GameModeSurvivalMP" value="${xe(list)}"/>\n</property>\n`;
    return [{ name: `${id}.sdkit.json`, text: json, label: 'Kit (JSON, everything)' }, { name: `${id}_loot.xml`, text: loot, label: 'loot.xml group + container' }, { name: `${id}_loadout.xml`, text: loadout, label: 'Spawn loadout (name=count)' }];
  }

  /** opts: { title, groups: { key: { label, items: [{count,item}], labels? } }, names } */
  KIT.open = function (opts) {
    const keys = Object.keys(opts.groups);
    const picked = new Set(keys);
    const st = { name: safe(opts.title || 'kit'), file: 0 };
    const body = h('div', {});
    const render = () => {
      const { recs, missing } = collect(opts.groups, opts.names, picked);
      const files = recs.length ? build(st.name, recs) : [];
      body.replaceChildren(
        h('div', { class: 'row' }, h('label', { class: 'muted' }, 'Kit name'), h('input', { type: 'text', value: st.name, style: 'width:200px', onchange: e => { st.name = e.target.value.trim() || 'kit'; render(); } }),
          keys.map(k => h('label', { style: 'display:flex;gap:5px;align-items:center' }, h('input', { type: 'checkbox', checked: picked.has(k), onchange: e => { if (e.target.checked) picked.add(k); else picked.delete(k); render(); } }), opts.groups[k].label + ' (' + opts.groups[k].items.filter(s => s.count > 0 && s.item && s.item.type).length + ')'))),
        h('div', { class: 'muted' }, `${recs.length} item stack(s): ` + (recs.length ? [...new Set(recs.map(r => r.item))].slice(0, 8).join(', ') + (new Set(recs.map(r => r.item)).size > 8 ? ', …' : '') : 'nothing selected')),
        missing.size ? h('div', { class: 'warn' }, missing.size + ' item(s) have no known name in this save (unknown ids) and were left out.') : null);
      if (!files.length) return;
      if (st.file >= files.length) st.file = 0;
      body.append(h('div', { class: 'tabs', style: 'margin-top:8px' }, files.map((f, i) => h('div', { class: 'tab' + (st.file === i ? ' sel' : ''), onclick: () => { st.file = i; render(); } }, f.label))),
        h('textarea', { readonly: true, style: 'width:100%;height:300px;font:12px var(--mono);white-space:pre', value: files[st.file].text }),
        h('div', { class: 'row', style: 'margin-top:8px;justify-content:flex-end' },
          h('button', { onclick: () => { navigator.clipboard.writeText(files[st.file].text).then(() => toast('Copied.'), () => toast('Copy failed – select the text and press Ctrl+C.', 'err')); } }, 'Copy'),
          h('button', { onclick: () => save(new Blob([files[st.file].text], { type: 'text/plain' }), files[st.file].name) }, '⬇ ' + files[st.file].name),
          h('button', { class: 'primary', onclick: () => save(zip(files), safe(st.name) + '_kit.zip') }, '⬇ All 3 files (.zip)')));
    };
    render();
    modal(h('div', {}, h('h2', {}, 'Export kit'), h('div', { class: 'muted', style: 'margin-bottom:8px' }, 'Exports the selected items with quality, installed mods, wear, ammo / fuel (meta) and extra item data. The JSON file keeps everything; the XML files are ready to paste into a mod.'), body), null, true);
  };
})();
