using Hexfront.Core;
using UnityEngine;

namespace Hexfront
{
    /// <summary>Shared materials, colours and lighting for the low-poly look.</summary>
    public static class Gfx
    {
        static Shader _lit, _overlay;
        static Material _litShared, _overlayShared;
        static bool _linear;

        public static readonly Color[] Team = { new Color(0.20f, 0.52f, 1.00f), new Color(1.00f, 0.32f, 0.26f) };
        public static readonly Color Neutral = new Color(0.92f, 0.92f, 0.88f);
        public static readonly Color Shroud = new Color(0.07f, 0.08f, 0.11f);

        public static void Init()
        {
            _linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            _lit = Resources.Load<Shader>("Shaders/HexfrontLit");
            _overlay = Resources.Load<Shader>("Shaders/HexfrontOverlay");
            // Safety net: an always-included unlit shader, so the game still renders if ours fails to load.
            if (_lit == null || !_lit.isSupported) _lit = Shader.Find("Sprites/Default");
            if (_overlay == null || !_overlay.isSupported) _overlay = Shader.Find("Sprites/Default");
            _litShared = new Material(_lit);
            _overlayShared = new Material(_overlay);

            Shader.SetGlobalVector("_HexSunDir", new Vector3(0.45f, 0.80f, -0.40f).normalized);
            Shader.SetGlobalColor("_HexSunColor", Lin(new Color(0.62f, 0.58f, 0.50f)));
            Shader.SetGlobalColor("_HexAmbientSky", Lin(new Color(0.62f, 0.66f, 0.74f)));
            Shader.SetGlobalColor("_HexAmbientGround", Lin(new Color(0.36f, 0.34f, 0.36f)));
        }

        /// <summary>The opaque flat-shaded material shared by terrain and models.</summary>
        public static Material Lit => _litShared;
        public static Material Overlay => _overlayShared;

        public static Material NewLit(Color tint)
        {
            var m = new Material(_lit);
            m.color = tint;
            return m;
        }

        public static Material NewOverlay(Color tint)
        {
            var m = new Material(_overlay);
            m.color = tint;
            return m;
        }

        static Color Lin(Color c) => _linear ? c.linear : c;

        /// <summary>
        /// Converts an authored (sRGB) colour to what must be stored in a mesh. Vertex colours are
        /// not colour-managed by Unity, so in Linear colour space we convert them ourselves.
        /// </summary>
        public static Color32 V(Color c) => _linear ? (Color32)c.linear.WithAlpha(c.a) : (Color32)c;

        static Color WithAlpha(this Color c, float a) { c.a = a; return c; }

        public static Color Mul(Color c, float k) => new Color(c.r * k, c.g * k, c.b * k, c.a);

        // ------------------------------------------------------------ biome palettes

        public static Color TerrainColor(TerrainType t, Region region)
        {
            switch (t)
            {
                case TerrainType.Grass:
                    return region == Region.Jungle ? new Color(0.30f, 0.62f, 0.26f)
                         : region == Region.Mountains ? new Color(0.50f, 0.64f, 0.34f)
                         : region == Region.Islands ? new Color(0.48f, 0.76f, 0.36f)
                         : new Color(0.45f, 0.72f, 0.33f);
                case TerrainType.Forest:
                    return region == Region.Arctic ? new Color(0.78f, 0.84f, 0.86f)
                         : region == Region.Desert ? new Color(0.72f, 0.66f, 0.40f)
                         : region == Region.Jungle ? new Color(0.20f, 0.48f, 0.22f)
                         : new Color(0.32f, 0.58f, 0.28f);
                case TerrainType.Desert: return new Color(0.90f, 0.78f, 0.50f);
                case TerrainType.Mountain: return region == Region.Desert ? new Color(0.66f, 0.50f, 0.38f) : new Color(0.52f, 0.52f, 0.56f);
                case TerrainType.Water:
                    return region == Region.Arctic ? new Color(0.24f, 0.44f, 0.62f)
                         : region == Region.Islands || region == Region.Jungle ? new Color(0.16f, 0.66f, 0.78f)
                         : new Color(0.20f, 0.50f, 0.82f);
                case TerrainType.Beach: return new Color(0.94f, 0.87f, 0.62f);
                case TerrainType.Snow: return new Color(0.93f, 0.95f, 0.98f);
                case TerrainType.Rocky: return region == Region.Desert ? new Color(0.74f, 0.58f, 0.42f) : new Color(0.62f, 0.60f, 0.56f);
                default: return new Color(0.66f, 0.66f, 0.68f); // urban
            }
        }

        public static Color SkyColor(Region region)
        {
            switch (region)
            {
                case Region.Desert: return new Color(0.93f, 0.80f, 0.60f);
                case Region.Arctic: return new Color(0.72f, 0.82f, 0.92f);
                case Region.Jungle: return new Color(0.50f, 0.78f, 0.80f);
                case Region.Islands: return new Color(0.42f, 0.74f, 0.92f);
                default: return new Color(0.55f, 0.76f, 0.94f);
            }
        }
    }

    /// <summary>Hex grid to world space. Pointy-top hexes with outer radius 1.</summary>
    public static class HexLayout
    {
        public const float Sqrt3 = 1.7320508f;
        public const float AirHeight = 1.55f;

        public static Vector3 Center(Hex h) => new Vector3(Sqrt3 * (h.Q + h.R * 0.5f), 0f, -1.5f * h.R);

        /// <summary>Height of a tile's top surface.</summary>
        public static float TopY(GameState s, Hex h)
        {
            if (!s.InBounds(h)) return 0f;
            int e = s.Elevation[s.Index(h)];
            return e == 0 ? -0.16f : (e - 1) * 0.26f;
        }

        public static Vector3 Surface(GameState s, Hex h)
        {
            var p = Center(h);
            p.y = TopY(s, h);
            return p;
        }

        /// <summary>Where a unit stands: on the surface, or hovering if it flies.</summary>
        public static Vector3 UnitPosition(GameState s, Hex h, bool air)
        {
            var p = Surface(s, h);
            if (air) p.y = Mathf.Max(p.y, 0f) + AirHeight;
            return p;
        }

        public static Hex FromWorld(Vector3 p)
        {
            float r = -p.z / 1.5f;
            float q = p.x / Sqrt3 - r * 0.5f;
            // cube rounding
            float x = q, z = r, y = -x - z;
            int rx = Mathf.RoundToInt(x), ry = Mathf.RoundToInt(y), rz = Mathf.RoundToInt(z);
            float dx = Mathf.Abs(rx - x), dy = Mathf.Abs(ry - y), dz = Mathf.Abs(rz - z);
            if (dx > dy && dx > dz) rx = -ry - rz;
            else if (dy <= dz) rz = -rx - ry;
            return new Hex(rx, rz);
        }

        public static Vector3 Corner(int i, float radius)
        {
            float a = Mathf.Deg2Rad * (60f * i + 30f);
            return new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
        }
    }
}
