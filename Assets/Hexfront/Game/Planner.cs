using System;
using System.Collections.Generic;
using Hexfront.Core;

namespace Hexfront
{
    /// <summary>
    /// One player's private draft of orders during the planning phase. Every change is checked by
    /// the same OrderValidator the host uses, so the UI can only build legal plans.
    /// </summary>
    public sealed class Planner
    {
        public readonly GameState S;
        public readonly int Player;
        public List<Order> Orders = new List<Order>();
        public OrderValidator V { get; private set; }

        public int SelectedUnit, SelectedBuilding;
        public bool BuildMode;
        public BuildingType BuildType;

        public bool[] Visible => V.Visible;
        public Occupancy Occ => V.Occ;
        public int CreditsLeft => V.CreditsLeft;

        public Planner(GameState s, int player)
        {
            S = s; Player = player;
            Rebuild();
        }

        /// <summary>Re-validate the whole draft from scratch (drops anything that is no longer legal).</summary>
        public void Rebuild()
        {
            V = new OrderValidator(S, Player);
            var keep = new List<Order>();
            foreach (var o in Orders) if (V.TryAdd(o) == null) keep.Add(o);
            Orders = keep;
        }

        public string Add(Order o)
        {
            string err = V.TryAdd(o);
            if (err == null) Orders.Add(o);
            return err;
        }

        public void RemoveAt(int index)
        {
            if (index < 0 || index >= Orders.Count) return;
            Orders.RemoveAt(index);
            Rebuild();
        }

        public void Clear() { Orders.Clear(); Rebuild(); }

        static bool IsUnitAction(OrderType t) =>
            t == OrderType.Attack || t == OrderType.Defend || t == OrderType.Hold || t == OrderType.Repair || t == OrderType.Seize;

        public bool TryGetMove(int unitId, out Order move)
        {
            foreach (var o in Orders) if (o.Type == OrderType.Move && o.ActorId == unitId) { move = o; return true; }
            move = default; return false;
        }

        public bool TryGetAction(int unitId, out Order action)
        {
            foreach (var o in Orders) if (IsUnitAction(o.Type) && o.ActorId == unitId && S.GetUnit(unitId) != null) { action = o; return true; }
            action = default; return false;
        }

        public bool TryGetBuildingOrder(int buildingId, out Order order)
        {
            foreach (var o in Orders)
                if ((o.Type == OrderType.Purchase || o.Type == OrderType.Upgrade || o.Type == OrderType.Repair) && o.ActorId == buildingId && S.GetBuilding(buildingId) != null)
                { order = o; return true; }
            order = default; return false;
        }

        /// <summary>The path the unit will attempt with its current move order (empty if none).</summary>
        public List<Hex> PlannedPath(Unit u) =>
            TryGetMove(u.Id, out var m) ? Pathfinding.PlanPath(S, Occ, u, m.Target, Visible) : new List<Hex>();

        /// <summary>Where the unit expects to be when it fires.</summary>
        public Hex PlannedPos(Unit u)
        {
            var path = PlannedPath(u);
            return path.Count > 0 ? path[path.Count - 1] : u.Pos;
        }

        /// <summary>Apply a change; if it turns out to be illegal, put the draft back exactly as it was.</summary>
        string Transaction(Func<string> change)
        {
            var backup = new List<Order>(Orders);
            string err = change();
            if (err != null) { Orders = backup; Rebuild(); }
            return err;
        }

        public string SetMove(Unit u, Hex dest) => Transaction(() =>
        {
            Orders.RemoveAll(o => o.ActorId == u.Id && S.GetUnit(o.ActorId) != null &&
                (o.Type == OrderType.Move || o.Type == OrderType.Defend || o.Type == OrderType.Hold || o.Type == OrderType.Repair ||
                 (o.Type == OrderType.Attack && u.Def.Has(UnitFlags.Indirect))));
            string why = new OrderValidator(S, Player).TryAdd(Order.Move(u.Id, dest));
            if (why != null) return why;
            // The move must be validated before the unit's other orders, so put it first.
            Orders.Insert(0, Order.Move(u.Id, dest));
            Rebuild();
            return TryGetMove(u.Id, out _) ? null : "Cannot move there";
        });

        public string SetAction(Unit u, Order action) => Transaction(() =>
        {
            Orders.RemoveAll(o => o.ActorId == u.Id && IsUnitAction(o.Type) && S.GetUnit(o.ActorId) != null);
            bool stays = action.Type == OrderType.Defend || action.Type == OrderType.Hold || action.Type == OrderType.Repair;
            if (stays || (action.Type == OrderType.Attack && u.Def.Has(UnitFlags.Indirect)))
                Orders.RemoveAll(o => o.ActorId == u.Id && o.Type == OrderType.Move);
            Rebuild();
            return Add(action);
        });

        public void ClearUnit(int unitId)
        {
            Orders.RemoveAll(o => o.ActorId == unitId && (o.Type == OrderType.Move || IsUnitAction(o.Type)) && S.GetUnit(unitId) != null);
            Rebuild();
        }

        public void ClearBuilding(int buildingId)
        {
            Orders.RemoveAll(o => o.ActorId == buildingId && (o.Type == OrderType.Purchase || o.Type == OrderType.Upgrade || o.Type == OrderType.Repair) && S.GetBuilding(buildingId) != null);
            Rebuild();
        }

        /// <summary>
        /// "Attack that": fire from the planned position if in range; otherwise find the cheapest
        /// reachable hex that is in range, and order move + attack together.
        /// </summary>
        public string Target(Unit u, int targetId, Hex targetPos)
        {
            if (!u.Def.CanAttackAnything) return u.Def.Name + " is unarmed";
            if (Rules.PreviewAttack(S, u, PlannedPos(u), targetId, out _, out _))
                return SetAction(u, Order.Attack(u.Id, targetId));
            if (Rules.PreviewAttack(S, u, u.Pos, targetId, out _, out _))
            {
                // In range from where it stands now: cancel the move and shoot.
                return Transaction(() =>
                {
                    Orders.RemoveAll(o => o.ActorId == u.Id && o.Type == OrderType.Move);
                    Rebuild();
                    return SetAction(u, Order.Attack(u.Id, targetId));
                });
            }
            if (u.Def.Has(UnitFlags.Indirect)) return "Out of range (this unit cannot move and fire)";

            var field = Pathfinding.ReachableThisRound(S, Occ, u, Visible);
            int best = int.MaxValue; Hex bestHex = default;
            for (int i = 0; i < S.TileCount; i++)
            {
                int cost = field.Cost[i];
                if (cost == Pathfinding.Unreachable || cost == 0 || cost >= best) continue;
                var h = S.HexAt(i);
                var blocker = u.IsAir ? Occ.Air[i] : Occ.Ground[i];
                if (blocker != null) continue;
                if (!Rules.InRange(u.Def, h, targetPos)) continue;
                best = cost; bestHex = h;
            }
            if (best == int.MaxValue) return "Target is out of reach this round";
            return Transaction(() =>
            {
                string err = SetMove(u, bestHex);
                return err ?? SetAction(u, Order.Attack(u.Id, targetId));
            });
        }

        public PlayerOrders ToPlayerOrders()
        {
            var po = new PlayerOrders(Player, S.Round);
            po.Orders.AddRange(Orders);
            return po;
        }

        public string Describe(Order o)
        {
            string Actor()
            {
                var u = S.GetUnit(o.ActorId);
                if (u != null) return u.Def.Name;
                var b = S.GetBuilding(o.ActorId);
                return b != null ? b.Def.Name : "?";
            }
            string TargetName()
            {
                var u = S.GetUnit(o.TargetId);
                if (u != null) return u.Def.Name;
                var b = S.GetBuilding(o.TargetId);
                return b != null ? b.Def.Name : "target";
            }
            switch (o.Type)
            {
                case OrderType.Move:
                {
                    int i = S.InBounds(o.Target) ? S.Index(o.Target) : -1;
                    var u = S.GetUnit(o.ActorId);
                    bool capture = i >= 0 && S.Site[i] != SiteType.None && S.SiteOwner[i] != Player && u != null && u.Def.Has(UnitFlags.CanCapture);
                    return Actor() + (capture ? ": capture " + SiteDefs.Name(S.Site[i]) : ": move");
                }
                case OrderType.Attack: return Actor() + ": attack " + TargetName();
                case OrderType.Defend: return Actor() + ": defend";
                case OrderType.Hold: return Actor() + ": hold position";
                case OrderType.Repair: return Actor() + ": repair";
                case OrderType.Seize: return Actor() + ": seize " + TargetName();
                case OrderType.Build: return "Build " + BuildingDefs.Get((BuildingType)o.Param).Name + "  (-" + BuildingDefs.Get((BuildingType)o.Param).Cost + ")";
                case OrderType.Upgrade: return "Upgrade " + Actor();
                case OrderType.Purchase: return "Buy " + UnitDefs.Get((UnitType)o.Param).Name + "  (-" + UnitDefs.Get((UnitType)o.Param).Cost + ")";
            }
            return o.Type.ToString();
        }
    }
}
