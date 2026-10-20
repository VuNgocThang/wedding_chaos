// Wedding Chaos rules — exact mirror of Core/SeatingSim.cs (links + blockers). Used by the level generator.
'use strict';
const SC = { CouplePenalty: 15, CoupleBonus: -10, ExFight: 30, ExGlare: 10, GrandmaNotStar: 20, GrandmaScold: 20,
  GossipBonus: -15, Perfect: -5, Rival: 20, Bff: -8, Family: -5, KidCries: 15, Crush: -12, Gift: -5 };
const left = k => (k + 3) % 4, right = k => (k + 1) % 4, opp = k => (k + 2) % 4;
const adjacent = (a, b) => left(a) === b || right(a) === b;
const clamp = d => Math.max(0, Math.min(100, d));
const isG = x => x !== null && typeof x === 'object';
const DIRS = [[-1, 0], [0, -1], [0, 1], [1, 0]];

function rng(seed) { return function () { seed |= 0; seed = seed + 0x6D2B79F5 | 0; let t = Math.imul(seed ^ seed >>> 15, 1 | seed); t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t; return ((t ^ t >>> 14) >>> 0) / 4294967296; }; }

/** level = {rows, cols, cells, bench, drama, ribbons:[{col,r1,c1,r2,c2}]}.
 *  cell: '.', '#', {gift:true, c, n} or a guest {c, t, hidden, reserved, links:[{kind, to}]}; ids are assigned row-major. */
function init(level) {
  const { rows, cols } = level;
  const g = []; let id = 0, total = 0; const colors = []; const gifts = [], giftN = [];
  for (let r = 0; r < rows; r++) {
    const row = [];
    for (let c = 0; c < cols; c++) {
      const cell = level.cells[r * cols + c]; id++;
      if (cell === '.' || cell === '#') { row.push(cell === '#' ? '#' : null); continue; }
      if (cell.gift) { gifts.push({ r, c, col: cell.c }); giftN.push(cell.n); row.push('G'); continue; }
      cell.id = id; row.push(cell); total++;
      if (!colors.includes(cell.c)) colors.push(cell.c);
    }
    g.push(row);
  }
  for (const row of g) for (const x of row) if (isG(x)) for (const l of x.links) l.other = l.to.id;
  colors.sort((a, b) => 'RBGY'.indexOf(a) - 'RBGY'.indexOf(b));
  const tables = {}; colors.forEach(c => tables[c] = { seats: [null, null, null, null], gen: 1, done: false, bad: false });
  const ribbons = (level.ribbons || []).map(x => ({ ...x }));
  return { rows, cols, g, bench: Array(level.bench).fill(null), tables, drama: clamp(level.drama), placed: {}, seated: 0, total,
    gifts, giftN, ribbons, cut: ribbons.map(() => false) };
}

function clone(s) {
  return { rows: s.rows, cols: s.cols, g: s.g.map(r => r.slice()), bench: s.bench.slice(),
    tables: Object.fromEntries(Object.entries(s.tables).map(([c, t]) => [c, { ...t, seats: t.seats.slice() }])),
    drama: s.drama, placed: { ...s.placed }, seated: s.seated, total: s.total,
    gifts: s.gifts, giftN: s.giftN.slice(), ribbons: s.ribbons, cut: s.cut.slice() };
}

function edgeBlocked(s, ya, xa, yb, xb) {
  for (let i = 0; i < s.ribbons.length; i++) {
    if (s.cut[i]) continue;
    const b = s.ribbons[i];
    if ((b.r1 === ya && b.c1 === xa && b.r2 === yb && b.c2 === xb) || (b.r1 === yb && b.c1 === xb && b.r2 === ya && b.c2 === xa)) return true;
  }
  return false;
}

function hasPath(s, r, c) {
  if (r === 0) return true;
  const seen = new Set([r * 16 + c]); const q = [[r, c]];
  while (q.length) {
    const [y, x] = q.shift();
    for (const [dy, dx] of DIRS) {
      const ny = y + dy, nx = x + dx;
      if (ny < 0 || nx < 0 || ny >= s.rows || nx >= s.cols) continue;
      const k = ny * 16 + nx; if (seen.has(k)) continue;
      if (s.g[ny][nx] !== null || edgeBlocked(s, y, x, ny, nx)) continue;
      if (ny === 0) return true;
      seen.add(k); q.push([ny, nx]);
    }
  }
  return false;
}

/** Effects list in the same order as C# PreviewEffects: traits, then links in the guest's link order. */
function effects(s, g, k) {
  const t = s.tables[g.c], se = t.seats, out = [];
  const L = se[left(k)], R = se[right(k)];
  const near = p => (L && p(L)) ? L : (R && p(R)) ? R : null;
  if (g.t === 'grandma' && k !== 2) out.push({ d: SC.GrandmaNotStar, key: 'grandma_not_star' });
  const sc = g.t === 'grandma' ? near(x => x.t === 'uncle') : g.t === 'uncle' ? near(x => x.t === 'grandma') : null;
  if (sc) out.push({ d: SC.GrandmaScold, key: 'scold' });
  const go = g.t === 'gossip' ? near(x => x.t === 'gossip') : null;
  if (go) out.push({ d: SC.GossipBonus, key: 'gossip' });
  for (const l of g.links) {
    const p = s.placed[l.other]; if (!p) continue;
    const same = p.c === g.c && p.gen === t.gen, adj = same && adjacent(p.k, k), op = same && opp(k) === p.k;
    switch (l.kind) {
      case 'couple': out.push(adj ? { d: SC.CoupleBonus, key: 'couple_together' } : { d: SC.CouplePenalty, key: 'couple_split' }); break;
      case 'ex': if (adj) out.push({ d: SC.ExFight, key: 'ex_fight', lock: true }); else if (op) out.push({ d: SC.ExGlare, key: 'ex_glare' }); break;
      case 'rival': if (same) out.push({ d: SC.Rival, key: 'rival' }); break;
      case 'bff': if (same) out.push({ d: SC.Bff, key: 'bff' }); break;
      case 'parent': case 'child': out.push(adj ? { d: SC.Family, key: 'family' } : { d: SC.KidCries, key: 'kid_cries' }); break;
      case 'crush': case 'crushedby': if (adj) out.push({ d: SC.Crush, key: 'crush' }); break;
    }
  }
  return out;
}
const sum = e => e.reduce((a, x) => a + x.d, 0);

function unseated(s) { const out = []; for (const r of s.g) for (const x of r) if (isG(x)) out.push(x); for (const x of s.bench) if (x) out.push(x); return out; }
function remainingOf(s, c) { let n = 0; for (const x of unseated(s)) if (x.c === c) n++; return n; }
function partner(g) { for (const l of g.links) if (l.kind === 'hands') return l.to; return null; }
function locate(s, g) { for (let r = 0; r < s.rows; r++) for (let c = 0; c < s.cols; c++) if (s.g[r][c] === g) return [r, c]; return null; }

function seatsFor(s, g) {
  const t = s.tables[g.c]; if (!t || t.done) return [];
  if (g.reserved >= 0) return t.seats[g.reserved] ? [] : [g.reserved];
  const res = new Set(); for (const o of unseated(s)) if (o !== g && o.c === g.c && o.reserved >= 0) res.add(o.reserved);
  const out = []; for (let k = 0; k < 4; k++) if (!t.seats[k] && !res.has(k)) out.push(k);
  return out;
}

function pairCanLeave(s, r, c) {
  const g = s.g[r][c], p = partner(g); const pos = p && locate(s, p);
  return !!pos && (hasPath(s, r, c) || hasPath(s, pos[0], pos[1]));
}
function pairSeats(s, g) {
  const p = partner(g); const out = [];
  const t = s.tables[g.c]; if (!p || !t || t.done) return out;
  const mine = seatsFor(s, g), theirs = seatsFor(s, p);
  for (const k of mine) for (const j of theirs) if (k !== j && adjacent(k, j)) out.push([k, j]);
  return out;
}

/** a guest steps out of crowd cell r,c: neighbouring gifts of its colour count down, opening at zero (−5% drama). */
function leaveCell(n, r, c, g) {
  n.g[r][c] = null;
  for (const [dy, dx] of DIRS) {
    const y = r + dy, x = c + dx;
    if (y < 0 || x < 0 || y >= n.rows || x >= n.cols || n.g[y][x] !== 'G') continue;
    const gi = n.gifts.findIndex(q => q.r === y && q.c === x);
    if (n.gifts[gi].col !== g.c || n.giftN[gi] <= 0) continue;
    n.giftN[gi]--;
    if (n.giftN[gi] === 0) { n.g[y][x] = null; n.drama = clamp(n.drama + SC.Gift); }
  }
}

function place(n, g, k) {
  const t = n.tables[g.c];
  const eff = effects(n, g, k);
  t.seats[k] = g; n.placed[g.id] = { c: g.c, k, gen: t.gen }; n.seated++;
  for (const e of eff) { n.drama = clamp(n.drama + e.d); if (e.d > 0) t.bad = true; }
  if (t.seats.every(Boolean)) {
    if (!t.bad) n.drama = Math.max(0, n.drama + SC.Perfect);
    n.ribbons.forEach((b, i) => { if (!n.cut[i] && b.col === g.c) n.cut[i] = true; });
    const rem = remainingOf(n, g.c);
    n.tables[g.c] = { seats: [null, null, null, null], gen: t.gen + 1, done: rem === 0, bad: false };
  }
}

function moves(s) {
  const out = []; const bf = s.bench.indexOf(null);
  for (let r = 0; r < s.rows; r++) for (let c = 0; c < s.cols; c++) {
    const g = s.g[r][c]; if (!isG(g)) continue;
    const p = partner(g);
    if (p) {
      if (p.id < g.id || !pairCanLeave(s, r, c)) continue;
      for (const [k, j] of pairSeats(s, g)) {
        const m = { src: ['g', r, c], g, k, j, pair: true };
        m.d = apply(s, m).drama - s.drama;
        out.push(m);
      }
      continue;
    }
    if (!hasPath(s, r, c)) continue;
    for (const k of seatsFor(s, g)) out.push({ src: ['g', r, c], g, k, d: sum(effects(s, g, k)) });
    if (bf >= 0) out.push({ src: ['g', r, c], g, bench: bf, d: 0 });
  }
  s.bench.forEach((g, i) => { if (!g) return; for (const k of seatsFor(s, g)) out.push({ src: ['b', i], g, k, d: sum(effects(s, g, k)) }); });
  return out;
}

function apply(s, m) {
  const n = clone(s);
  if (m.pair) {
    const p = partner(m.g); const pos = locate(n, p);
    leaveCell(n, m.src[1], m.src[2], m.g);
    leaveCell(n, pos[0], pos[1], p);
    place(n, m.g, m.k);
    place(n, p, m.j);
    return n;
  }
  if (m.src[0] === 'g') leaveCell(n, m.src[1], m.src[2], m.g); else n.bench[m.src[1]] = null;
  if (m.bench !== undefined) { n.bench[m.bench] = m.g; return n; }
  place(n, m.g, m.k);
  return n;
}

function key(s) {
  return s.g.map(r => r.map(x => x === null ? '.' : isG(x) ? x.id : x).join(',')).join('/') + '|' +
    s.bench.filter(Boolean).map(x => x.id).sort((a, b) => a - b).join(',') + '|' +
    Object.entries(s.tables).map(([c, t]) => c + t.gen + t.seats.map(x => x ? x.id : '.').join(':')).join(';') + '|' +
    s.giftN.join(',') + s.cut.map(x => x ? 1 : 0).join('') + '|' + s.drama;
}

function solve(s0, limit = 80000) {
  let nodes = 0; const seen = new Set(); let best = null;
  function dfs(s, path) {
    if (++nodes > limit) return false;
    if (s.drama >= 100) return false;
    if (s.seated === s.total) { if (!best || s.drama < best.drama) best = { drama: s.drama, path: path.slice() }; return best.drama <= s0.drama; }
    const k = key(s); if (seen.has(k)) return false; seen.add(k);
    const ms = moves(s).sort((a, b) => (a.bench !== undefined) - (b.bench !== undefined) || a.d - b.d);
    for (const m of ms) { path.push(m); if (dfs(apply(s, m), path)) return true; path.pop(); }
    return false;
  }
  dfs(s0, []);
  return { best, nodes };
}

const srcKey = m => m.src.join();

/** Same policy as Solver.GreedyWinRate: random movable guest, lowest-drama seat, bench it if every seat hurts. */
function greedy(s0, R) { return noisy(s0, R, 0); }

/** "Careful" player: plays the best previewed seat overall (ties random), benches when nothing is free of drama. */
function careful(s0, R, record = false) {
  let s = s0; const path = [];
  for (let step = 0; step < 400; step++) {
    if (s.seated === s.total) return { win: true, drama: s.drama, path };
    const ms = moves(s); if (!ms.length) return { win: false, why: 'stuck' };
    const seats = ms.filter(m => m.bench === undefined);
    let bestD = Infinity; for (const m of seats) bestD = Math.min(bestD, m.d);
    let m;
    if (seats.length && bestD <= 0) { const eq = seats.filter(x => x.d === bestD); m = eq[Math.floor(R() * eq.length)]; }
    else { const b = ms.filter(x => x.bench !== undefined); m = b.length ? b[Math.floor(R() * b.length)] : seats.filter(x => x.d === bestD)[0]; }
    if (record) path.push(m);
    s = apply(s, m);
    if (s.drama >= 100) return { win: false, why: 'drama' };
  }
  return { win: false, why: 'loop' };
}

/** Casual player: random movable guest, usually the best previewed seat but sometimes a random one (no planning). */
function noisy(s0, R, eps = 0.25) {
  let s = s0;
  for (let step = 0; step < 400; step++) {
    if (s.seated === s.total) return { win: true, drama: s.drama };
    const ms = moves(s); if (!ms.length) return { win: false, why: 'stuck' };
    const srcs = [...new Set(ms.map(srcKey))];
    const pick = srcs[Math.floor(R() * srcs.length)];
    const mine = ms.filter(m => srcKey(m) === pick);
    const seats = mine.filter(m => m.bench === undefined); const b = mine.find(m => m.bench !== undefined);
    let m;
    if (seats.length && eps > 0 && R() < eps) m = seats[Math.floor(R() * seats.length)];
    else {
      let bestD = Infinity; for (const x of seats) bestD = Math.min(bestD, x.d);
      if (seats.length && (bestD <= 0 || !b)) { const eq = seats.filter(x => x.d === bestD); m = eq[Math.floor(R() * eq.length)]; }
      else m = b || seats[0];
    }
    s = apply(s, m);
    if (s.drama >= 100) return { win: false, why: 'drama' };
  }
  return { win: false, why: 'loop' };
}

function moveText(m) {
  const from = m.src[0] === 'b' ? 'b' + m.src[1] : m.src[1] + ',' + m.src[2];
  if (m.pair) return from + '>' + m.k + '+' + m.j;
  return from + '>' + (m.bench !== undefined ? 'b' : m.k);
}

module.exports = { init, solve, greedy, careful, noisy, rng, moves, apply, moveText, SC, partner };
