using System;
using System.Collections.Generic;

namespace WeddingChaos.Core
{
    /// <summary>
    /// Pure, deterministic game rules for Wedding Chaos. No Unity references:
    /// the view layer reads state, calls moves, and animates the returned results.
    /// Timing concerns (table lock after a fight, walk animations) live in the view.
    /// </summary>
    public sealed class SeatingSim
    {
        public const int CouplePenalty = 15, CoupleBonus = -10, ExFight = 30, ExGlare = 10,
                         GrandmaNotStar = 20, GrandmaScold = 20, GossipBonus = -15, PerfectTableBonus = -5,
                         RivalClash = 20, BffBonus = -8, FamilyBonus = -5, KidCries = 15, CrushBonus = -12, GiftBonus = -5,
                         VipSeatTaken = 15, VipImpatient = 15;

        struct Placement { public char Color; public int Seat; public int Gen; }

        public int Rows { get; private set; }
        public int Cols { get; private set; }
        public string LevelName { get; private set; }
        public string Tier { get; private set; }
        /// <summary>Every guest of the level by id (stable for the whole level).</summary>
        public Dictionary<int, Guest> Guests = new Dictionary<int, Guest>();
        public Guest[,] Grid;          // null = empty or pillar
        public bool[,] Pillar;
        public Guest[] Bench;
        public List<Gift> Gifts = new List<Gift>();
        public List<Ribbon> Ribbons = new List<Ribbon>();
        int[,] giftIndex;
        List<BlockerEvent> events = new List<BlockerEvent>();
        public Dictionary<char, Table> Tables = new Dictionary<char, Table>();
        public List<char> Colors = new List<char>();
        public int Drama { get; private set; }
        public int Seated { get; private set; }
        public int Total { get; private set; }
        public GameStatus Status { get; private set; } = GameStatus.Playing;
        /// <summary>Moves the player has made (each seat, pair or bench move counts one). Drives VIP patience.</summary>
        public int Turn { get; private set; }
        HashSet<int> angryVips = new HashSet<int>();

        Dictionary<int, Placement> placed = new Dictionary<int, Placement>();
        Dictionary<int, Guest> placedGuests = new Dictionary<int, Guest>();

        SeatingSim() { }

        public static SeatingSim FromLevel(LevelDef def)
        {
            var s = new SeatingSim { Rows = def.Rows, Cols = def.Cols, LevelName = def.Name, Tier = def.Tier };
            s.Grid = new Guest[s.Rows, s.Cols];
            s.Pillar = new bool[s.Rows, s.Cols];
            s.giftIndex = new int[s.Rows, s.Cols];
            s.Bench = new Guest[def.Bench];
            s.Drama = Math.Max(0, Math.Min(100, def.StartDrama));
            int id = 0;
            var labels = new Dictionary<string, Guest>();
            var legacy = new Dictionary<string, List<Guest>>();
            for (int r = 0; r < s.Rows; r++)
                for (int c = 0; c < s.Cols; c++)
                {
                    s.giftIndex[r, c] = -1;
                    if (LevelDef.TryParseGift(def.Grid[r][c], out char gc, out int gn))
                    {
                        s.giftIndex[r, c] = s.Gifts.Count;
                        s.Gifts.Add(new Gift { Row = r, Col = c, Color = gc, Count = gn });
                    }
                    var g = LevelDef.ParseToken(def.Grid[r][c], ++id, out bool pillar, out string pair);
                    s.Pillar[r, c] = pillar;
                    s.Grid[r, c] = g;
                    if (g != null)
                    {
                        s.Total++;
                        s.Guests[g.Id] = g;
                        if (!s.Colors.Contains(g.Color)) s.Colors.Add(g.Color);
                        if (g.Label != null)
                        {
                            if (labels.ContainsKey(g.Label)) throw new FormatException("Duplicate label @" + g.Label);
                            labels[g.Label] = g;
                        }
                        if (pair != "") { if (!legacy.TryGetValue(pair, out var l)) legacy[pair] = l = new List<Guest>(); l.Add(g); }
                    }
                }
            char tag = 'A';
            foreach (var kv in legacy)
            {
                var kind = kv.Key[0] == 'x' ? LinkKind.Ex : LinkKind.Couple;
                var l = kv.Value;
                for (int i = 0; i < l.Count; i++) for (int j = i + 1; j < l.Count; j++) LevelDef.Connect(l[i], l[j], kind, tag);
                tag++;
            }
            foreach (var (kind, a, b) in def.Links)
            {
                if (!labels.TryGetValue(a, out var ga) || !labels.TryGetValue(b, out var gb)) throw new FormatException($"Unknown label in link '{kind} {a} {b}'");
                var k = LevelDef.ParseKind(kind);
                if (k == LinkKind.ParentOf && gb.Type != GuestType.Kid) throw new FormatException($"'parent {a} {b}': @{b} must be a kid (suffix k)");
                LevelDef.Connect(ga, gb, k, tag++);
            }
            foreach (var (label, seat, patience) in def.Reserves)
            {
                if (!labels.TryGetValue(label, out var gr)) throw new FormatException("Unknown label in reserve '" + label + "'");
                if (seat < 0 || seat >= Table.SeatCount) throw new FormatException("Reserved seat out of range for @" + label);
                gr.Reserved = seat;
                gr.Patience = patience;
            }
            foreach (var (color, r1, c1, r2, c2) in def.Ribbons)
            {
                if (Math.Abs(r1 - r2) + Math.Abs(c1 - c2) != 1) throw new FormatException($"Ribbon {r1},{c1}-{r2},{c2} must join two neighbouring cells");
                s.Ribbons.Add(new Ribbon { Color = color, R1 = r1, C1 = c1, R2 = r2, C2 = c2 });
            }
            s.Colors.Sort((a, b) => "RBGY".IndexOf(a).CompareTo("RBGY".IndexOf(b)));
            foreach (var col in s.Colors) s.Tables[col] = new Table { Color = col, Generation = 1 };
            s.Validate();
            s.RevealReachable();
            return s;
        }

        void Validate()
        {
            var count = new Dictionary<char, int>();
            foreach (var g in AllUnseated()) count[g.Color] = (count.TryGetValue(g.Color, out var n) ? n : 0) + 1;
            foreach (var kv in count)
                if (kv.Value % Table.SeatCount != 0)
                    throw new InvalidOperationException($"Color {kv.Key} has {kv.Value} guests; must be a multiple of {Table.SeatCount}");
            var reserved = new HashSet<string>();
            foreach (var g in AllUnseated())
            {
                if (g.Reserved >= 0 && !reserved.Add(g.Color + ":" + g.Reserved)) throw new InvalidOperationException($"Two guests reserve seat {g.Reserved} of table {g.Color}");
                var p = HandsPartner(g);
                if (p == null) continue;
                if (p.Color != g.Color) throw new InvalidOperationException("Hand-holding guests must share an invitation colour");
                if (!Locate(g, out int r1, out int c1) || !Locate(p, out int r2, out int c2) || Math.Abs(r1 - r2) + Math.Abs(c1 - c2) != 1)
                    throw new InvalidOperationException("Hand-holding guests must stand side by side in the crowd");
                if (g.Reserved >= 0 || p.Reserved >= 0) throw new InvalidOperationException("Hand-holding guests cannot hold reservations");
            }
        }

        public SeatingSim Clone()
        {
            var s = (SeatingSim)MemberwiseClone();
            s.Grid = (Guest[,])Grid.Clone();
            s.Bench = (Guest[])Bench.Clone();
            s.Tables = new Dictionary<char, Table>();
            foreach (var kv in Tables) s.Tables[kv.Key] = kv.Value.Clone();
            s.Colors = new List<char>(Colors);
            s.placed = new Dictionary<int, Placement>(placed);
            s.placedGuests = new Dictionary<int, Guest>(placedGuests);
            s.Gifts = Gifts.ConvertAll(x => x.Clone());
            s.Ribbons = Ribbons.ConvertAll(x => x.Clone());
            s.giftIndex = (int[,])giftIndex.Clone();
            s.events = new List<BlockerEvent>();
            s.angryVips = new HashSet<int>(angryVips);
            return s;
        }

        // ---------- queries ----------

        public bool IsEmptyCell(int r, int c) => Grid[r, c] == null && !Pillar[r, c] && giftIndex[r, c] < 0;

        /// <summary>Index of the unopened gift in a cell, or -1.</summary>
        public int GiftAt(int r, int c) => giftIndex[r, c];

        bool EdgeBlocked(int ya, int xa, int yb, int xb)
        {
            foreach (var rb in Ribbons) if (!rb.Cut && rb.Between(ya, xa, yb, xb)) return true;
            return false;
        }

        public bool Locate(Guest g, out int row, out int col)
        {
            for (int r = 0; r < Rows; r++) for (int c = 0; c < Cols; c++) if (Grid[r, c] == g) { row = r; col = c; return true; }
            row = col = -1; return false;
        }

        public Guest HandsPartner(Guest g)
        {
            foreach (var l in g.Links) if (l.Kind == LinkKind.Hands) return Guests[l.Other];
            return null;
        }

        /// <summary>Blocker changes since the last call (gift counters, gifts opened, ribbons cut).</summary>
        public List<BlockerEvent> TakeEvents() { var e = events; events = new List<BlockerEvent>(); return e; }

        /// <summary>A crowd guest can leave if an empty-cell path reaches row 0 (the entrance).</summary>
        public bool HasPath(int r, int c) => FindPath(r, c) != null;

        /// <summary>Shortest path from the guest's cell to the entrance row, start included; null if blocked.</summary>
        public List<Cell> FindPath(int r, int c)
        {
            if (r == 0) return new List<Cell> { new Cell(r, c) };
            var prev = new Dictionary<int, int>();
            var q = new Queue<int>();
            int start = r * Cols + c;
            prev[start] = -1;
            q.Enqueue(start);
            int[] dr = { -1, 0, 0, 1 }, dc = { 0, -1, 1, 0 };
            while (q.Count > 0)
            {
                int cur = q.Dequeue(), y = cur / Cols, x = cur % Cols;
                for (int d = 0; d < 4; d++)
                {
                    int ny = y + dr[d], nx = x + dc[d];
                    if (ny < 0 || nx < 0 || ny >= Rows || nx >= Cols) continue;
                    int k = ny * Cols + nx;
                    if (prev.ContainsKey(k) || !IsEmptyCell(ny, nx) || EdgeBlocked(y, x, ny, nx)) continue;
                    prev[k] = cur;
                    if (ny == 0)
                    {
                        var path = new List<Cell>();
                        for (int p = k; p != -1; p = prev[p]) path.Add(new Cell(p / Cols, p % Cols));
                        path.Reverse();
                        return path;
                    }
                    q.Enqueue(k);
                }
            }
            return null;
        }

        public int FreeBenchSlot() { for (int i = 0; i < Bench.Length; i++) if (Bench[i] == null) return i; return -1; }

        public bool TableOpen(char color) => Tables.TryGetValue(color, out var t) && !t.Done;

        public IEnumerable<int> FreeSeats(char color)
        {
            if (!TableOpen(color)) yield break;
            var t = Tables[color];
            for (int k = 0; k < Table.SeatCount; k++) if (t.Seats[k] == null) yield return k;
        }

        /// <summary>
        /// Seats guest g may take now. A VIP sits on its name-card seat while it is free; once someone else took it,
        /// the VIP settles for any free seat. Everybody else may sit anywhere, but taking a VIP's seat costs drama.
        /// </summary>
        public IEnumerable<int> SeatsFor(Guest g)
        {
            if (!TableOpen(g.Color)) yield break;
            var t = Tables[g.Color];
            if (g.Reserved >= 0 && t.Seats[g.Reserved] == null) { yield return g.Reserved; yield break; }
            for (int k = 0; k < Table.SeatCount; k++) if (t.Seats[k] == null) yield return k;
        }

        /// <summary>The still-waiting VIP whose name card is on seat k of g's colour table (null if none).</summary>
        public Guest VipOfSeat(Guest g, int seat)
        {
            foreach (var o in AllUnseated()) if (o != g && o.Color == g.Color && o.Reserved == seat) return o;
            return null;
        }

        /// <summary>Turns left before a waiting VIP gets angry (-1 = no limit or already angry).</summary>
        public int PatienceLeft(Guest g)
        {
            if (g.Patience < 0 || angryVips.Contains(g.Id)) return -1;
            return Math.Max(0, g.Patience - Turn);
        }

        public bool IsAngryVip(Guest g) => angryVips.Contains(g.Id);

        /// <summary>One player move is over: advance the turn and let VIPs whose patience ran out get angry (+15% once).</summary>
        void EndTurn()
        {
            Turn++;
            foreach (var g in AllUnseated())
            {
                if (g.Patience < 0 || Turn < g.Patience || angryVips.Contains(g.Id)) continue;
                angryVips.Add(g.Id);
                Drama = Math.Max(0, Math.Min(100, Drama + VipImpatient));
                events.Add(new BlockerEvent { Kind = "vip_angry", Index = g.Id });
            }
            UpdateStatus();
        }

        /// <summary>A hand-holding pair leaves together once either of them has a free lane (the other follows).</summary>
        public bool PairCanLeave(int r, int c)
        {
            var g = Grid[r, c];
            var p = g == null ? null : HandsPartner(g);
            if (p == null || !Locate(p, out int pr, out int pc)) return false;
            return HasPath(r, c) || HasPath(pr, pc);
        }

        /// <summary>Seat pairs (k for g, j for its partner) side by side at their table.</summary>
        public List<(int k, int j)> PairSeats(Guest g)
        {
            var list = new List<(int, int)>();
            var p = HandsPartner(g);
            if (p == null || !TableOpen(g.Color)) return list;
            var mine = new List<int>(SeatsFor(g)); var theirs = new List<int>(SeatsFor(p));
            foreach (var k in mine) foreach (var j in theirs) if (k != j && Table.Adjacent(k, j)) list.Add((k, j));
            return list;
        }

        public IEnumerable<Guest> AllUnseated()
        {
            for (int r = 0; r < Rows; r++) for (int c = 0; c < Cols; c++) if (Grid[r, c] != null) yield return Grid[r, c];
            foreach (var g in Bench) if (g != null) yield return g;
        }

        public int RemainingOf(char color) { int n = 0; foreach (var g in AllUnseated()) if (g.Color == color) n++; return n; }

        /// <summary>The drama effects seating guest g at seat k of its color's table would cause (preview chips).
        /// Trait rules look at the neighbours; every relationship edge is scored once, when its second guest sits.</summary>
        public List<Effect> PreviewEffects(Guest g, int k)
        {
            var list = new List<Effect>();
            var t = Tables[g.Color];
            var s = t.Seats;
            Guest left = s[Table.Left(k)], right = s[Table.Right(k)];
            Guest Near(Func<Guest, bool> p) => left != null && p(left) ? left : right != null && p(right) ? right : null;

            // somebody else's name card on this chair
            var vip = VipOfSeat(g, k);
            if (vip != null) list.Add(new Effect(VipSeatTaken, EffectKeys.VipSeatTaken, otherId: vip.Id));

            // traits
            if (g.Type == GuestType.Grandma && k != Table.StarSeat)
                list.Add(new Effect(GrandmaNotStar, EffectKeys.GrandmaNotStar));
            var scold = g.Type == GuestType.Grandma ? Near(x => x.Type == GuestType.Uncle) : g.Type == GuestType.Uncle ? Near(x => x.Type == GuestType.Grandma) : null;
            if (scold != null) list.Add(new Effect(GrandmaScold, EffectKeys.GrandmaScoldsUncle, otherId: scold.Id));
            var gossip = g.Type == GuestType.Gossip ? Near(x => x.Type == GuestType.Gossip) : null;
            if (gossip != null) list.Add(new Effect(GossipBonus, EffectKeys.GossipPair, otherId: gossip.Id));

            // relationships
            foreach (var l in g.Links)
            {
                if (!placed.TryGetValue(l.Other, out var p)) continue;          // scored when the other one sits
                bool same = p.Color == g.Color && p.Gen == t.Generation;
                bool adj = same && Table.Adjacent(p.Seat, k);
                bool opp = same && Table.Opposite(k) == p.Seat;
                switch (l.Kind)
                {
                    case LinkKind.Couple:
                        list.Add(adj ? new Effect(CoupleBonus, EffectKeys.CoupleTogether, otherId: l.Other) : new Effect(CouplePenalty, EffectKeys.CoupleSplit, otherId: l.Other));
                        break;
                    case LinkKind.Ex:
                        if (adj) list.Add(new Effect(ExFight, EffectKeys.ExFight, locksTable: true, otherId: l.Other));
                        else if (opp) list.Add(new Effect(ExGlare, EffectKeys.ExGlare, otherId: l.Other));
                        break;
                    case LinkKind.Rival:
                        if (same) list.Add(new Effect(RivalClash, EffectKeys.RivalClash, otherId: l.Other));
                        break;
                    case LinkKind.Bff:
                        if (same) list.Add(new Effect(BffBonus, EffectKeys.BffTogether, otherId: l.Other));
                        break;
                    case LinkKind.ParentOf:
                    case LinkKind.ChildOf:
                        list.Add(adj ? new Effect(FamilyBonus, EffectKeys.FamilyTogether, otherId: l.Other) : new Effect(KidCries, EffectKeys.KidCries, otherId: l.Other));
                        break;
                    case LinkKind.CrushOn:
                    case LinkKind.CrushedBy:
                        if (adj) list.Add(new Effect(CrushBonus, EffectKeys.CrushHappy, otherId: l.Other));
                        break;
                }
            }
            return list;
        }

        /// <summary>Where a seated guest is (table color, seat, table generation), if seated.</summary>
        public bool TryGetPlacement(int guestId, out char color, out int seat, out int generation)
        {
            if (placed.TryGetValue(guestId, out var p)) { color = p.Color; seat = p.Seat; generation = p.Gen; return true; }
            color = '\0'; seat = -1; generation = 0; return false;
        }

        // ---------- moves ----------

        public bool CanSeatFromCrowd(int r, int c, int seat)
        {
            var g = Grid[r, c];
            return Status == GameStatus.Playing && g != null && HandsPartner(g) == null && HasPath(r, c) && Contains(SeatsFor(g), seat);
        }

        public bool CanSeatFromBench(int i, int seat)
        {
            var g = Bench[i];
            return Status == GameStatus.Playing && g != null && Contains(SeatsFor(g), seat);
        }

        public bool CanBench(int r, int c) =>
            Status == GameStatus.Playing && Grid[r, c] != null && HandsPartner(Grid[r, c]) == null && HasPath(r, c) && FreeBenchSlot() >= 0;

        public bool CanSeatPair(int r, int c, int k, int j)
        {
            var g = Grid[r, c];
            return Status == GameStatus.Playing && g != null && PairCanLeave(r, c) && PairSeats(g).Contains((k, j));
        }

        static bool Contains(IEnumerable<int> seats, int k) { foreach (var x in seats) if (x == k) return true; return false; }

        public SeatResult SeatFromCrowd(int r, int c, int seat)
        {
            if (!CanSeatFromCrowd(r, c, seat)) throw new InvalidOperationException($"Illegal seat move from {r},{c} to seat {seat}");
            var g = Grid[r, c];
            LeaveCell(r, c, g);
            return Finish(Place(g, seat));
        }

        SeatResult Finish(SeatResult res)
        {
            EndTurn();
            res.DramaAfter = Drama;
            res.StatusAfter = Status;
            return res;
        }

        /// <summary>A hand-holding pair: the guest at r,c takes seat k, the partner seat j (side by side).</summary>
        public (SeatResult first, SeatResult second) SeatPairFromCrowd(int r, int c, int k, int j)
        {
            if (!CanSeatPair(r, c, k, j)) throw new InvalidOperationException($"Illegal pair move from {r},{c} to seats {k}+{j}");
            var g = Grid[r, c]; var p = HandsPartner(g);
            Locate(p, out int pr, out int pc);
            LeaveCell(r, c, g);
            LeaveCell(pr, pc, p);
            var a = Place(g, k);
            var b = Place(p, j);
            Finish(b);
            a.DramaAfter = b.DramaAfter; a.StatusAfter = b.StatusAfter;
            return (a, b);
        }

        public SeatResult SeatFromBench(int i, int seat)
        {
            if (!CanSeatFromBench(i, seat)) throw new InvalidOperationException($"Illegal seat move from bench {i} to seat {seat}");
            var g = Bench[i];
            Bench[i] = null;
            return Finish(Place(g, seat));
        }

        /// <summary>Moves a free crowd guest to the waiting bench. Returns the bench slot.</summary>
        public int MoveToBench(int r, int c)
        {
            if (!CanBench(r, c)) throw new InvalidOperationException($"Cannot bench {r},{c}");
            int slot = FreeBenchSlot();
            var g = Grid[r, c];
            Bench[slot] = g;
            LeaveCell(r, c, g);
            RevealReachable();
            EndTurn();
            return slot;
        }

        /// <summary>Booster: the bouncer moves any crowd guest (even blocked) to the bench. Hand-holding pairs refuse.</summary>
        public bool CanBouncer(int r, int c) => Grid[r, c] != null && HandsPartner(Grid[r, c]) == null && FreeBenchSlot() >= 0;

        public int BouncerToBench(int r, int c)
        {
            int slot = FreeBenchSlot();
            if (!CanBouncer(r, c)) throw new InvalidOperationException("Bouncer needs a single guest and a free bench slot");
            var g = Grid[r, c];
            Bench[slot] = g;
            g.Hidden = false;
            LeaveCell(r, c, g);
            RevealReachable();
            UpdateStatus();
            return slot;
        }

        /// <summary>A guest steps out of a crowd cell: neighbouring gifts of its colour count down; at zero they open (−5% drama).</summary>
        void LeaveCell(int r, int c, Guest g)
        {
            Grid[r, c] = null;
            int[] dr = { -1, 0, 0, 1 }, dc = { 0, -1, 1, 0 };
            for (int d = 0; d < 4; d++)
            {
                int y = r + dr[d], x = c + dc[d];
                if (y < 0 || x < 0 || y >= Rows || x >= Cols) continue;
                int gi = giftIndex[y, x];
                if (gi < 0) continue;
                var gift = Gifts[gi];
                if (gift.Color != g.Color || gift.Count <= 0) continue;
                gift.Count--;
                events.Add(new BlockerEvent { Kind = "gift", Index = gi, CountAfter = gift.Count });
                if (gift.Count == 0)
                {
                    giftIndex[y, x] = -1;
                    Drama = Math.Max(0, Math.Min(100, Drama + GiftBonus));
                    events.Add(new BlockerEvent { Kind = "gift_open", Index = gi });
                }
            }
        }

        public void AddBenchSlot()
        {
            var b = new Guest[Bench.Length + 1];
            Array.Copy(Bench, b, Bench.Length);
            Bench = b;
            if (Status == GameStatus.LostStuck) Status = GameStatus.Playing;
            UpdateStatus();
        }

        public void ChangeDrama(int delta)
        {
            Drama = Math.Max(0, Math.Min(100, Drama + delta));
            if (Status == GameStatus.LostDrama && Drama < 100) Status = GameStatus.Playing;
            UpdateStatus();
        }

        SeatResult Place(Guest g, int seat)
        {
            var t = Tables[g.Color];
            var res = new SeatResult { Guest = g, TableColor = g.Color, Seat = seat };
            res.Effects = PreviewEffects(g, seat);
            t.Seats[seat] = g;
            placed[g.Id] = new Placement { Color = g.Color, Seat = seat, Gen = t.Generation };
            placedGuests[g.Id] = g;
            Seated++;
            foreach (var e in res.Effects)
            {
                Drama = Math.Max(0, Math.Min(100, Drama + e.Delta));
                if (e.Delta > 0) t.Bad = true;
            }
            if (t.IsFull)
            {
                res.TableCompleted = true;
                if (!t.Bad)
                {
                    res.PerfectTable = true;
                    Drama = Math.Max(0, Drama + PerfectTableBonus);
                    res.Effects.Add(new Effect(PerfectTableBonus, EffectKeys.PerfectTable));
                }
                for (int i = 0; i < Ribbons.Count; i++)
                    if (!Ribbons[i].Cut && Ribbons[i].Color == g.Color) { Ribbons[i].Cut = true; events.Add(new BlockerEvent { Kind = "ribbon_cut", Index = i }); }
                bool more = RemainingOf(g.Color) > 0;
                Tables[g.Color] = new Table { Color = g.Color, Generation = t.Generation + 1, Done = !more };
                res.NewTableSpawned = more;
            }
            RevealReachable();
            UpdateStatus();
            res.DramaAfter = Drama;
            res.StatusAfter = Status;
            return res;
        }

        void RevealReachable()
        {
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                    if (Grid[r, c] != null && Grid[r, c].Hidden && HasPath(r, c)) Grid[r, c].Hidden = false;
        }

        void UpdateStatus()
        {
            if (Drama >= 100) { Status = GameStatus.LostDrama; return; }
            if (Seated == Total) { Status = GameStatus.Won; return; }
            Status = HasAnyMove() ? GameStatus.Playing : GameStatus.LostStuck;
        }

        public bool HasAnyMove()
        {
            bool benchFree = FreeBenchSlot() >= 0;
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                {
                    var g = Grid[r, c];
                    if (g == null) continue;
                    if (HandsPartner(g) != null) { if (PairCanLeave(r, c) && PairSeats(g).Count > 0) return true; continue; }
                    if (!HasPath(r, c)) continue;
                    if (benchFree) return true;
                    foreach (var _ in SeatsFor(g)) return true;
                }
            foreach (var g in Bench)
                if (g != null) foreach (var _ in SeatsFor(g)) return true;
            return false;
        }

        // ---------- move enumeration (solver, hints, bots) ----------

        public struct Move
        {
            public bool FromBench, Pair;
            public int Row, Col, BenchIndex;
            public int Seat;          // -1 = move to bench
            public int Seat2;         // partner's seat for a hand-holding pair
            public int Delta;         // previewed drama delta (seat moves)
        }

        public List<Move> LegalMoves()
        {
            var list = new List<Move>();
            bool benchFree = FreeBenchSlot() >= 0;
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                {
                    var g = Grid[r, c];
                    if (g == null) continue;
                    var p = HandsPartner(g);
                    if (p != null)
                    {
                        if (p.Id < g.Id || !PairCanLeave(r, c)) continue;      // one entry per pair
                        foreach (var (k, j) in PairSeats(g))
                        {
                            var probe = Clone();
                            int before = probe.Drama;
                            probe.SeatPairFromCrowd(r, c, k, j);
                            list.Add(new Move { Pair = true, Row = r, Col = c, Seat = k, Seat2 = j, Delta = probe.Drama - before });
                        }
                        continue;
                    }
                    if (!HasPath(r, c)) continue;
                    foreach (var k in SeatsFor(g))
                        list.Add(new Move { Row = r, Col = c, Seat = k, Delta = Sum(PreviewEffects(g, k)) });
                    if (benchFree) list.Add(new Move { Row = r, Col = c, Seat = -1 });
                }
            for (int i = 0; i < Bench.Length; i++)
            {
                var g = Bench[i];
                if (g == null) continue;
                foreach (var k in SeatsFor(g))
                    list.Add(new Move { FromBench = true, BenchIndex = i, Seat = k, Delta = Sum(PreviewEffects(g, k)) });
            }
            return list;
        }

        public void Apply(Move m)
        {
            if (m.Pair) SeatPairFromCrowd(m.Row, m.Col, m.Seat, m.Seat2);
            else if (m.FromBench) SeatFromBench(m.BenchIndex, m.Seat);
            else if (m.Seat < 0) MoveToBench(m.Row, m.Col);
            else SeatFromCrowd(m.Row, m.Col, m.Seat);
        }

        static int Sum(List<Effect> e) { int s = 0; foreach (var x in e) s += x.Delta; return s; }

        /// <summary>Compact state key for search memoisation.</summary>
        public string StateKey()
        {
            var sb = new System.Text.StringBuilder();
            for (int r = 0; r < Rows; r++) for (int c = 0; c < Cols; c++) sb.Append(Grid[r, c] == null ? "." : Grid[r, c].Id.ToString()).Append(',');
            var bench = new List<int>();
            foreach (var g in Bench) if (g != null) bench.Add(g.Id);
            bench.Sort();
            sb.Append('|').Append(string.Join(",", bench)).Append('|');
            foreach (var col in Colors)
            {
                var t = Tables[col];
                sb.Append(col).Append(t.Generation);
                foreach (var s in t.Seats) sb.Append(s == null ? "." : s.Id.ToString()).Append(':');
            }
            sb.Append('|');
            foreach (var gf in Gifts) sb.Append(gf.Count).Append(',');
            foreach (var rb in Ribbons) sb.Append(rb.Cut ? '1' : '0');
            sb.Append('|').Append(Drama);
            // VIP patience makes the move count matter (only while some VIP still has patience left)
            bool clock = false;
            foreach (var g in AllUnseated()) if (g.Patience >= 0 && !angryVips.Contains(g.Id)) { clock = true; break; }
            if (clock) sb.Append('|').Append(Turn);
            foreach (var id in angryVips) sb.Append('a').Append(id);
            return sb.ToString();
        }
    }
}
