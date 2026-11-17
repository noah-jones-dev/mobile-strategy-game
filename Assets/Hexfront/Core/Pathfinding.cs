using System.Collections.Generic;

namespace Hexfront.Core
{
    /// <summary>
    /// Dijkstra shortest paths over the hex grid. Ties are always broken the same way
    /// (lowest cost, then first discovered), so every device plans the identical path.
    /// </summary>
    public static class Pathfinding
    {
        public const int Unreachable = int.MaxValue;

        /// <summary>The result of a search from one unit's position.</summary>
        public sealed class Field
        {
            public GameState State;
            public Hex Origin;
            public int[] Cost;
            public int[] Prev;

            public bool CanReach(Hex h) => State.InBounds(h) && Cost[State.Index(h)] != Unreachable;
            public int CostTo(Hex h) => State.InBounds(h) ? Cost[State.Index(h)] : Unreachable;

            /// <summary>Full path to dest, excluding the origin. Empty if unreachable.</summary>
            public List<Hex> PathTo(Hex dest)
            {
                var path = new List<Hex>();
                if (!CanReach(dest) || dest == Origin) return path;
                int i = State.Index(dest), origin = State.Index(Origin);
                while (i != origin && i >= 0)
                {
                    path.Add(State.HexAt(i));
                    i = Prev[i];
                }
                path.Reverse();
                return path;
            }
        }

        /// <summary>
        /// Cost for this unit type to enter a tile, or -1 if it cannot.
        /// <paramref name="knowledge"/> is the planning player (hidden enemies do not block planning),
        /// or -1 to use the true board.
        /// </summary>
        public static int EnterCost(GameState s, Occupancy occ, UnitDef def, int owner, Hex h, bool[] visible, int knowledge)
        {
            if (!s.InBounds(h)) return -1;
            int i = s.Index(h);
            if (def.IsAir)
            {
                var blocker = occ.Air[i];
                if (blocker != null && blocker.Owner != owner && (knowledge < 0 || visible == null || visible[i])) return -1;
                return 1;
            }
            int cost = TerrainDefs.MoveCost(s.Terrain[i], def.MoveClass);
            if (cost < 0) return -1;
            var b = occ.Buildings[i];
            if (b != null && (knowledge < 0 || b.Owner == owner || s.Explored[knowledge][i] || (visible != null && visible[i]))) return -1;
            var g = occ.Ground[i];
            if (g != null && g.Owner != owner && (knowledge < 0 || visible == null || visible[i])) return -1;
            return cost;
        }

        /// <summary>Search outward from the unit. maxCost limits the search (use Unreachable for whole map).</summary>
        public static Field Compute(GameState s, Occupancy occ, Unit unit, bool[] visible, int maxCost = Unreachable)
        {
            int n = s.TileCount;
            var field = new Field { State = s, Origin = unit.Pos, Cost = new int[n], Prev = new int[n] };
            for (int i = 0; i < n; i++) { field.Cost[i] = Unreachable; field.Prev[i] = -1; }
            var def = unit.Def;
            int start = s.Index(unit.Pos);
            field.Cost[start] = 0;

            var heap = new MinHeap();
            heap.Push(0, start);
            while (heap.Count > 0)
            {
                heap.Pop(out int cost, out int idx);
                if (cost > field.Cost[idx]) continue;
                var h = s.HexAt(idx);
                for (int d = 0; d < 6; d++)
                {
                    var nb = h.Neighbor(d);
                    int step = EnterCost(s, occ, def, unit.Owner, nb, visible, unit.Owner);
                    if (step < 0) continue;
                    int nc = cost + step;
                    if (nc > maxCost) continue;
                    int ni = s.Index(nb);
                    if (nc < field.Cost[ni])
                    {
                        field.Cost[ni] = nc;
                        field.Prev[ni] = idx;
                        heap.Push(nc, ni);
                    }
                }
            }
            return field;
        }

        /// <summary>
        /// The steps a unit will attempt this round when ordered to dest: the shortest path,
        /// cut off where its movement points run out. A unit can always take one step.
        /// </summary>
        public static List<Hex> PlanPath(GameState s, Occupancy occ, Unit unit, Hex dest, bool[] visible)
        {
            if (unit.Def.Move <= 0 || !s.InBounds(dest) || dest == unit.Pos) return new List<Hex>();
            var field = Compute(s, occ, unit, visible);
            return Truncate(field, field.PathTo(dest), unit.Def.Move);
        }

        public static List<Hex> Truncate(Field field, List<Hex> full, int movePoints)
        {
            var result = new List<Hex>();
            for (int i = 0; i < full.Count; i++)
            {
                if (i > 0 && field.CostTo(full[i]) > movePoints) break;
                result.Add(full[i]);
            }
            return result;
        }

        /// <summary>Tiles the unit can reach this round, for range display and AI.</summary>
        public static Field ReachableThisRound(GameState s, Occupancy occ, Unit unit, bool[] visible) =>
            Compute(s, occ, unit, visible, unit.Def.Move);

        sealed class MinHeap
        {
            int[] _cost = new int[64];
            int[] _idx = new int[64];
            long[] _seq = new long[64];
            long _counter;
            public int Count;

            bool Less(int a, int b) => _cost[a] != _cost[b] ? _cost[a] < _cost[b] : _seq[a] < _seq[b];

            void Swap(int a, int b)
            {
                int c = _cost[a]; _cost[a] = _cost[b]; _cost[b] = c;
                int i = _idx[a]; _idx[a] = _idx[b]; _idx[b] = i;
                long q = _seq[a]; _seq[a] = _seq[b]; _seq[b] = q;
            }

            public void Push(int cost, int idx)
            {
                if (Count == _cost.Length)
                {
                    System.Array.Resize(ref _cost, Count * 2);
                    System.Array.Resize(ref _idx, Count * 2);
                    System.Array.Resize(ref _seq, Count * 2);
                }
                int i = Count++;
                _cost[i] = cost; _idx[i] = idx; _seq[i] = _counter++;
                while (i > 0)
                {
                    int p = (i - 1) / 2;
                    if (!Less(i, p)) break;
                    Swap(i, p); i = p;
                }
            }

            public void Pop(out int cost, out int idx)
            {
                cost = _cost[0]; idx = _idx[0];
                Count--;
                if (Count > 0)
                {
                    _cost[0] = _cost[Count]; _idx[0] = _idx[Count]; _seq[0] = _seq[Count];
                    int i = 0;
                    while (true)
                    {
                        int l = i * 2 + 1, r = l + 1, m = i;
                        if (l < Count && Less(l, m)) m = l;
                        if (r < Count && Less(r, m)) m = r;
                        if (m == i) break;
                        Swap(i, m); i = m;
                    }
                }
            }
        }
    }
}
