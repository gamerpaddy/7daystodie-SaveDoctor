'use strict';
/* World tools → Players & claims: land claims, bedrolls, ownership transfers and copying characters from another save. */
(function () {
  const { num, fld, fmtN } = WT;
  const P = (p) => p ? p.join(', ') : '–';

  WT.register('players', '👥 Players & claims', async el => {
    const r = await api('/api/players/list');
    const players = r.players;
    el.append(h('div', { class: 'card muted' }, 'players.xml remembers which land claims, bedrolls, dropped backpacks and vending machines belong to whom. Claims can be moved to another player (the claim block\'s owner in the world is changed too), removed, or added by hand. Close the game first.'));
    if (!players.length) { el.append(h('div', { class: 'card' }, 'players.xml lists no players.')); return; }
    const opts = (skip) => players.filter(p => p.userId !== skip).map(p => h('option', { value: p.userId }, p.name + ' (' + p.userId.slice(0, 8) + '…)'));

    for (const p of players) {
      const claims = p.claims || [];
      const card = h('div', { class: 'card' }, h('div', { class: 'row', style: 'margin:0' }, h('h3', { style: 'margin:0' }, p.name), h('span', { class: 'muted mono' }, p.platform + ':' + p.userId), h('span', { class: 'muted' }, 'last login ' + (p.lastLogin || '?')), p.file ? h('a', { href: '#', onclick: e => { e.preventDefault(); guarded(() => selectFile(p.file)); } }, 'open player file') : h('span', { class: 'muted' }, 'no player file')));
      card.append(h('div', { class: 'muted' }, 'position ' + P(p.position) + ' · bedroll ' + P(p.bedroll) + ' · ' + claims.length + ' claim(s) · ' + (p.backpacks || []).length + ' dropped backpack(s) · ' + (p.vending || []).length + ' vending machine(s)'));
      if (claims.length) card.append(h('table', {}, h('tr', {}, ['Land claim at', '', ''].map(x => h('th', {}, x))), claims.map(pos => {
        const to = h('select', {}, h('option', { value: '' }, 'give to…'), opts(p.userId));
        return h('tr', {}, h('td', {}, WT.posLink(pos[0], pos[1], pos[2])), h('td', {}, to, ' ', h('button', { class: 'small', onclick: () => guarded(async () => {
          if (!to.value) { toast('Choose the new owner first.', 'err'); return; } if (!WT.needWrite()) return;
          const t = players.find(x => x.userId === to.value);
          if (!(await confirmBox('Transfer this claim?', `The land claim at ${pos.join(', ')} moves from ${p.name} to ${t.name}. The owner of the claim block in the world is changed as well.`, 'Transfer', true))) return;
          const res = await api('/api/players/claim', { body: { action: 'transfer', user: p.userId, to: to.value, pos } }); toast(res.message, 'ok', 9000); WT.render($('#main'));
        }) }, 'Transfer')),
          h('td', {}, h('button', { class: 'small danger', onclick: () => guarded(async () => {
            if (!WT.needWrite()) return;
            const both = await confirmBox('Remove this claim?', `${p.name}'s claim at ${pos.join(', ')} is removed from players.xml (area becomes unprotected). The claim block stays in the world unless you press "Remove claim and block".`, 'Remove entry', true);
            if (!both) return;
            const res = await api('/api/players/claim', { body: { action: 'remove', user: p.userId, pos } }); toast(res.message, 'ok', 9000); WT.render($('#main'));
          }) }, 'Remove'), ' ', h('button', { class: 'small danger', title: 'Also deletes the land claim block from the world', onclick: () => guarded(async () => {
            if (!WT.needWrite()) return;
            if (!(await confirmBox('Remove claim and block?', 'The list entry and the land claim block in the world are both removed.', 'Remove both', true))) return;
            const res = await api('/api/players/claim', { body: { action: 'remove', user: p.userId, pos, removeBlock: true } }); toast(res.message, 'ok', 9000); WT.render($('#main'));
          }) }, 'Remove claim and block')));
      })));
      const add = { x: 0, y: 0, z: 0 };
      card.append(h('div', { class: 'row' }, h('span', { class: 'muted' }, 'Add a claim at'), ['x', 'y', 'z'].map(k => num(add, k, { step: 1, style: 'width:80px' })), h('button', { class: 'small', onclick: () => guarded(async () => {
        if (!WT.needWrite()) return;
        const res = await api('/api/players/claim', { body: { action: 'add', user: p.userId, pos: [add.x, add.y, add.z] } }); toast(res.message, 'ok', 9000); WT.render($('#main'));
      }) }, 'Add')));
      if ((p.backpacks || []).length) card.append(h('div', { class: 'muted' }, 'Dropped backpacks: ' + p.backpacks.map(b => P(b.pos)).join(' · ')));
      el.append(card);
    }

    // bulk transfer
    if (players.length > 1) {
      const t = { from: players[0].userId, to: players[1].userId, vehicles: true, drones: true, claims: true };
      const sel = (k) => h('select', { onchange: e => { t[k] = e.target.value; } }, players.map(p => h('option', { value: p.userId, selected: t[k] === p.userId }, p.name)));
      const ck = (k, l) => h('label', { style: 'display:flex;gap:5px;align-items:center' }, h('input', { type: 'checkbox', checked: t[k], onchange: e => { t[k] = e.target.checked; } }), l);
      el.append(h('h2', {}, 'Hand over everything'), h('div', { class: 'card' }, h('div', { class: 'row' }, 'From', sel('from'), 'to', sel('to'), ck('vehicles', 'vehicles'), ck('drones', 'drones'), ck('claims', 'land claims'),
        h('button', { class: 'primary', onclick: () => guarded(async () => {
          if (t.from === t.to) { toast('Choose two different players.', 'err'); return; } if (!WT.needWrite()) return;
          if (!(await confirmBox('Transfer ownership?', 'All selected things owned by the first player are given to the second one.', 'Transfer', true))) return;
          const res = await api('/api/players/transfer', { body: t }); toast(res.message, 'ok', 9000); WT.render($('#main'));
        }) }, 'Transfer'))));
    }

    // copy a character from another save
    const src = { path: '' };
    const box = h('div', {});
    const files = (S.files || []).filter(f => /^Player\/[^/]+\.ttp$/.test(f.rel));
    el.append(h('h2', {}, 'Copy a character from another save'), h('div', { class: 'card' },
      h('div', { class: 'muted' }, 'Reads a player file from another save folder and copies items / skills / stats / position into a player of THIS save. Items that do not exist here are dropped. Pick the source folder (the one that contains main.ttw).'),
      h('div', { class: 'row' }, h('input', { type: 'text', placeholder: 'C:\\Users\\…\\Saves\\World\\Game', style: 'width:min(560px,100%)', onchange: e => { src.path = e.target.value; } }),
        h('button', { onclick: () => guarded(async () => loadSource(src, files, box)) }, 'Read players')), box));
  }, 60);

  async function loadSource(src, files, box) {
    box.replaceChildren(h('div', { class: 'muted' }, 'Reading…'));
    const r = await api('/api/players/source?' + qs({ path: src.path }));
    const st = { rel: r.players[0] && r.players[0].rel, target: files[0] && files[0].rel, items: true, skills: true, stats: false, position: false };
    const ck = (k, l) => h('label', { style: 'display:flex;gap:5px;align-items:center' }, h('input', { type: 'checkbox', checked: st[k], onchange: e => { st[k] = e.target.checked; } }), l);
    box.replaceChildren(h('div', { class: 'row' }, 'Take', h('select', { onchange: e => { st.rel = e.target.value; } }, r.players.map(p => h('option', { value: p.rel }, `${p.name} (level ${p.level ?? '?'}, ${p.items} item stacks)`))),
      'from "' + r.name + '" and put it into', h('select', { onchange: e => { st.target = e.target.value; } }, files.map(f => h('option', { value: f.rel }, f.label || f.rel)))),
      h('div', { class: 'row' }, ck('items', 'toolbelt, backpack, equipment'), ck('skills', 'skills & perks'), ck('stats', 'vitals & kill counters'), ck('position', 'position')),
      h('div', { class: 'row' }, h('button', { class: 'primary', onclick: () => guarded(async () => {
        if (!WT.needWrite()) return;
        if (!st.rel || !st.target) { toast('Nothing to copy.', 'err'); return; }
        if (!(await confirmBox('Copy the character?', 'The selected parts of the target player file are overwritten (backup first).', 'Copy', true))) return;
        const res = await api('/api/players/import', { body: { path: src.path, sourceRel: st.rel, targetRel: st.target, items: st.items, skills: st.skills, stats: st.stats, position: st.position } });
        toast(res.message, 'ok', 12000);
      }) }, 'Copy')));
  }
})();
