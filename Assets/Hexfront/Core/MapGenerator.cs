using System;
using System.Collections.Generic;

namespace Hexfront.Core
{
    public enum Region : byte { Grasslands, Desert, Arctic, Jungle, Mountains, Islands }

    /// <summary>Everything needed to (re)create a match setup. Sharing the code string shares the map.</summary>
    public sealed class MapConfig
    {
        /// <summary>Index into PremadeMaps.All, or -1 for a procedurally generated map.</summary>
        public int Premade = -1;
        public int Width = 24, Height = 16;
        public Region Region = Region.Grasslands;
        public int TerrainDensity = 50;   // 0..100: forests, hills and mountains
        public int WaterCoverage = 15;    // 0..100 (percent of tiles, before region adjustment)
        public int ResourceDensity = 50;  // 0..100: richer sites
        public int Objectives = 4;        // pairs of neutral strategic sites
        public int StartDistance = 1;     // 0 close, 1 medium, 2 far
        public uint Seed = 1;

        // Match rules (not part of the map code).
        public int StartingCredits = 600;
        public bool FogOfWar = true;
        public int MaxRounds;

        public static readonly string[] RegionNames = { "Grasslands", "Desert", "Arctic", "Tropical Jungle", "Mountainous", "Coastal Islands" };

        public void Clamp()
        {
            Width = Math.Max(12, Math.Min(40, Width));
            Height = Math.Max(10, Math.Min(30, Height));
            if ((Height & 1) == 1) Height++; // even height makes the point-mirror exact on a hex grid
            TerrainDensity = Math.Max(0, Math.Min(100, TerrainDensity));
            WaterCoverage = Math.Max(0, Math.Min(100, WaterCoverage));
            ResourceDensity = Math.Max(0, Math.Min(100, ResourceDensity));
            Objectives = Math.Max(1, Math.Min(8, Objectives));
            StartDistance = Math.Max(0, Math.Min(2, StartDistance));
            if ((int)Region > 5) Region = Region.Grasslands;
        }

        /// <summary>A short text code players can read out or message to each other.</summary>
        public string ToCode()
        {
            if (Premade >= 0) return "HF-P" + Premade;
            return "HF-" + (int)Region + "-" + Width + "-" + Height + "-" + TerrainDensity + "-" + WaterCoverage + "-" +
                   ResourceDensity + "-" + Objectives + "-" + StartDistance + "-" + Seed;
        }

        public static bool TryParse(string code, out MapConfig cfg)
        {
            cfg = new MapConfig();
            if (string.IsNullOrEmpty(code)) return false;
            var parts = code.Trim().ToUpperInvariant().Split('-');
            if (parts.Length < 2 || parts[0] != "HF") return false;
            if (parts.Length == 2 && parts[1].StartsWith("P"))
            {
                if (!int.TryParse(parts[1].Substring(1), out int idx) || idx < 0 || idx >= PremadeMaps.All.Length) return false;
                cfg.Premade = idx;
                return true;
            }
            if (parts.Length != 10) return false;
            var v = new int[8];
            for (int i = 0; i < 8; i++) if (!int.TryParse(parts[i + 1], out v[i])) return false;
            if (!uint.TryParse(parts[9], out uint seed)) return false;
            cfg.Region = (Region)Math.Max(0, Math.Min(5, v[0]));
            cfg.Width = v[1]; cfg.Height = v[2]; cfg.TerrainDensity = v[3]; cfg.WaterCoverage = v[4];
            cfg.ResourceDensity = v[5]; cfg.Objectives = v[6]; cfg.StartDistance = v[7]; cfg.Seed = seed;
            cfg.Clamp();
            return true;
        }
    }

    public static class MapGenerator
    {
        struct RegionProfile
        {
            public TerrainType Base, Shore;
            public int WaterAdd, WaterScale, Mountain, Rocky, Forest;
            public bool Beaches;
        }

        static RegionProfile Profile(Region r)
        {
            switch (r)
            {
                case Region.Desert: return new RegionProfile { Base = TerrainType.Desert, Shore = TerrainType.Desert, WaterScale = 40, Mountain = 6, Rocky = 14, Forest = 3 };
                case Region.Arctic: return new RegionProfile { Base = TerrainType.Snow, Shore = TerrainType.Snow, WaterScale = 100, Mountain = 8, Rocky = 8, Forest = 14 };
                case Region.Jungle: return new RegionProfile { Base = TerrainType.Grass, Shore = TerrainType.Beach, WaterAdd = 5, WaterScale = 100, Mountain = 3, Rocky = 4, Forest = 42, Beaches = true };
                case Region.Mountains: return new RegionProfile { Base = TerrainType.Grass, Shore = TerrainType.Rocky, WaterScale = 60, Mountain = 17, Rocky = 16, Forest = 16 };
                case Region.Islands: return new RegionProfile { Base = TerrainType.Grass, Shore = TerrainType.Beach, WaterAdd = 30, WaterScale = 100, Mountain = 3, Rocky = 4, Forest = 16, Beaches = true };
                default: return new RegionProfile { Base = TerrainType.Grass, Shore = TerrainType.Beach, WaterScale = 100, Mountain = 5, Rocky = 6, Forest = 22, Beaches = true };
            }
        }

        public static TerrainType BaseTerrain(Region r) => Profile(r).Base;

        /// <summary>Creates a ready-to-play match (map, bases, starting forces) from a config.</summary>
        public static GameState CreateMatch(MapConfig cfg)
        {
            if (cfg.Premade >= 0 && cfg.Premade < PremadeMaps.All.Length)
                return PremadeMaps.Create(cfg);

            cfg.Clamp();
            GameState s = null;
            Hex hq = default;
            const int attempts = 12;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                uint seed = cfg.Seed * 2654435761u + (uint)attempt * 7919u + 1u;
                s = Generate(cfg, seed, out hq);
                if (Validate(s, hq, s.Mirror(hq)) == null) break;
                if (attempt == attempts - 1) CarveCorridors(s, cfg, hq);
            }
            s.Seed = cfg.Seed == 0 ? 1u : cfg.Seed;
            s.MapName = MapConfig.RegionNames[(int)cfg.Region] + " #" + cfg.Seed;
            FinishSetup(s, cfg, hq);
            return s;
        }

        // ---------------------------------------------------------------- noise (integer only)

        /// <summary>Smooth value noise in 0..1023 at fixed-point position (x, y); cell = lattice spacing.</summary>
        static int Noise(uint seed, int x, int y, int cell)
        {
            int ix = x / cell, iy = y / cell;
            int fx = x - ix * cell, fy = y - iy * cell;
            int tx = Smooth(fx * 256 / cell), ty = Smooth(fy * 256 / cell);
            int v00 = (int)(Rng.Hash(seed, ix, iy) & 1023), v10 = (int)(Rng.Hash(seed, ix + 1, iy) & 1023);
            int v01 = (int)(Rng.Hash(seed, ix, iy + 1) & 1023), v11 = (int)(Rng.Hash(seed, ix + 1, iy + 1) & 1023);
            int top = v00 + (v10 - v00) * tx / 256, bottom = v01 + (v11 - v01) * tx / 256;
            return top + (bottom - top) * ty / 256;
        }

        static int Smooth(int t) => t * t * (768 - 2 * t) / 65536; // 3t^2 - 2t^3 with t in 0..256

        static int Fractal(uint seed, int col, int row)
        {
            int x = col * 16 + (row & 1) * 8, y = row * 14;
            return (Noise(seed, x, y, 88) * 2 + Noise(seed ^ 0x5bd1e995u, x, y, 36)) / 3;
        }

        // ---------------------------------------------------------------- generation

        static GameState Generate(MapConfig cfg, uint seed, out Hex hq)
        {
            var prof = Profile(cfg.Region);
            var s = new GameState();
            s.InitMap(cfg.Width, cfg.Height);
            s.Region = (byte)cfg.Region;
            int w = cfg.Width, h = cfg.Height, n = w * h;
            var rng = new Rng(seed);

            // Height and moisture fields, averaged with their mirror image so the map is point-symmetric.
            var elev = new int[n]; var moist = new int[n];
            for (int i = 0; i < n; i++)
            {
                int c = i % w, r = i / w, mc = w - 1 - c, mr = h - 1 - r;
                elev[i] = (Fractal(seed, c, r) + Fractal(seed, mc, mr)) / 2;
                moist[i] = (Fractal(seed ^ 0xA5A5A5A5u, c, r) + Fractal(seed ^ 0xA5A5A5A5u, mc, mr)) / 2;
            }

            int waterPct = Math.Max(0, Math.Min(62, cfg.WaterCoverage * prof.WaterScale / 100 + prof.WaterAdd));
            int mountainPct = Math.Min(30, prof.Mountain * cfg.TerrainDensity / 50);
            int rockyPct = Math.Min(30, prof.Rocky * cfg.TerrainDensity / 50);
            int forestPct = Math.Min(70, prof.Forest * cfg.TerrainDensity / 50);

            var sortedElev = (int[])elev.Clone(); Array.Sort(sortedElev);
            var sortedMoist = (int[])moist.Clone(); Array.Sort(sortedMoist);
            int waterLevel = waterPct == 0 ? int.MinValue : sortedElev[Math.Min(n - 1, n * waterPct / 100)];
            int mountainLevel = mountainPct == 0 ? int.MaxValue : sortedElev[Math.Max(0, n - 1 - n * mountainPct / 100)];
            int rockyLevel = rockyPct == 0 ? int.MaxValue : sortedElev[Math.Max(0, n - 1 - n * (mountainPct + rockyPct) / 100)];
            int hillLevel = sortedElev[Math.Max(0, n - 1 - n * (mountainPct + rockyPct + 12) / 100)];
            int forestLevel = forestPct == 0 ? int.MaxValue : sortedMoist[Math.Max(0, n - 1 - n * forestPct / 100)];

            for (int i = 0; i < n; i++)
            {
                TerrainType t; byte e = 1;
                if (elev[i] < waterLevel) { t = TerrainType.Water; e = 0; }
                else if (elev[i] > mountainLevel) { t = TerrainType.Mountain; e = 3; }
                else if (elev[i] > rockyLevel) { t = TerrainType.Rocky; e = 2; }
                else
                {
                    t = moist[i] > forestLevel ? TerrainType.Forest : prof.Base;
                    if (elev[i] > hillLevel) e = 2;
                }
                s.Terrain[i] = t; s.Elevation[i] = e;
            }

            if (prof.Beaches)
                for (int i = 0; i < n; i++)
                {
                    if (s.Terrain[i] != prof.Base && s.Terrain[i] != TerrainType.Forest) continue;
                    var hx = s.HexAt(i);
                    for (int d = 0; d < 6; d++)
                    {
                        var nb = hx.Neighbor(d);
                        if (s.InBounds(nb) && elev[s.Index(nb)] < waterLevel) { s.Terrain[i] = TerrainType.Beach; s.Elevation[i] = 1; break; }
                    }
                }

            // Towns.
            int towns = 1 + n / 260;
            for (int k = 0; k < towns; k++)
            {
                var c = RandomHalfHex(s, ref rng);
                if (!IsOpenLand(s, c)) continue;
                SetSym(s, c, TerrainType.Urban, 1);
                int extra = 1 + rng.Next(3);
                for (int j = 0; j < extra; j++)
                {
                    var nb = c.Neighbor(rng.Next(6));
                    if (s.InBounds(nb) && IsOpenLand(s, nb)) SetSym(s, nb, TerrainType.Urban, 1);
                }
            }

            // Headquarters: player 0 in the top-left part, player 1 is its mirror image.
            int[] colPct = { 36, 24, 12 }, rowPct = { 36, 27, 18 };
            int hc = Math.Max(2, w * colPct[cfg.StartDistance] / 100), hr = Math.Max(2, Math.Min(h / 2 - 2, h * rowPct[cfg.StartDistance] / 100));
            hq = Hex.FromOffset(hc, hr);
            ClearArea(s, hq, 2, prof.Base);

            // The guaranteed nearby resource, three hexes from the HQ.
            var ring = new List<Hex>();
            for (int i = 0; i < n / 2; i++)
            {
                var c = s.HexAt(i);
                if (Hex.Distance(c, hq) == 3 && c.Col >= 1 && c.Row >= 1 && Hex.Distance(s.Mirror(c), hq) > 6) ring.Add(c);
            }
            if (ring.Count > 0)
            {
                var c = ring[rng.Next(ring.Count)];
                SetSym(s, c, prof.Base, 1);
                SetSiteSym(s, c, SiteType.OilField);
            }

            // A contested pair right at the centre line.
            int centerRow = h / 2 - 1;
            var center = Hex.FromOffset((centerRow & 1) == 1 ? w / 2 - 1 : w / 2, centerRow);
            if (s.Terrain[s.Index(center)] == TerrainType.Water || s.Terrain[s.Index(center)] == TerrainType.Mountain) SetSym(s, center, prof.Shore, 1);
            SetSiteSym(s, center, cfg.ResourceDensity >= 50 ? SiteType.OilField : SiteType.Industry);

            // Remaining objectives scattered over player 0's half (and mirrored).
            for (int k = 1; k < cfg.Objectives; k++)
            {
                for (int tries = 0; tries < 200; tries++)
                {
                    var c = RandomHalfHex(s, ref rng);
                    int i = s.Index(c);
                    if (TerrainDefs.MoveCost(s.Terrain[i], MoveClass.Foot) < 0 || s.Terrain[i] == TerrainType.Mountain) continue;
                    if (Hex.Distance(c, hq) < 4 || Hex.Distance(c, s.Mirror(hq)) < 4) continue;
                    if (NearSite(s, c, 3)) continue;
                    int roll = rng.Next(100), oil = 20 + cfg.ResourceDensity / 2;
                    SetSiteSym(s, c, roll < oil ? SiteType.OilField : roll < oil + 30 ? SiteType.Industry : SiteType.SupplyCache);
                    break;
                }
            }
            return s;
        }

        static Hex RandomHalfHex(GameState s, ref Rng rng) => s.HexAt(rng.Next(s.TileCount / 2));

        static bool IsOpenLand(GameState s, Hex h)
        {
            var t = s.Terrain[s.Index(h)];
            return t != TerrainType.Water && t != TerrainType.Mountain && s.Site[s.Index(h)] == SiteType.None;
        }

        static bool NearSite(GameState s, Hex c, int radius)
        {
            for (int i = 0; i < s.TileCount; i++)
                if (s.Site[i] != SiteType.None && Hex.Distance(c, s.HexAt(i)) < radius) return true;
            return false;
        }

        /// <summary>Sets a tile and its mirror image, keeping the map symmetric.</summary>
        static void SetSym(GameState s, Hex h, TerrainType t, byte elevation)
        {
            int i = s.Index(h), m = s.TileCount - 1 - i;
            s.Terrain[i] = t; s.Elevation[i] = elevation;
            s.Terrain[m] = t; s.Elevation[m] = elevation;
        }

        static void SetSiteSym(GameState s, Hex h, SiteType site)
        {
            int i = s.Index(h);
            s.Site[i] = site; s.Site[s.TileCount - 1 - i] = site;
        }

        static void ClearArea(GameState s, Hex center, int radius, TerrainType t)
        {
            for (int i = 0; i < s.TileCount; i++)
            {
                var h = s.HexAt(i);
                if (Hex.Distance(h, center) <= radius) SetSym(s, h, t, 1);
            }
        }

        /// <summary>Last resort when random terrain keeps failing validation: cut guaranteed land routes.</summary>
        static void CarveCorridors(GameState s, MapConfig cfg, Hex hq)
        {
            var prof = Profile(cfg.Region);
            int centerRow = s.Height / 2 - 1;
            var center = Hex.FromOffset((centerRow & 1) == 1 ? s.Width / 2 - 1 : s.Width / 2, centerRow);
            Carve(s, prof, hq, center);
            // A second, separate route along the top edge (its mirror runs along the bottom edge).
            var corner = Hex.FromOffset(s.Width - 3, 1);
            Carve(s, prof, hq, corner);
            Carve(s, prof, corner, s.Mirror(hq));
            for (int i = 0; i < s.TileCount / 2; i++)
                if (s.Site[i] != SiteType.None) Carve(s, prof, hq, s.HexAt(i));
        }

        static void Carve(GameState s, RegionProfile prof, Hex a, Hex b)
        {
            foreach (var h in Hex.Line(a, b))
            {
                if (!s.InBounds(h)) continue;
                var t = s.Terrain[s.Index(h)];
                if (t == TerrainType.Water) SetSym(s, h, prof.Shore, 1);
                else if (t == TerrainType.Mountain) SetSym(s, h, TerrainType.Rocky, 2);
            }
        }

        // ---------------------------------------------------------------- validation

        /// <summary>
        /// Checks the fairness/playability rules. Returns null when the map is good, else the problem.
        /// (Symmetry already guarantees equal resources and equal distances for both players.)
        /// </summary>
        public static string Validate(GameState s, Hex hq0, Hex hq1)
        {
            if (!s.InBounds(hq0) || !s.InBounds(hq1)) return "Headquarters outside the map";
            // Vehicles (tracked) must be able to drive between the bases...
            var prev = Flood(s, hq0, MoveClass.Tracked, null);
            if (prev[s.Index(hq1)] == -2) return "No ground route between the bases";
            // ...by at least two different routes: block the first route's middle and search again.
            var blocked = new bool[s.TileCount];
            for (int i = prev[s.Index(hq1)]; i >= 0 && i != s.Index(hq0); i = prev[i])
            {
                var h = s.HexAt(i);
                if (Hex.Distance(h, hq0) > 2 && Hex.Distance(h, hq1) > 2) blocked[i] = true;
            }
            if (Flood(s, hq0, MoveClass.Tracked, blocked)[s.Index(hq1)] == -2) return "Only one route between the bases";
            // Every site must be reachable on foot.
            var foot = Flood(s, hq0, MoveClass.Foot, null);
            for (int i = 0; i < s.TileCount; i++)
                if (s.Site[i] != SiteType.None && foot[i] == -2) return "A strategic site is unreachable";
            // Room to build a base.
            int buildable = 0;
            for (int i = 0; i < s.TileCount; i++)
                if (Hex.Distance(s.HexAt(i), hq0) <= 3 && TerrainDefs.IsBuildable(s.Terrain[i]) && s.Site[i] == SiteType.None) buildable++;
            return buildable < 10 ? "Not enough building space around the Headquarters" : null;
        }

        /// <summary>Breadth-first flood. Returns predecessor index per tile; -2 = unreachable, -1 = the start.</summary>
        static int[] Flood(GameState s, Hex start, MoveClass mc, bool[] blocked)
        {
            var prev = new int[s.TileCount];
            for (int i = 0; i < prev.Length; i++) prev[i] = -2;
            var queue = new Queue<int>();
            prev[s.Index(start)] = -1;
            queue.Enqueue(s.Index(start));
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                var h = s.HexAt(i);
                for (int d = 0; d < 6; d++)
                {
                    var nb = h.Neighbor(d);
                    if (!s.InBounds(nb)) continue;
                    int ni = s.Index(nb);
                    if (prev[ni] != -2 || (blocked != null && blocked[ni])) continue;
                    if (TerrainDefs.MoveCost(s.Terrain[ni], mc) < 0) continue;
                    prev[ni] = i;
                    queue.Enqueue(ni);
                }
            }
            return prev;
        }

        // ---------------------------------------------------------------- starting forces

        /// <summary>Places each player's Headquarters, Barracks and first squad, and sets starting Credits.</summary>
        public static void FinishSetup(GameState s, MapConfig cfg, Hex hq0)
        {
            s.FogOfWar = cfg.FogOfWar;
            s.MaxRounds = cfg.MaxRounds;
            for (int p = 0; p < 2; p++)
            {
                Hex hq = p == 0 ? hq0 : s.Mirror(hq0);
                // Player 1's layout is the mirror image: opposite directions.
                Hex barracks = p == 0 ? hq.Neighbor(0) : hq.Neighbor(3);
                Hex squad = p == 0 ? hq.Neighbor(5) : hq.Neighbor(2);
                s.AddBuilding(BuildingType.Headquarters, p, hq, true);
                s.AddBuilding(BuildingType.Barracks, p, barracks, true);
                s.AddUnit(UnitType.RifleSquad, p, squad);
                s.Players[p].Credits = cfg.StartingCredits;
            }
            s.RecomputeTerritory();
            Visibility.UpdateExplored(s);
            for (int p = 0; p < 2; p++) s.Players[p].LastIncome = s.ComputeIncome(p);
        }
    }

    /// <summary>
    /// Handcrafted maps. Only the top half is drawn; the bottom half is its point-mirror, so both
    /// players always get an identical position. Legend: . base terrain, f forest, m mountain,
    /// w water, b beach, r rocky, u urban, H headquarters, O oil field, I industry, C supply cache.
    /// </summary>
    public static class PremadeMaps
    {
        public sealed class Premade
        {
            public string Name, Description;
            public Region Region;
            public string[] TopHalf;
        }

        public static readonly Premade[] All =
        {
            new Premade
            {
                Name = "Twin Fords", Region = Region.Grasslands,
                Description = "A river splits the field. Two shallow fords are the only crossings - hold them or be overrun.",
                TopHalf = new[]
                {
                    "....f.....f......f..",
                    "..H....f.....C...ff.",
                    ".....O.......r......",
                    "..f......u.......I..",
                    "....f...ff....f.....",
                    "wwwbbbwwwwwwwwbbbwww",
                },
            },
            new Premade
            {
                Name = "Dune Sea", Region = Region.Desert,
                Description = "Wide open desert with rich oil in the middle. Fast vehicles and air power rule here.",
                TopHalf = new[]
                {
                    "mm.....r........rr..",
                    "m.H.......r....O....",
                    "....O...mm......r...",
                    "..r.....mm...u......",
                    "......r.......mm..C.",
                    ".I.......O....mm....",
                },
            },
            new Premade
            {
                Name = "Frostbite Pass", Region = Region.Arctic,
                Description = "Mountain walls leave only narrow passes. Infantry can climb; armor must fight through the gaps.",
                TopHalf = new[]
                {
                    "...f....mm.....f....",
                    ".H.....mm...C.......",
                    "...O..mm.......ff...",
                    ".f.........r.....I..",
                    "......mmm.....f.....",
                    "mmmm..mmmmmmmm..mmmm",
                },
            },
        };

        public static GameState Create(MapConfig cfg)
        {
            var map = All[cfg.Premade];
            int w = map.TopHalf[0].Length, half = map.TopHalf.Length, h = half * 2;
            var s = new GameState();
            s.InitMap(w, h);
            s.Region = (byte)map.Region;
            s.Seed = cfg.Seed == 0 ? 1u : cfg.Seed;
            s.MapName = map.Name;
            var baseTerrain = MapGenerator.BaseTerrain(map.Region);
            Hex hq = default;
            for (int r = 0; r < half; r++)
                for (int c = 0; c < w; c++)
                {
                    char ch = map.TopHalf[r][c];
                    TerrainType t = baseTerrain; byte e = 1; SiteType site = SiteType.None;
                    switch (ch)
                    {
                        case 'f': t = TerrainType.Forest; break;
                        case 'm': t = TerrainType.Mountain; e = 3; break;
                        case 'w': t = TerrainType.Water; e = 0; break;
                        case 'b': t = TerrainType.Beach; break;
                        case 'r': t = TerrainType.Rocky; e = 2; break;
                        case 'u': t = TerrainType.Urban; break;
                        case 'H': hq = Hex.FromOffset(c, r); break;
                        case 'O': site = SiteType.OilField; break;
                        case 'I': site = SiteType.Industry; break;
                        case 'C': site = SiteType.SupplyCache; break;
                    }
                    int i = r * w + c, m = w * h - 1 - i;
                    s.Terrain[i] = s.Terrain[m] = t;
                    s.Elevation[i] = s.Elevation[m] = e;
                    s.Site[i] = s.Site[m] = site;
                }
            MapGenerator.FinishSetup(s, cfg, hq);
            return s;
        }
    }
}
