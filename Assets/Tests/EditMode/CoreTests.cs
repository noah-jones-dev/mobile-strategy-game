using System;
using System.Collections.Generic;
using Hexfront.Core;
using NUnit.Framework;

namespace Hexfront.Tests
{
    public class HexTests
    {
        [Test]
        public void OffsetRoundTripAndDistance()
        {
            for (int r = 0; r < 12; r++)
                for (int c = 0; c < 12; c++)
                {
                    var h = Hex.FromOffset(c, r);
                    Assert.AreEqual(c, h.Col);
                    Assert.AreEqual(r, h.Row);
                    for (int d = 0; d < 6; d++) Assert.AreEqual(1, Hex.Distance(h, h.Neighbor(d)));
                }
            Assert.AreEqual(5, Hex.Distance(new Hex(0, 0), new Hex(5, -2)));
        }

        [Test]
        public void LineIsContiguous()
        {
            var a = new Hex(-3, 7); var b = new Hex(9, -2);
            var line = Hex.Line(a, b);
            Assert.AreEqual(Hex.Distance(a, b) + 1, line.Count);
            Assert.AreEqual(a, line[0]);
            Assert.AreEqual(b, line[line.Count - 1]);
            for (int i = 1; i < line.Count; i++) Assert.AreEqual(1, Hex.Distance(line[i - 1], line[i]));
        }
    }

    public class MapTests
    {
        [Test]
        public void PremadeMapsAreValidAndSymmetric()
        {
            Assert.GreaterOrEqual(PremadeMaps.All.Length, 3);
            for (int i = 0; i < PremadeMaps.All.Length; i++)
            {
                var s = MapGenerator.CreateMatch(new MapConfig { Premade = i });
                var hq0 = s.FindHeadquarters(0).Pos; var hq1 = s.FindHeadquarters(1).Pos;
                Assert.IsNull(MapGenerator.Validate(s, hq0, hq1), PremadeMaps.All[i].Name);
                AssertSymmetric(s);
            }
        }

        [Test]
        public void GeneratedMapsAreValidForEveryRegion()
        {
            for (int region = 0; region < 6; region++)
                for (uint seed = 1; seed <= 12; seed++)
                {
                    var cfg = new MapConfig
                    {
                        Region = (Region)region, Seed = seed, Width = 16 + (int)(seed % 4) * 6, Height = 12 + (int)(seed % 3) * 4,
                        WaterCoverage = (int)(seed * 7 % 50), TerrainDensity = (int)(seed * 13 % 100), StartDistance = (int)(seed % 3),
                        Objectives = 1 + (int)(seed % 6),
                    };
                    var s = MapGenerator.CreateMatch(cfg);
                    string problem = MapGenerator.Validate(s, s.FindHeadquarters(0).Pos, s.FindHeadquarters(1).Pos);
                    Assert.IsNull(problem, "region " + region + " seed " + seed + ": " + problem);
                    AssertSymmetric(s);
                    Assert.AreEqual(s.Hash(), MapGenerator.CreateMatch(cfg).Hash(), "same config must give the same map");
                }
        }

        [Test]
        public void MapCodeRoundTrips()
        {
            var cfg = new MapConfig { Region = Region.Islands, Width = 30, Height = 20, Seed = 987654, WaterCoverage = 33, Objectives = 5, StartDistance = 2 };
            Assert.IsTrue(MapConfig.TryParse(cfg.ToCode(), out var parsed));
            Assert.AreEqual(MapGenerator.CreateMatch(cfg).Hash(), MapGenerator.CreateMatch(parsed).Hash());
            Assert.IsTrue(MapConfig.TryParse("HF-P1", out var premade));
            Assert.AreEqual(1, premade.Premade);
            Assert.IsFalse(MapConfig.TryParse("nonsense", out _));
        }

        static void AssertSymmetric(GameState s)
        {
            int n = s.TileCount;
            for (int i = 0; i < n; i++)
            {
                Assert.AreEqual(s.Terrain[i], s.Terrain[n - 1 - i]);
                Assert.AreEqual(s.Site[i], s.Site[n - 1 - i]);
                Assert.AreEqual(s.Index(s.Mirror(s.HexAt(i))), n - 1 - i);
            }
        }
    }

    public class SimulationTests
    {
        /// <summary>An empty all-grass board with one HQ each, far apart.</summary>
        static GameState Flat(int w = 14, int h = 10, bool fog = false)
        {
            var s = new GameState();
            s.InitMap(w, h);
            s.Seed = 42; s.FogOfWar = fog;
            for (int i = 0; i < s.TileCount; i++) s.Elevation[i] = 1;
            s.AddBuilding(BuildingType.Headquarters, 0, Hex.FromOffset(0, 0), true);
            s.AddBuilding(BuildingType.Headquarters, 1, Hex.FromOffset(w - 1, h - 1), true);
            s.Players[0].Credits = s.Players[1].Credits = 1000;
            s.RecomputeTerritory();
            Visibility.UpdateExplored(s);
            return s;
        }

        static PlayerOrders Orders(GameState s, int player, params Order[] orders)
        {
            var po = new PlayerOrders(player, s.Round);
            po.Orders.AddRange(orders);
            return po;
        }

        [Test]
        public void SerializationRoundTripKeepsHash()
        {
            var s = MapGenerator.CreateMatch(new MapConfig { Seed = 5 });
            var copy = GameState.Deserialize(s.Serialize());
            Assert.AreEqual(s.Hash(), copy.Hash());
            var po = Orders(s, 0, Order.Move(3, new Hex(1, 2)), Order.Purchase(2, UnitType.RifleSquad));
            var back = PlayerOrders.Deserialize(po.Serialize());
            Assert.AreEqual(2, back.Orders.Count);
            Assert.AreEqual(OrderType.Purchase, back.Orders[1].Type);
            Assert.AreEqual(new Hex(1, 2), back.Orders[0].Target);
        }

        [Test]
        public void UnitMovesAlongPathLimitedByMovePoints()
        {
            var s = Flat();
            var u = s.AddUnit(UnitType.RifleSquad, 0, Hex.FromOffset(2, 4)); // move 3
            Simulation.Resolve(s, Orders(s, 0, Order.Move(u.Id, Hex.FromOffset(9, 4))), null);
            Assert.AreEqual(Hex.FromOffset(5, 4), u.Pos);
        }

        [Test]
        public void ContestedHexGoesToHeavierUnit()
        {
            var s = Flat();
            var tank = s.AddUnit(UnitType.MainBattleTank, 0, Hex.FromOffset(4, 4));
            var rifle = s.AddUnit(UnitType.RifleSquad, 1, Hex.FromOffset(6, 4));
            var mid = Hex.FromOffset(5, 4);
            Simulation.Resolve(s, Orders(s, 0, Order.Move(tank.Id, mid)), Orders(s, 1, Order.Move(rifle.Id, mid)));
            Assert.AreEqual(mid, tank.Pos);
            Assert.AreEqual(Hex.FromOffset(6, 4), rifle.Pos);
        }

        [Test]
        public void ContestedHexExactTieBlocksBoth()
        {
            var s = Flat();
            var a = s.AddUnit(UnitType.RifleSquad, 0, Hex.FromOffset(4, 4));
            var b = s.AddUnit(UnitType.RifleSquad, 1, Hex.FromOffset(6, 4));
            var mid = Hex.FromOffset(5, 4);
            Simulation.Resolve(s, Orders(s, 0, Order.Move(a.Id, mid)), Orders(s, 1, Order.Move(b.Id, mid)));
            Assert.AreEqual(Hex.FromOffset(4, 4), a.Pos);
            Assert.AreEqual(Hex.FromOffset(6, 4), b.Pos);
        }

        [Test]
        public void EnemiesCannotSwapPlaces()
        {
            var s = Flat();
            var a = s.AddUnit(UnitType.RifleSquad, 0, Hex.FromOffset(4, 4));
            var b = s.AddUnit(UnitType.RifleSquad, 1, Hex.FromOffset(5, 4));
            Simulation.Resolve(s, Orders(s, 0, Order.Move(a.Id, b.Pos)), Orders(s, 1, Order.Move(b.Id, a.Pos)));
            Assert.AreEqual(Hex.FromOffset(4, 4), a.Pos);
            Assert.AreEqual(Hex.FromOffset(5, 4), b.Pos);
        }

        [Test]
        public void FriendlyUnitsNeverEndStacked()
        {
            var s = Flat();
            var a = s.AddUnit(UnitType.RifleSquad, 0, Hex.FromOffset(3, 4));
            var b = s.AddUnit(UnitType.RifleSquad, 0, Hex.FromOffset(7, 4));
            var mid = Hex.FromOffset(5, 4);
            Simulation.Resolve(s, Orders(s, 0, Order.Move(a.Id, mid), Order.Move(b.Id, mid)), null);
            Assert.AreNotEqual(a.Pos, b.Pos);
            Assert.AreEqual(mid, a.Pos, "lower id keeps the hex when both arrive on the same tick");
            Assert.AreEqual(Hex.FromOffset(6, 4), b.Pos);
        }

        [Test]
        public void ResultDoesNotDependOnOrderListOrder()
        {
            var s1 = Flat(); var s2 = Flat();
            foreach (var s in new[] { s1, s2 })
            {
                s.AddUnit(UnitType.RifleSquad, 0, Hex.FromOffset(3, 4));
                s.AddUnit(UnitType.MainBattleTank, 0, Hex.FromOffset(3, 5));
                s.AddUnit(UnitType.RifleSquad, 1, Hex.FromOffset(6, 4));
            }
            var m1 = Order.Move(3, Hex.FromOffset(5, 4)); var m2 = Order.Move(4, Hex.FromOffset(5, 5));
            Simulation.Resolve(s1, Orders(s1, 0, m1, m2), Orders(s1, 1, Order.Move(5, Hex.FromOffset(4, 4))));
            Simulation.Resolve(s2, Orders(s2, 0, m2, m1), Orders(s2, 1, Order.Move(5, Hex.FromOffset(4, 4))));
            Assert.AreEqual(s1.Hash(), s2.Hash());
        }

        [Test]
        public void CombatIsSimultaneous()
        {
            var s = Flat();
            var a = s.AddUnit(UnitType.RifleSquad, 0, Hex.FromOffset(4, 4));
            var b = s.AddUnit(UnitType.RifleSquad, 1, Hex.FromOffset(5, 4));
            a.Hp = 5; b.Hp = 5;
            var result = Simulation.Resolve(s, Orders(s, 0, Order.Attack(a.Id, b.Id)), Orders(s, 1, Order.Attack(b.Id, a.Id)));
            Assert.IsNull(s.GetUnit(a.Id), "both units fire before either is removed");
            Assert.IsNull(s.GetUnit(b.Id));
            Assert.AreEqual(2, result.Events.FindAll(e => e.Type == SimEventType.UnitDestroyed).Count);
        }

        [Test]
        public void AttackDamageMatchesPreviewRange()
        {
            var s = Flat();
            var tank = s.AddUnit(UnitType.MainBattleTank, 0, Hex.FromOffset(4, 4));
            var target = s.AddUnit(UnitType.HeavyTank, 1, Hex.FromOffset(5, 4));
            Assert.IsTrue(Rules.PreviewAttack(s, tank, tank.Pos, target.Id, out int min, out int max));
            int before = target.Hp;
            Simulation.Resolve(s, Orders(s, 0, Order.Attack(tank.Id, target.Id), Order.Defend(999)), Orders(s, 1, Order.Repair(target.Id)));
            int dealt = before - target.Hp;
            Assert.GreaterOrEqual(dealt, min);
            Assert.LessOrEqual(dealt, max);
        }

        [Test]
        public void ArtilleryCannotMoveAndFire()
        {
            var s = Flat();
            var art = s.AddUnit(UnitType.Artillery, 0, Hex.FromOffset(3, 4));
            var foe = s.AddUnit(UnitType.RifleSquad, 1, Hex.FromOffset(7, 4));
            var v = new OrderValidator(s, 0);
            Assert.IsNull(v.TryAdd(Order.Move(art.Id, Hex.FromOffset(4, 4))));
            Assert.IsNotNull(v.TryAdd(Order.Attack(art.Id, foe.Id)));
            // Standing still, it out-ranges the rifle squad and takes no return fire.
            Simulation.Resolve(s, Orders(s, 0, Order.Attack(art.Id, foe.Id)), null);
            Assert.Less(foe.Hp, foe.Def.MaxHp);
            Assert.AreEqual(art.Def.MaxHp, art.Hp);
        }

        [Test]
        public void PurchaseBuildAndIncome()
        {
            var s = MapGenerator.CreateMatch(new MapConfig { Premade = 0, StartingCredits = 1000 });
            var barracks = s.Buildings.Find(b => b.Owner == 0 && b.Type == BuildingType.Barracks);
            var hq = s.FindHeadquarters(0);
            // Find any legal build hex.
            var occ = new Occupancy(s);
            Hex site = default; bool found = false;
            for (int i = 0; i < s.TileCount && !found; i++)
                if (Rules.CheckBuildSite(s, occ, 0, s.HexAt(i)) == null && Hex.Distance(s.HexAt(i), hq.Pos) == 2) { site = s.HexAt(i); found = true; }
            Assert.IsTrue(found);
            int units = s.Units.Count;
            int income = s.ComputeIncome(0);
            Simulation.Resolve(s, Orders(s, 0, Order.Purchase(barracks.Id, UnitType.RifleSquad), Order.Build(BuildingType.VehicleFactory, site),
                Order.Purchase(barracks.Id, UnitType.RifleSquad)), null); // second purchase from the same building is refused
            Assert.AreEqual(units + 1, s.Units.Count);
            Assert.AreEqual(1000 - 100 - 450 + income, s.Players[0].Credits);
            var factory = s.BuildingAt(site);
            Assert.IsNotNull(factory);
            Assert.IsFalse(factory.IsComplete);
            Simulation.Resolve(s, null, null);
            Assert.IsTrue(factory.IsComplete, "Vehicle Factory takes two rounds");
        }

        [Test]
        public void CannotSpendMoreThanYouHave()
        {
            var s = MapGenerator.CreateMatch(new MapConfig { Premade = 0, StartingCredits = 150 });
            var barracks = s.Buildings.Find(b => b.Owner == 0 && b.Type == BuildingType.Barracks);
            var v = new OrderValidator(s, 0);
            Assert.IsNotNull(v.TryAdd(Order.Purchase(barracks.Id, UnitType.AntiTankSquad)));
            Assert.IsNull(v.TryAdd(Order.Purchase(barracks.Id, UnitType.RifleSquad)));
            Assert.AreEqual(50, v.CreditsLeft);
            Assert.IsNotNull(v.TryAdd(Order.Purchase(s.Buildings.Find(b => b.Owner == 1 && b.Type == BuildingType.Barracks).Id, UnitType.RifleSquad)),
                "cannot order the enemy's buildings");
        }

        [Test]
        public void SiteCaptureAndUpgrade()
        {
            var s = Flat();
            int tile = s.Index(Hex.FromOffset(5, 4));
            s.Site[tile] = SiteType.OilField;
            var rifle = s.AddUnit(UnitType.RifleSquad, 0, Hex.FromOffset(4, 4));
            int hqId = s.FindHeadquarters(0).Id;
            Simulation.Resolve(s, Orders(s, 0, Order.Move(rifle.Id, Hex.FromOffset(5, 4)), Order.Upgrade(hqId)), null);
            Assert.AreEqual(0, s.SiteOwner[tile]);
            Assert.AreEqual(2, s.GetBuilding(hqId).Level);
            Assert.AreEqual(100 + 50 + 60, s.ComputeIncome(0));
        }

        [Test]
        public void FogHidesEnemiesAndBlocksBlindAttacks()
        {
            var s = Flat(20, 10, fog: true);
            var mine = s.AddUnit(UnitType.Artillery, 0, Hex.FromOffset(3, 4));   // vision 2, range 4
            var foe = s.AddUnit(UnitType.RifleSquad, 1, Hex.FromOffset(7, 4));
            var vis = Visibility.Compute(s, 0);
            Assert.IsFalse(vis[s.Index(foe.Pos)]);
            Assert.IsNotNull(new OrderValidator(s, 0).TryAdd(Order.Attack(mine.Id, foe.Id)));
            s.AddUnit(UnitType.ReconSquad, 0, Hex.FromOffset(4, 4)); // spotter, vision 6
            Assert.IsNull(new OrderValidator(s, 0).TryAdd(Order.Attack(mine.Id, foe.Id)));
        }

        [Test]
        public void DestroyingHeadquartersWins()
        {
            var s = Flat();
            var hq1 = s.FindHeadquarters(1);
            hq1.Hp = 1;
            var bomber = s.AddUnit(UnitType.Bomber, 0, hq1.Pos.Neighbor(3));
            var result = Simulation.Resolve(s, Orders(s, 0, Order.Attack(bomber.Id, hq1.Id)), null);
            Assert.AreEqual(0, s.Winner);
            Assert.IsTrue(result.Events.Exists(e => e.Type == SimEventType.MatchOver));
        }

        [Test]
        public void AircraftRunOutOfFuelAwayFromAirfield()
        {
            var s = Flat();
            var jet = s.AddUnit(UnitType.FighterJet, 0, Hex.FromOffset(6, 5));
            for (int i = 0; i < jet.Def.Fuel; i++) Simulation.Resolve(s, null, null);
            Assert.IsNull(s.GetUnit(jet.Id));
        }

        [Test]
        public void StaleRoundOrdersAreIgnored()
        {
            var s = Flat();
            var u = s.AddUnit(UnitType.RifleSquad, 0, Hex.FromOffset(2, 4));
            var old = new PlayerOrders(0, s.Round + 3);
            old.Orders.Add(Order.Move(u.Id, Hex.FromOffset(4, 4)));
            Simulation.Resolve(s, old, null);
            Assert.AreEqual(Hex.FromOffset(2, 4), u.Pos);
        }
    }

    public class AiAndDeterminismTests
    {
        public static void AssertInvariants(GameState s)
        {
            var seen = new HashSet<int>();
            foreach (var u in s.Units)
            {
                Assert.IsTrue(s.InBounds(u.Pos), "unit off the map");
                Assert.IsTrue(seen.Add(s.Index(u.Pos) * 2 + (u.IsAir ? 1 : 0)), "two units share a hex in round " + s.Round);
                Assert.Greater(u.Hp, 0);
                if (!u.IsAir)
                {
                    Assert.GreaterOrEqual(TerrainDefs.MoveCost(s.TerrainAt(u.Pos), u.Def.MoveClass), 0, u.Def.Name + " on impassable terrain");
                    Assert.IsNull(s.BuildingAt(u.Pos), "unit inside a building");
                }
            }
            var tiles = new HashSet<int>();
            foreach (var b in s.Buildings) Assert.IsTrue(tiles.Add(s.Index(b.Pos)), "two buildings share a hex");
            Assert.GreaterOrEqual(s.Players[0].Credits, 0);
            Assert.GreaterOrEqual(s.Players[1].Credits, 0);
        }

        static int Play(MapConfig cfg, AiDifficulty d0, AiDifficulty d1, int maxRounds, List<uint> hashes)
        {
            var s = MapGenerator.CreateMatch(cfg);
            while (!s.IsOver && s.Round <= maxRounds)
            {
                var a = AiPlayer.Plan(s, 0, d0);
                var b = AiPlayer.Plan(s, 1, d1);
                // Orders survive a trip over the network.
                a = PlayerOrders.Deserialize(a.Serialize()); b = PlayerOrders.Deserialize(b.Serialize());
                var result = Simulation.Resolve(s, a, b);
                Assert.AreEqual(result.StateHash, s.Hash());
                hashes?.Add(result.StateHash);
                AssertInvariants(s);
                // Saving and loading mid-match must not change anything.
                if (s.Round % 7 == 0) s = GameState.Deserialize(s.Serialize());
            }
            return s.Winner;
        }

        [Test]
        public void AiMatchesAreDeterministicOnEveryMap()
        {
            var configs = new List<MapConfig>();
            for (int i = 0; i < PremadeMaps.All.Length; i++) configs.Add(new MapConfig { Premade = i, Seed = 3 });
            for (int region = 0; region < 6; region++) configs.Add(new MapConfig { Region = (Region)region, Seed = (uint)(100 + region), Width = 22, Height = 14 });
            foreach (var cfg in configs)
            {
                var h1 = new List<uint>(); var h2 = new List<uint>();
                Play(cfg, AiDifficulty.Hard, AiDifficulty.Normal, 40, h1);
                Play(cfg, AiDifficulty.Hard, AiDifficulty.Normal, 40, h2);
                CollectionAssert.AreEqual(h1, h2, "replaying " + cfg.ToCode() + " diverged");
            }
        }

        [Test]
        public void HardAiBeatsEasyAi()
        {
            int hardWins = 0, games = 0;
            for (int i = 0; i < PremadeMaps.All.Length; i++)
                for (int side = 0; side < 2; side++)
                {
                    var cfg = new MapConfig { Premade = i, Seed = (uint)(11 + i) };
                    int winner = side == 0 ? Play(cfg, AiDifficulty.Hard, AiDifficulty.Easy, 150, null)
                                           : Play(cfg, AiDifficulty.Easy, AiDifficulty.Hard, 150, null);
                    games++;
                    if (winner == side) hardWins++;
                }
            Assert.GreaterOrEqual(hardWins * 2, games, "Hard should win at least half its games against Easy");
        }
    }
}
