using System.Linq;
using NUnit.Framework;
using WeddingChaos.Core;

namespace WeddingChaos.Tests
{
    public class SeatingSimTests
    {
        static SeatingSim Make(int bench, int drama, params string[] rows)
        {
            var text = $"name: test\nbench: {bench}\ndrama: {drama}\ngrid:\n" + string.Join("\n", rows);
            return SeatingSim.FromLevel(LevelDef.Parse(text));
        }

        // ---------- parsing ----------
        [Test] public void Parser_ReadsHeaderAndGrid()
        {
            var d = LevelDef.Parse("name: L\nbench: 2\ndrama: 40\ngrid:\nR Rg\nRx1 ?Rc2");
            Assert.AreEqual(2, d.Bench); Assert.AreEqual(40, d.StartDrama); Assert.AreEqual(2, d.Rows); Assert.AreEqual(2, d.Cols);
            var g = LevelDef.ParseToken("?Rc2", 1, out _, out var pair);
            Assert.IsTrue(g.Hidden); Assert.AreEqual('R', g.Color); Assert.AreEqual("c2", pair);
            var k = LevelDef.ParseToken("Bk@mia", 2, out _);
            Assert.AreEqual(GuestType.Kid, k.Type); Assert.AreEqual("mia", k.Label);
        }

        static SeatingSim MakeLinked(int bench, int drama, string[] rows, params string[] links)
        {
            var text = $"name: test\nbench: {bench}\ndrama: {drama}\ngrid:\n" + string.Join("\n", rows) + "\nlinks:\n" + string.Join("\n", links);
            return SeatingSim.FromLevel(LevelDef.Parse(text));
        }

        [Test] public void Links_AreSymmetricWithSharedTag_AndLegacyPairsBecomeLinks()
        {
            var s = MakeLinked(1, 0, new[] { "R@a R@b Rk@c R@d" }, "couple a b", "parent d c", "crush a d");
            var a = s.Grid[0, 0]; var b = s.Grid[0, 1]; var c = s.Grid[0, 2]; var d = s.Grid[0, 3];
            Assert.AreEqual(LinkKind.Couple, b.LinkTo(a.Id).Kind);
            Assert.AreEqual(a.LinkTo(b.Id).Tag, b.LinkTo(a.Id).Tag);
            Assert.AreEqual(LinkKind.ChildOf, c.LinkTo(d.Id).Kind);
            Assert.AreEqual(LinkKind.CrushedBy, d.LinkTo(a.Id).Kind);
            Assert.AreEqual(2, a.Links.Count, "one guest can carry several relationships");
            var legacy = Make(1, 0, "Rx1 Rx1 R R");
            Assert.AreEqual(LinkKind.Ex, legacy.Grid[0, 0].LinkTo(legacy.Grid[0, 1].Id).Kind);
        }

        [Test] public void Parent_MustPointAtAKid()
        {
            Assert.Throws<System.FormatException>(() => MakeLinked(1, 0, new[] { "R@a R@b R R" }, "parent a b"));
        }

        [Test] public void Rival_SameTableAnySeat_Clashes()
        {
            var s = MakeLinked(1, 0, new[] { "R@a R@b R R", "B B B B" }, "rival a b");
            s.SeatFromCrowd(0, 0, 0);
            var r = s.SeatFromCrowd(0, 1, 2);          // opposite, not adjacent: still a clash
            Assert.IsTrue(r.Effects.Any(e => e.Key == EffectKeys.RivalClash && e.OtherId == s.Tables['R'].Seats[0].Id));
            Assert.AreEqual(SeatingSim.RivalClash, s.Drama);
        }

        [Test] public void Bff_SameTable_Bonus()
        {
            var s = MakeLinked(1, 40, new[] { "R@a R@b R R" }, "bff a b");
            s.SeatFromCrowd(0, 0, 0);
            var r = s.SeatFromCrowd(0, 1, 2);
            Assert.IsTrue(r.Effects.Any(e => e.Key == EffectKeys.BffTogether));
            Assert.AreEqual(40 + SeatingSim.BffBonus, s.Drama);
        }

        [Test] public void Kid_NextToParent_ElseCries()
        {
            var a = MakeLinked(1, 30, new[] { "R@p Rk@k R R" }, "parent p k");
            a.SeatFromCrowd(0, 0, 0);
            Assert.IsTrue(a.SeatFromCrowd(0, 1, 1).Effects.Any(e => e.Key == EffectKeys.FamilyTogether));
            var b = MakeLinked(1, 30, new[] { "R@p Rk@k R R" }, "parent p k");
            b.SeatFromCrowd(0, 1, 0);                  // kid first
            Assert.IsTrue(b.SeatFromCrowd(0, 0, 2).Effects.Any(e => e.Key == EffectKeys.KidCries && e.Delta == SeatingSim.KidCries));
        }

        [Test] public void Crush_AdjacentBonus_NoPenaltyOtherwise()
        {
            var a = MakeLinked(1, 30, new[] { "R@a R@b R R" }, "crush a b");
            a.SeatFromCrowd(0, 1, 0);                  // the loved one sits first
            Assert.IsTrue(a.SeatFromCrowd(0, 0, 3).Effects.Any(e => e.Key == EffectKeys.CrushHappy));
            var b = MakeLinked(1, 30, new[] { "R@a R@b R R" }, "crush a b");
            b.SeatFromCrowd(0, 0, 0);
            Assert.AreEqual(0, b.SeatFromCrowd(0, 1, 2).Effects.Count);
        }

        [Test] public void LoveTriangle_OneGuestScoresEveryLink()
        {
            // a loves b but is the ex of c, all on the red table: a sits between b and c
            var s = MakeLinked(1, 20, new[] { "R@b R@c R@a R" }, "couple a b", "ex a c");
            s.SeatFromCrowd(0, 0, 0);
            s.SeatFromCrowd(0, 1, 2);
            var r = s.SeatFromCrowd(0, 2, 1);          // seat 1 touches seats 0 and 2
            Assert.IsTrue(r.Effects.Any(e => e.Key == EffectKeys.CoupleTogether));
            Assert.IsTrue(r.Effects.Any(e => e.Key == EffectKeys.ExFight));
            Assert.AreEqual(20 + SeatingSim.ExFight + SeatingSim.CoupleBonus, s.Drama);
        }

        [Test] public void Level_RejectsColorCountNotMultipleOfFour()
        {
            Assert.Throws<System.InvalidOperationException>(() => Make(1, 0, "R R R"));
        }

        // ---------- crowd jam ----------
        [Test] public void Path_BlockedGuestCannotMove()
        {
            var s = Make(1, 0, "R R", "R R");
            Assert.IsTrue(s.HasPath(0, 0));
            Assert.IsFalse(s.HasPath(1, 0));
            s.SeatFromCrowd(0, 0, 0);
            Assert.IsTrue(s.HasPath(1, 0));
        }

        [Test] public void Path_PillarsBlock()
        {
            var s = Make(1, 0, "R # R", "# R #", ". R .");
            Assert.IsFalse(s.HasPath(1, 1));
            var p = s.FindPath(0, 2); Assert.AreEqual(1, p.Count);
        }

        [Test] public void Hidden_RevealedWhenReachable()
        {
            var s = Make(1, 0, "R R", "?R R");
            Assert.IsTrue(s.Grid[1, 0].Hidden);
            s.SeatFromCrowd(0, 0, 0);
            Assert.IsFalse(s.Grid[1, 0].Hidden);
        }

        // ---------- seat rules ----------
        [Test] public void Couple_AdjacentGivesBonus_SplitGivesPenalty()
        {
            var a = Make(1, 50, "Rc1 Rc1 R R");
            a.SeatFromCrowd(0, 0, 0);
            var r = a.SeatFromCrowd(0, 1, 1);
            Assert.IsTrue(r.Effects.Any(e => e.Key == EffectKeys.CoupleTogether));

            var b = Make(1, 50, "Rc1 Rc1 R R");
            b.SeatFromCrowd(0, 0, 0);
            var r2 = b.SeatFromCrowd(0, 1, 2);   // opposite seat
            Assert.IsTrue(r2.Effects.Any(e => e.Key == EffectKeys.CoupleSplit && e.Delta == SeatingSim.CouplePenalty));
        }

        [Test] public void Ex_AdjacentFights_OppositeGlares()
        {
            var a = Make(1, 0, "Rx1 Rx1 R R");
            a.SeatFromCrowd(0, 0, 0);
            var r = a.SeatFromCrowd(0, 1, 3);
            Assert.IsTrue(r.Effects.Any(e => e.Key == EffectKeys.ExFight && e.LocksTable));
            Assert.AreEqual(SeatingSim.ExFight, a.Drama);

            var b = Make(1, 0, "Rx1 Rx1 R R");
            b.SeatFromCrowd(0, 0, 0);
            var r2 = b.SeatFromCrowd(0, 1, 2);
            Assert.IsTrue(r2.Effects.Any(e => e.Key == EffectKeys.ExGlare));
        }

        [Test] public void Grandma_NeedsStarSeat_AndScoldsUncleNextToHer()
        {
            var s = Make(1, 0, "Rg Ru R R");
            var g = s.Grid[0, 0];
            Assert.AreEqual(SeatingSim.GrandmaNotStar, s.PreviewEffects(g, 0).Sum(e => e.Delta));
            Assert.AreEqual(0, s.PreviewEffects(g, Table.StarSeat).Count);
            s.SeatFromCrowd(0, 0, Table.StarSeat);
            var r = s.SeatFromCrowd(0, 1, 1);           // seat 1 is next to the star seat
            Assert.IsTrue(r.Effects.Any(e => e.Key == EffectKeys.GrandmaScoldsUncle));
        }

        [Test] public void Gossips_SideBySide_LowerDrama()
        {
            var s = Make(1, 30, "Rs Rs R R");
            s.SeatFromCrowd(0, 0, 0);
            var r = s.SeatFromCrowd(0, 1, 1);
            Assert.IsTrue(r.Effects.Any(e => e.Key == EffectKeys.GossipPair));
            Assert.AreEqual(30 + SeatingSim.GossipBonus, s.Drama);
        }

        [Test] public void PerfectTable_GivesBonus_AndSpawnsNextTable()
        {
            var s = Make(1, 20, "R R R R", "R R R R");
            for (int c = 0; c < 4; c++) s.SeatFromCrowd(0, c, c);
            Assert.AreEqual(20 + SeatingSim.PerfectTableBonus, s.Drama);
            Assert.AreEqual(2, s.Tables['R'].Generation);
            Assert.IsFalse(s.Tables['R'].Done);
        }

        // ---------- end states ----------
        [Test] public void Win_WhenEveryoneSeated()
        {
            var s = Make(1, 0, "R R R R");
            for (int c = 0; c < 4; c++) s.SeatFromCrowd(0, c, c);
            Assert.AreEqual(GameStatus.Won, s.Status);
            Assert.IsTrue(s.Tables['R'].Done);
        }

        [Test] public void Lose_WhenDramaHitsHundred()
        {
            var s = Make(1, 80, "Rx1 Rx1 R R");
            s.SeatFromCrowd(0, 0, 0);
            s.SeatFromCrowd(0, 1, 1);
            Assert.AreEqual(GameStatus.LostDrama, s.Status);
        }

        [Test] public void Bench_IsOptional_AMoveAlwaysExistsWhileGuestsRemain()
        {
            var s = Make(1, 0, "B R R R", "B R B B");
            s.MoveToBench(0, 0);
            Assert.AreEqual(GameStatus.Playing, s.Status);
            Assert.IsTrue(s.HasAnyMove());
        }

        // ---------- blockers ----------
        [Test] public void Gift_BlocksUntilEnoughGuestsOfItsColourLeaveBesideIt()
        {
            // the red gift blocks the guest below it; two reds standing next to it must leave first
            var s = Make(2, 30, "R $R2 R B", "B R B B", "R . . .");
            Assert.IsFalse(s.HasPath(1, 1));
            s.SeatFromCrowd(0, 0, 0);
            Assert.AreEqual(1, s.Gifts[0].Count);
            Assert.IsTrue(s.TakeEvents().Exists(e => e.Kind == "gift"));
            s.MoveToBench(0, 3);                       // a blue leaving beside it does not count
            Assert.AreEqual(1, s.Gifts[0].Count);
            s.SeatFromCrowd(0, 2, 1);
            Assert.AreEqual(0, s.Gifts[0].Count);
            Assert.IsTrue(s.IsEmptyCell(0, 1));
            Assert.IsTrue(s.HasPath(1, 1));
            Assert.AreEqual(30 + SeatingSim.GiftBonus, s.Drama);
        }

        [Test] public void ReservedSeat_OnlyItsOwnerMaySitThere()
        {
            var s = MakeLinked(1, 0, new[] { "R@a R R R" }, "reserve a 2");
            var a = s.Grid[0, 0]; var b = s.Grid[0, 1];
            CollectionAssert.AreEqual(new[] { 2 }, s.SeatsFor(a).ToArray());
            CollectionAssert.DoesNotContain(s.SeatsFor(b).ToArray(), 2);
            Assert.IsFalse(s.CanSeatFromCrowd(0, 0, 0));
            s.SeatFromCrowd(0, 0, 2);
            Assert.IsTrue(s.SeatsFor(s.Grid[0, 1]).Count() == 3);
        }

        [Test] public void HandHoldingPair_LeavesTogether_AndSitsSideBySide()
        {
            var s = MakeLinked(1, 0, new[] { "R R@a B B", "B R@b B R" }, "hands a b");
            var a = s.Grid[0, 1];
            Assert.IsFalse(s.CanBench(0, 1));
            Assert.IsFalse(s.CanSeatFromCrowd(0, 1, 0));
            Assert.IsTrue(s.PairCanLeave(1, 1));       // b is blocked alone, but follows a
            var seats = s.PairSeats(a);
            Assert.IsTrue(seats.All(p => Table.Adjacent(p.k, p.j)));
            s.SeatPairFromCrowd(1, 1, 1, 2);           // start from b: b -> 1, a -> 2
            Assert.AreEqual(2, s.Seated);
            Assert.IsTrue(s.LegalMoves().All(m => !m.Pair));
        }

        [Test] public void Ribbon_BlocksAnEdge_UntilATableOfItsColourIsDone()
        {
            var s = MakeLinked(1, 0, new[] { "R R R R", "B B B B" }, "ribbon R 0,0 1,0", "ribbon R 0,1 1,1", "ribbon R 0,2 1,2", "ribbon R 0,3 1,3");
            s.SeatFromCrowd(0, 0, 0);
            Assert.IsFalse(s.HasPath(1, 0), "the ribbon still blocks the way up");
            s.SeatFromCrowd(0, 1, 1); s.SeatFromCrowd(0, 2, 2); s.SeatFromCrowd(0, 3, 3);
            Assert.IsTrue(s.Ribbons.All(r => r.Cut));
            Assert.IsTrue(s.HasPath(1, 0));
        }

        [Test] public void PairMoves_RoundTripThroughSolutionText()
        {
            var moves = LevelDef.ParseSolution("1,1>1+2 0,0>b b0>3");
            Assert.IsTrue(moves[0].Pair); Assert.AreEqual(2, moves[0].Seat2);
            Assert.AreEqual(-1, moves[1].Seat); Assert.IsTrue(moves[2].FromBench);
        }

        // ---------- solver ----------
        [Test] public void Solver_FindsWinOnSimpleLevel()
        {
            var s = Make(1, 0, "Rc1 R Rc1 R", "R R R R");
            var res = Solver.Solve(s);
            Assert.IsTrue(res.Solved);
            var replay = s.Clone();
            foreach (var m in res.Moves) replay.Apply(m);
            Assert.AreEqual(GameStatus.Won, replay.Status);
        }

        [Test] public void Solver_BossLevelIsWinnable()
        {
            var s = Make(1, 60,
                "Rx1 Gc2 Gx2 Rc1 Y Rx1",
                "Gx2 # R # B Bu",
                "G Bs Y Bs # Bg",
                "B Yu Gc2 Y R B",
                "Yg # G ?Y G R",
                "Y R B Y Rc1 ?G");
            var res = Solver.Solve(s, 200000);
            Assert.IsTrue(res.Solved, "boss level should be solvable");
            Assert.Less(res.FinalDrama, 60);
        }
    }
}
