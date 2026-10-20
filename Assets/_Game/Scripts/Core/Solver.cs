using System;
using System.Collections.Generic;

namespace WeddingChaos.Core
{
    /// <summary>Level checking: a depth-first solver (is the level winnable?) and a greedy
    /// "average player" bot (how often does a non-planning player win?).</summary>
    public static class Solver
    {
        public sealed class Result
        {
            public bool Solved;
            public int FinalDrama = 100;
            public List<SeatingSim.Move> Moves = new List<SeatingSim.Move>();
            public int Nodes;
        }

        public static Result Solve(SeatingSim start, int nodeLimit = 60000)
        {
            var res = new Result();
            var seen = new HashSet<string>();
            var path = new List<SeatingSim.Move>();
            bool Dfs(SeatingSim s)
            {
                if (++res.Nodes > nodeLimit) return false;
                if (s.Status == GameStatus.LostDrama || s.Status == GameStatus.LostStuck) return false;
                if (s.Status == GameStatus.Won)
                {
                    if (!res.Solved || s.Drama < res.FinalDrama) { res.Solved = true; res.FinalDrama = s.Drama; res.Moves = new List<SeatingSim.Move>(path); }
                    return s.Drama <= start.Drama;   // good enough: did not end worse than it began
                }
                if (!seen.Add(s.StateKey())) return false;
                var moves = s.LegalMoves();
                moves.Sort((a, b) => (a.Seat < 0).CompareTo(b.Seat < 0) != 0 ? (a.Seat < 0).CompareTo(b.Seat < 0) : a.Delta.CompareTo(b.Delta));
                foreach (var m in moves)
                {
                    var n = s.Clone();
                    n.Apply(m);
                    path.Add(m);
                    if (Dfs(n)) return true;
                    path.RemoveAt(path.Count - 1);
                }
                return false;
            }
            Dfs(start.Clone());
            return res;
        }

        /// <summary>Greedy player: picks a random movable guest, then the lowest-drama seat;
        /// benches the guest instead when every seat would raise drama and the bench has room.</summary>
        public static double GreedyWinRate(SeatingSim start, int runs, int seed)
        {
            var rng = new Random(seed);
            int wins = 0;
            for (int run = 0; run < runs; run++)
            {
                var s = start.Clone();
                for (int step = 0; step < 400 && s.Status == GameStatus.Playing; step++)
                {
                    var moves = s.LegalMoves();
                    if (moves.Count == 0) break;
                    var sources = new List<string>();
                    foreach (var m in moves) { var key = Src(m); if (!sources.Contains(key)) sources.Add(key); }
                    var pick = sources[rng.Next(sources.Count)];
                    SeatingSim.Move? best = null, bench = null;
                    var ties = new List<SeatingSim.Move>();
                    foreach (var m in moves)
                    {
                        if (Src(m) != pick) continue;
                        if (m.Seat < 0) { bench = m; continue; }
                        if (best == null || m.Delta < best.Value.Delta) { best = m; ties.Clear(); ties.Add(m); }
                        else if (m.Delta == best.Value.Delta) ties.Add(m);
                    }
                    SeatingSim.Move chosen;
                    if (best != null && (best.Value.Delta <= 0 || bench == null)) chosen = ties[rng.Next(ties.Count)];
                    else chosen = bench ?? best.Value;
                    s.Apply(chosen);
                }
                if (s.Status == GameStatus.Won) wins++;
            }
            return (double)wins / runs;
        }

        static string Src(SeatingSim.Move m) => m.FromBench ? "b" + m.BenchIndex : m.Row + "," + m.Col;
    }
}
