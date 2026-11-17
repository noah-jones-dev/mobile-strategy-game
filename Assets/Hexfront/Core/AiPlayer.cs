using System;
using System.Collections.Generic;

namespace Hexfront.Core
{
    public enum AiDifficulty : byte { Easy, Normal, Hard }

    /// <summary>
    /// Computer opponent. It produces a PlayerOrders plan through the same OrderValidator a human
    /// uses, sees only what fog of war lets it see (it does know the map layout), and is fully
    /// deterministic for a given state.
    /// </summary>
    public sealed class AiPlayer
    {
        readonly GameState _s;
        readonly int _me;
        readonly AiDifficulty _diff;
        readonly OrderValidator _v;
        readonly PlayerOrders _plan;
        readonly Occupancy _occ;
        readonly bool[] _vis;
        Rng _rng;

        readonly List<Unit> _mine = new List<Unit>();
        readonly List<Unit> _enemies = new List<Unit>();            // visible enemy units
        readonly List<Building> _enemyBuildings = new List<Building>(); // on tiles we have seen
        readonly HashSet<int> _claimed = new HashSet<int>();         // tile*2+layer already promised to a unit
        readonly Dictionary<int, int> _assigned = new Dictionary<int, int>(); // target id -> damage already aimed at it
        readonly HashSet<int> _siteTargets = new HashSet<int>();
        Hex _myHq, _enemyHq;
        bool _enemyAirSeen, _enemyArmorSeen;
        int _combatUnits;

        AiPlayer(GameState s, int player, AiDifficulty diff)
        {
            _s = s; _me = player; _diff = diff;
            _v = new OrderValidator(s, player);
            _occ = _v.Occ; _vis = _v.Visible;
            _plan = new PlayerOrders(player, s.Round);
            _rng = new Rng(Rng.Hash(s.Seed, s.Round, player, 77));
        }

        public static PlayerOrders Plan(GameState s, int player, AiDifficulty difficulty)
        {
            var ai = new AiPlayer(s, player, difficulty);
            if (!s.IsOver) ai.Think();
            return ai._plan;
        }

        bool Try(Order o)
        {
            if (_v.TryAdd(o) != null) return false;
            _plan.Orders.Add(o);
            return true;
        }

        void Think()
        {
            var hq = _s.FindHeadquarters(_me);
            if (hq == null) return;
            _myHq = hq.Pos;
            _enemyHq = _s.Mirror(_myHq); // maps are symmetric, so this is where the enemy started
            foreach (var u in _s.Units)
            {
                if (u.Owner == _me) { _mine.Add(u); if (u.Def.CanAttackAnything) _combatUnits++; }
                else if (_vis[_s.Index(u.Pos)])
                {
                    _enemies.Add(u);
                    if (u.IsAir) _enemyAirSeen = true;
                    if (u.Def.Category == UnitCategory.Vehicle && u.Def.Armor >= 40) _enemyArmorSeen = true;
                }
            }
            foreach (var b in _s.Buildings)
                if (b.Owner != _me && (_vis[_s.Index(b.Pos)] || _s.Explored[_me][_s.Index(b.Pos)]))
                {
                    _enemyBuildings.Add(b);
                    if (b.Type == BuildingType.Headquarters) _enemyHq = b.Pos;
                }

            PlanUnits();      // units first: field repairs get first call on the budget
            PlanEconomy();
        }

        // ------------------------------------------------------------------ economy

        int Count(BuildingType t)
        {
            int n = 0;
            foreach (var b in _s.Buildings) if (b.Owner == _me && b.Type == t) n++;
            return n;
        }

        int CountUnits(UnitType t)
        {
            int n = 0;
            foreach (var u in _mine) if (u.Type == t) n++;
            return n;
        }

        bool ThreatNearHq()
        {
            foreach (var e in _enemies) if (Hex.Distance(e.Pos, _myHq) <= 5) return true;
            return false;
        }

        void PlanEconomy()
        {
            int round = _s.Round;
            bool easy = _diff == AiDifficulty.Easy, hard = _diff == AiDifficulty.Hard;

            // What do we want to build next?
            BuildingType? want = null;
            if (Count(BuildingType.Barracks) == 0) want = BuildingType.Barracks;
            else if (ThreatNearHq() && Count(BuildingType.Turret) < (hard ? 3 : 2) && !easy) want = BuildingType.Turret;
            else if (Count(BuildingType.Generator) < 1 && round >= (hard ? 2 : 4)) want = BuildingType.Generator;
            else if (Count(BuildingType.VehicleFactory) == 0 && round >= (easy ? 7 : hard ? 3 : 4)) want = BuildingType.VehicleFactory;
            else if (Count(BuildingType.Generator) < Math.Min(3, 1 + round / (hard ? 6 : 9)) && !easy) want = BuildingType.Generator;
            else if (Count(BuildingType.SupplyDepot) == 0 && round >= 8) want = BuildingType.SupplyDepot;
            else if (Count(BuildingType.Airfield) == 0 && round >= (hard ? 9 : 12) && !easy) want = BuildingType.Airfield;
            else if (Count(BuildingType.RadarStation) == 0 && round >= 10 && hard) want = BuildingType.RadarStation;
            else if (Count(BuildingType.Barracks) < 2 && round >= 14 && !easy) want = BuildingType.Barracks;
            else if (!easy && _v.CreditsLeft > 1800 && Count(BuildingType.VehicleFactory) < 3) want = BuildingType.VehicleFactory;
            else if (!easy && _v.CreditsLeft > 2500 && Count(BuildingType.Airfield) < 2) want = BuildingType.Airfield;

            bool saving = false;
            if (want.HasValue)
            {
                var def = BuildingDefs.Get(want.Value);
                if (_v.CreditsLeft >= def.Cost)
                {
                    if (FindBuildSite(want.Value, out Hex site)) Try(Order.Build(want.Value, site));
                }
                else if (!easy && _combatUnits >= 3 && _v.CreditsLeft * 100 >= def.Cost * 55) saving = true;
            }

            // Upgrades are for when there is money to spare.
            if (!easy && !saving)
            {
                var order = new[] { BuildingType.Barracks, BuildingType.VehicleFactory, BuildingType.Headquarters, BuildingType.Airfield, BuildingType.Generator };
                foreach (var t in order)
                    foreach (var b in _s.Buildings)
                        if (b.Owner == _me && b.Type == t && b.IsOperational && b.Level < b.Def.MaxLevel &&
                            _v.CreditsLeft >= Rules.UpgradeCost(b) + (hard ? 250 : 450) && round >= (hard ? 7 : 10))
                            Try(Order.Upgrade(b.Id));
            }

            // Purchases.
            int cap = (easy ? 9 : hard ? 26 : 16) + (easy ? 0 : _v.CreditsLeft / 400);
            int bought = 0;
            foreach (var b in _s.Buildings)
            {
                if (b.Owner != _me || !b.Def.IsProducer || !b.IsOperational) continue;
                if (_mine.Count + bought >= cap) break;
                if (easy && bought >= 1) break;
                if (saving && b.Type != BuildingType.Barracks) continue;
                var pick = ChooseUnit(b);
                if (pick.HasValue && Try(Order.Purchase(b.Id, pick.Value))) bought++;
            }
        }

        UnitType? ChooseUnit(Building b)
        {
            int credits = _v.CreditsLeft;
            var options = new List<UnitType>();
            Action<UnitType, int> add = (t, weight) =>
            {
                var d = UnitDefs.Get(t);
                if (d.Producer != b.Type || d.ProducerLevel > b.Level || d.Cost > credits) return;
                for (int i = 0; i < weight; i++) options.Add(t);
            };
            int capturers = 0;
            foreach (var u in _mine) if (u.Def.Has(UnitFlags.CanCapture)) capturers++;
            int freeSites = 0;
            for (int i = 0; i < _s.Site.Length; i++) if (_s.Site[i] != SiteType.None && _s.SiteOwner[i] != _me) freeSites++;

            switch (b.Type)
            {
                case BuildingType.Barracks:
                    add(UnitType.RifleSquad, capturers < Math.Min(4, freeSites + 1) ? 6 : 2);
                    if (_diff != AiDifficulty.Easy && CountUnits(UnitType.ReconSquad) == 0) add(UnitType.ReconSquad, 2);
                    add(UnitType.AntiTankSquad, _enemyArmorSeen ? 5 : 1);
                    add(UnitType.RocketInfantry, _enemyAirSeen ? 5 : 0);
                    add(UnitType.HeavyInfantry, 3);
                    if (CountUnits(UnitType.Engineers) == 0 && _s.Round > 8) add(UnitType.Engineers, 2);
                    break;
                case BuildingType.VehicleFactory:
                    add(UnitType.MainBattleTank, 5);
                    add(UnitType.HeavyTank, 4);
                    if (CountUnits(UnitType.ReconVehicle) == 0) add(UnitType.ReconVehicle, 2);
                    add(UnitType.Apc, freeSites > 0 ? 2 : 1);
                    if (CountUnits(UnitType.MainBattleTank) + CountUnits(UnitType.HeavyTank) >= 2)
                    {
                        add(UnitType.Artillery, 3);
                        add(UnitType.RocketArtillery, 2);
                    }
                    if (_enemyAirSeen && CountUnits(UnitType.AntiAir) < 2) add(UnitType.AntiAir, 8);
                    if (_diff == AiDifficulty.Hard && _combatUnits >= 6)
                    {
                        if (CountUnits(UnitType.SupplyTruck) == 0) add(UnitType.SupplyTruck, 2);
                        if (CountUnits(UnitType.CommandVehicle) == 0) add(UnitType.CommandVehicle, 2);
                    }
                    break;
                case BuildingType.Airfield:
                    add(UnitType.AttackHelicopter, 5);
                    add(UnitType.FighterJet, _enemyAirSeen ? 6 : 1);
                    add(UnitType.Bomber, 3);
                    if (CountUnits(UnitType.TransportHelicopter) == 0 && freeSites > 1) add(UnitType.TransportHelicopter, 2);
                    break;
            }
            if (options.Count == 0) return null;
            return options[_rng.Next(options.Count)];
        }

        bool FindBuildSite(BuildingType type, out Hex site)
        {
            site = default;
            int best = int.MinValue;
            for (int i = 0; i < _s.TileCount; i++)
            {
                if (_s.Territory[i] != _me) continue;
                var h = _s.HexAt(i);
                if (Rules.CheckBuildSite(_s, _occ, _me, h) != null) continue;
                int score = -Hex.Distance(h, _myHq) * 4;
                // Turrets go on the enemy side; everything else hides behind the HQ.
                int toEnemy = Hex.Distance(h, _enemyHq);
                score += type == BuildingType.Turret ? -toEnemy * 6 : toEnemy;
                // Leave breathing room so producers can still deploy units.
                foreach (var b in _s.Buildings) if (Hex.Distance(b.Pos, h) <= 1) score -= 9;
                if (_s.Terrain[i] == TerrainType.Urban || _s.Terrain[i] == TerrainType.Rocky) score += type == BuildingType.Turret ? 4 : 0;
                if (score > best) { best = score; site = h; }
            }
            return best != int.MinValue;
        }

        // ------------------------------------------------------------------ units

        void PlanUnits()
        {
            bool threat = ThreatNearHq();
            int needForAttack = _diff == AiDifficulty.Easy ? 6 : _diff == AiDifficulty.Normal ? 5 : 4;

            // A rally point a few hexes from the HQ toward the enemy. Units gather there and only
            // push forward as a wave; units already past it keep attacking.
            var line = Hex.Line(_myHq, _enemyHq);
            Hex rally = line[Math.Min(line.Count - 1, 4)];
            int rallyFromHq = Hex.Distance(rally, _myHq);
            int gathered = 0;
            foreach (var u in _mine)
                if (u.Def.CanAttackAnything && Hex.Distance(u.Pos, rally) <= 3) gathered++;
            bool waveReady = gathered >= needForAttack || _combatUnits >= needForAttack * 3;

            foreach (var u in _mine)
            {
                var def = u.Def;

                // Aircraft low on fuel fly home.
                if (def.Fuel > 0 && u.Fuel <= 2 && !Simulation.NearAirfield(_s, u))
                {
                    var home = NearestOwn(BuildingType.Airfield, u.Pos);
                    if (home != null) { MoveToward(u, home.Pos, 0); continue; }
                }

                // Badly hurt units repair where they can, or fall back.
                if (_diff != AiDifficulty.Easy && u.Hp * 100 < def.MaxHp * 40)
                {
                    if (Try(Order.Repair(u.Id))) continue;
                    if (_diff == AiDifficulty.Hard && !EnemyWithin(u.Pos, 2) && Hex.Distance(u.Pos, _myHq) > 2)
                    {
                        MoveToward(u, _myHq, 1);
                        continue;
                    }
                }

                if (def.Has(UnitFlags.Engineer) && TrySeize(u)) continue;
                if (def.CanAttackAnything && TryAttack(u)) continue;

                // Nothing to shoot: pick somewhere useful to go.
                if (def.Has(UnitFlags.CanCapture) && FindSite(u, out Hex siteHex))
                {
                    MoveToward(u, siteHex, 0);
                    continue;
                }
                bool attackMode = waveReady || Hex.Distance(u.Pos, _myHq) > rallyFromHq + 3;
                if (!def.CanAttackAnything)
                {
                    // Support units trail the army.
                    MoveToward(u, attackMode ? _enemyHq : rally, def.IsAir ? 6 : 4);
                    continue;
                }
                if (threat)
                {
                    var foe = NearestEnemy(_myHq);
                    if (foe != null) { MoveToward(u, foe.Pos, Math.Max(1, def.MinRange)); continue; }
                }
                if (attackMode)
                {
                    Hex goal = _enemyHq;
                    int bestD = int.MaxValue;
                    foreach (var e in _enemies)
                    {
                        int d = Hex.Distance(u.Pos, e.Pos);
                        if (d < bestD && def.AttackVs(e.Def.Category) > 0) { bestD = d; goal = e.Pos; }
                    }
                    if (bestD > 8)
                        foreach (var b in _enemyBuildings)
                        {
                            int d = Hex.Distance(u.Pos, b.Pos);
                            if (d < bestD && def.AtkBuilding > 0) { bestD = d; goal = b.Pos; }
                        }
                    MoveToward(u, goal, Math.Max(1, def.MaxRange > 1 ? def.MaxRange - 1 : 1));
                }
                else if (Hex.Distance(u.Pos, rally) > 2)
                    MoveToward(u, rally, 1);
            }
        }

        bool EnemyWithin(Hex pos, int range)
        {
            foreach (var e in _enemies) if (Hex.Distance(e.Pos, pos) <= range) return true;
            return false;
        }

        Unit NearestEnemy(Hex pos)
        {
            Unit best = null; int bestD = int.MaxValue;
            foreach (var e in _enemies)
            {
                int d = Hex.Distance(e.Pos, pos);
                if (d < bestD) { bestD = d; best = e; }
            }
            return best;
        }

        Building NearestOwn(BuildingType type, Hex pos)
        {
            Building best = null; int bestD = int.MaxValue;
            foreach (var b in _s.Buildings)
            {
                if (b.Owner != _me || b.Type != type || !b.IsComplete) continue;
                int d = Hex.Distance(b.Pos, pos);
                if (d < bestD) { bestD = d; best = b; }
            }
            return best;
        }

        bool TrySeize(Unit u)
        {
            foreach (var b in _enemyBuildings)
                if (b.Type != BuildingType.Headquarters && Hex.Distance(b.Pos, u.Pos) <= 1 &&
                    b.Hp * 100 <= b.Def.MaxHp * Balance.SeizeThresholdPercent && Try(Order.Seize(u.Id, b.Id)))
                    return true;
            return false;
        }

        bool FindSite(Unit u, out Hex site)
        {
            site = default;
            int best = int.MaxValue, bestIdx = -1;
            for (int i = 0; i < _s.Site.Length; i++)
            {
                if (_s.Site[i] == SiteType.None || _s.SiteOwner[i] == _me || _siteTargets.Contains(i)) continue;
                var h = _s.HexAt(i);
                if (!u.IsAir && TerrainDefs.MoveCost(_s.Terrain[i], u.Def.MoveClass) < 0) continue;
                // Prefer sites nearer to us than to the enemy; Easy only grabs the close ones.
                int d = Hex.Distance(u.Pos, h) * 2 + Hex.Distance(_myHq, h) - Hex.Distance(_enemyHq, h) / 2;
                if (_diff == AiDifficulty.Easy && Hex.Distance(_myHq, h) > Hex.Distance(_enemyHq, h)) continue;
                if (d < best) { best = d; bestIdx = i; }
            }
            if (bestIdx < 0) return false;
            _siteTargets.Add(bestIdx);
            site = _s.HexAt(bestIdx);
            return true;
        }

        bool IsFree(Unit u, Hex h)
        {
            int i = _s.Index(h);
            bool air = u.IsAir;
            if (_claimed.Contains(i * 2 + (air ? 1 : 0))) return false;
            var other = air ? _occ.Air[i] : _occ.Ground[i];
            return other == null || other.Id == u.Id;
        }

        void Claim(Unit u, Hex h) => _claimed.Add(_s.Index(h) * 2 + (u.IsAir ? 1 : 0));

        /// <summary>Find the best (position, target) pair reachable this round and order it.</summary>
        bool TryAttack(Unit u)
        {
            var def = u.Def;
            bool indirect = def.Has(UnitFlags.Indirect);
            bool stayOnly = indirect || (_diff == AiDifficulty.Easy && _rng.Next(100) < 40);
            var field = stayOnly ? null : Pathfinding.ReachableThisRound(_s, _occ, u, _vis);

            int bestScore = 0, bestTarget = 0; Hex bestPos = u.Pos; int bestDamage = 0;
            Action<Hex, int> consider = (pos, moveCost) =>
            {
                foreach (var e in _enemies)
                {
                    if (def.AttackVs(e.Def.Category) <= 0 || !Rules.InRange(def, pos, e.Pos)) continue;
                    int already = 0;
                    if (_diff != AiDifficulty.Easy) _assigned.TryGetValue(e.Id, out already);
                    int hpLeft = e.Hp - already;
                    if (hpLeft <= 0) continue;
                    int dmg = Rules.UnitVsUnit(_s, u, pos, e, false, false);
                    int score = Math.Min(dmg, hpLeft) * e.Def.Cost / e.Def.MaxHp * 2 + (dmg >= hpLeft ? e.Def.Cost : 0);
                    score = score * 8 - moveCost + CoverBonus(u, pos);
                    if (score > bestScore) { bestScore = score; bestTarget = e.Id; bestPos = pos; bestDamage = dmg; }
                }
                if (def.AtkBuilding <= 0) return;
                foreach (var b in _enemyBuildings)
                {
                    if (!Rules.InRange(def, pos, b.Pos)) continue;
                    _assigned.TryGetValue(b.Id, out int already);
                    int hpLeft = b.Hp - already;
                    if (hpLeft <= 0) continue;
                    int dmg = Rules.UnitVsBuilding(_s, u, pos, b, false);
                    int value = b.Type == BuildingType.Headquarters ? b.Def.Cost * 2 : b.Def.Cost;
                    int score = Math.Min(dmg, hpLeft) * value / b.Def.MaxHp + (dmg >= hpLeft ? value : 0);
                    score = score * 8 - moveCost + CoverBonus(u, pos);
                    if (score > bestScore) { bestScore = score; bestTarget = b.Id; bestPos = pos; bestDamage = dmg; }
                }
            };

            if (IsFree(u, u.Pos)) consider(u.Pos, 0);
            if (field != null)
                for (int i = 0; i < _s.TileCount; i++)
                {
                    if (field.Cost[i] == Pathfinding.Unreachable || field.Cost[i] == 0) continue;
                    var h = _s.HexAt(i);
                    if (IsFree(u, h)) consider(h, field.Cost[i]);
                }

            if (bestTarget == 0) return false;
            if (bestPos != u.Pos && !Try(Order.Move(u.Id, bestPos))) return false;
            Claim(u, bestPos);
            if (!Try(Order.Attack(u.Id, bestTarget))) return true; // moved at least
            _assigned.TryGetValue(bestTarget, out int sum);
            _assigned[bestTarget] = sum + bestDamage;
            return true;
        }

        int CoverBonus(Unit u, Hex pos) =>
            u.IsAir || _diff == AiDifficulty.Easy ? 0 : TerrainDefs.DefensePercent(_s.TerrainAt(pos)) / 10;

        /// <summary>Order the unit as far as it can get this round toward a tile within stopDistance of goal.</summary>
        void MoveToward(Unit u, Hex goal, int stopDistance)
        {
            if (u.Def.Move <= 0) { Claim(u, u.Pos); return; }
            if (Hex.Distance(u.Pos, goal) <= stopDistance && IsFree(u, u.Pos)) { Claim(u, u.Pos); return; }
            var field = Pathfinding.Compute(_s, _occ, u, _vis);

            // Best tile to aim for: reachable, close enough to the goal, cheapest to get to.
            int bestCost = int.MaxValue, bestIdx = -1, bestDist = int.MaxValue;
            for (int i = 0; i < _s.TileCount; i++)
            {
                if (field.Cost[i] == Pathfinding.Unreachable) continue;
                int d = Hex.Distance(_s.HexAt(i), goal);
                if (d < stopDistance) continue;
                // Primary: as close to the goal as allowed. Secondary: cheapest.
                if (d < bestDist || (d == bestDist && field.Cost[i] < bestCost)) { bestDist = d; bestCost = field.Cost[i]; bestIdx = i; }
            }
            if (bestIdx < 0 || bestCost == 0) { Claim(u, u.Pos); return; }

            var path = Pathfinding.Truncate(field, field.PathTo(_s.HexAt(bestIdx)), u.Def.Move);
            // Do not plan to stop on a hex another friendly unit holds or was promised.
            int end = path.Count - 1;
            while (end >= 0 && !IsFree(u, path[end])) end--;
            if (end < 0) { Claim(u, u.Pos); return; }
            if (Try(Order.Move(u.Id, path[end]))) Claim(u, path[end]); else Claim(u, u.Pos);
        }
    }
}
