using System;
using System.Collections.Generic;
using System.IO;

namespace Hexfront.Core
{
    public sealed class Unit
    {
        public int Id;
        public UnitType Type;
        public int Owner;
        public Hex Pos;
        public int Hp;
        public int Fuel;

        public UnitDef Def => UnitDefs.Get(Type);
        public bool IsAir => Def.IsAir;
    }

    public sealed class Building
    {
        public int Id;
        public BuildingType Type;
        public int Owner;
        public Hex Pos;
        public int Hp;
        public int Level = 1;
        /// <summary>Rounds until construction completes. 0 = operational.</summary>
        public int BuildRoundsLeft;
        /// <summary>Rounds until an upgrade completes. The building is offline while upgrading.</summary>
        public int UpgradeRoundsLeft;

        public BuildingDef Def => BuildingDefs.Get(Type);
        public bool IsComplete => BuildRoundsLeft == 0;
        public bool IsOperational => BuildRoundsLeft == 0 && UpgradeRoundsLeft == 0;

        public int Income => IsComplete ? Def.Income + Def.IncomePerLevel * (Level - 1) : 0;

        public int Vision
        {
            get
            {
                int v = Def.Vision;
                if (Type == BuildingType.RadarStation && IsComplete) v += Balance.RadarLevelBonus * (Level - 1);
                return IsComplete ? v : 1;
            }
        }
    }

    public sealed class PlayerState
    {
        public int Credits;
        /// <summary>Income received at the end of the previous round (for display).</summary>
        public int LastIncome;
        public int UnitsLost;
        public int UnitsKilled;
    }

    /// <summary>
    /// The complete, authoritative state of a match. Contains no Unity types and no floating
    /// point, so it can be simulated, hashed, saved and sent over the network identically
    /// on every device.
    /// </summary>
    public sealed class GameState
    {
        public const int NoWinner = -1;
        public const int Draw = 2;
        public const int PlayerCount = 2;

        public int Width, Height;
        public TerrainType[] Terrain;
        public byte[] Elevation;
        public SiteType[] Site;
        public sbyte[] SiteOwner;      // -1 = neutral
        public sbyte[] Territory;      // -1 = nobody; recomputed every round
        public bool[][] Explored;      // [player][tile]

        public readonly List<Unit> Units = new List<Unit>();
        public readonly List<Building> Buildings = new List<Building>();
        public readonly PlayerState[] Players = { new PlayerState(), new PlayerState() };

        public uint Seed;
        public int Round = 1;
        public int NextId = 1;
        public int Winner = NoWinner;
        public bool FogOfWar = true;
        /// <summary>0 = no limit. When reached, the player with the higher score wins.</summary>
        public int MaxRounds;
        public string MapName = "";
        /// <summary>Which biome art to use (a Region value). Cosmetic only.</summary>
        public byte Region;

        public int TileCount => Width * Height;
        public bool IsOver => Winner != NoWinner;

        public void InitMap(int width, int height)
        {
            Width = width; Height = height;
            int n = width * height;
            Terrain = new TerrainType[n];
            Elevation = new byte[n];
            Site = new SiteType[n];
            SiteOwner = new sbyte[n];
            Territory = new sbyte[n];
            Explored = new[] { new bool[n], new bool[n] };
            for (int i = 0; i < n; i++) { SiteOwner[i] = -1; Territory[i] = -1; }
        }

        // ---------------------------------------------------------------- grid helpers

        public bool InBounds(Hex h)
        {
            int col = h.Col;
            return h.R >= 0 && h.R < Height && col >= 0 && col < Width;
        }

        public int Index(Hex h) => h.R * Width + h.Col;
        public Hex HexAt(int index) => Hex.FromOffset(index % Width, index / Width);

        /// <summary>The tile's exact point-mirror. Maps are generated symmetric under this for fairness.</summary>
        public Hex Mirror(Hex h) => Hex.FromOffset(Width - 1 - h.Col, Height - 1 - h.Row);

        public TerrainType TerrainAt(Hex h) => Terrain[Index(h)];

        // ---------------------------------------------------------------- entity lookup

        public Unit GetUnit(int id)
        {
            for (int i = 0; i < Units.Count; i++) if (Units[i].Id == id) return Units[i];
            return null;
        }

        public Building GetBuilding(int id)
        {
            for (int i = 0; i < Buildings.Count; i++) if (Buildings[i].Id == id) return Buildings[i];
            return null;
        }

        public Unit UnitAt(Hex h, bool airLayer)
        {
            for (int i = 0; i < Units.Count; i++)
            {
                var u = Units[i];
                if (u.Pos == h && u.IsAir == airLayer) return u;
            }
            return null;
        }

        public Building BuildingAt(Hex h)
        {
            for (int i = 0; i < Buildings.Count; i++) if (Buildings[i].Pos == h) return Buildings[i];
            return null;
        }

        public Unit AddUnit(UnitType type, int owner, Hex pos)
        {
            var def = UnitDefs.Get(type);
            var u = new Unit { Id = NextId++, Type = type, Owner = owner, Pos = pos, Hp = def.MaxHp, Fuel = def.Fuel };
            Units.Add(u);
            return u;
        }

        public Building AddBuilding(BuildingType type, int owner, Hex pos, bool complete)
        {
            var def = BuildingDefs.Get(type);
            var b = new Building
            {
                Id = NextId++, Type = type, Owner = owner, Pos = pos,
                Hp = complete ? def.MaxHp : Math.Max(1, def.MaxHp / 2),
                BuildRoundsLeft = complete ? 0 : def.BuildRounds,
            };
            Buildings.Add(b);
            return b;
        }

        public Building FindHeadquarters(int player)
        {
            Building best = null;
            foreach (var b in Buildings)
                if (b.Owner == player && b.Type == BuildingType.Headquarters && (best == null || b.Id < best.Id)) best = b;
            return best;
        }

        // ---------------------------------------------------------------- economy / score

        public int ComputeIncome(int player)
        {
            int income = 0;
            foreach (var b in Buildings) if (b.Owner == player) income += b.Income;
            for (int i = 0; i < Site.Length; i++)
                if (Site[i] != SiteType.None && SiteOwner[i] == player) income += SiteDefs.Income(Site[i]);
            return income;
        }

        /// <summary>Used to pick a winner when the round limit is reached.</summary>
        public int Score(int player)
        {
            int score = Players[player].Credits / 2;
            foreach (var u in Units) if (u.Owner == player) score += u.Def.Cost * u.Hp / u.Def.MaxHp;
            foreach (var b in Buildings) if (b.Owner == player) score += b.Def.Cost * b.Hp / b.Def.MaxHp;
            score += ComputeIncome(player) * 5;
            return score;
        }

        // ---------------------------------------------------------------- territory

        /// <summary>
        /// Each tile belongs to the player whose completed building (or captured site) is closest
        /// and in radius. Tiles equally close to both players belong to nobody.
        /// </summary>
        public void RecomputeTerritory()
        {
            int n = TileCount;
            var best = new int[n * 2];
            for (int i = 0; i < best.Length; i++) best[i] = int.MaxValue;

            foreach (var b in Buildings)
                if (b.IsComplete) Spread(best, b.Owner, b.Pos, b.Def.TerritoryRadius);
            for (int i = 0; i < n; i++)
                if (Site[i] != SiteType.None && SiteOwner[i] >= 0) Spread(best, SiteOwner[i], HexAt(i), Balance.SiteTerritoryRadius);

            for (int i = 0; i < n; i++)
            {
                int a = best[i * 2], c = best[i * 2 + 1];
                Territory[i] = a == c ? (sbyte)-1 : (a < c ? (sbyte)0 : (sbyte)1);
            }
        }

        void Spread(int[] best, int owner, Hex center, int radius)
        {
            for (int dq = -radius; dq <= radius; dq++)
            {
                int rMin = Math.Max(-radius, -dq - radius), rMax = Math.Min(radius, -dq + radius);
                for (int dr = rMin; dr <= rMax; dr++)
                {
                    var h = new Hex(center.Q + dq, center.R + dr);
                    if (!InBounds(h)) continue;
                    int d = Hex.Distance(center, h);
                    int k = Index(h) * 2 + owner;
                    if (d < best[k]) best[k] = d;
                }
            }
        }

        // ---------------------------------------------------------------- serialization

        const int FormatVersion = 3;

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(FormatVersion);
                w.Write(Width); w.Write(Height);
                int n = TileCount;
                for (int i = 0; i < n; i++)
                {
                    w.Write((byte)Terrain[i]); w.Write(Elevation[i]); w.Write((byte)Site[i]);
                    w.Write(SiteOwner[i]); w.Write(Territory[i]);
                    w.Write((byte)((Explored[0][i] ? 1 : 0) | (Explored[1][i] ? 2 : 0)));
                }
                w.Write(Seed); w.Write(Round); w.Write(NextId); w.Write(Winner);
                w.Write(FogOfWar); w.Write(MaxRounds); w.Write(MapName ?? ""); w.Write(Region);
                foreach (var p in Players)
                {
                    w.Write(p.Credits); w.Write(p.LastIncome); w.Write(p.UnitsLost); w.Write(p.UnitsKilled);
                }
                w.Write(Units.Count);
                foreach (var u in Units)
                {
                    w.Write(u.Id); w.Write((byte)u.Type); w.Write((byte)u.Owner);
                    w.Write(u.Pos.Q); w.Write(u.Pos.R); w.Write(u.Hp); w.Write(u.Fuel);
                }
                w.Write(Buildings.Count);
                foreach (var b in Buildings)
                {
                    w.Write(b.Id); w.Write((byte)b.Type); w.Write((byte)b.Owner);
                    w.Write(b.Pos.Q); w.Write(b.Pos.R); w.Write(b.Hp); w.Write(b.Level);
                    w.Write(b.BuildRoundsLeft); w.Write(b.UpgradeRoundsLeft);
                }
                w.Flush();
                return ms.ToArray();
            }
        }

        public static GameState Deserialize(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var r = new BinaryReader(ms))
            {
                int version = r.ReadInt32();
                if (version != FormatVersion) throw new InvalidDataException("Unsupported save version " + version);
                var s = new GameState();
                int width = r.ReadInt32(), height = r.ReadInt32();
                if (width < 1 || height < 1 || width > 200 || height > 200) throw new InvalidDataException("Bad map size");
                s.InitMap(width, height);
                int n = s.TileCount;
                for (int i = 0; i < n; i++)
                {
                    s.Terrain[i] = (TerrainType)r.ReadByte(); s.Elevation[i] = r.ReadByte(); s.Site[i] = (SiteType)r.ReadByte();
                    s.SiteOwner[i] = r.ReadSByte(); s.Territory[i] = r.ReadSByte();
                    byte e = r.ReadByte();
                    s.Explored[0][i] = (e & 1) != 0; s.Explored[1][i] = (e & 2) != 0;
                }
                s.Seed = r.ReadUInt32(); s.Round = r.ReadInt32(); s.NextId = r.ReadInt32(); s.Winner = r.ReadInt32();
                s.FogOfWar = r.ReadBoolean(); s.MaxRounds = r.ReadInt32(); s.MapName = r.ReadString(); s.Region = r.ReadByte();
                foreach (var p in s.Players)
                {
                    p.Credits = r.ReadInt32(); p.LastIncome = r.ReadInt32(); p.UnitsLost = r.ReadInt32(); p.UnitsKilled = r.ReadInt32();
                }
                int units = r.ReadInt32();
                for (int i = 0; i < units; i++)
                {
                    var u = new Unit { Id = r.ReadInt32(), Type = (UnitType)r.ReadByte(), Owner = r.ReadByte() };
                    int q = r.ReadInt32(), rr = r.ReadInt32();
                    u.Pos = new Hex(q, rr); u.Hp = r.ReadInt32(); u.Fuel = r.ReadInt32();
                    s.Units.Add(u);
                }
                int buildings = r.ReadInt32();
                for (int i = 0; i < buildings; i++)
                {
                    var b = new Building { Id = r.ReadInt32(), Type = (BuildingType)r.ReadByte(), Owner = r.ReadByte() };
                    int q = r.ReadInt32(), rr = r.ReadInt32();
                    b.Pos = new Hex(q, rr); b.Hp = r.ReadInt32(); b.Level = r.ReadInt32();
                    b.BuildRoundsLeft = r.ReadInt32(); b.UpgradeRoundsLeft = r.ReadInt32();
                    s.Buildings.Add(b);
                }
                return s;
            }
        }

        public GameState Clone() => Deserialize(Serialize());

        /// <summary>FNV-1a hash of the whole state. Two devices in sync always produce the same value.</summary>
        public uint Hash()
        {
            var data = Serialize();
            uint h = 2166136261u;
            for (int i = 0; i < data.Length; i++) { h ^= data[i]; h *= 16777619u; }
            return h;
        }
    }

    /// <summary>Fast "what is on this tile" lookup, built once per calculation.</summary>
    public sealed class Occupancy
    {
        public readonly Unit[] Ground;
        public readonly Unit[] Air;
        public readonly Building[] Buildings;
        readonly GameState _s;

        public Occupancy(GameState s)
        {
            _s = s;
            Ground = new Unit[s.TileCount];
            Air = new Unit[s.TileCount];
            Buildings = new Building[s.TileCount];
            foreach (var u in s.Units) (u.IsAir ? Air : Ground)[s.Index(u.Pos)] = u;
            foreach (var b in s.Buildings) Buildings[s.Index(b.Pos)] = b;
        }

        public Unit UnitAt(Hex h, bool air) => _s.InBounds(h) ? (air ? Air : Ground)[_s.Index(h)] : null;
        public Building BuildingAt(Hex h) => _s.InBounds(h) ? Buildings[_s.Index(h)] : null;
    }

    public static class Visibility
    {
        /// <summary>Which tiles the player can currently see.</summary>
        public static bool[] Compute(GameState s, int player)
        {
            var vis = new bool[s.TileCount];
            if (!s.FogOfWar)
            {
                for (int i = 0; i < vis.Length; i++) vis[i] = true;
                return vis;
            }
            foreach (var u in s.Units)
            {
                if (u.Owner != player) continue;
                int range = u.Def.Vision;
                if (!u.IsAir)
                {
                    int i = s.Index(u.Pos);
                    if (s.Terrain[i] == TerrainType.Mountain || s.Elevation[i] >= 2) range += Balance.HighGroundVisionBonus;
                }
                Reveal(s, vis, u.Pos, range);
            }
            foreach (var b in s.Buildings)
                if (b.Owner == player) Reveal(s, vis, b.Pos, b.Vision);
            for (int i = 0; i < s.Site.Length; i++)
                if (s.Site[i] != SiteType.None && s.SiteOwner[i] == player) Reveal(s, vis, s.HexAt(i), Balance.SiteVision);
            return vis;
        }

        static void Reveal(GameState s, bool[] vis, Hex center, int radius)
        {
            for (int dq = -radius; dq <= radius; dq++)
            {
                int rMin = Math.Max(-radius, -dq - radius), rMax = Math.Min(radius, -dq + radius);
                for (int dr = rMin; dr <= rMax; dr++)
                {
                    var h = new Hex(center.Q + dq, center.R + dr);
                    if (s.InBounds(h)) vis[s.Index(h)] = true;
                }
            }
        }

        /// <summary>Remember everything currently visible so terrain stays revealed later.</summary>
        public static void UpdateExplored(GameState s)
        {
            for (int p = 0; p < GameState.PlayerCount; p++)
            {
                var vis = Compute(s, p);
                var ex = s.Explored[p];
                for (int i = 0; i < vis.Length; i++) if (vis[i]) ex[i] = true;
            }
        }
    }
}
