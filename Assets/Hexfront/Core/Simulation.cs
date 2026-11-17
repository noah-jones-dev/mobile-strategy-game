using System;
using System.Collections.Generic;

namespace Hexfront.Core
{
    public enum SimEventType : byte
    {
        OrderRejected, UnitProduced, BuildStarted, UpgradeStarted, Move, Attack, UnitDestroyed, BuildingDestroyed,
        SiteCaptured, BuildingSeized, Repaired, ConstructionDone, UpgradeDone, OutOfFuel, Income, MatchOver,
    }

    /// <summary>Something that happened during execution. The 3D view plays these back as animation.</summary>
    public sealed class SimEvent
    {
        public SimEventType Type;
        public int Player = -1;
        /// <summary>Main subject id (unit or building).</summary>
        public int A;
        /// <summary>Secondary id (attack target, etc).</summary>
        public int B;
        public int Amount;
        public Hex From, To;
        public List<Hex> Path;
        public bool Killed;
        public string Text;
    }

    public sealed class RoundResult
    {
        public int Round;
        public readonly List<SimEvent> Events = new List<SimEvent>();
        /// <summary>Hash of the state after this round. Clients compare it with the host's.</summary>
        public uint StateHash;
    }

    /// <summary>
    /// The authoritative round resolver. Given the state and both players' locked-in orders it
    /// produces the next state. It never reads clocks, frame time or anything device-specific:
    /// same inputs, same outputs, on every phone.
    /// </summary>
    public static class Simulation
    {
        sealed class Mover
        {
            public Unit Unit;
            public Hex Origin;
            public List<Hex> Plan;
            public int Step;
            public readonly List<Hex> Traveled = new List<Hex>();
            public int ArrivalTick = -1;
        }

        public static RoundResult Resolve(GameState s, PlayerOrders p0, PlayerOrders p1)
        {
            var result = new RoundResult { Round = s.Round };
            var ev = result.Events;
            if (s.IsOver) { result.StateHash = s.Hash(); return result; }

            // ---- 1 & 2. Validate both plans against the same start-of-round state; deduct costs.
            var submitted = new[] { p0, p1 };
            var accepted = new List<Order>[2];
            var credits = new int[2];
            for (int p = 0; p < 2; p++)
            {
                int player = p;
                var po = submitted[p];
                IEnumerable<Order> list = po != null && po.Round == s.Round ? po.Orders : new List<Order>();
                accepted[p] = OrderValidator.Filter(s, p, list, out credits[p],
                    (o, why) => ev.Add(new SimEvent { Type = SimEventType.OrderRejected, Player = player, A = o.ActorId, Text = why }));
            }
            for (int p = 0; p < 2; p++) s.Players[p].Credits = credits[p];

            // Plan every move now, on the start-of-round board using only what each player could see.
            // (This is exactly the path the planning UI previewed.)
            var movers = new List<Mover>();
            var moverById = new Dictionary<int, Mover>();
            {
                var occ = new Occupancy(s);
                for (int p = 0; p < 2; p++)
                {
                    var vis = Visibility.Compute(s, p);
                    foreach (var o in accepted[p])
                    {
                        if (o.Type != OrderType.Move) continue;
                        var u = s.GetUnit(o.ActorId);
                        var m = new Mover { Unit = u, Origin = u.Pos, Plan = Pathfinding.PlanPath(s, occ, u, o.Target, vis) };
                        movers.Add(m); moverById[u.Id] = m;
                    }
                }
                movers.Sort((a, b) => a.Unit.Id.CompareTo(b.Unit.Id));
            }

            // ---- 3. Construction and production.
            for (int p = 0; p < 2; p++)
                foreach (var o in accepted[p])
                {
                    if (o.Type == OrderType.Build)
                    {
                        var b = s.AddBuilding((BuildingType)o.Param, p, o.Target, false);
                        ev.Add(new SimEvent { Type = SimEventType.BuildStarted, Player = p, A = b.Id, To = b.Pos });
                    }
                    else if (o.Type == OrderType.Upgrade)
                    {
                        var b = s.GetBuilding(o.ActorId);
                        b.UpgradeRoundsLeft = 1;
                        ev.Add(new SimEvent { Type = SimEventType.UpgradeStarted, Player = p, A = b.Id, To = b.Pos });
                    }
                }
            var purchases = new List<Order>();
            for (int p = 0; p < 2; p++) foreach (var o in accepted[p]) if (o.Type == OrderType.Purchase) purchases.Add(o);
            purchases.Sort((a, b) => a.ActorId.CompareTo(b.ActorId)); // by building id: independent of player/arrival order
            foreach (var o in purchases)
            {
                var b = s.GetBuilding(o.ActorId);
                var def = UnitDefs.Get((UnitType)o.Param);
                if (Rules.FindSpawnHex(s, new Occupancy(s), b, def, out Hex spawn))
                {
                    var u = s.AddUnit(def.Type, b.Owner, spawn);
                    ev.Add(new SimEvent { Type = SimEventType.UnitProduced, Player = b.Owner, A = u.Id, B = b.Id, From = b.Pos, To = spawn });
                }
                else
                {
                    s.Players[b.Owner].Credits += def.Cost; // no room: refund
                    ev.Add(new SimEvent { Type = SimEventType.OrderRejected, Player = b.Owner, A = b.Id, Text = "No room to deploy " + def.Name });
                }
            }

            // ---- 4 & 5. Movement, one hex per tick for everybody at once.
            ResolveMovement(s, movers, ev);

            // ---- 6 & 7. Combat: all shots are computed first, then all damage lands together.
            ResolveCombat(s, accepted, moverById, ev);

            // ---- 8. Captures.
            ResolveCaptures(s, accepted, ev);

            // ---- 9. Repairs and support.
            ResolveRepairs(s, accepted, ev);

            // ---- 10. Round-end effects.
            foreach (var b in s.Buildings)
            {
                if (b.BuildRoundsLeft > 0)
                {
                    if (--b.BuildRoundsLeft == 0)
                    {
                        b.Hp = Math.Min(b.Def.MaxHp, b.Hp + b.Def.MaxHp / 2);
                        ev.Add(new SimEvent { Type = SimEventType.ConstructionDone, Player = b.Owner, A = b.Id, To = b.Pos });
                    }
                }
                else if (b.UpgradeRoundsLeft > 0 && --b.UpgradeRoundsLeft == 0)
                {
                    b.Level++;
                    ev.Add(new SimEvent { Type = SimEventType.UpgradeDone, Player = b.Owner, A = b.Id, To = b.Pos, Amount = b.Level });
                }
            }
            ResolveFuel(s, ev);
            for (int p = 0; p < 2; p++)
            {
                int income = s.ComputeIncome(p);
                s.Players[p].Credits += income;
                s.Players[p].LastIncome = income;
                ev.Add(new SimEvent { Type = SimEventType.Income, Player = p, Amount = income });
            }
            s.RecomputeTerritory();
            Visibility.UpdateExplored(s);

            // ---- 11. Victory.
            bool hq0 = HasHeadquarters(s, 0), hq1 = HasHeadquarters(s, 1);
            if (!hq0 && !hq1) s.Winner = GameState.Draw;
            else if (!hq0) s.Winner = 1;
            else if (!hq1) s.Winner = 0;
            else if (s.MaxRounds > 0 && s.Round >= s.MaxRounds)
            {
                int a = s.Score(0), b = s.Score(1);
                s.Winner = a == b ? GameState.Draw : (a > b ? 0 : 1);
            }
            if (s.IsOver) ev.Add(new SimEvent { Type = SimEventType.MatchOver, Player = s.Winner });

            s.Round++;
            result.StateHash = s.Hash();
            return result;
        }

        static bool HasHeadquarters(GameState s, int player)
        {
            foreach (var b in s.Buildings)
                if (b.Owner == player && b.Type == BuildingType.Headquarters && b.IsComplete) return true;
            return false;
        }

        // ------------------------------------------------------------------ movement

        /// <summary>
        /// Movement conflict rules:
        ///  * A unit never enters a hex holding an enemy unit of its layer (ground/air) or, for
        ///    ground units, any building. It waits and may try again on later ticks.
        ///  * Two enemies stepping into the same empty hex on the same tick is a contest: the
        ///    heavier unit wins (Mass, then current health). An exact tie means neither enters.
        ///  * Friendly units may pass through each other, but only one may stay. Whoever arrived
        ///    later (ties: higher id) backs up along its own path until it finds a free hex.
        /// </summary>
        static void ResolveMovement(GameState s, List<Mover> movers, List<SimEvent> ev)
        {
            int ticks = 0;
            foreach (var m in movers) ticks = Math.Max(ticks, m.Plan.Count);
            int n = s.TileCount;
            var buildingAt = new bool[n];
            foreach (var b in s.Buildings) buildingAt[s.Index(b.Pos)] = true;

            // owners present per tile per layer, as a bit mask (1 = player 0, 2 = player 1)
            var mask = new byte[n * 2];
            var claims = new Dictionary<int, List<Mover>>();

            for (int t = 0; t < ticks; t++)
            {
                Array.Clear(mask, 0, mask.Length);
                foreach (var u in s.Units) mask[s.Index(u.Pos) * 2 + (u.IsAir ? 1 : 0)] |= (byte)(1 << u.Owner);

                claims.Clear();
                foreach (var m in movers)
                {
                    if (m.Step >= m.Plan.Count) continue;
                    var next = m.Plan[m.Step];
                    int i = s.Index(next);
                    bool air = m.Unit.IsAir;
                    if (!air && buildingAt[i]) continue;
                    int enemyBit = 1 << (1 - m.Unit.Owner);
                    if ((mask[i * 2 + (air ? 1 : 0)] & enemyBit) != 0) continue;
                    int key = i * 2 + (air ? 1 : 0);
                    if (!claims.TryGetValue(key, out var list)) claims[key] = list = new List<Mover>();
                    list.Add(m);
                }

                foreach (var kv in claims)
                {
                    var list = kv.Value;
                    int winner = ContestWinner(list);
                    foreach (var m in list)
                    {
                        if (winner != -1 && m.Unit.Owner != winner) continue;
                        if (winner == -1 && HasBothOwners(list)) continue;
                        m.Unit.Pos = m.Plan[m.Step];
                        m.Traveled.Add(m.Unit.Pos);
                        m.Step++;
                        m.ArrivalTick = t;
                    }
                }
            }

            // Un-stack: at most one unit per hex per layer.
            var arrival = new Dictionary<int, Mover>();
            foreach (var m in movers) arrival[m.Unit.Id] = m;
            var keeper = new Dictionary<int, Unit>();
            bool changed = true;
            while (changed)
            {
                changed = false;
                keeper.Clear();
                foreach (var u in s.Units)
                {
                    int key = s.Index(u.Pos) * 2 + (u.IsAir ? 1 : 0);
                    if (!keeper.TryGetValue(key, out var k)) { keeper[key] = u; continue; }
                    // Decide which of the two stays.
                    int tu = arrival.TryGetValue(u.Id, out var mu) ? mu.ArrivalTick : -1;
                    int tk = arrival.TryGetValue(k.Id, out var mk) ? mk.ArrivalTick : -1;
                    bool uStays = tu != tk ? tu < tk : u.Id < k.Id;
                    var loser = uStays ? k : u;
                    if (uStays) keeper[key] = u;
                    var lm = arrival[loser.Id]; // a loser always moved: two stationary units never share a hex
                    lm.Traveled.RemoveAt(lm.Traveled.Count - 1);
                    loser.Pos = lm.Traveled.Count > 0 ? lm.Traveled[lm.Traveled.Count - 1] : lm.Origin;
                    if (lm.Traveled.Count == 0) lm.ArrivalTick = -1;
                    changed = true;
                    break; // positions changed: rebuild and re-check
                }
            }

            foreach (var m in movers)
                if (m.Traveled.Count > 0)
                    ev.Add(new SimEvent
                    {
                        Type = SimEventType.Move, Player = m.Unit.Owner, A = m.Unit.Id, From = m.Origin, To = m.Unit.Pos,
                        Path = new List<Hex>(m.Traveled),
                    });
        }

        static bool HasBothOwners(List<Mover> list)
        {
            bool a = false, b = false;
            foreach (var m in list) { if (m.Unit.Owner == 0) a = true; else b = true; }
            return a && b;
        }

        /// <summary>-1 when uncontested or tied; otherwise the player whose units may enter.</summary>
        static int ContestWinner(List<Mover> list)
        {
            if (!HasBothOwners(list)) return -1;
            int best0 = -1, best1 = -1;
            foreach (var m in list)
            {
                int score = m.Unit.Def.Mass * 100000 + m.Unit.Hp;
                if (m.Unit.Owner == 0) best0 = Math.Max(best0, score); else best1 = Math.Max(best1, score);
            }
            return best0 == best1 ? -1 : (best0 > best1 ? 0 : 1);
        }

        // ------------------------------------------------------------------ combat

        struct Shot { public int Attacker, Target, Damage; public Hex From, To; public int Owner; }

        static void ResolveCombat(GameState s, List<Order>[] accepted, Dictionary<int, Mover> movers, List<SimEvent> ev)
        {
            var defending = new HashSet<int>();
            var busy = new HashSet<int>();
            var explicitTarget = new Dictionary<int, int>();
            for (int p = 0; p < 2; p++)
                foreach (var o in accepted[p])
                {
                    if (o.Type == OrderType.Defend) defending.Add(o.ActorId);
                    else if (o.Type == OrderType.Repair || o.Type == OrderType.Seize) busy.Add(o.ActorId);
                    else if (o.Type == OrderType.Attack) explicitTarget[o.ActorId] = o.TargetId;
                }

            var vis = new[] { Visibility.Compute(s, 0), Visibility.Compute(s, 1) };
            var shots = new List<Shot>();

            foreach (var u in s.Units)
            {
                var def = u.Def;
                if (!def.CanAttackAnything || busy.Contains(u.Id)) continue;
                bool moved = movers.TryGetValue(u.Id, out var mv) && mv.Traveled.Count > 0;
                if (def.Has(UnitFlags.Indirect) && moved) continue;
                bool dug = defending.Contains(u.Id);

                int targetId = 0, dmg = 0; Hex to = default;
                if (explicitTarget.TryGetValue(u.Id, out int wanted))
                {
                    var tu = s.GetUnit(wanted);
                    if (tu != null && tu.Owner != u.Owner && Rules.InRange(def, u.Pos, tu.Pos) && def.AttackVs(tu.Def.Category) > 0)
                    {
                        targetId = tu.Id; to = tu.Pos;
                        dmg = Rules.UnitVsUnit(s, u, u.Pos, tu, dug, defending.Contains(tu.Id));
                    }
                    else
                    {
                        var tb = s.GetBuilding(wanted);
                        if (tb != null && tb.Owner != u.Owner && Rules.InRange(def, u.Pos, tb.Pos) && def.AtkBuilding > 0)
                        {
                            targetId = tb.Id; to = tb.Pos;
                            dmg = Rules.UnitVsBuilding(s, u, u.Pos, tb, dug);
                        }
                    }
                }
                if (targetId == 0) AutoTarget(s, u, vis[u.Owner], dug, defending, out targetId, out dmg, out to);
                if (targetId == 0) continue;
                shots.Add(new Shot { Attacker = u.Id, Target = targetId, Damage = Rules.Roll(s, dmg, u.Id, targetId), From = u.Pos, To = to, Owner = u.Owner });
            }

            foreach (var b in s.Buildings)
            {
                if (b.Type != BuildingType.Turret || !b.IsOperational) continue;
                Unit best = null; int bestScore = -1, bestDmg = 0;
                foreach (var t in s.Units)
                {
                    if (t.Owner == b.Owner || !vis[b.Owner][s.Index(t.Pos)]) continue;
                    int dist = Hex.Distance(b.Pos, t.Pos);
                    if (dist < 1 || dist > b.Def.MaxRange) continue;
                    int d = Rules.TurretVsUnit(s, b, t, defending.Contains(t.Id));
                    int score = TargetScore(d, t.Hp, t.Def.Cost, t.Def.MaxHp);
                    if (score > bestScore) { best = t; bestScore = score; bestDmg = d; }
                }
                if (best != null)
                    shots.Add(new Shot { Attacker = b.Id, Target = best.Id, Damage = Rules.Roll(s, bestDmg, b.Id, best.Id), From = b.Pos, To = best.Pos, Owner = b.Owner });
            }

            // Apply everything simultaneously: a unit destroyed this round still fired this round.
            var totalDamage = new Dictionary<int, int>();
            foreach (var sh in shots)
            {
                totalDamage.TryGetValue(sh.Target, out int d);
                totalDamage[sh.Target] = d + sh.Damage;
            }
            var dying = new HashSet<int>();
            foreach (var kv in totalDamage)
            {
                var tu = s.GetUnit(kv.Key);
                if (tu != null) { tu.Hp -= kv.Value; if (tu.Hp <= 0) dying.Add(tu.Id); continue; }
                var tb = s.GetBuilding(kv.Key);
                if (tb != null) { tb.Hp -= kv.Value; if (tb.Hp <= 0) dying.Add(tb.Id); }
            }
            foreach (var sh in shots)
                ev.Add(new SimEvent
                {
                    Type = SimEventType.Attack, Player = sh.Owner, A = sh.Attacker, B = sh.Target, Amount = sh.Damage,
                    From = sh.From, To = sh.To, Killed = dying.Contains(sh.Target),
                });

            // Report and remove the destroyed, in id order so playback is stable.
            var deadIds = new List<int>(dying);
            deadIds.Sort();
            foreach (int id in deadIds)
            {
                var u = s.GetUnit(id);
                if (u != null)
                {
                    s.Players[u.Owner].UnitsLost++;
                    s.Players[1 - u.Owner].UnitsKilled++;
                    s.Units.Remove(u);
                    ev.Add(new SimEvent { Type = SimEventType.UnitDestroyed, Player = u.Owner, A = id, To = u.Pos });
                    continue;
                }
                var b = s.GetBuilding(id);
                s.Buildings.Remove(b);
                ev.Add(new SimEvent { Type = SimEventType.BuildingDestroyed, Player = b.Owner, A = id, To = b.Pos });
            }
        }

        static int TargetScore(int damage, int targetHp, int targetCost, int targetMaxHp)
        {
            int dealt = Math.Min(damage, targetHp);
            int score = dealt * targetCost / targetMaxHp;
            if (damage >= targetHp) score += targetCost / 2; // finishing a unit off is worth extra
            return score;
        }

        /// <summary>Fire-at-will: pick the most valuable visible enemy in range. Ties go to the lowest id.</summary>
        static void AutoTarget(GameState s, Unit u, bool[] visible, bool dug, HashSet<int> defending, out int targetId, out int damage, out Hex to)
        {
            targetId = 0; damage = 0; to = default;
            var def = u.Def;
            int bestScore = -1;
            foreach (var t in s.Units)
            {
                if (t.Owner == u.Owner || !visible[s.Index(t.Pos)]) continue;
                if (!Rules.InRange(def, u.Pos, t.Pos) || def.AttackVs(t.Def.Category) <= 0) continue;
                int d = Rules.UnitVsUnit(s, u, u.Pos, t, dug, defending.Contains(t.Id));
                int score = TargetScore(d, t.Hp, t.Def.Cost, t.Def.MaxHp) * 2;
                if (score > bestScore) { bestScore = score; targetId = t.Id; damage = d; to = t.Pos; }
            }
            if (def.AtkBuilding <= 0) return;
            foreach (var b in s.Buildings)
            {
                if (b.Owner == u.Owner || !visible[s.Index(b.Pos)] || !Rules.InRange(def, u.Pos, b.Pos)) continue;
                int d = Rules.UnitVsBuilding(s, u, u.Pos, b, dug);
                int score = TargetScore(d, b.Hp, b.Def.Cost, b.Def.MaxHp);
                if (score > bestScore) { bestScore = score; targetId = b.Id; damage = d; to = b.Pos; }
            }
        }

        // ------------------------------------------------------------------ captures, repairs, fuel

        static void ResolveCaptures(GameState s, List<Order>[] accepted, List<SimEvent> ev)
        {
            // Sites: a capturing unit standing on the site takes it, unless both players have one there.
            var claim = new Dictionary<int, int>(); // tile -> owner bit mask
            foreach (var u in s.Units)
            {
                if (!u.Def.Has(UnitFlags.CanCapture)) continue;
                int i = s.Index(u.Pos);
                if (s.Site[i] == SiteType.None) continue;
                claim.TryGetValue(i, out int m);
                claim[i] = m | (1 << u.Owner);
            }
            var tiles = new List<int>(claim.Keys);
            tiles.Sort();
            foreach (int i in tiles)
            {
                int m = claim[i];
                if (m == 3) continue;
                int owner = m == 1 ? 0 : 1;
                if (s.SiteOwner[i] == owner) continue;
                s.SiteOwner[i] = (sbyte)owner;
                ev.Add(new SimEvent { Type = SimEventType.SiteCaptured, Player = owner, To = s.HexAt(i), Amount = (int)s.Site[i] });
            }

            for (int p = 0; p < 2; p++)
                foreach (var o in accepted[p])
                {
                    if (o.Type != OrderType.Seize) continue;
                    var u = s.GetUnit(o.ActorId);
                    var b = s.GetBuilding(o.TargetId);
                    if (u == null || b == null || b.Owner == p || b.Type == BuildingType.Headquarters) continue;
                    if (Hex.Distance(u.Pos, b.Pos) > 1) continue;
                    if (b.Hp * 100 > b.Def.MaxHp * Balance.SeizeThresholdPercent) continue;
                    b.Owner = p;
                    ev.Add(new SimEvent { Type = SimEventType.BuildingSeized, Player = p, A = b.Id, B = u.Id, To = b.Pos });
                }
        }

        static void ResolveRepairs(GameState s, List<Order>[] accepted, List<SimEvent> ev)
        {
            for (int p = 0; p < 2; p++)
                foreach (var o in accepted[p])
                {
                    if (o.Type != OrderType.Repair) continue;
                    var u = s.GetUnit(o.ActorId);
                    if (u != null)
                    {
                        if (!Rules.HasRepairSupport(s, u)) { s.Players[p].Credits += Rules.RepairCost(u); continue; }
                        Heal(u, Balance.UnitRepairHealPercent, ev);
                        continue;
                    }
                    var b = s.GetBuilding(o.ActorId);
                    if (b != null && b.Owner == p) Heal(b, Balance.BuildingRepairHealPercent, ev);
                }

            // Automatic support. Collect first so the result does not depend on iteration order.
            var healUnits = new SortedSet<int>();
            var patchBuildings = new SortedSet<int>();
            foreach (var src in s.Units)
            {
                if (src.Def.Has(UnitFlags.Supply))
                    foreach (var t in s.Units)
                        if (t.Owner == src.Owner && t.Id != src.Id && t.Hp < t.Def.MaxHp && Hex.Distance(src.Pos, t.Pos) <= 1) healUnits.Add(t.Id);
                if (src.Def.Has(UnitFlags.Engineer))
                    foreach (var b in s.Buildings)
                        if (b.Owner == src.Owner && b.IsComplete && b.Hp < b.Def.MaxHp && Hex.Distance(src.Pos, b.Pos) <= 1) patchBuildings.Add(b.Id);
            }
            foreach (var depot in s.Buildings)
            {
                if (depot.Type != BuildingType.SupplyDepot || !depot.IsOperational) continue;
                foreach (var t in s.Units)
                    if (t.Owner == depot.Owner && t.Hp < t.Def.MaxHp && Hex.Distance(depot.Pos, t.Pos) <= 1) healUnits.Add(t.Id);
            }
            foreach (int id in healUnits) Heal(s.GetUnit(id), Balance.SupplyHealPercent, ev);
            foreach (int id in patchBuildings) Heal(s.GetBuilding(id), Balance.EngineerPatchPercent, ev);
        }

        static void Heal(Unit u, int percent, List<SimEvent> ev)
        {
            int before = u.Hp;
            u.Hp = Math.Min(u.Def.MaxHp, u.Hp + u.Def.MaxHp * percent / 100);
            if (u.Hp > before) ev.Add(new SimEvent { Type = SimEventType.Repaired, Player = u.Owner, A = u.Id, Amount = u.Hp - before, To = u.Pos });
        }

        static void Heal(Building b, int percent, List<SimEvent> ev)
        {
            int before = b.Hp;
            b.Hp = Math.Min(b.Def.MaxHp, b.Hp + b.Def.MaxHp * percent / 100);
            if (b.Hp > before) ev.Add(new SimEvent { Type = SimEventType.Repaired, Player = b.Owner, A = b.Id, Amount = b.Hp - before, To = b.Pos });
        }

        /// <summary>Aircraft burn one fuel per round away from a friendly Airfield and crash at zero.</summary>
        static void ResolveFuel(GameState s, List<SimEvent> ev)
        {
            for (int i = s.Units.Count - 1; i >= 0; i--)
            {
                var u = s.Units[i];
                if (u.Def.Fuel <= 0) continue;
                if (NearAirfield(s, u)) { u.Fuel = u.Def.Fuel; continue; }
                if (--u.Fuel > 0) continue;
                s.Players[u.Owner].UnitsLost++;
                s.Units.RemoveAt(i);
                ev.Add(new SimEvent { Type = SimEventType.OutOfFuel, Player = u.Owner, A = u.Id, To = u.Pos });
            }
        }

        public static bool NearAirfield(GameState s, Unit u)
        {
            foreach (var b in s.Buildings)
                if (b.Owner == u.Owner && b.Type == BuildingType.Airfield && b.IsComplete && Hex.Distance(b.Pos, u.Pos) <= 1) return true;
            return false;
        }
    }
}
