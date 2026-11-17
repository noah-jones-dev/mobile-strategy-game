using System.Collections.Generic;
using Hexfront.Core;
using UnityEngine;

namespace Hexfront
{
    /// <summary>
    /// Procedural low-poly models. Everything is assembled from boxes, prisms and cones, with the
    /// owner's team colour baked into the vertex colours. Meshes are cached per (type, owner).
    /// All models face +Z.
    /// </summary>
    public static class Models
    {
        static readonly Dictionary<int, Mesh> Cache = new Dictionary<int, Mesh>();
        static Mesh _rotor, _shadow, _gem;

        static readonly Color Steel = new Color(0.34f, 0.36f, 0.40f);
        static readonly Color Dark = new Color(0.16f, 0.17f, 0.20f);
        static readonly Color Skin = new Color(0.93f, 0.76f, 0.62f);
        static readonly Color Glass = new Color(0.55f, 0.80f, 0.92f);
        static readonly Color Concrete = new Color(0.72f, 0.72f, 0.70f);
        static readonly Color Yellow = new Color(0.98f, 0.80f, 0.20f);
        static readonly Color White = new Color(0.96f, 0.96f, 0.96f);
        static readonly Color Wood = new Color(0.62f, 0.44f, 0.26f);

        static Color32 C(Color c) => Gfx.V(c);

        public static Mesh Unit(UnitType type, int owner)
        {
            int key = (int)type * 4 + owner;
            if (Cache.TryGetValue(key, out var m)) return m;
            var b = new MeshBuilder();
            BuildUnit(b, type, Gfx.Team[owner]);
            m = b.ToMesh(type + "_" + owner);
            Cache[key] = m;
            return m;
        }

        public static Mesh Building(BuildingType type, int owner)
        {
            int key = 1000 + (int)type * 4 + owner;
            if (Cache.TryGetValue(key, out var m)) return m;
            var b = new MeshBuilder();
            BuildBuilding(b, type, Gfx.Team[owner]);
            m = b.ToMesh(type + "_" + owner);
            Cache[key] = m;
            return m;
        }

        public static Mesh Site(SiteType type)
        {
            int key = 2000 + (int)type;
            if (Cache.TryGetValue(key, out var m)) return m;
            var b = new MeshBuilder();
            BuildSite(b, type);
            m = b.ToMesh("Site_" + type);
            Cache[key] = m;
            return m;
        }

        /// <summary>A flag in a given colour (shows who owns a site).</summary>
        public static Mesh Flag(int owner)
        {
            int key = 3000 + owner + 1;
            if (Cache.TryGetValue(key, out var m)) return m;
            var b = new MeshBuilder();
            Color col = owner < 0 ? Gfx.Neutral : Gfx.Team[owner];
            b.Box(new Vector3(0, 0.45f, 0), new Vector3(0.035f, 0.9f, 0.035f), C(Steel));
            b.Box(new Vector3(0.17f, 0.76f, 0), new Vector3(0.32f, 0.22f, 0.02f), C(col));
            m = b.ToMesh("Flag_" + owner);
            Cache[key] = m;
            return m;
        }

        public static bool HasRotor(UnitType t) => t == UnitType.AttackHelicopter || t == UnitType.TransportHelicopter;

        public static Mesh Rotor()
        {
            if (_rotor != null) return _rotor;
            var b = new MeshBuilder();
            b.Box(Vector3.zero, new Vector3(1.0f, 0.015f, 0.07f), C(Dark));
            b.Box(Vector3.zero, new Vector3(0.07f, 0.015f, 1.0f), C(Dark));
            return _rotor = b.ToMesh("Rotor");
        }

        /// <summary>Soft dark disc drawn under units instead of real-time shadows (cheap on phones).</summary>
        public static Mesh BlobShadow()
        {
            if (_shadow != null) return _shadow;
            var b = new MeshBuilder();
            var c = new Color32(0, 0, 0, 70);
            for (int i = 0; i < 10; i++)
            {
                float a0 = Mathf.PI * 2 * i / 10, a1 = Mathf.PI * 2 * (i + 1) / 10;
                b.Tri(Vector3.zero, new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * 0.42f, new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * 0.42f, c, Vector3.up);
            }
            return _shadow = b.ToMesh("Shadow");
        }

        /// <summary>White faceted ball (two six-sided pyramids base to base), diameter 1.</summary>
        public static Mesh Gem()
        {
            if (_gem != null) return _gem;
            var b = new MeshBuilder();
            var white = new Color32(255, 255, 255, 255);
            for (int i = 0; i < 6; i++)
            {
                float a0 = Mathf.PI * 2 * i / 6, a1 = Mathf.PI * 2 * (i + 1) / 6;
                Vector3 p0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * 0.5f, p1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * 0.5f;
                b.Tri(p0, p1, Vector3.up * 0.5f, white, p0 + p1 + Vector3.up);
                b.Tri(p0, p1, Vector3.down * 0.5f, white, p0 + p1 + Vector3.down);
            }
            return _gem = b.ToMesh("Gem");
        }

        // ------------------------------------------------------------------ units

        static void Soldier(MeshBuilder b, Vector3 at, float scale, Color team, Color helmet, float gunLength, float gunThickness, float gunPitch)
        {
            b.SetTransform(at, 0, 0, scale);
            b.Box(new Vector3(0, 0.08f, 0), new Vector3(0.12f, 0.16f, 0.09f), C(Gfx.Mul(team, 0.55f)));
            b.Box(new Vector3(0, 0.25f, 0), new Vector3(0.16f, 0.19f, 0.11f), C(team));
            b.Box(new Vector3(0, 0.40f, 0), new Vector3(0.10f, 0.10f, 0.10f), C(Skin));
            b.Box(new Vector3(0, 0.46f, 0), new Vector3(0.13f, 0.05f, 0.13f), C(helmet));
            if (gunLength > 0)
            {
                b.Transform = Matrix4x4.TRS(at + new Vector3(0.09f, 0.27f, 0.02f) * scale, Quaternion.Euler(gunPitch, 0, 0), Vector3.one * scale);
                b.Box(new Vector3(0, 0, gunLength * 0.35f), new Vector3(gunThickness, gunThickness, gunLength), C(Dark));
            }
            b.ResetTransform();
        }

        static void Squad(MeshBuilder b, int count, float scale, Color team, Color helmet, float gunLength, float gunThickness, float gunPitch)
        {
            Vector3[] spots = count == 2
                ? new[] { new Vector3(-0.17f, 0, 0.02f), new Vector3(0.17f, 0, -0.04f) }
                : new[] { new Vector3(0, 0, 0.2f), new Vector3(-0.22f, 0, -0.14f), new Vector3(0.22f, 0, -0.14f) };
            foreach (var s in spots) Soldier(b, s, scale, team, helmet, gunLength, gunThickness, gunPitch);
        }

        static void Wheels(MeshBuilder b, float halfWidth, float[] zs, float size)
        {
            foreach (float z in zs)
            {
                b.Box(new Vector3(-halfWidth, size * 0.5f, z), new Vector3(0.09f, size, size), C(Dark));
                b.Box(new Vector3(halfWidth, size * 0.5f, z), new Vector3(0.09f, size, size), C(Dark));
            }
        }

        static void Tracks(MeshBuilder b, float halfWidth, float length)
        {
            b.Box(new Vector3(-halfWidth, 0.08f, 0), new Vector3(0.15f, 0.16f, length), C(Dark));
            b.Box(new Vector3(halfWidth, 0.08f, 0), new Vector3(0.15f, 0.16f, length), C(Dark));
        }

        /// <summary>A gun barrel starting at <paramref name="pivot"/>, pointing forward and pitched up.</summary>
        static void Barrel(MeshBuilder b, Vector3 pivot, float length, float thickness, float pitchUp, Color color)
        {
            b.Transform = Matrix4x4.TRS(pivot, Quaternion.Euler(-pitchUp, 0, 0), Vector3.one);
            b.Box(new Vector3(0, 0, length * 0.5f), new Vector3(thickness, thickness, length), C(color));
            b.ResetTransform();
        }

        static void Wing(MeshBuilder b, Vector3 root, Vector3 tipFront, Vector3 tipBack, Vector3 rootBack, Color color)
        {
            b.Quad(root, tipFront, tipBack, rootBack, C(color), Vector3.up);
            b.Quad(root, tipFront, tipBack, rootBack, C(Gfx.Mul(color, 0.7f)), Vector3.down);
        }

        static void BuildUnit(MeshBuilder b, UnitType type, Color team)
        {
            Color teamDark = Gfx.Mul(team, 0.7f), teamLight = Color.Lerp(team, Color.white, 0.25f);
            Color olive = new Color(0.30f, 0.36f, 0.24f);
            switch (type)
            {
                case UnitType.RifleSquad: Squad(b, 3, 1.25f, team, olive, 0.22f, 0.03f, 0); break;
                case UnitType.HeavyInfantry: Squad(b, 2, 1.65f, team, Dark, 0.26f, 0.06f, 0); break;
                case UnitType.AntiTankSquad: Squad(b, 2, 1.35f, team, olive, 0.34f, 0.07f, -12); break;
                case UnitType.RocketInfantry: Squad(b, 2, 1.35f, team, White, 0.30f, 0.06f, -40); break;
                case UnitType.ReconSquad:
                    Squad(b, 2, 1.15f, team, new Color(0.55f, 0.45f, 0.25f), 0.18f, 0.03f, 0);
                    b.Box(new Vector3(0, 0.35f, -0.25f), new Vector3(0.03f, 0.7f, 0.03f), C(Steel)); // radio mast
                    break;
                case UnitType.Engineers:
                    Squad(b, 2, 1.3f, team, Yellow, 0, 0, 0);
                    b.Box(new Vector3(0, 0.09f, 0.26f), new Vector3(0.22f, 0.18f, 0.16f), C(Yellow)); // toolbox
                    break;

                case UnitType.ReconVehicle:
                    Wheels(b, 0.2f, new[] { 0.2f, -0.2f }, 0.16f);
                    b.Box(new Vector3(0, 0.2f, 0), new Vector3(0.34f, 0.14f, 0.6f), C(team));
                    b.Box(new Vector3(0, 0.32f, -0.05f), new Vector3(0.28f, 0.11f, 0.26f), C(Glass));
                    Barrel(b, new Vector3(0, 0.42f, -0.05f), 0.3f, 0.04f, 5, Dark);
                    break;
                case UnitType.Apc:
                    Wheels(b, 0.24f, new[] { 0.26f, 0f, -0.26f }, 0.17f);
                    b.Box(new Vector3(0, 0.26f, 0), new Vector3(0.42f, 0.24f, 0.78f), C(team));
                    b.Box(new Vector3(0, 0.30f, 0.40f), new Vector3(0.36f, 0.12f, 0.04f), C(Glass));
                    b.Box(new Vector3(0, 0.43f, -0.08f), new Vector3(0.2f, 0.1f, 0.2f), C(teamDark));
                    Barrel(b, new Vector3(0, 0.44f, 0), 0.26f, 0.04f, 0, Dark);
                    break;
                case UnitType.MainBattleTank:
                    Tracks(b, 0.25f, 0.76f);
                    b.Box(new Vector3(0, 0.2f, 0), new Vector3(0.44f, 0.16f, 0.68f), C(team));
                    b.Box(new Vector3(0, 0.35f, -0.04f), new Vector3(0.32f, 0.14f, 0.34f), C(teamLight));
                    Barrel(b, new Vector3(0, 0.36f, 0.12f), 0.5f, 0.06f, 2, Steel);
                    break;
                case UnitType.HeavyTank:
                    Tracks(b, 0.31f, 0.9f);
                    b.Box(new Vector3(0, 0.23f, 0), new Vector3(0.56f, 0.22f, 0.82f), C(team));
                    b.Box(new Vector3(0, 0.42f, -0.06f), new Vector3(0.42f, 0.18f, 0.42f), C(teamLight));
                    Barrel(b, new Vector3(-0.08f, 0.43f, 0.14f), 0.56f, 0.07f, 2, Steel);
                    Barrel(b, new Vector3(0.08f, 0.43f, 0.14f), 0.56f, 0.07f, 2, Steel);
                    b.Box(new Vector3(0, 0.55f, -0.14f), new Vector3(0.14f, 0.08f, 0.14f), C(teamDark));
                    break;
                case UnitType.Artillery:
                    Tracks(b, 0.24f, 0.74f);
                    b.Box(new Vector3(0, 0.2f, 0), new Vector3(0.42f, 0.16f, 0.66f), C(team));
                    b.Box(new Vector3(0, 0.36f, -0.14f), new Vector3(0.34f, 0.18f, 0.34f), C(teamLight));
                    Barrel(b, new Vector3(0, 0.40f, -0.02f), 0.72f, 0.07f, 28, Steel);
                    break;
                case UnitType.RocketArtillery:
                    Wheels(b, 0.22f, new[] { 0.28f, -0.06f, -0.28f }, 0.16f);
                    b.Box(new Vector3(0, 0.2f, 0), new Vector3(0.38f, 0.12f, 0.8f), C(teamDark));
                    b.Box(new Vector3(0, 0.33f, 0.28f), new Vector3(0.34f, 0.16f, 0.2f), C(team));
                    b.Transform = Matrix4x4.TRS(new Vector3(0, 0.34f, -0.12f), Quaternion.Euler(-30, 0, 0), Vector3.one);
                    b.Box(new Vector3(0, 0.08f, 0.06f), new Vector3(0.34f, 0.2f, 0.46f), C(teamLight));
                    b.Box(new Vector3(0, 0.08f, 0.30f), new Vector3(0.28f, 0.14f, 0.02f), C(Dark));
                    b.ResetTransform();
                    break;
                case UnitType.AntiAir:
                    Tracks(b, 0.24f, 0.72f);
                    b.Box(new Vector3(0, 0.2f, 0), new Vector3(0.42f, 0.16f, 0.64f), C(team));
                    b.Box(new Vector3(0, 0.34f, -0.05f), new Vector3(0.28f, 0.14f, 0.26f), C(teamLight));
                    Barrel(b, new Vector3(-0.09f, 0.38f, 0), 0.42f, 0.035f, 45, Steel);
                    Barrel(b, new Vector3(0.09f, 0.38f, 0), 0.42f, 0.035f, 45, Steel);
                    b.Box(new Vector3(0, 0.50f, -0.14f), new Vector3(0.2f, 0.12f, 0.03f), C(White)); // radar panel
                    break;
                case UnitType.CommandVehicle:
                    Wheels(b, 0.23f, new[] { 0.24f, -0.24f }, 0.17f);
                    b.Box(new Vector3(0, 0.28f, -0.04f), new Vector3(0.42f, 0.28f, 0.6f), C(team));
                    b.Box(new Vector3(0, 0.24f, 0.34f), new Vector3(0.38f, 0.18f, 0.16f), C(teamLight));
                    b.Box(new Vector3(0.12f, 0.72f, -0.2f), new Vector3(0.025f, 0.6f, 0.025f), C(Steel));
                    b.Prism(new Vector3(-0.08f, 0.42f, -0.1f), 0.03f, 0.14f, 0.1f, 6, C(White));
                    break;
                case UnitType.SupplyTruck:
                    Wheels(b, 0.21f, new[] { 0.26f, -0.24f }, 0.16f);
                    b.Box(new Vector3(0, 0.26f, 0.28f), new Vector3(0.36f, 0.22f, 0.2f), C(team));
                    b.Box(new Vector3(0, 0.30f, -0.12f), new Vector3(0.4f, 0.3f, 0.54f), C(White));
                    b.Box(new Vector3(0, 0.46f, -0.12f), new Vector3(0.24f, 0.02f, 0.08f), C(new Color(0.9f, 0.2f, 0.2f)));
                    b.Box(new Vector3(0, 0.46f, -0.12f), new Vector3(0.08f, 0.02f, 0.24f), C(new Color(0.9f, 0.2f, 0.2f)));
                    break;

                case UnitType.AttackHelicopter:
                case UnitType.TransportHelicopter:
                {
                    bool transport = type == UnitType.TransportHelicopter;
                    float w = transport ? 0.34f : 0.22f, len = transport ? 0.66f : 0.56f;
                    b.Box(new Vector3(0, 0.12f, 0.02f), new Vector3(w, 0.24f, len), C(team));
                    b.Box(new Vector3(0, 0.16f, len * 0.5f + 0.02f), new Vector3(w * 0.85f, 0.16f, 0.12f), C(Glass));
                    b.Box(new Vector3(0, 0.16f, -len * 0.5f - 0.2f), new Vector3(0.07f, 0.07f, 0.46f), C(teamDark));
                    b.Box(new Vector3(0, 0.26f, -len * 0.5f - 0.4f), new Vector3(0.03f, 0.2f, 0.1f), C(team));
                    b.Box(new Vector3(-w * 0.5f, -0.04f, 0), new Vector3(0.03f, 0.03f, 0.5f), C(Dark));
                    b.Box(new Vector3(w * 0.5f, -0.04f, 0), new Vector3(0.03f, 0.03f, 0.5f), C(Dark));
                    b.Box(new Vector3(0, 0.27f, 0), new Vector3(0.05f, 0.08f, 0.05f), C(Steel));
                    if (!transport)
                    {
                        b.Box(new Vector3(-0.2f, 0.08f, 0.04f), new Vector3(0.2f, 0.03f, 0.14f), C(teamDark));
                        b.Box(new Vector3(0.2f, 0.08f, 0.04f), new Vector3(0.2f, 0.03f, 0.14f), C(teamDark));
                        Barrel(b, new Vector3(0, 0.02f, 0.26f), 0.2f, 0.04f, 0, Dark);
                    }
                    break;
                }
                case UnitType.FighterJet:
                    b.Box(new Vector3(0, 0, 0), new Vector3(0.13f, 0.12f, 0.8f), C(team));
                    b.Box(new Vector3(0, 0.02f, 0.5f), new Vector3(0.08f, 0.07f, 0.22f), C(teamLight));
                    b.Box(new Vector3(0, 0.08f, 0.18f), new Vector3(0.09f, 0.07f, 0.2f), C(Glass));
                    Wing(b, new Vector3(0.06f, 0, 0.12f), new Vector3(0.5f, 0, -0.22f), new Vector3(0.5f, 0, -0.32f), new Vector3(0.06f, 0, -0.3f), teamDark);
                    Wing(b, new Vector3(-0.06f, 0, 0.12f), new Vector3(-0.5f, 0, -0.22f), new Vector3(-0.5f, 0, -0.32f), new Vector3(-0.06f, 0, -0.3f), teamDark);
                    b.Box(new Vector3(0, 0.14f, -0.32f), new Vector3(0.025f, 0.2f, 0.16f), C(team));
                    break;
                case UnitType.Bomber:
                    b.Box(new Vector3(0, 0, 0), new Vector3(0.2f, 0.17f, 0.95f), C(team));
                    b.Box(new Vector3(0, 0.06f, 0.42f), new Vector3(0.14f, 0.08f, 0.14f), C(Glass));
                    Wing(b, new Vector3(0.1f, 0.02f, 0.2f), new Vector3(0.72f, 0.02f, -0.1f), new Vector3(0.72f, 0.02f, -0.26f), new Vector3(0.1f, 0.02f, -0.2f), teamDark);
                    Wing(b, new Vector3(-0.1f, 0.02f, 0.2f), new Vector3(-0.72f, 0.02f, -0.1f), new Vector3(-0.72f, 0.02f, -0.26f), new Vector3(-0.1f, 0.02f, -0.2f), teamDark);
                    b.Box(new Vector3(0.34f, -0.04f, 0.0f), new Vector3(0.09f, 0.09f, 0.26f), C(Steel));
                    b.Box(new Vector3(-0.34f, -0.04f, 0.0f), new Vector3(0.09f, 0.09f, 0.26f), C(Steel));
                    b.Box(new Vector3(0, 0.16f, -0.4f), new Vector3(0.03f, 0.24f, 0.18f), C(team));
                    b.Box(new Vector3(0, 0.05f, -0.42f), new Vector3(0.44f, 0.02f, 0.12f), C(teamDark));
                    break;
                case UnitType.ReconAircraft:
                    b.Box(new Vector3(0, 0, 0), new Vector3(0.12f, 0.12f, 0.7f), C(White));
                    b.Box(new Vector3(0, 0.07f, 0.02f), new Vector3(1.1f, 0.025f, 0.16f), C(team));
                    b.Box(new Vector3(0, 0.12f, -0.32f), new Vector3(0.025f, 0.18f, 0.12f), C(team));
                    b.Box(new Vector3(0, 0.04f, -0.32f), new Vector3(0.34f, 0.02f, 0.1f), C(team));
                    b.Prism(new Vector3(0, 0.1f, -0.05f), 0.16f, 0.16f, 0.03f, 8, C(Dark)); // radar dish
                    break;
            }
        }

        // ------------------------------------------------------------------ buildings

        static void Slab(MeshBuilder b, Color edge)
        {
            b.Prism(new Vector3(0, 0, 0), 0.86f, 0.82f, 0.06f, 6, C(edge), 30f, C(Concrete));
        }

        static void BuildBuilding(MeshBuilder b, BuildingType type, Color team)
        {
            Color teamDark = Gfx.Mul(team, 0.7f), teamLight = Color.Lerp(team, Color.white, 0.3f);
            Slab(b, team);
            switch (type)
            {
                case BuildingType.Headquarters:
                    b.Box(new Vector3(0, 0.26f, 0), new Vector3(0.9f, 0.4f, 0.8f), C(Concrete));
                    b.Box(new Vector3(0, 0.6f, 0), new Vector3(0.6f, 0.3f, 0.52f), C(team));
                    b.Box(new Vector3(0, 0.82f, 0), new Vector3(0.34f, 0.14f, 0.3f), C(teamLight));
                    b.Box(new Vector3(0, 0.3f, 0.41f), new Vector3(0.24f, 0.3f, 0.02f), C(Dark));
                    b.Box(new Vector3(0.2f, 1.25f, 0.1f), new Vector3(0.035f, 0.75f, 0.035f), C(Steel));
                    b.Box(new Vector3(0.38f, 1.48f, 0.1f), new Vector3(0.34f, 0.22f, 0.02f), C(team));
                    b.Prism(new Vector3(-0.3f, 0.46f, -0.22f), 0.03f, 0.13f, 0.1f, 6, C(White));
                    break;
                case BuildingType.Barracks:
                    b.Box(new Vector3(0, 0.2f, 0), new Vector3(1.0f, 0.28f, 0.56f), C(Concrete));
                    b.Quad(new Vector3(-0.52f, 0.34f, 0.3f), new Vector3(0.52f, 0.34f, 0.3f), new Vector3(0.52f, 0.54f, 0), new Vector3(-0.52f, 0.54f, 0), C(team), new Vector3(0, 1, 1));
                    b.Quad(new Vector3(-0.52f, 0.34f, -0.3f), new Vector3(0.52f, 0.34f, -0.3f), new Vector3(0.52f, 0.54f, 0), new Vector3(-0.52f, 0.54f, 0), C(teamDark), new Vector3(0, 1, -1));
                    b.Tri(new Vector3(0.52f, 0.34f, 0.3f), new Vector3(0.52f, 0.34f, -0.3f), new Vector3(0.52f, 0.54f, 0), C(Concrete), Vector3.right);
                    b.Tri(new Vector3(-0.52f, 0.34f, 0.3f), new Vector3(-0.52f, 0.34f, -0.3f), new Vector3(-0.52f, 0.54f, 0), C(Concrete), Vector3.left);
                    b.Box(new Vector3(0, 0.17f, 0.285f), new Vector3(0.16f, 0.22f, 0.02f), C(Dark));
                    break;
                case BuildingType.VehicleFactory:
                    b.Box(new Vector3(0, 0.27f, 0), new Vector3(1.0f, 0.42f, 0.8f), C(Concrete));
                    for (int i = 0; i < 3; i++)
                    {
                        float x = -0.34f + i * 0.34f;
                        b.Quad(new Vector3(x - 0.17f, 0.48f, -0.4f), new Vector3(x - 0.17f, 0.48f, 0.4f), new Vector3(x + 0.17f, 0.64f, 0.4f), new Vector3(x + 0.17f, 0.64f, -0.4f), C(team), new Vector3(-1, 1, 0));
                        b.Quad(new Vector3(x + 0.17f, 0.48f, -0.4f), new Vector3(x + 0.17f, 0.48f, 0.4f), new Vector3(x + 0.17f, 0.64f, 0.4f), new Vector3(x + 0.17f, 0.64f, -0.4f), C(Glass), Vector3.right);
                    }
                    b.Box(new Vector3(0, 0.2f, 0.405f), new Vector3(0.44f, 0.28f, 0.02f), C(Dark));
                    b.Prism(new Vector3(0.36f, 0.48f, -0.24f), 0.07f, 0.055f, 0.5f, 6, C(Steel));
                    break;
                case BuildingType.Airfield:
                    b.Box(new Vector3(0, 0.075f, 0), new Vector3(0.5f, 0.03f, 1.4f), C(Dark));
                    for (int i = 0; i < 4; i++) b.Box(new Vector3(0, 0.095f, -0.5f + i * 0.33f), new Vector3(0.04f, 0.01f, 0.16f), C(White));
                    b.Box(new Vector3(-0.5f, 0.24f, -0.25f), new Vector3(0.34f, 0.36f, 0.5f), C(team));
                    b.Box(new Vector3(-0.5f, 0.2f, 0.005f), new Vector3(0.26f, 0.24f, 0.02f), C(Dark));
                    b.Prism(new Vector3(0.52f, 0.06f, 0.3f), 0.09f, 0.08f, 0.6f, 6, C(Concrete));
                    b.Prism(new Vector3(0.52f, 0.66f, 0.3f), 0.15f, 0.15f, 0.13f, 6, C(Glass), 0, C(teamDark));
                    break;
                case BuildingType.RadarStation:
                    b.Box(new Vector3(0, 0.18f, 0), new Vector3(0.5f, 0.24f, 0.5f), C(team));
                    b.Prism(new Vector3(0, 0.3f, 0), 0.07f, 0.05f, 0.5f, 6, C(Steel));
                    b.Transform = Matrix4x4.TRS(new Vector3(0, 0.86f, 0), Quaternion.Euler(-50, 30, 0), Vector3.one);
                    b.Prism(new Vector3(0, 0, 0), 0.05f, 0.36f, 0.14f, 8, C(White), 0, C(teamLight));
                    b.ResetTransform();
                    break;
                case BuildingType.Turret:
                    b.Prism(new Vector3(0, 0.06f, 0), 0.46f, 0.36f, 0.3f, 6, C(Concrete), 30f);
                    b.Box(new Vector3(0, 0.46f, 0), new Vector3(0.4f, 0.2f, 0.44f), C(team));
                    Barrel(b, new Vector3(-0.09f, 0.48f, 0.1f), 0.56f, 0.07f, 12, Steel);
                    Barrel(b, new Vector3(0.09f, 0.48f, 0.1f), 0.56f, 0.07f, 12, Steel);
                    break;
                case BuildingType.SupplyDepot:
                    b.Box(new Vector3(-0.2f, 0.22f, -0.1f), new Vector3(0.6f, 0.32f, 0.6f), C(team));
                    b.Box(new Vector3(-0.2f, 0.40f, -0.1f), new Vector3(0.66f, 0.05f, 0.66f), C(White));
                    b.Box(new Vector3(0.36f, 0.16f, 0.24f), new Vector3(0.26f, 0.2f, 0.26f), C(Wood));
                    b.Box(new Vector3(0.38f, 0.14f, -0.12f), new Vector3(0.22f, 0.16f, 0.22f), C(Wood));
                    b.Box(new Vector3(0.36f, 0.34f, 0.22f), new Vector3(0.18f, 0.16f, 0.18f), C(Gfx.Mul(Wood, 0.8f)));
                    break;
                case BuildingType.Generator:
                    b.Prism(new Vector3(-0.26f, 0.06f, 0.1f), 0.24f, 0.24f, 0.5f, 8, C(team), 0, C(teamLight));
                    b.Prism(new Vector3(0.26f, 0.06f, 0.1f), 0.24f, 0.24f, 0.5f, 8, C(team), 0, C(teamLight));
                    b.Box(new Vector3(0, 0.34f, 0.1f), new Vector3(0.3f, 0.06f, 0.06f), C(Steel));
                    b.Box(new Vector3(0, 0.2f, -0.36f), new Vector3(0.6f, 0.26f, 0.2f), C(Concrete));
                    b.Box(new Vector3(0, 0.36f, -0.36f), new Vector3(0.1f, 0.08f, 0.1f), C(Yellow));
                    break;
            }
        }

        static void BuildSite(MeshBuilder b, SiteType type)
        {
            b.Prism(Vector3.zero, 0.7f, 0.66f, 0.04f, 6, C(Dark), 30f, C(new Color(0.42f, 0.40f, 0.36f)));
            switch (type)
            {
                case SiteType.OilField:
                    b.Prism(new Vector3(0, 0.04f, 0), 0.26f, 0.05f, 0.85f, 4, C(Steel), 45f);
                    b.Box(new Vector3(0, 0.12f, 0), new Vector3(0.42f, 0.16f, 0.42f), C(Dark));
                    b.Prism(new Vector3(0.36f, 0.04f, 0.2f), 0.14f, 0.14f, 0.24f, 8, C(new Color(0.85f, 0.30f, 0.20f)), 0, C(Steel));
                    break;
                case SiteType.Industry:
                    b.Box(new Vector3(-0.1f, 0.2f, 0), new Vector3(0.56f, 0.32f, 0.5f), C(new Color(0.78f, 0.62f, 0.42f)));
                    b.Prism(new Vector3(0.3f, 0.04f, 0.18f), 0.12f, 0.12f, 0.5f, 8, C(Steel), 0, C(White));
                    b.Prism(new Vector3(-0.22f, 0.36f, -0.1f), 0.06f, 0.05f, 0.4f, 6, C(new Color(0.7f, 0.25f, 0.2f)));
                    break;
                case SiteType.SupplyCache:
                    b.Box(new Vector3(-0.16f, 0.15f, 0.08f), new Vector3(0.3f, 0.22f, 0.3f), C(Wood));
                    b.Box(new Vector3(0.2f, 0.13f, -0.1f), new Vector3(0.26f, 0.18f, 0.26f), C(Gfx.Mul(Wood, 0.85f)));
                    b.Box(new Vector3(-0.1f, 0.34f, 0.04f), new Vector3(0.2f, 0.16f, 0.2f), C(new Color(0.36f, 0.5f, 0.3f)));
                    break;
            }
        }

        // ------------------------------------------------------------------ terrain decoration

        /// <summary>Adds trees, peaks, rocks or houses for one tile directly into the terrain mesh.</summary>
        public static void Decorate(MeshBuilder b, TerrainType terrain, Region region, Vector3 top, uint hash)
        {
            Vector3 Spot(int k, float radius)
            {
                uint h = Rng.Hash(hash, k, 17);
                float a = (h & 1023) / 1023f * Mathf.PI * 2f, r = ((h >> 10) & 255) / 255f * radius;
                return top + new Vector3(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r);
            }
            float Jitter(int k) => 0.8f + ((Rng.Hash(hash, k, 99) & 255) / 255f) * 0.45f;

            switch (terrain)
            {
                case TerrainType.Forest:
                {
                    int trees = 3 + (int)(hash % 2);
                    for (int i = 0; i < trees; i++)
                    {
                        var p = Spot(i, 0.52f); float k = Jitter(i);
                        if (region == Region.Desert)
                        {   // palms / scrub
                            b.Prism(p, 0.035f, 0.03f, 0.42f * k, 4, C(Wood));
                            b.Prism(p + Vector3.up * 0.36f * k, 0.24f * k, 0, 0.16f * k, 5, C(new Color(0.40f, 0.62f, 0.26f)));
                        }
                        else if (region == Region.Jungle || region == Region.Islands)
                        {   // round broadleaf crowns
                            b.Prism(p, 0.04f, 0.035f, 0.3f * k, 4, C(Wood));
                            b.Prism(p + Vector3.up * 0.24f * k, 0.16f * k, 0.26f * k, 0.16f * k, 6, C(new Color(0.16f, 0.50f, 0.22f)));
                            b.Prism(p + Vector3.up * 0.40f * k, 0.26f * k, 0, 0.26f * k, 6, C(new Color(0.22f, 0.60f, 0.26f)));
                        }
                        else
                        {   // conifers (snow-dusted in the arctic)
                            Color needles = region == Region.Arctic ? new Color(0.30f, 0.46f, 0.42f) : new Color(0.18f, 0.46f, 0.24f);
                            b.Prism(p, 0.035f, 0.03f, 0.14f * k, 4, C(Wood));
                            b.Prism(p + Vector3.up * 0.12f * k, 0.22f * k, 0, 0.36f * k, 5, C(needles));
                            b.Prism(p + Vector3.up * 0.34f * k, 0.15f * k, 0, 0.30f * k, 5,
                                C(region == Region.Arctic ? new Color(0.92f, 0.95f, 0.97f) : Gfx.Mul(needles, 1.2f)));
                        }
                    }
                    break;
                }
                case TerrainType.Mountain:
                {
                    Color rock = Gfx.TerrainColor(TerrainType.Mountain, region);
                    bool snowcap = region != Region.Desert;
                    for (int i = 0; i < 2; i++)
                    {
                        var p = Spot(i, 0.28f); float k = Jitter(i) * (i == 0 ? 1.15f : 0.8f);
                        b.Prism(p, 0.5f * k, 0.16f * k, 0.52f * k, 5, C(Gfx.Mul(rock, 0.9f)), hash % 60);
                        b.Prism(p + Vector3.up * 0.52f * k, 0.16f * k, 0, 0.24f * k, 5, C(snowcap ? White : Gfx.Mul(rock, 1.15f)), hash % 60);
                    }
                    break;
                }
                case TerrainType.Rocky:
                    for (int i = 0; i < 3; i++)
                    {
                        var p = Spot(i, 0.5f); float k = Jitter(i);
                        b.Prism(p, 0.14f * k, 0.06f * k, 0.13f * k, 5, C(Gfx.Mul(Gfx.TerrainColor(TerrainType.Rocky, region), 0.78f)), i * 40);
                    }
                    break;
                case TerrainType.Urban:
                    for (int i = 0; i < 3; i++)
                    {
                        var p = Spot(i, 0.44f); float k = Jitter(i);
                        float w = 0.24f * k, hgt = (0.2f + (Rng.Hash(hash, i, 5) % 3) * 0.12f) * k;
                        b.Box(p + Vector3.up * hgt * 0.5f, new Vector3(w, hgt, w), C(i == 0 ? new Color(0.86f, 0.82f, 0.74f) : new Color(0.78f, 0.74f, 0.72f)));
                        b.Box(p + Vector3.up * (hgt + 0.02f), new Vector3(w * 1.08f, 0.04f, w * 1.08f), C(new Color(0.70f, 0.36f, 0.30f)));
                    }
                    break;
                case TerrainType.Desert:
                    if (hash % 5 == 0)
                    {
                        var p = Spot(0, 0.4f);
                        b.Box(p + Vector3.up * 0.12f, new Vector3(0.06f, 0.24f, 0.06f), C(new Color(0.36f, 0.60f, 0.32f)));
                        b.Box(p + new Vector3(0.07f, 0.14f, 0), new Vector3(0.1f, 0.04f, 0.04f), C(new Color(0.36f, 0.60f, 0.32f)));
                    }
                    break;
                case TerrainType.Snow:
                case TerrainType.Grass:
                    if (hash % 7 == 0)
                    {
                        var p = Spot(0, 0.45f);
                        b.Prism(p, 0.09f, 0.03f, 0.07f, 5, C(new Color(0.58f, 0.58f, 0.56f)));
                    }
                    break;
            }
        }
    }
}
