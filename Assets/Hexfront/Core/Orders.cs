using System;
using System.Collections.Generic;
using System.IO;

namespace Hexfront.Core
{
    public enum OrderType : byte
    {
        /// <summary>Move toward Target. Ending on a strategic site with a capturing unit captures it.</summary>
        Move,
        /// <summary>Attack the unit or building with id TargetId (after moving, if also ordered to move).</summary>
        Attack,
        /// <summary>Dig in: take less damage, return only half fire. Cannot move.</summary>
        Defend,
        /// <summary>Stay put and fire at will. (Same as giving no order; exists so it can be shown in the plan.)</summary>
        Hold,
        /// <summary>Pay to repair the unit or building with id ActorId.</summary>
        Repair,
        /// <summary>Engineers take over the badly damaged enemy building TargetId.</summary>
        Seize,
        /// <summary>Construct building type Param at Target.</summary>
        Build,
        /// <summary>Upgrade building ActorId.</summary>
        Upgrade,
        /// <summary>Building ActorId produces unit type Param.</summary>
        Purchase,
    }

    public struct Order
    {
        public OrderType Type;
        public int ActorId;
        public Hex Target;
        public int TargetId;
        public int Param;

        public static Order Move(int unitId, Hex dest) => new Order { Type = OrderType.Move, ActorId = unitId, Target = dest };
        public static Order Attack(int unitId, int targetId) => new Order { Type = OrderType.Attack, ActorId = unitId, TargetId = targetId };
        public static Order Defend(int unitId) => new Order { Type = OrderType.Defend, ActorId = unitId };
        public static Order Hold(int unitId) => new Order { Type = OrderType.Hold, ActorId = unitId };
        public static Order Repair(int unitOrBuildingId) => new Order { Type = OrderType.Repair, ActorId = unitOrBuildingId };
        public static Order Seize(int unitId, int buildingId) => new Order { Type = OrderType.Seize, ActorId = unitId, TargetId = buildingId };
        public static Order Build(BuildingType type, Hex at) => new Order { Type = OrderType.Build, Target = at, Param = (int)type };
        public static Order Upgrade(int buildingId) => new Order { Type = OrderType.Upgrade, ActorId = buildingId };
        public static Order Purchase(int buildingId, UnitType unit) => new Order { Type = OrderType.Purchase, ActorId = buildingId, Param = (int)unit };
    }

    /// <summary>One player's private plan for one round.</summary>
    public sealed class PlayerOrders
    {
        public int Player;
        public int Round;
        public readonly List<Order> Orders = new List<Order>();

        public PlayerOrders() { }
        public PlayerOrders(int player, int round) { Player = player; Round = round; }

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write((byte)Player); w.Write(Round); w.Write(Orders.Count);
                foreach (var o in Orders)
                {
                    w.Write((byte)o.Type); w.Write(o.ActorId); w.Write(o.Target.Q); w.Write(o.Target.R);
                    w.Write(o.TargetId); w.Write(o.Param);
                }
                w.Flush();
                return ms.ToArray();
            }
        }

        public static PlayerOrders Deserialize(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var r = new BinaryReader(ms))
            {
                var po = new PlayerOrders { Player = r.ReadByte(), Round = r.ReadInt32() };
                int count = r.ReadInt32();
                if (count < 0 || count > 10000) throw new InvalidDataException("Bad order count");
                for (int i = 0; i < count; i++)
                {
                    var o = new Order { Type = (OrderType)r.ReadByte(), ActorId = r.ReadInt32() };
                    int q = r.ReadInt32(), rr = r.ReadInt32();
                    o.Target = new Hex(q, rr); o.TargetId = r.ReadInt32(); o.Param = r.ReadInt32();
                    po.Orders.Add(o);
                }
                return po;
            }
        }
    }

    /// <summary>
    /// Checks orders one at a time against the start-of-round state, keeping a running budget.
    /// The planning UI, the AI and the authoritative simulation all use this same class, so an
    /// order the UI accepts is exactly an order the host will accept.
    /// </summary>
    public sealed class OrderValidator
    {
        public readonly GameState State;
        public readonly int Player;
        public readonly Occupancy Occ;
        public readonly bool[] Visible;
        public int CreditsLeft;

        readonly HashSet<int> _moved = new HashSet<int>();
        readonly Dictionary<int, OrderType> _action = new Dictionary<int, OrderType>();
        readonly HashSet<int> _buildingBusy = new HashSet<int>();
        readonly HashSet<int> _buildTiles = new HashSet<int>();

        public OrderValidator(GameState state, int player)
        {
            State = state; Player = player;
            Occ = new Occupancy(state);
            Visible = Visibility.Compute(state, player);
            CreditsLeft = state.Players[player].Credits;
        }

        /// <summary>Validates and records the order. Returns null on success, else why it was refused.</summary>
        public string TryAdd(Order o)
        {
            string err = Check(o, out int cost);
            if (err != null) return err;
            CreditsLeft -= cost;
            switch (o.Type)
            {
                case OrderType.Move: _moved.Add(o.ActorId); break;
                case OrderType.Build: _buildTiles.Add(State.Index(o.Target)); break;
                case OrderType.Upgrade:
                case OrderType.Purchase: _buildingBusy.Add(o.ActorId); break;
                case OrderType.Repair:
                    if (State.GetUnit(o.ActorId) != null) _action[o.ActorId] = o.Type; else _buildingBusy.Add(-o.ActorId);
                    break;
                default: _action[o.ActorId] = o.Type; break;
            }
            return null;
        }

        string Check(Order o, out int cost)
        {
            cost = 0;
            var s = State;
            if (s.IsOver) return "The match is over";
            switch (o.Type)
            {
                case OrderType.Move:
                {
                    var u = OwnUnit(o.ActorId, out string e); if (u == null) return e;
                    if (u.Def.Move <= 0) return "This unit cannot move";
                    if (_moved.Contains(u.Id)) return "Unit already has a move order";
                    if (_action.TryGetValue(u.Id, out var act))
                    {
                        if (act == OrderType.Defend || act == OrderType.Hold || act == OrderType.Repair) return "Unit is ordered to stay in place";
                        if (act == OrderType.Attack && u.Def.Has(UnitFlags.Indirect)) return u.Def.Name + " cannot move and fire in the same round";
                    }
                    if (!s.InBounds(o.Target) || o.Target == u.Pos) return "Invalid destination";
                    if (Pathfinding.PlanPath(s, Occ, u, o.Target, Visible).Count == 0) return "No route to that hex";
                    return null;
                }
                case OrderType.Attack:
                {
                    var u = OwnUnit(o.ActorId, out string e); if (u == null) return e;
                    if (_action.ContainsKey(u.Id)) return "Unit already has an action this round";
                    if (u.Def.Has(UnitFlags.Indirect) && _moved.Contains(u.Id)) return u.Def.Name + " cannot move and fire in the same round";
                    var tu = s.GetUnit(o.TargetId);
                    if (tu != null)
                    {
                        if (tu.Owner == Player) return "Cannot attack your own unit";
                        if (!Visible[s.Index(tu.Pos)]) return "Target is not visible";
                        if (u.Def.AttackVs(tu.Def.Category) <= 0) return u.Def.Name + " cannot attack " + tu.Def.Name;
                        return null;
                    }
                    var tb = s.GetBuilding(o.TargetId);
                    if (tb == null) return "Target not found";
                    if (tb.Owner == Player) return "Cannot attack your own building";
                    int ti = s.Index(tb.Pos);
                    if (!Visible[ti] && !s.Explored[Player][ti]) return "Target is not visible";
                    if (u.Def.AtkBuilding <= 0) return u.Def.Name + " cannot attack buildings";
                    return null;
                }
                case OrderType.Defend:
                case OrderType.Hold:
                {
                    var u = OwnUnit(o.ActorId, out string e); if (u == null) return e;
                    if (_action.ContainsKey(u.Id)) return "Unit already has an action this round";
                    if (_moved.Contains(u.Id)) return "Unit is ordered to move";
                    return null;
                }
                case OrderType.Repair:
                {
                    var u = s.GetUnit(o.ActorId);
                    if (u != null)
                    {
                        if (u.Owner != Player) return "Not your unit";
                        if (_action.ContainsKey(u.Id)) return "Unit already has an action this round";
                        if (_moved.Contains(u.Id)) return "Unit is ordered to move";
                        if (u.Hp >= u.Def.MaxHp) return "Unit is at full health";
                        if (!Rules.HasRepairSupport(s, u)) return "Must be next to a friendly building or supply unit";
                        cost = Rules.RepairCost(u);
                        return cost > CreditsLeft ? "Not enough Credits" : null;
                    }
                    var b = s.GetBuilding(o.ActorId);
                    if (b == null) return "Nothing to repair";
                    if (b.Owner != Player) return "Not your building";
                    if (!b.IsComplete) return "Still under construction";
                    if (b.Hp >= b.Def.MaxHp) return "Building is undamaged";
                    if (_buildingBusy.Contains(-b.Id)) return "Already being repaired";
                    cost = Rules.RepairCost(b);
                    return cost > CreditsLeft ? "Not enough Credits" : null;
                }
                case OrderType.Seize:
                {
                    var u = OwnUnit(o.ActorId, out string e); if (u == null) return e;
                    if (!u.Def.Has(UnitFlags.Engineer)) return "Only Engineers can seize buildings";
                    if (_action.ContainsKey(u.Id)) return "Unit already has an action this round";
                    var b = s.GetBuilding(o.TargetId);
                    if (b == null || b.Owner == Player) return "Invalid target";
                    if (b.Type == BuildingType.Headquarters) return "A Headquarters cannot be seized";
                    int ti = s.Index(b.Pos);
                    if (!Visible[ti] && !s.Explored[Player][ti]) return "Target is not visible";
                    return null;
                }
                case OrderType.Build:
                {
                    if (o.Param < 0 || o.Param >= BuildingDefs.Count) return "Unknown building";
                    var def = BuildingDefs.Get((BuildingType)o.Param);
                    string e = Rules.CheckBuildSite(s, Occ, Player, o.Target);
                    if (e != null) return e;
                    if (_buildTiles.Contains(s.Index(o.Target))) return "Already building there";
                    cost = def.Cost;
                    return cost > CreditsLeft ? "Not enough Credits" : null;
                }
                case OrderType.Upgrade:
                {
                    var b = s.GetBuilding(o.ActorId);
                    if (b == null || b.Owner != Player) return "Not your building";
                    if (!b.IsOperational) return "Building is busy";
                    if (b.Level >= b.Def.MaxLevel) return "Already at maximum level";
                    if (_buildingBusy.Contains(b.Id)) return "Building already has an order";
                    cost = Rules.UpgradeCost(b);
                    return cost > CreditsLeft ? "Not enough Credits" : null;
                }
                case OrderType.Purchase:
                {
                    var b = s.GetBuilding(o.ActorId);
                    if (b == null || b.Owner != Player) return "Not your building";
                    if (!b.IsOperational) return "Building is not operational";
                    if (o.Param < 0 || o.Param >= UnitDefs.Count) return "Unknown unit";
                    var def = UnitDefs.Get((UnitType)o.Param);
                    if (def.Producer != b.Type) return b.Def.Name + " cannot produce " + def.Name;
                    if (b.Level < def.ProducerLevel) return "Requires " + b.Def.Name + " level " + def.ProducerLevel;
                    if (_buildingBusy.Contains(b.Id)) return "One order per building each round";
                    if (!Rules.FindSpawnHex(s, Occ, b, def, out _)) return "No free space next to the building";
                    cost = def.Cost;
                    return cost > CreditsLeft ? "Not enough Credits" : null;
                }
            }
            return "Unknown order";
        }

        Unit OwnUnit(int id, out string error)
        {
            var u = State.GetUnit(id);
            error = u == null ? "Unit not found" : (u.Owner != Player ? "Not your unit" : null);
            return error == null ? u : null;
        }

        public bool HasMoved(int unitId) => _moved.Contains(unitId);
        public bool HasAction(int unitId) => _action.ContainsKey(unitId);

        /// <summary>Validate a whole list; returns the accepted orders and reports refusals.</summary>
        public static List<Order> Filter(GameState s, int player, IEnumerable<Order> orders, out int creditsLeft, Action<Order, string> onRejected = null)
        {
            var v = new OrderValidator(s, player);
            var ok = new List<Order>();
            foreach (var o in orders)
            {
                string err = v.TryAdd(o);
                if (err == null) ok.Add(o); else onRejected?.Invoke(o, err);
            }
            creditsLeft = v.CreditsLeft;
            return ok;
        }
    }

    /// <summary>Stateless rule helpers shared by validation, simulation, UI previews and AI.</summary>
    public static class Rules
    {
        public static int RepairCost(Unit u) => Math.Max(1, u.Def.Cost * Balance.UnitRepairCostPercent / 100);
        public static int RepairCost(Building b) => Math.Max(1, b.Def.Cost * Balance.BuildingRepairCostPercent / 100);
        public static int UpgradeCost(Building b) => b.Def.UpgradeCost * b.Level;

        /// <summary>A unit can be repaired when adjacent to a friendly finished building or supply unit.</summary>
        public static bool HasRepairSupport(GameState s, Unit u)
        {
            foreach (var b in s.Buildings)
                if (b.Owner == u.Owner && b.IsComplete && Hex.Distance(b.Pos, u.Pos) <= 1) return true;
            foreach (var o in s.Units)
                if (o.Owner == u.Owner && o.Id != u.Id && o.Def.Has(UnitFlags.Supply) && Hex.Distance(o.Pos, u.Pos) <= 1) return true;
            return false;
        }

        /// <summary>Null if the player may build on this hex, otherwise the reason not.</summary>
        public static string CheckBuildSite(GameState s, Occupancy occ, int player, Hex h)
        {
            if (!s.InBounds(h)) return "Outside the map";
            int i = s.Index(h);
            if (s.Territory[i] != player) return "Must build inside your territory";
            if (!TerrainDefs.IsBuildable(s.Terrain[i])) return "Cannot build on " + TerrainDefs.Name(s.Terrain[i]);
            if (s.Site[i] != SiteType.None) return "Cannot build on a strategic site";
            if (occ.Buildings[i] != null) return "Hex already has a building";
            if (occ.Ground[i] != null) return "A unit is in the way";
            return null;
        }

        /// <summary>Where a newly produced unit appears: the first free hex around the producer, in fixed order.</summary>
        public static bool FindSpawnHex(GameState s, Occupancy occ, Building b, UnitDef def, out Hex spawn)
        {
            if (def.IsAir && occ.Air[s.Index(b.Pos)] == null) { spawn = b.Pos; return true; }
            for (int d = 0; d < 6; d++)
            {
                var h = b.Pos.Neighbor(d);
                if (!s.InBounds(h)) continue;
                int i = s.Index(h);
                if (def.IsAir)
                {
                    if (occ.Air[i] == null) { spawn = h; return true; }
                    continue;
                }
                if (TerrainDefs.MoveCost(s.Terrain[i], def.MoveClass) < 0) continue;
                if (occ.Ground[i] != null || occ.Buildings[i] != null) continue;
                spawn = h; return true;
            }
            spawn = default;
            return false;
        }

        public static List<UnitDef> ProductionOptions(Building b)
        {
            var list = new List<UnitDef>();
            foreach (var d in UnitDefs.All) if (d.Producer == b.Type) list.Add(d);
            return list;
        }

        public static bool InRange(UnitDef def, Hex from, Hex to)
        {
            int d = Hex.Distance(from, to);
            return d >= def.MinRange && d <= def.MaxRange && def.MaxRange > 0;
        }

        public static bool HasCommandAura(GameState s, int owner, int selfId, Hex pos)
        {
            foreach (var o in s.Units)
                if (o.Owner == owner && o.Id != selfId && o.Def.Has(UnitFlags.Command) && Hex.Distance(o.Pos, pos) <= 1) return true;
            return false;
        }

        // ------------------------------------------------------------ damage

        /// <summary>
        /// Core damage formula (before the +/-10% roll). Every step is integer maths.
        /// atk: attack value versus this target type. hp/maxHp: attacker health (wounded units hit weaker).
        /// </summary>
        static int Damage(int atk, int hp, int maxHp, int armor, int percent)
        {
            if (atk <= 0) return 0;
            int raw = atk * (50 + 50 * hp / maxHp) / 100;
            raw = raw * 100 / (100 + armor);
            return Math.Max(1, raw * Math.Max(10, percent) / 100);
        }

        /// <summary>Expected damage of a unit (standing at attackerPos) against an enemy unit.</summary>
        public static int UnitVsUnit(GameState s, Unit a, Hex attackerPos, Unit t, bool attackerDefending, bool targetDefending)
        {
            var ad = a.Def; var td = t.Def;
            int pct = 100;
            if (HasCommandAura(s, a.Owner, a.Id, attackerPos)) pct += Balance.CommandBonusPercent;
            if (attackerDefending) pct = pct * Balance.DefendAttackPercent / 100;
            pct += DirectFireModifier(s, ad, attackerPos, t.Pos, td.IsAir);
            int cover = 0;
            if (!td.IsAir)
            {
                cover = TerrainDefs.DefensePercent(s.TerrainAt(t.Pos));
                if (td.Category == UnitCategory.Vehicle) cover = cover * Balance.VehicleCoverPercent / 100;
            }
            if (targetDefending) cover += Balance.DefendBonusPercent;
            pct = pct * (100 - cover) / 100;
            return Damage(ad.AttackVs(td.Category), a.Hp, ad.MaxHp, td.Armor, pct);
        }

        public static int UnitVsBuilding(GameState s, Unit a, Hex attackerPos, Building t, bool attackerDefending)
        {
            var ad = a.Def;
            int pct = 100;
            if (HasCommandAura(s, a.Owner, a.Id, attackerPos)) pct += Balance.CommandBonusPercent;
            if (attackerDefending) pct = pct * Balance.DefendAttackPercent / 100;
            pct += DirectFireModifier(s, ad, attackerPos, t.Pos, false);
            return Damage(ad.AtkBuilding, a.Hp, ad.MaxHp, t.Def.Armor, pct);
        }

        public static int TurretVsUnit(GameState s, Building turret, Unit t, bool targetDefending)
        {
            var bd = turret.Def; var td = t.Def;
            int atk = td.Category == UnitCategory.Infantry ? bd.AtkInfantry : td.Category == UnitCategory.Vehicle ? bd.AtkVehicle : bd.AtkAir;
            atk = atk * (100 + Balance.TurretLevelBonusPercent * (turret.Level - 1)) / 100;
            int cover = 0;
            if (!td.IsAir)
            {
                cover = TerrainDefs.DefensePercent(s.TerrainAt(t.Pos));
                if (td.Category == UnitCategory.Vehicle) cover = cover * Balance.VehicleCoverPercent / 100;
            }
            if (targetDefending) cover += Balance.DefendBonusPercent;
            return Damage(atk, turret.Hp, bd.MaxHp, td.Armor, 100 - cover);
        }

        /// <summary>Range penalty and high-ground bonus. Indirect fire and aircraft ignore both.</summary>
        static int DirectFireModifier(GameState s, UnitDef ad, Hex from, Hex to, bool targetIsAir)
        {
            if (ad.Has(UnitFlags.Indirect)) return 0;
            int mod = 0;
            int dist = Hex.Distance(from, to);
            if (dist > 1) mod -= Balance.RangePenaltyPercent * (dist - 1);
            if (!ad.IsAir && !targetIsAir)
            {
                int ea = s.Elevation[s.Index(from)], et = s.Elevation[s.Index(to)];
                if (ea > et) mod += Balance.ElevationBonusPercent;
                else if (ea < et) mod -= Balance.ElevationBonusPercent;
            }
            return mod;
        }

        /// <summary>Applies the deterministic +/-variance roll for this attacker/target pair this round.</summary>
        public static int Roll(GameState s, int baseDamage, int attackerId, int targetId)
        {
            if (baseDamage <= 0) return 0;
            int span = Balance.DamageVariancePercent * 2 + 1;
            int v = (int)(Rng.Hash(s.Seed, s.Round, attackerId, targetId) % (uint)span) - Balance.DamageVariancePercent;
            return Math.Max(1, baseDamage * (100 + v) / 100);
        }

        /// <summary>
        /// Damage preview for the planning UI: the unit attacks from <paramref name="from"/> (its planned
        /// position). Returns false if the attack is impossible from there.
        /// </summary>
        public static bool PreviewAttack(GameState s, Unit attacker, Hex from, int targetId, out int min, out int max)
        {
            min = max = 0;
            int dmg;
            var tu = s.GetUnit(targetId);
            if (tu != null)
            {
                if (!InRange(attacker.Def, from, tu.Pos) || attacker.Def.AttackVs(tu.Def.Category) <= 0) return false;
                dmg = UnitVsUnit(s, attacker, from, tu, false, false);
            }
            else
            {
                var tb = s.GetBuilding(targetId);
                if (tb == null || !InRange(attacker.Def, from, tb.Pos) || attacker.Def.AtkBuilding <= 0) return false;
                dmg = UnitVsBuilding(s, attacker, from, tb, false);
            }
            min = Math.Max(1, dmg * (100 - Balance.DamageVariancePercent) / 100);
            max = Math.Max(1, dmg * (100 + Balance.DamageVariancePercent) / 100);
            return true;
        }
    }
}
