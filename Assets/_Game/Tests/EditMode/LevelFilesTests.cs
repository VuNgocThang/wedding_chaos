using System.IO;
using NUnit.Framework;
using UnityEngine;
using WeddingChaos.Core;

namespace WeddingChaos.Tests
{
    public class LevelFilesTests
    {
        static string[] LevelFiles() =>
            Directory.GetFiles(Path.Combine(Application.dataPath, "_Game/Resources/Levels"), "*.txt");

        /// <summary>Every level parses and its stored solution (written by the generator) wins without hitting 100% drama.</summary>
        [Test] public void EveryLevelParsesAndItsSolutionWins()
        {
            var files = LevelFiles();
            Assert.GreaterOrEqual(files.Length, 50);
            foreach (var f in files)
            {
                var def = LevelDef.Parse(File.ReadAllText(f));
                var sim = SeatingSim.FromLevel(def);
                var moves = LevelDef.ParseSolution(def.Solution);
                if (moves.Count == 0)
                {
                    Assert.IsTrue(Solver.Solve(sim, 200000).Solved, Path.GetFileName(f) + " is not winnable");
                    continue;
                }
                foreach (var m in moves)
                {
                    Assert.AreEqual(GameStatus.Playing, sim.Status, Path.GetFileName(f) + " ended early");
                    sim.Apply(m);
                }
                Assert.AreEqual(GameStatus.Won, sim.Status, Path.GetFileName(f) + " solution does not win");
            }
        }

        [Test] public void TiersFollowTheTenLevelCycle()
        {
            string[] cycle = { "easy", "easy", "medium", "easy", "easy", "hard", "easy", "easy", "hard", "super" };
            for (int i = 1; i <= 50; i++)
            {
                var asset = Path.Combine(Application.dataPath, $"_Game/Resources/Levels/level_{i:00}.txt");
                Assert.IsTrue(File.Exists(asset), "missing " + asset);
                Assert.AreEqual(cycle[(i - 1) % 10], LevelDef.Parse(File.ReadAllText(asset)).Tier, "level " + i);
            }
        }
    }
}
