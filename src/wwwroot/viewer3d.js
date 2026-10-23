'use strict';
/* SaveDoctor 3D chunk viewer – plain WebGL, no dependencies.
   Loads the block data of a chunk neighbourhood from /api/region/voxels, meshes only the visible faces
   (rebuilt whenever a clipping plane / filter changes, so cut surfaces are always closed) and lets you orbit,
   pan, zoom, slice and hover blocks. */
(function () {
  const RULES = [
    [/bedrock/i, [48, 48, 54]], [/snow|ice|frost/i, [236, 241, 246]], [/oreCoal|coal/i, [34, 34, 36]], [/oreIron/i, [176, 112, 92]],
    [/oreLead/i, [96, 104, 140]], [/oreNitrate|orePotassium|nitrate/i, [210, 210, 226]], [/oreOil|oil/i, [60, 52, 40]], [/ore/i, [206, 176, 70]],
    [/grass|plant|leaf|leaves|moss|cactus|bush|shrub|crop|corn|potato|mushroom|aloe|yucca/i, [74, 134, 58]],
    [/sand|desert/i, [216, 198, 132]], [/gravel/i, [142, 137, 132]], [/dirt|soil|mud|farm|clay|forest/i, [122, 86, 56]],
    [/concrete|cement|asphalt|road|pavement/i, [152, 152, 158]], [/stone|rock|cobble|boulder|granite/i, [126, 126, 132]],
    [/glass|window/i, [150, 212, 232]], [/brick|terracotta|tile/i, [164, 72, 56]],
    [/wood|plank|log|tree|trunk|pole|door|frame|fence|scaffold|beam/i, [168, 122, 72]],
    [/iron|steel|metal|rebar|tin|aluminum|vault|pipe|barrel|duct|vent|bar|grate/i, [122, 134, 150]],
    [/cloth|carpet|couch|bed|curtain|fabric|wool|sofa/i, [176, 92, 124]],
    [/light|lamp|torch|neon|candle|lantern|fire|flame/i, [255, 226, 110]],
    [/cnt|chest|storage|safe|crate|locker|cabinet|workbench|forge|campfire|cooker|desk/i, [208, 124, 42]],
    [/wire|power|solar|battery|generator|switch|trap|turret|spike/i, [244, 204, 44]],
  ];
  const colorCache = new Map();
  function blockColor(name, id) {
    let c = colorCache.get(id); if (c) return c;
    const base = (name || '').split(':')[0];
    for (const [rx, col] of RULES) if (rx.test(base)) { c = col; break; }
    if (!c) { let hsh = 2166136261; for (const ch of base || String(id)) hsh = Math.imul(hsh ^ ch.charCodeAt(0), 16777619); const r = 90 + ((hsh >>> 0) & 127), g = 90 + ((hsh >>> 8) & 127), b = 90 + ((hsh >>> 16) & 127); c = [r, g, b]; }
    colorCache.set(id, c); return c;
  }
  const isTerrain = name => /^terr/i.test(name || '') || /^terrain/i.test(name || '');

  // ───────── tiny matrix library (column-major) ─────────
  const M = {
    perspective(f, a, n, fr) { const t = 1 / Math.tan(f / 2), o = new Float32Array(16); o[0] = t / a; o[5] = t; o[10] = (fr + n) / (n - fr); o[11] = -1; o[14] = 2 * fr * n / (n - fr); return o; },
    lookAt(e, c, u) {
      let zx = e[0] - c[0], zy = e[1] - c[1], zz = e[2] - c[2]; let l = Math.hypot(zx, zy, zz) || 1; zx /= l; zy /= l; zz /= l;
      let xx = u[1] * zz - u[2] * zy, xy = u[2] * zx - u[0] * zz, xz = u[0] * zy - u[1] * zx; l = Math.hypot(xx, xy, xz) || 1; xx /= l; xy /= l; xz /= l;
      const yx = zy * xz - zz * xy, yy = zz * xx - zx * xz, yz = zx * xy - zy * xx;
      return new Float32Array([xx, yx, zx, 0, xy, yy, zy, 0, xz, yz, zz, 0, -(xx * e[0] + xy * e[1] + xz * e[2]), -(yx * e[0] + yy * e[1] + yz * e[2]), -(zx * e[0] + zy * e[1] + zz * e[2]), 1]);
    },
    mul(a, b) { const o = new Float32Array(16); for (let i = 0; i < 4; i++) for (let j = 0; j < 4; j++) { let s = 0; for (let k = 0; k < 4; k++) s += a[k * 4 + j] * b[i * 4 + k]; o[i * 4 + j] = s; } return o; },
    invert(m) {
      const inv = new Float64Array(16), a = m;
      inv[0] = a[5] * a[10] * a[15] - a[5] * a[11] * a[14] - a[9] * a[6] * a[15] + a[9] * a[7] * a[14] + a[13] * a[6] * a[11] - a[13] * a[7] * a[10];
      inv[4] = -a[4] * a[10] * a[15] + a[4] * a[11] * a[14] + a[8] * a[6] * a[15] - a[8] * a[7] * a[14] - a[12] * a[6] * a[11] + a[12] * a[7] * a[10];
      inv[8] = a[4] * a[9] * a[15] - a[4] * a[11] * a[13] - a[8] * a[5] * a[15] + a[8] * a[7] * a[13] + a[12] * a[5] * a[11] - a[12] * a[7] * a[9];
      inv[12] = -a[4] * a[9] * a[14] + a[4] * a[10] * a[13] + a[8] * a[5] * a[14] - a[8] * a[6] * a[13] - a[12] * a[5] * a[10] + a[12] * a[6] * a[9];
      inv[1] = -a[1] * a[10] * a[15] + a[1] * a[11] * a[14] + a[9] * a[2] * a[15] - a[9] * a[3] * a[14] - a[13] * a[2] * a[11] + a[13] * a[3] * a[10];
      inv[5] = a[0] * a[10] * a[15] - a[0] * a[11] * a[14] - a[8] * a[2] * a[15] + a[8] * a[3] * a[14] + a[12] * a[2] * a[11] - a[12] * a[3] * a[10];
      inv[9] = -a[0] * a[9] * a[15] + a[0] * a[11] * a[13] + a[8] * a[1] * a[15] - a[8] * a[3] * a[13] - a[12] * a[1] * a[11] + a[12] * a[3] * a[9];
      inv[13] = a[0] * a[9] * a[14] - a[0] * a[10] * a[13] - a[8] * a[1] * a[14] + a[8] * a[2] * a[13] + a[12] * a[1] * a[10] - a[12] * a[2] * a[9];
      inv[2] = a[1] * a[6] * a[15] - a[1] * a[7] * a[14] - a[5] * a[2] * a[15] + a[5] * a[3] * a[14] + a[13] * a[2] * a[7] - a[13] * a[3] * a[6];
      inv[6] = -a[0] * a[6] * a[15] + a[0] * a[7] * a[14] + a[4] * a[2] * a[15] - a[4] * a[3] * a[14] - a[12] * a[2] * a[7] + a[12] * a[3] * a[6];
      inv[10] = a[0] * a[5] * a[15] - a[0] * a[7] * a[13] - a[4] * a[1] * a[15] + a[4] * a[3] * a[13] + a[12] * a[1] * a[7] - a[12] * a[3] * a[5];
      inv[14] = -a[0] * a[5] * a[14] + a[0] * a[6] * a[13] + a[4] * a[1] * a[14] - a[4] * a[2] * a[13] - a[12] * a[1] * a[6] + a[12] * a[2] * a[5];
      inv[3] = -a[1] * a[6] * a[11] + a[1] * a[7] * a[10] + a[5] * a[2] * a[11] - a[5] * a[3] * a[10] - a[9] * a[2] * a[7] + a[9] * a[3] * a[6];
      inv[7] = a[0] * a[6] * a[11] - a[0] * a[7] * a[10] - a[4] * a[2] * a[11] + a[4] * a[3] * a[10] + a[8] * a[2] * a[7] - a[8] * a[3] * a[6];
      inv[11] = -a[0] * a[5] * a[11] + a[0] * a[7] * a[9] + a[4] * a[1] * a[11] - a[4] * a[3] * a[9] - a[8] * a[1] * a[7] + a[8] * a[3] * a[5];
      inv[15] = a[0] * a[5] * a[10] - a[0] * a[6] * a[9] - a[4] * a[1] * a[10] + a[4] * a[2] * a[9] + a[8] * a[1] * a[6] - a[8] * a[2] * a[5];
      let det = a[0] * inv[0] + a[1] * inv[4] + a[2] * inv[8] + a[3] * inv[12]; if (!det) return null; det = 1 / det;
      const o = new Float32Array(16); for (let i = 0; i < 16; i++) o[i] = inv[i] * det; return o;
    },
    mulVec(m, v) { const o = [0, 0, 0, 0]; for (let j = 0; j < 4; j++) o[j] = m[j] * v[0] + m[4 + j] * v[1] + m[8 + j] * v[2] + m[12 + j] * v[3]; return o; },
  };

  const FACES = [
    { d: [1, 0, 0], v: [[1, 0, 0], [1, 1, 0], [1, 1, 1], [1, 0, 1]], s: 0.82 },
    { d: [-1, 0, 0], v: [[0, 0, 1], [0, 1, 1], [0, 1, 0], [0, 0, 0]], s: 0.82 },
    { d: [0, 1, 0], v: [[0, 1, 0], [0, 1, 1], [1, 1, 1], [1, 1, 0]], s: 1.0 },
    { d: [0, -1, 0], v: [[0, 0, 1], [0, 0, 0], [1, 0, 0], [1, 0, 1]], s: 0.5 },
    { d: [0, 0, 1], v: [[1, 0, 1], [1, 1, 1], [0, 1, 1], [0, 0, 1]], s: 0.68 },
    { d: [0, 0, -1], v: [[0, 0, 0], [0, 1, 0], [1, 1, 0], [1, 0, 0]], s: 0.68 },
  ];

  window.open3D = async function (opts) {
    const { rel, slots, centerSlot, ro } = opts; // slots: [{index,chunkX,chunkZ}]
    const byCoord = new Map(slots.map(s => [s.chunkX + ',' + s.chunkZ, s]));
    const center = slots.find(s => s.index === centerSlot);
    if (!center) return;

    const ov = h('div', { style: 'position:fixed;inset:0;z-index:70;background:#0b0e12;display:flex;flex-direction:column' });
    const panel = h('div', { style: 'width:300px;overflow:auto;padding:10px 12px;background:var(--panel);border-right:1px solid var(--line);flex:none' });
    const stage = h('div', { style: 'flex:1;position:relative;min-width:0' });
    const canvas = h('canvas', { style: 'position:absolute;inset:0;width:100%;height:100%;display:block' });
    const hover = h('div', { style: 'position:absolute;left:10px;bottom:10px;background:rgba(0,0,0,.65);color:#fff;padding:4px 9px;border-radius:5px;font:12px var(--mono);pointer-events:none' }, '');
    const status = h('div', { style: 'position:absolute;right:10px;top:10px;background:rgba(0,0,0,.55);color:#cfd6df;padding:4px 9px;border-radius:5px;font:12px var(--mono);pointer-events:none' }, 'loading…');
    stage.append(canvas, hover, status);
    ov.append(h('div', { style: 'display:flex;flex:1;min-height:0' }, panel, stage));
    document.body.append(ov);
    const close = () => { stopped = true; ov.remove(); window.removeEventListener('keydown', onKey); };
    let stopped = false;
    const onKey = e => { if (e.key === 'Escape') close(); };
    window.addEventListener('keydown', onKey);

    const gl = canvas.getContext('webgl', { antialias: true });
    if (!gl) { toast('WebGL is not available in this browser.', 'err'); close(); return; }

    // ── GL program
    const mk = (type, src) => { const s = gl.createShader(type); gl.shaderSource(s, src); gl.compileShader(s); if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s)); return s; };
    const prog = gl.createProgram();
    gl.attachShader(prog, mk(gl.VERTEX_SHADER, 'attribute vec3 aPos;attribute vec4 aCol;uniform mat4 uVP;varying vec3 vC;void main(){vC=aCol.rgb;gl_Position=uVP*vec4(aPos,1.0);}'));
    gl.attachShader(prog, mk(gl.FRAGMENT_SHADER, 'precision mediump float;varying vec3 vC;void main(){gl_FragColor=vec4(vC,1.0);}'));
    gl.linkProgram(prog); gl.useProgram(prog);
    const aPos = gl.getAttribLocation(prog, 'aPos'), aCol = gl.getAttribLocation(prog, 'aCol'), uVP = gl.getUniformLocation(prog, 'uVP');
    const vbo = gl.createBuffer();
    gl.enable(gl.DEPTH_TEST);

    // ── state
    const S3 = { radius: 1, hideTerrain: false, hidden: new Set(), grid: null, W: 0, D: 0, x0: 0, z0: 0, names: {}, clip: [0, 0, 0, 0, 0, 0], vcount: 0, yMax: 0, counts: new Map() };
    const cam = { yaw: 0.7, pitch: 0.6, dist: 60, target: [8, 40, -8] };
    let dirty = true, meshDirty = true;

    const legend = h('div', { style: 'max-height:240px;overflow:auto;margin-top:6px' });
    const sliders = h('div');
    const sizeSel = h('select', { onchange: () => { S3.radius = Number(sizeSel.value); load(); } }, [[0, 'this chunk only'], [1, '3 × 3 chunks'], [2, '5 × 5 chunks'], [3, '7 × 7 chunks']].map(([v, l]) => h('option', { value: v, selected: v === S3.radius }, l)));
    sizeSel.value = String(S3.radius);
    const chkTerr = h('input', { type: 'checkbox', onchange: () => { S3.hideTerrain = chkTerr.checked; applyVisibility(); meshDirty = true; dirty = true; } });
    panel.append(
      h('div', { style: 'display:flex;justify-content:space-between;align-items:center' }, h('b', {}, '3D chunk viewer'), h('button', { onclick: close }, 'Close (Esc)')),
      h('div', { class: 'muted', style: 'font-size:12px;margin:4px 0 8px' }, `${rel.split('/').pop()} · chunk (${center.chunkX}, ${center.chunkZ})`),
      h('div', { class: 'field' }, h('label', {}, 'Area to load'), sizeSel),
      h('label', { style: 'display:flex;gap:6px;align-items:center;margin:4px 0' }, chkTerr, 'Hide natural terrain (terr* blocks) – shows only built things'),
      h('h3', {}, 'Clipping planes'), sliders,
      h('div', { class: 'row' }, h('button', { onclick: () => { resetClip(); } }, 'Reset clipping'), h('button', { onclick: () => { resetView(); } }, 'Reset view')),
      h('h3', {}, 'Blocks (click to hide / show)'), legend,
      h('div', { class: 'muted', style: 'font-size:12px;margin-top:8px' }, 'Left-drag: orbit · right-drag / shift-drag: pan · wheel: zoom. Cut faces are closed automatically.'));

    function resetView() {
      cam.target = [S3.W / 2, Math.max(30, S3.yMax * 0.55), -S3.D / 2]; cam.dist = Math.max(S3.W, S3.D) * 1.6 + 20; cam.yaw = 0.7; cam.pitch = 0.6; dirty = true;
    }
    function resetClip() {
      S3.clip = [0, S3.W - 1, 0, Math.min(255, S3.yMax + 1), 0, S3.D - 1]; buildSliders(); meshDirty = true; dirty = true;
    }
    function buildSliders() {
      sliders.replaceChildren();
      const defs = [['X', 0, S3.W - 1, S3.x0], ['Y (height)', 0, 255, 0], ['Z', 0, S3.D - 1, S3.z0]];
      defs.forEach(([lab, lo, hi, off], ax) => {
        const mn = h('input', { type: 'range', min: lo, max: hi, value: S3.clip[ax * 2], style: 'width:100%' });
        const mx = h('input', { type: 'range', min: lo, max: hi, value: S3.clip[ax * 2 + 1], style: 'width:100%' });
        const t = h('div', { class: 'muted mono', style: 'font-size:12px' });
        const upd = () => { t.textContent = `${lab}: ${S3.clip[ax * 2] + off} … ${S3.clip[ax * 2 + 1] + off}`; };
        mn.oninput = () => { let v = Number(mn.value); if (v > S3.clip[ax * 2 + 1]) { v = S3.clip[ax * 2 + 1]; mn.value = v; } S3.clip[ax * 2] = v; upd(); meshDirty = true; dirty = true; };
        mx.oninput = () => { let v = Number(mx.value); if (v < S3.clip[ax * 2]) { v = S3.clip[ax * 2]; mx.value = v; } S3.clip[ax * 2 + 1] = v; upd(); meshDirty = true; dirty = true; };
        upd(); sliders.append(t, mn, mx);
      });
    }

    // ── data
    const vis = new Uint8Array(65536);
    function applyVisibility() {
      vis.fill(1); vis[0] = 0;
      for (const id of S3.hidden) vis[id] = 0;
      if (S3.hideTerrain) for (const [id, n] of Object.entries(S3.names)) if (isTerrain(n)) vis[Number(id)] = 0;
    }
    function buildLegend() {
      legend.replaceChildren();
      const arr = [...S3.counts.entries()].sort((a, b) => b[1] - a[1]).slice(0, 60);
      for (const [id, cnt] of arr) {
        const nm = S3.names[id] || 'block#' + id; const c = blockColor(nm, id);
        const row = h('div', { style: 'display:flex;gap:6px;align-items:center;cursor:pointer;font-size:12px;padding:1px 0;' + (S3.hidden.has(id) ? 'opacity:.35' : ''), onclick: () => { if (S3.hidden.has(id)) S3.hidden.delete(id); else S3.hidden.add(id); applyVisibility(); buildLegend(); meshDirty = true; dirty = true; } },
          h('i', { style: `width:12px;height:12px;border-radius:2px;flex:none;background:rgb(${c[0]},${c[1]},${c[2]})` }), h('span', { style: 'flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap' }, nm), h('span', { class: 'muted' }, cnt.toLocaleString()));
        legend.append(row);
      }
    }
    async function load() {
      status.textContent = 'loading…';
      const r = S3.radius, want = [];
      for (let dz = -r; dz <= r; dz++) for (let dx = -r; dx <= r; dx++) { const s = byCoord.get((center.chunkX + dx) + ',' + (center.chunkZ + dz)); if (s && s.status !== 'bad') want.push(s); }
      const resp = await api('/api/region/voxels?' + qs({ rel, slots: want.map(s => s.index).join(',') }));
      const minCx = Math.min(...resp.chunks.map(c => c.cx)), maxCx = Math.max(...resp.chunks.map(c => c.cx));
      const minCz = Math.min(...resp.chunks.map(c => c.cz)), maxCz = Math.max(...resp.chunks.map(c => c.cz));
      const W = (maxCx - minCx + 1) * 16, D = (maxCz - minCz + 1) * 16;
      const grid = new Uint16Array(W * D * 256);
      let yMax = 0; const counts = new Map();
      for (const ch of resp.chunks) {
        const bin = atob(ch.data); const bytes = new Uint8Array(bin.length); for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
        const d = new Uint16Array(bytes.buffer);
        const bx = (ch.cx - minCx) * 16, bz = (ch.cz - minCz) * 16;
        for (let y = 0; y < 256; y++) for (let z = 0; z < 16; z++) {
          const so = y * 256 + z * 16, go = (y * D + bz + z) * W + bx;
          for (let x = 0; x < 16; x++) { const id = d[so + x]; if (id) { grid[go + x] = id; if (y > yMax) yMax = y; counts.set(id, (counts.get(id) || 0) + 1); } }
        }
      }
      Object.assign(S3, { grid, W, D, x0: minCx * 16, z0: minCz * 16, names: resp.names, yMax, counts });
      applyVisibility(); buildLegend(); resetClip(); resetView();
      status.textContent = `${resp.chunks.length} chunk(s) · ${W}×${D} blocks · top y=${yMax}`;
    }

    // ── meshing
    let vbuf = new ArrayBuffer(1 << 22), f32 = new Float32Array(vbuf), u32 = new Uint32Array(vbuf), nv = 0;
    function ensure(extra) { if ((nv + extra) * 4 > f32.length) { const nb = new ArrayBuffer(vbuf.byteLength * 2); new Uint8Array(nb).set(new Uint8Array(vbuf)); vbuf = nb; f32 = new Float32Array(vbuf); u32 = new Uint32Array(vbuf); } }
    function buildMesh() {
      const { grid, W, D, clip } = S3; if (!grid) return;
      nv = 0;
      const [cx0, cx1, cy0, cy1, cz0, cz1] = clip;
      const solid = (x, y, z) => x >= cx0 && x <= cx1 && y >= cy0 && y <= cy1 && z >= cz0 && z <= cz1 && vis[grid[(y * D + z) * W + x]] === 1;
      const nameCache = new Map();
      const colOf = id => { let c = nameCache.get(id); if (!c) { c = blockColor(S3.names[id], id); nameCache.set(id, c); } return c; };
      for (let y = cy0; y <= cy1; y++) for (let z = cz0; z <= cz1; z++) {
        const row = (y * D + z) * W;
        for (let x = cx0; x <= cx1; x++) {
          const id = grid[row + x]; if (!id || !vis[id]) continue;
          const col = colOf(id); const jit = 0.94 + ((x * 73856093 ^ y * 19349663 ^ z * 83492791) & 15) / 16 * 0.1;
          for (const f of FACES) {
            const nx = x + f.d[0], ny = y + f.d[1], nz = z + f.d[2];
            if (solid(nx, ny, nz)) continue;
            const sh = f.s * jit;
            const r = Math.min(255, col[0] * sh) | 0, g = Math.min(255, col[1] * sh) | 0, b = Math.min(255, col[2] * sh) | 0;
            const packed = (255 << 24) | (b << 16) | (g << 8) | r;
            ensure(6);
            const q = f.v;
            for (const k of [0, 1, 2, 0, 2, 3]) {
              const o = nv * 4; f32[o] = x + q[k][0]; f32[o + 1] = y + q[k][1]; f32[o + 2] = -(z + q[k][2]); u32[o + 3] = packed >>> 0; nv++;
            }
          }
        }
      }
      gl.bindBuffer(gl.ARRAY_BUFFER, vbo);
      gl.bufferData(gl.ARRAY_BUFFER, new Uint8Array(vbuf, 0, nv * 16), gl.STATIC_DRAW);
      S3.vcount = nv;
    }

    // ── camera / draw
    function viewProj() {
      const aspect = canvas.width / Math.max(1, canvas.height);
      const e = [cam.target[0] + cam.dist * Math.cos(cam.pitch) * Math.sin(cam.yaw), cam.target[1] + cam.dist * Math.sin(cam.pitch), cam.target[2] + cam.dist * Math.cos(cam.pitch) * Math.cos(cam.yaw)];
      const v = M.lookAt(e, cam.target, [0, 1, 0]);
      const p = M.perspective(0.9, aspect, Math.max(0.05, cam.dist * 0.01), cam.dist * 30 + 500);
      return { vp: M.mul(p, v), eye: e };
    }
    function draw() {
      const dpr = window.devicePixelRatio || 1;
      const w = Math.floor(stage.clientWidth * dpr), hh = Math.floor(stage.clientHeight * dpr);
      if (canvas.width !== w || canvas.height !== hh) { canvas.width = w; canvas.height = hh; }
      gl.viewport(0, 0, canvas.width, canvas.height);
      gl.clearColor(0.043, 0.055, 0.07, 1); gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
      if (meshDirty) { buildMesh(); meshDirty = false; }
      if (!S3.vcount) return;
      gl.useProgram(prog);
      gl.uniformMatrix4fv(uVP, false, viewProj().vp);
      gl.bindBuffer(gl.ARRAY_BUFFER, vbo);
      gl.enableVertexAttribArray(aPos); gl.vertexAttribPointer(aPos, 3, gl.FLOAT, false, 16, 0);
      gl.enableVertexAttribArray(aCol); gl.vertexAttribPointer(aCol, 4, gl.UNSIGNED_BYTE, true, 16, 12);
      gl.drawArrays(gl.TRIANGLES, 0, S3.vcount);
    }
    (function loop() { if (stopped) return; if (dirty) { dirty = false; try { draw(); } catch (e) { console.error(e); } } requestAnimationFrame(loop); })();
    new ResizeObserver(() => { dirty = true; }).observe(stage);

    // ── input
    let drag = null;
    canvas.addEventListener('contextmenu', e => e.preventDefault());
    canvas.addEventListener('mousedown', e => { drag = { x: e.clientX, y: e.clientY, pan: e.button !== 0 || e.shiftKey }; });
    window.addEventListener('mouseup', () => { drag = null; });
    window.addEventListener('mousemove', e => {
      if (stopped) return;
      if (drag) {
        const dx = e.clientX - drag.x, dy = e.clientY - drag.y; drag.x = e.clientX; drag.y = e.clientY;
        if (drag.pan) {
          const { eye } = viewProj(); let fx = cam.target[0] - eye[0], fy = cam.target[1] - eye[1], fz = cam.target[2] - eye[2]; const l = Math.hypot(fx, fy, fz); fx /= l; fy /= l; fz /= l;
          let rx = fy * 0 - fz * 1, ry = fz * 0 - fx * 0, rz = fx * 1 - fy * 0; const rl = Math.hypot(rx, ry, rz) || 1; rx /= rl; ry /= rl; rz /= rl;
          const ux = ry * fz - rz * fy, uy = rz * fx - rx * fz, uz = rx * fy - ry * fx; const k = cam.dist * 0.0016;
          cam.target[0] += (-dx * rx + dy * ux) * k; cam.target[1] += (-dx * ry + dy * uy) * k; cam.target[2] += (-dx * rz + dy * uz) * k;
        } else { cam.yaw -= dx * 0.006; cam.pitch = Math.max(-1.5, Math.min(1.5, cam.pitch + dy * 0.006)); }
        dirty = true;
      } else pick(e);
    });
    canvas.addEventListener('wheel', e => { e.preventDefault(); cam.dist *= Math.exp(e.deltaY * 0.0012); cam.dist = Math.max(2, Math.min(5000, cam.dist)); dirty = true; }, { passive: false });

    function pick(e) {
      if (!S3.grid) return;
      const r = canvas.getBoundingClientRect(); const nx = ((e.clientX - r.left) / r.width) * 2 - 1, ny = 1 - ((e.clientY - r.top) / r.height) * 2;
      if (nx < -1 || nx > 1 || ny < -1 || ny > 1) { hover.textContent = ''; return; }
      const { vp } = viewProj(); const inv = M.invert(vp); if (!inv) return;
      const a = M.mulVec(inv, [nx, ny, -1, 1]), b = M.mulVec(inv, [nx, ny, 1, 1]);
      const o = [a[0] / a[3], a[1] / a[3], -a[2] / a[3]], t = [b[0] / b[3], b[1] / b[3], -b[2] / b[3]]; // render z = -grid z
      let dir = [t[0] - o[0], t[1] - o[1], t[2] - o[2]]; const dl = Math.hypot(...dir); dir = dir.map(v => v / dl);
      const { grid, W, D, clip } = S3; const [cx0, cx1, cy0, cy1, cz0, cz1] = clip;
      let p = o.slice(), x = Math.floor(p[0]), y = Math.floor(p[1]), z = Math.floor(p[2]);
      const step = dir.map(v => v > 0 ? 1 : -1), tD = dir.map(v => Math.abs(1 / (v || 1e-9)));
      const tM = [0, 1, 2].map(i => { const c = [x, y, z][i]; const edge = dir[i] > 0 ? c + 1 - p[i] : p[i] - c; return edge * tD[i]; });
      for (let i = 0; i < 1200; i++) {
        if (x >= cx0 && x <= cx1 && y >= cy0 && y <= cy1 && z >= cz0 && z <= cz1) {
          const id = grid[(y * D + z) * W + x];
          if (id && vis[id]) { hover.textContent = `${S3.names[id] || 'block#' + id}  (id ${id})   x ${x + S3.x0}  y ${y}  z ${z + S3.z0}`; return; }
        }
        const m = tM[0] < tM[1] ? (tM[0] < tM[2] ? 0 : 2) : (tM[1] < tM[2] ? 1 : 2);
        if (m === 0) { x += step[0]; tM[0] += tD[0]; } else if (m === 1) { y += step[1]; tM[1] += tD[1]; } else { z += step[2]; tM[2] += tD[2]; }
        if (x < -2 || x > W + 2 || z < -2 || z > D + 2 || y < -2 || y > 260) { if (i > 4 && ((step[0] > 0 && x > W) || (step[0] < 0 && x < 0) || (step[2] > 0 && z > D) || (step[2] < 0 && z < 0)) && (y < 0 || y > 256)) break; }
      }
      hover.textContent = '';
    }

    try { await load(); } catch (e) { toast('3D load failed: ' + e.message, 'err'); close(); }
  };
})();
