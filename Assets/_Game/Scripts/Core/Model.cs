using System.Collections.Generic;

namespace WeddingChaos.Core
{
    /// <summary>A guest's own trait. Relationships between guests are <see cref="Link"/>s.</summary>
    public enum GuestType { Normal, Grandma, Uncle, Gossip, Kid }

    /// <summary>Relationship kinds. Symmetric kinds are stored on both guests; directed kinds store
    /// the reverse kind on the other guest (ParentOf/ChildOf, CrushOn/CrushedBy).</summary>
    public enum LinkKind { Couple, Ex, Rival, Bff, ParentOf, ChildOf, CrushOn, CrushedBy, Hands }

    /// <summary>One relationship edge as seen from one guest. Tag is the letter shown on both badges.</summary>
    public sealed class Link
    {
        public LinkKind Kind;
        public int Other;
        public char Tag;
        public override string ToString() => $"{Kind}->{Other}({Tag})";

        public static LinkKind Reverse(LinkKind k)
        {
            switch (k)
            {
                case LinkKind.ParentOf: return LinkKind.ChildOf;
                case LinkKind.ChildOf: return LinkKind.ParentOf;
                case LinkKind.CrushOn: return LinkKind.CrushedBy;
                case LinkKind.CrushedBy: return LinkKind.CrushOn;
                default: return k;
            }
        }
    }

    /// <summary>One guest. Color is the invitation color ('R','B','G','Y'). A guest can have any number of links.</summary>
    public sealed class Guest
    {
        public int Id;
        public char Color;
        public GuestType Type;
        public bool Hidden;
        public string Label;
        /// <summary>Reserved seat index at the guest's colour table (-1 = none). The guest may only sit there; nobody else may.</summary>
        public int Reserved = -1;
        /// <summary>VIP patience in turns (moves the player makes), -1 = unlimited. Still unseated after that many turns: +15% once.</summary>
        public int Patience = -1;
        public List<Link> Links = new List<Link>();

        public bool Has(LinkKind k) { foreach (var l in Links) if (l.Kind == k) return true; return false; }
        public Link LinkTo(int otherId) { foreach (var l in Links) if (l.Other == otherId) return l; return null; }

        public Guest Clone() => (Guest)MemberwiseClone();
        public override string ToString() => $"{Color}{Type}#{Id}{(Links.Count > 0 ? "[" + string.Join(",", Links) + "]" : "")}";
    }

    /// <summary>A round table: 4 seats in a ring. Seat 2 is the star seat (faces the stage).</summary>
    public sealed class Table
    {
        public const int SeatCount = 4;
        public const int StarSeat = 2;
        public char Color;
        public int Generation;
        public Guest[] Seats = new Guest[SeatCount];
        public bool Done;
        /// <summary>True once any positive-drama effect happened at this table generation.</summary>
        public bool Bad;

        public bool IsFull { get { foreach (var s in Seats) if (s == null) return false; return true; } }
        public static int Left(int k) => (k + 3) % SeatCount;
        public static int Right(int k) => (k + 1) % SeatCount;
        public static int Opposite(int k) => (k + 2) % SeatCount;
        public static bool Adjacent(int a, int b) => Left(a) == b || Right(a) == b;

        public Table Clone()
        {
            var t = (Table)MemberwiseClone();
            t.Seats = (Guest[])Seats.Clone();
            return t;
        }
    }

    public readonly struct Effect
    {
        public readonly int Delta;
        public readonly string Key;
        public readonly bool LocksTable;
        /// <summary>Id of the other guest involved (0 when the effect is about the guest alone).</summary>
        public readonly int OtherId;
        public Effect(int delta, string key, bool locksTable = false, int otherId = 0) { Delta = delta; Key = key; LocksTable = locksTable; OtherId = otherId; }
        public override string ToString() => $"{Key}({Delta:+0;-0})";
    }

    public static class EffectKeys
    {
        public const string GrandmaNotStar = "grandma_not_star";
        public const string GrandmaScoldsUncle = "grandma_scolds_uncle";
        public const string ExFight = "ex_fight";
        public const string ExGlare = "ex_glare";
        public const string CoupleTogether = "couple_together";
        public const string CoupleSplit = "couple_split";
        public const string GossipPair = "gossip_pair";
        public const string PerfectTable = "perfect_table";
        public const string RivalClash = "rival_clash";
        public const string BffTogether = "bff_together";
        public const string FamilyTogether = "family_together";
        public const string KidCries = "kid_cries";
        public const string CrushHappy = "crush_happy";
        public const string GiftOpened = "gift_opened";
        public const string VipSeatTaken = "vip_seat_taken";
        public const string VipImpatient = "vip_impatient";
    }

    /// <summary>A colour-locked gift stack in the crowd: blocks its cell until Count guests of its colour leave from a neighbouring cell.</summary>
    public sealed class Gift
    {
        public int Row, Col, Count;
        public char Color;
        public Gift Clone() => (Gift)MemberwiseClone();
    }

    /// <summary>A coloured ribbon across the edge between two orthogonal crowd cells; cut when a table of its colour is completed.</summary>
    public sealed class Ribbon
    {
        public int R1, C1, R2, C2;
        public char Color;
        public bool Cut;
        public Ribbon Clone() => (Ribbon)MemberwiseClone();
        public bool Between(int ra, int ca, int rb, int cb) => (R1 == ra && C1 == ca && R2 == rb && C2 == cb) || (R1 == rb && C1 == cb && R2 == ra && C2 == ca);
    }

    /// <summary>Things that happened to blockers during the last move, for the view to animate.</summary>
    public struct BlockerEvent
    {
        public string Kind;        // "gift" (counter changed), "gift_open", "ribbon_cut"
        public int Index;          // gift / ribbon index
        public int CountAfter;
    }

    public enum GameStatus { Playing, Won, LostDrama, LostStuck }

    public readonly struct Cell
    {
        public readonly int Row, Col;
        public Cell(int r, int c) { Row = r; Col = c; }
        public override string ToString() => $"({Row},{Col})";
    }

    /// <summary>What happened after a seat move, for the view to animate.</summary>
    public sealed class SeatResult
    {
        public Guest Guest;
        public char TableColor;
        public int Seat;
        public List<Effect> Effects = new List<Effect>();
        public bool TableCompleted;
        public bool PerfectTable;
        public bool NewTableSpawned;
        public int DramaAfter;
        public GameStatus StatusAfter;
    }
}
