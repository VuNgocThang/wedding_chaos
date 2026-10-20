// Level generator: builds 50 levels along a 10-level difficulty cycle, checks each with the solver and two bots.
'use strict';
const fs = require('fs');
const S = require('./sim.js');

const CYCLE = ['easy', 'easy', 'medium', 'easy', 'easy', 'hard', 'easy', 'easy', 'hard', 'super'];
const NAMES = [
  'Khách đầu tiên', 'Đôi uyên ương', 'Lối đi chật chội', 'Bà nội VIP', 'Bà nội & ông chú', 'Tiệc bắt đầu nóng', 'Người yêu cũ', 'Hội bà tám', 'Bàn tiệc rối ren', 'BOSS · Hai gia tộc',
  'Khách bí ẩn', 'Bạn thân từ bé', 'Sảnh chờ đông nghịt', 'Hai bà sui gia', 'Tam giác tình', 'Nhạc nổi lên', 'Bánh cưới năm tầng', 'Ai ngồi cạnh ai?', 'Mưa ngoài sảnh', 'BOSS · Đêm tiệc hỗn loạn',
  'Em bé đi lạc', 'Cả nhà cùng vui', 'Bàn trẻ con', 'Thầm thương trộm nhớ', 'Lời tỏ tình', 'Ly champagne vỡ', 'Điệu nhảy đầu tiên', 'Họ hàng đông đủ', 'Bố cô dâu phát biểu', 'BOSS · Bí mật bị lộ',
  'Tiệc sân vườn', 'Bàn khách VIP', 'Hội ngộ bạn cũ', 'Mẹ chồng tới', 'Lấp ló sau cột', 'Tung hoa cưới', 'Ông chú hát karaoke', 'Bữa tối dưới nến', 'Chuyện tình tay ba', 'BOSS · Hai họ đối đầu',
  'Tiệc cưới bãi biển', 'Pháo hoa', 'Khách không mời', 'Ghế ngôi sao', 'Phù dâu & phù rể', 'Mưa lời đồn', 'Bánh kem bay', 'Đêm không ngủ', 'Hỗn loạn tột độ', 'BOSS · Đám cưới thế kỷ'];

// level at which each mechanic unlocks (and gets featured)
const UNLOCK = { couple: 2, grandma: 4, uncle: 5, ex: 7, gossip: 8, hidden: 11, bff: 12, rival: 14, multi: 15, gift: 17, kid: 21, reserve: 22, crush: 24, hands: 25, ribbon: 31 };
const BLOCKERS = ['hidden', 'multi', 'gift', 'reserve', 'hands', 'ribbon'];
// level that teaches the mechanic: it must appear at least `n` times
const FEATURE = { 2: { couple: 1 }, 4: { grandma: 1 }, 5: { grandma: 1, uncle: 1 }, 7: { ex: 1 }, 8: { gossip: 1 }, 12: { bff: 2 }, 14: { rival: 1 }, 15: { triangle: 1 }, 17: { gift: 1 }, 21: { kid: 1 }, 22: { reserve: 1 }, 24: { crush: 2 }, 25: { hands: 1 }, 31: { ribbon: 1 } };

function spec(i) {
  const c = Math.floor((i - 1) / 10), tier = CYCLE[(i - 1) % 10];
  const T = { easy: 0, medium: 1, hard: 2, super: 3 }[tier];
  let N = [12 + 4 * c, 16 + 4 * c, 20 + 4 * c, 24 + 4 * c][T];
  if (i === 1) N = 8; if (i === 2) N = 12;
  N = Math.min(N, [28, 28, 32, 36][T]);
  let colors = c === 0 ? (T === 3 ? 3 : 2) : c === 1 ? 3 : c === 2 ? (T >= 2 ? 4 : 3) : 4;
  colors = Math.min(colors, N / 4);
  const pillars = Math.min(5, [0, 1, 2, 3][T] + c - (i <= 3 ? 1 : 0));
  const empties = [7, 4, 3, 1][T];
  const bench = [c >= 3 ? 3 : 4, c >= 2 ? 2 : 3, c >= 2 ? 1 : 2, 1][T];
  const drama = [[0, 0, 5, 10, 10], [30, 35, 35, 40, 40], [45, 50, 50, 55, 55], [60, 60, 65, 65, 70]][T][c];
  const hidden = i < UNLOCK.hidden ? 0 : [1, 2, 3, 4][T] + Math.floor(c / 2);
  const budget = i === 1 ? 0 : [c === 0 ? 1 : 1 + c, 6 + c, 7 + 2 * c, 10 + 2 * c][T];
  const lo = [0.85 - 0.035 * c, 0.58 - 0.03 * c, 0.32 - 0.03 * c, 0.06 - 0.01 * c][T];
  const hi = [1.01, 0.78 - 0.03 * c, 0.5 - 0.03 * c, 0.2 - 0.02 * c][T];
  return { i, c, tier, T, N, colors, pillars: Math.max(0, pillars), empties, bench, drama, hidden, budget, lo, hi, name: NAMES[i - 1] };
}

function unlocked(i) { return Object.keys(UNLOCK).filter(k => UNLOCK[k] <= i); }

function shuffle(a, R) { for (let k = a.length - 1; k > 0; k--) { const j = Math.floor(R() * (k + 1)); [a[k], a[j]] = [a[j], a[k]]; } return a; }

function build(sp, R) {
  const cols = 6;
  const palette = ['R', 'B', 'G', 'Y'].slice(0, sp.colors);
  // tables per colour (each 4 guests): spread N/4 tables over the colours
  const tablesN = sp.N / 4; const per = palette.map(() => 1);
  for (let k = palette.length; k < tablesN; k++) per[Math.floor(R() * palette.length)]++;
  const guests = [];
  palette.forEach((col, ci) => { for (let k = 0; k < per[ci] * 4; k++) guests.push({ c: col, t: 'normal', hidden: false, reserved: -1, links: [] }); });
  const linkLines = [];
  const has = k => unlocked(sp.i).includes(k);
  const pick = (pred) => { const pool = guests.filter(pred); return pool.length ? pool[Math.floor(R() * pool.length)] : null; };
  const linkedTo = (a, b) => a.links.some(l => l.to === b);
  function connect(a, b, kind) {
    if (!a || !b || a === b || linkedTo(a, b)) return false;
    const rev = { parent: 'child', crush: 'crushedby' }[kind] || kind;
    a.links.push({ kind, to: b }); b.links.push({ kind: rev, to: a });
    linkLines.push({ kind, a, b });
    return true;
  }
  const multiP = has('multi') ? 0.35 + 0.06 * sp.c : 0;
  function endpoint(color, avoidKid = true, preferLinked = true) {
    if (preferLinked && R() < multiP) {
      const g = pick(x => x.c === color && x.links.length > 0 && x.links.length < 3 && x.t !== 'kid');
      if (g) return g;
    }
    return pick(x => x.c === color && x.links.length === 0 && (!avoidKid || x.t !== 'kid') && x.t !== 'grandma');
  }
  const sameColor = p => R() < p ? null : palette[Math.floor(R() * palette.length)];
  function addKind(kind) {
    const col = palette[Math.floor(R() * palette.length)];
    switch (kind) {
      case 'couple': { const a = endpoint(col); const b = endpoint(col, true, false); return connect(a, b, 'couple'); }
      case 'ex': { const a = endpoint(col); const b = endpoint(R() < 0.75 ? col : palette[Math.floor(R() * palette.length)]); return connect(a, b, 'ex'); }
      case 'rival': { const a = endpoint(col); const b = endpoint(R() < 0.8 ? col : palette[Math.floor(R() * palette.length)]); return connect(a, b, 'rival'); }
      case 'bff': { const a = endpoint(col); const b = endpoint(col); return connect(a, b, 'bff'); }
      case 'crush': { const a = endpoint(col); const b = endpoint(col); return connect(a, b, 'crush'); }
      case 'kid': {
        const k = pick(x => x.c === col && x.t === 'normal' && x.links.length === 0); if (!k) return false;
        const p = pick(x => x.c === col && x !== k && x.t !== 'kid' && x.links.length < 2); if (!p) return false;
        k.t = 'kid'; return connect(p, k, 'parent');
      }
      case 'grandma': { const g = pick(x => x.c === col && x.t === 'normal' && x.links.length === 0); if (!g) return false; g.t = 'grandma'; return true; }
      case 'uncle': {
        const gm = guests.find(x => x.t === 'grandma'); const cc = gm && R() < 0.75 ? gm.c : col;
        const g = pick(x => x.c === cc && x.t === 'normal'); if (!g) return false; g.t = 'uncle'; return true;
      }
      case 'gossip': {
        const a = pick(x => x.c === col && x.t === 'normal'), b = pick(x => x.c === col && x.t === 'normal' && x !== a);
        if (!a || !b) return false; a.t = b.t = 'gossip'; return true;
      }
      case 'triangle': {
        // a loves b, but a is also the ex (or rival) of c — all on one colour
        const a = endpoint(col, true, false), b = pick(x => x.c === col && x !== a && x.links.length === 0 && x.t !== 'kid'), cc = pick(x => x.c === col && x !== a && x !== b && x.links.length === 0 && x.t !== 'kid');
        if (!a || !b || !cc) return false;
        connect(a, b, 'couple'); connect(a, cc, R() < 0.6 ? 'ex' : 'rival'); return true;
      }
    }
    return false;
  }
  // featured mechanics first
  const feat = FEATURE[sp.i] || {};
  let budget = sp.budget;
  for (const [k, n] of Object.entries(feat)) if (!BLOCKERS.includes(k)) for (let j = 0; j < n; j++) { if (addKind(k)) budget--; }
  // fill the rest of the budget from unlocked mechanics, recent unlocks weighted up
  const pool = unlocked(sp.i).filter(k => !BLOCKERS.includes(k));
  if (has('multi')) pool.push('triangle');
  let guard = 0;
  while (budget > 0 && guard++ < 60) {
    // harder tiers lean on the relationships that hurt; easy tiers on the ones that help
    const harsh = { ex: 1, rival: 1, kid: 1, couple: 0.6, uncle: 0.6, grandma: 0.6, triangle: 1, bff: -1, crush: -1, gossip: -1 };
    const w = pool.map(k => Math.max(0.15, (k === 'triangle' ? 0.6 : 1) * (sp.i - (UNLOCK[k] || 15) < 6 ? 2 : 1) * (1 + 0.45 * (sp.T - 1) * (harsh[k] || 0))));
    let r = R() * w.reduce((a, b) => a + b, 0), k = pool[0];
    for (let j = 0; j < pool.length; j++) { r -= w[j]; if (r <= 0) { k = pool[j]; break; } }
    if (k === 'grandma' && guests.filter(x => x.t === 'grandma').length >= 1 + Math.floor(sp.c / 2)) continue;
    if (k === 'uncle' && guests.filter(x => x.t === 'uncle').length >= 1 + Math.floor(sp.c / 2)) continue;
    if (addKind(k)) budget--;
  }
  // blockers
  // each level mixes a rotating subset of the unlocked blockers (the one being taught is always in)
  const avail = ['gift', 'reserve', 'hands', 'ribbon'].filter(has);
  const want = Math.min(avail.length, [1, 2, 2, 3][sp.T] + (sp.T === 3 && sp.c >= 3 ? 1 : 0));
  const chosen = new Set(Object.keys(feat).filter(k => avail.includes(k)));
  for (let j = 0; chosen.size < want && j < avail.length; j++) chosen.add(avail[(sp.i * 3 + j) % avail.length]);
  const nb = (k, arr) => !chosen.has(k) ? 0 : Math.max((feat[k] || 0), arr[sp.T] + (sp.c >= 3 && sp.T >= 2 ? 1 : 0));
  const nGifts = nb('gift', [1, 1, 2, 2]), nRes = nb('reserve', [1, 2, 2, 3]), nHands = nb('hands', [1, 1, 2, 2]), nRibbons = nb('ribbon', [1, 1, 2, 2]);
  // layout
  const cells0 = sp.N + sp.pillars + sp.empties + nGifts;
  const rows = Math.max(2, Math.ceil(cells0 / cols));
  const total = rows * cols;
  const cells = Array(total).fill('.');
  let p = 0, g2 = 0;
  while (p < sp.pillars && g2++ < 200) { const k = cols + Math.floor(R() * (total - cols)); if (cells[k] === '.') { cells[k] = '#'; p++; } }
  // gift stacks sit in rows 1-2 where they block somebody
  const giftCells = [];
  g2 = 0;
  while (giftCells.length < nGifts && g2++ < 200) {
    const k = cols + Math.floor(R() * Math.min(total - cols, 2 * cols));
    if (cells[k] === '.') { cells[k] = 'GIFT'; giftCells.push(k); }
  }
  // empties sit lower (the crowd packs toward the entrance), so fill guest cells top-first with some randomness
  const free = cells.map((x, k) => x === '.' ? k : -1).filter(k => k >= 0);
  free.sort((a, b) => (Math.floor(a / cols) + R() * 2.2) - (Math.floor(b / cols) + R() * 2.2));
  shuffle(guests, R);
  guests.forEach((g, k) => cells[free[k]] = g);
  // hidden guests: not in the first row
  const hid = guests.filter((g, k) => free[k] >= cols); shuffle(hid, R);
  hid.slice(0, sp.hidden).forEach(g => g.hidden = true);
  const at = (r, c) => r >= 0 && c >= 0 && r < rows && c < cols ? cells[r * cols + c] : null;
  const isGuest = x => x && typeof x === 'object' && !x.gift;
  // gift colour = the colour most common among its neighbours; count <= those neighbours so it can open
  for (const k of giftCells) {
    const r = Math.floor(k / cols), c = k % cols;
    const nbrs = [at(r - 1, c), at(r, c - 1), at(r, c + 1), at(r + 1, c)].filter(isGuest);
    const byCol = {}; nbrs.forEach(g => byCol[g.c] = (byCol[g.c] || 0) + 1);
    const best = Object.entries(byCol).sort((a, b) => b[1] - a[1])[0];
    if (!best) { cells[k] = '.'; continue; }
    cells[k] = { gift: true, c: best[0], n: Math.min(best[1], sp.T >= 2 ? 3 : 2) };
  }
  // hand-holding pairs: two neighbours of one colour, both plain to read
  const okHand = g => isGuest(g) && !g.hidden && g.t !== 'kid' && g.t !== 'grandma' && !g.links.some(l => l.kind === 'hands');
  for (let h = 0, tries = 0; h < nHands && tries < 120; tries++) {
    const k = Math.floor(R() * total), r = Math.floor(k / cols), c = k % cols;
    const a = at(r, c); const b = R() < 0.5 ? at(r, c + 1) : at(r + 1, c);
    if (!okHand(a) || !okHand(b) || a.c !== b.c) continue;
    if (connect(a, b, 'hands')) h++;
  }
  // reserved seats: distinct seat per colour, never on hand-holders or grandmas
  const taken = new Set();
  for (let h = 0, tries = 0; h < nRes && tries < 80; tries++) {
    const g = guests[Math.floor(R() * guests.length)];
    if (g.reserved >= 0 || g.t === 'grandma' || g.links.some(l => l.kind === 'hands')) continue;
    const seat = Math.floor(R() * 4);
    if (taken.has(g.c + seat)) continue;
    taken.add(g.c + seat); g.reserved = seat; h++;
  }
  // drop trailing rows that hold nobody
  let usedRows = rows;
  while (usedRows > 1 && cells.slice((usedRows - 1) * cols, usedRows * cols).every(x => x === '.')) usedRows--;
  cells.length = usedRows * cols;
  // ribbons across vertical edges in the upper rows, coloured by a table that must finish first
  const ribbons = [];
  for (let h = 0, tries = 0; h < nRibbons && tries < 80; tries++) {
    const r = Math.floor(R() * Math.min(usedRows - 1, 3)), c = Math.floor(R() * cols);
    if (r + 1 >= usedRows) continue;
    const a = cells[r * cols + c], b = cells[(r + 1) * cols + c];
    if (a === '#' || b === '#' || (a && a.gift) || (b && b.gift)) continue;
    if (ribbons.some(x => x.c1 === c && x.r1 === r)) continue;
    ribbons.push({ col: palette[Math.floor(R() * palette.length)], r1: r, c1: c, r2: r + 1, c2: c });
    h++;
  }
  return { rows: usedRows, cols, cells, bench: sp.bench, drama: sp.drama, linkLines, guests, ribbons };
}

function evaluate(level, R, runs = 100) {
  const s0 = S.init(level);
  const sol = S.solve(s0, 60000);
  let best = sol.best;
  // random careful playouts often find cleaner finishes than the node-limited DFS
  for (let k = 0; k < 150; k++) {
    const r = S.careful(s0, R, true);
    if (r.win && (!best || r.drama < best.drama)) best = { drama: r.drama, path: r.path };
  }
  if (!best) return null;
  let gw = 0, cw = 0, nw = 0;
  for (let k = 0; k < runs; k++) { if (S.greedy(s0, R).win) gw++; if (S.careful(s0, R).win) cw++; if (S.noisy(s0, R, 0.3).win) nw++; if (S.noisy(s0, R, 0.3).win) nw++; }
  return { greedy: gw / runs, careful: cw / runs, noisy: nw / (2 * runs), score: nw / (2 * runs), best: best.drama, nodes: sol.nodes, path: best.path };
}

function labelOf(n) { let s = ''; n++; while (n > 0) { n--; s = String.fromCharCode(97 + n % 26) + s; n = Math.floor(n / 26); } return s; }

function toText(sp, level, ev) {
  const labels = new Map(); let n = 0;
  for (const l of level.linkLines) for (const g of [l.a, l.b]) if (!labels.has(g)) labels.set(g, labelOf(n++));
  for (const g of level.guests) if (g.reserved >= 0 && !labels.has(g)) labels.set(g, labelOf(n++));
  // labels must follow grid order for readability
  const ordered = level.cells.filter(x => x !== '.' && x !== '#' && !x.gift && labels.has(x));
  ordered.forEach((g, k) => labels.set(g, labelOf(k)));
  const tok = x => {
    if (x === '.' || x === '#') return x;
    if (x.gift) return '$' + x.c + x.n;
    const tr = { grandma: 'g', uncle: 'u', gossip: 's', kid: 'k', normal: '' }[x.t];
    return (x.hidden ? '?' : '') + x.c + tr + (labels.has(x) ? '@' + labels.get(x) : '');
  };
  const rows = [];
  for (let r = 0; r < level.rows; r++) rows.push(level.cells.slice(r * level.cols, r * level.cols + level.cols).map(tok).join(' '));
  const lines = [
    `// gen: tier ${sp.tier}, casual win ${(ev.noisy * 100).toFixed(0)}%, greedy win ${(ev.greedy * 100).toFixed(0)}%, careful win ${(ev.careful * 100).toFixed(0)}%, solver best drama ${ev.best}`,
    `name: ${sp.name}`, `tier: ${sp.tier}`, `bench: ${level.bench}`, `drama: ${level.drama}`, 'grid:', ...rows];
  const resv = level.cells.filter(x => x && typeof x === 'object' && !x.gift && x.reserved >= 0);
  if (level.linkLines.length || resv.length || level.ribbons.length) {
    lines.push('links:');
    for (const l of level.linkLines) lines.push(`${l.kind} ${labels.get(l.a)} ${labels.get(l.b)}`);
    for (const g of resv) lines.push(`reserve ${labels.get(g)} ${g.reserved}`);
    for (const b of level.ribbons) lines.push(`ribbon ${b.col} ${b.r1},${b.c1} ${b.r2},${b.c2}`);
  }
  lines.push('solution: ' + ev.path.map(S.moveText).join(' '));
  return lines.join('\n') + '\n';
}

function generate(i, maxTries, timeMs) {
  const sp = spec(i);
  const t0 = Date.now();
  let best = null;
  for (let seed = 1; seed <= maxTries && Date.now() - t0 < timeMs; seed++) {
    const R = S.rng(i * 100003 + seed * 7919);
    const lvl = build(sp, R);
    // feature check
    const feat = FEATURE[i] || {};
    const cnt = k => k === 'gift' ? lvl.cells.filter(x => x && x.gift).length : k === 'reserve' ? lvl.guests.filter(g => g.reserved >= 0).length : k === 'ribbon' ? lvl.ribbons.length : k === 'triangle' ? lvl.guests.filter(g => g.links.length >= 2).length : ['grandma', 'uncle', 'gossip', 'kid'].includes(k) ? lvl.guests.filter(g => g.t === k).length : lvl.linkLines.filter(l => l.kind === k).length;
    if (Object.entries(feat).some(([k, n]) => cnt(k) < (k === 'gossip' ? 2 : n))) continue;
    const ev = evaluate(lvl, S.rng(seed * 31 + i), 60);
    if (!ev) continue;
    if (sp.T === 0 && ev.best > sp.drama) continue;          // easy levels must allow a clean finish
    const mid = (sp.lo + Math.min(1, sp.hi)) / 2;
    const dist = ev.score < sp.lo ? sp.lo - ev.score : ev.score > sp.hi ? ev.score - sp.hi : 0;
    const cand = { lvl, ev, dist, tieb: Math.abs(ev.score - mid) };
    if (!best || cand.dist < best.dist || (cand.dist === best.dist && cand.tieb < best.tieb)) best = cand;
    if (best.dist === 0 && seed > 6) break;
  }
  return { sp, best };
}

if (require.main === module) {
  const ids = process.argv.slice(2).map(Number);
  const outDir = require('path').join(__dirname, 'out');
  fs.mkdirSync(outDir, { recursive: true });
  for (const i of ids) {
    const sp = spec(i);
    const tries = Number(process.env.TRIES) || [40, 80, 160, 260][sp.T], time = Number(process.env.TIME) || [30000, 60000, 110000, 170000][sp.T];
    const { best } = generate(i, tries, time);
    if (!best) { console.log(`L${i} FAILED`); continue; }
    fs.writeFileSync(`${outDir}/level_${String(i).padStart(2, '0')}.txt`, toText(sp, best.lvl, best.ev));
    console.log(`L${i} ${sp.tier} N${sp.N} c${sp.colors} bench${sp.bench} drama${sp.drama} links${best.lvl.linkLines.length} -> noisy ${best.ev.noisy.toFixed(2)} greedy ${best.ev.greedy.toFixed(2)} careful ${best.ev.careful.toFixed(2)} score ${best.ev.score.toFixed(2)} band [${sp.lo.toFixed(2)},${sp.hi.toFixed(2)}] dist ${best.dist.toFixed(2)} best ${best.ev.best}`);
  }
}

module.exports = { spec, build, evaluate, toText };
