using System;
using System.Collections.Generic;

namespace WeddingChaos.Core
{
    /// <summary>
    /// Level text format (one level per file):
    ///   name: Tam giác tình
    ///   tier: hard                      (optional: easy / medium / hard / super — difficulty slot)
    ///   bench: 2
    ///   drama: 20
    ///   grid:
    ///   R@a  B   Rg  .
    ///   #    Bk@c R@b B@d
    ///   links:
    ///   couple a b
    ///   ex a d
    ///   parent d c
    ///   solution: 0,0>2 0,1>b 1,2>1 b0>3 ...
    ///
    /// Grid tokens: "." empty, "#" pillar, "R" plain guest of color R, "?R" hidden, "$R3" gift stack of colour R needing 3 red guests to leave beside it.
    /// Trait suffix: g=grandma u=uncle s=gossip k=kid. "@label" names the guest for the links block.
    /// Legacy pair suffixes still parse: x1/x2 = ex pair, c1/c2 = couple pair.
    /// Link lines: couple|ex|rival|bff A B (symmetric), parent P K (P is the kid K's parent), crush A B (A secretly loves B),
    /// hands A B (two neighbours holding hands: they leave together and need two seats side by side).
    /// Blocker lines (same block): reserve A 2 [P] (seat 2 of A's colour table carries A's name card; anyone else sitting
    /// there while A still waits costs +15%; optional P = A's patience in turns, after which A gets angry once: +15%),
    /// ribbon R 2,1 3,1 (red ribbon across the edge between cells 2,1 and 3,1; cut when a red table is completed).
    /// Solution moves (optional, used by tests and hints): "r,c>k" crowd to seat k, "r,c>b" crowd to bench, "bI>k" bench slot I to seat k,
    /// "r,c>k+j" a hand-holding pair: the guest at r,c to seat k and the partner to seat j.
    /// </summary>
    public sealed class LevelDef
    {
        public string Name = "Level";
        public string Tier = "";
        public int Bench = 3;
        public int StartDrama;
        public List<string[]> Grid = new List<string[]>();
        public List<(string kind, string a, string b)> Links = new List<(string, string, string)>();
        public List<(string label, int seat, int patience)> Reserves = new List<(string, int, int)>();
        public List<(char color, int r1, int c1, int r2, int c2)> Ribbons = new List<(char, int, int, int, int)>();
        public string Solution = "";

        public int Rows => Grid.Count;
        public int Cols => Grid.Count == 0 ? 0 : Grid[0].Length;

        public static LevelDef Parse(string text)
        {
            var def = new LevelDef();
            string block = null;
            foreach (var raw in text.Replace("\r", "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("//")) continue;
                int colon = line.IndexOf(':');
                if (colon < 0)
                {
                    var toks = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (block == "grid")
                    {
                        if (def.Grid.Count > 0 && toks.Length != def.Cols)
                            throw new FormatException($"Grid row {def.Grid.Count} has {toks.Length} cells, expected {def.Cols}");
                        def.Grid.Add(toks);
                    }
                    else if (block == "links")
                    {
                        string kind = toks[0].ToLowerInvariant();
                        if (kind == "reserve" && (toks.Length == 3 || toks.Length == 4))
                            def.Reserves.Add((toks[1], int.Parse(toks[2]), toks.Length == 4 ? int.Parse(toks[3]) : -1));
                        else if (kind == "ribbon" && toks.Length == 4)
                        {
                            var a = toks[2].Split(','); var b = toks[3].Split(',');
                            def.Ribbons.Add((toks[1][0], int.Parse(a[0]), int.Parse(a[1]), int.Parse(b[0]), int.Parse(b[1])));
                        }
                        else if (toks.Length == 3) def.Links.Add((kind, toks[1], toks[2]));
                        else throw new FormatException("Bad links line: '" + line + "'");
                    }
                    else throw new FormatException("Expected key: value, got '" + line + "'");
                    continue;
                }
                block = null;
                var key = line.Substring(0, colon).Trim().ToLowerInvariant();
                var val = line.Substring(colon + 1).Trim();
                switch (key)
                {
                    case "name": def.Name = val; break;
                    case "tier": def.Tier = val.ToLowerInvariant(); break;
                    case "bench": def.Bench = int.Parse(val); break;
                    case "drama": def.StartDrama = int.Parse(val); break;
                    case "grid": block = "grid"; break;
                    case "links": block = "links"; break;
                    case "solution": def.Solution = val; break;
                    default: throw new FormatException("Unknown key '" + key + "'");
                }
            }
            if (def.Grid.Count == 0) throw new FormatException("Level has no grid");
            return def;
        }

        /// <summary>"$R3" → a gift stack of colour R with count 3.</summary>
        public static bool TryParseGift(string tok, out char color, out int count)
        {
            color = '\0'; count = 0;
            if (tok.Length < 3 || tok[0] != '$') return false;
            color = tok[1];
            count = int.Parse(tok.Substring(2));
            if ("RBGY".IndexOf(color) < 0 || count <= 0) throw new FormatException("Bad gift token '" + tok + "'");
            return true;
        }

        /// <summary>Parses one grid token into a guest, or returns null for empty / pillar.</summary>
        public static Guest ParseToken(string tok, int id, out bool pillar) => ParseToken(tok, id, out pillar, out _);

        /// <summary>As above; legacyPair receives "x1"/"c2" style pair keys (empty when none).</summary>
        public static Guest ParseToken(string tok, int id, out bool pillar, out string legacyPair)
        {
            legacyPair = "";
            pillar = tok == "#";
            if (tok == "." || pillar || tok.StartsWith("$")) return null;
            var g = new Guest { Id = id };
            int at = tok.IndexOf('@');
            if (at >= 0) { g.Label = tok.Substring(at + 1); tok = tok.Substring(0, at); }
            int i = 0;
            if (tok[0] == '?') { g.Hidden = true; i = 1; }
            g.Color = tok[i++];
            if ("RBGY".IndexOf(g.Color) < 0) throw new FormatException("Unknown color in '" + tok + "'");
            while (i < tok.Length)
            {
                char ch = tok[i++];
                switch (ch)
                {
                    case 'g': g.Type = GuestType.Grandma; break;
                    case 'u': g.Type = GuestType.Uncle; break;
                    case 's': g.Type = GuestType.Gossip; break;
                    case 'k': g.Type = GuestType.Kid; break;
                    case 'x':
                    case 'c':
                        if (i >= tok.Length || !char.IsDigit(tok[i])) throw new FormatException("Pair suffix needs a digit in '" + tok + "'");
                        legacyPair = ch.ToString() + tok[i++];
                        break;
                    default: throw new FormatException("Unknown guest suffix in '" + tok + "'");
                }
            }
            return g;
        }

        public static LinkKind ParseKind(string kind)
        {
            switch (kind)
            {
                case "couple": return LinkKind.Couple;
                case "ex": return LinkKind.Ex;
                case "rival": return LinkKind.Rival;
                case "bff": return LinkKind.Bff;
                case "parent": return LinkKind.ParentOf;
                case "crush": return LinkKind.CrushOn;
                case "hands": return LinkKind.Hands;
                default: throw new FormatException("Unknown link kind '" + kind + "'");
            }
        }

        /// <summary>Adds a link edge to both guests (with the reverse kind on b) under one shared tag letter.</summary>
        public static void Connect(Guest a, Guest b, LinkKind kind, char tag)
        {
            if (a == b) throw new FormatException("A guest cannot link to itself");
            if (a.LinkTo(b.Id) != null) throw new FormatException($"Guests {a.Id} and {b.Id} are linked twice");
            a.Links.Add(new Link { Kind = kind, Other = b.Id, Tag = tag });
            b.Links.Add(new Link { Kind = Link.Reverse(kind), Other = a.Id, Tag = tag });
        }

        /// <summary>Parses the solution line into moves.</summary>
        public static List<SeatingSim.Move> ParseSolution(string text)
        {
            var list = new List<SeatingSim.Move>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            foreach (var tok in text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = tok.Split('>');
                if (parts.Length != 2) throw new FormatException("Bad move '" + tok + "'");
                var m = new SeatingSim.Move();
                if (parts[0].StartsWith("b")) { m.FromBench = true; m.BenchIndex = int.Parse(parts[0].Substring(1)); }
                else { var rc = parts[0].Split(','); m.Row = int.Parse(rc[0]); m.Col = int.Parse(rc[1]); }
                if (parts[1].Contains("+"))
                {
                    var ks = parts[1].Split('+');
                    m.Pair = true; m.Seat = int.Parse(ks[0]); m.Seat2 = int.Parse(ks[1]);
                }
                else m.Seat = parts[1] == "b" ? -1 : int.Parse(parts[1]);
                list.Add(m);
            }
            return list;
        }
    }
}
