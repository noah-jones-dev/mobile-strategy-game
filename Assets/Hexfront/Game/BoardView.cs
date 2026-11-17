using System;
using System.Collections;
using System.Collections.Generic;
using Hexfront.Core;
using UnityEngine;

namespace Hexfront
{
    /// <summary>
    /// The 3D presentation of a GameState. It never changes the simulation: it draws the state,
    /// and after each round it plays the list of SimEvents as animation, then re-syncs itself to
    /// the authoritative state (so a visual glitch can never become a gameplay bug).
    /// </summary>
    public sealed class BoardView : MonoBehaviour
    {
        sealed class EntityView
        {
            public int Id, Owner;
            public bool IsUnit, IsAir, HasRotor;
            public UnitType UnitType;
            public BuildingType BuildingType;
            public GameObject Root;
            public Transform Model, Rotor, Shadow, Billboard;
            public MeshRenderer Renderer;
            public MeshFilter Filter;
            public Mesh BarMesh;
            public TextMesh Label;
            public float Hp01 = -1f;
            public Hex Pos;
            public float BobPhase;
            public bool Animating;
        }

        sealed class SiteView
        {
            public GameObject Root;
            public MeshFilter Flag;
            public int Owner = -2;
        }

        GameState _s;
        Camera _cam;
        Transform _root;
        Mesh _terrainMesh, _overlayMesh;
        Color32[] _baseColors, _colors;
        int[] _tileStart;      // first vertex of each tile (tile i owns [start[i], start[i+1]))
        int _tableStart;       // vertices from here on belong to the table, not a tile
        readonly Dictionary<int, EntityView> _views = new Dictionary<int, EntityView>();
        readonly Dictionary<int, SiteView> _sites = new Dictionary<int, SiteView>();
        readonly MeshBuilder _overlay = new MeshBuilder();
        readonly MaterialPropertyBlock _mpb = new MaterialPropertyBlock();
        bool[] _visible, _explored;
        int _viewer;           // whose units are always shown; -1 = both (pass & play playback)

        public bool IsPlaying { get; private set; }
        public Rect WorldBounds { get; private set; }

        public void Init(Camera cam) => _cam = cam;

        // ------------------------------------------------------------------ building the board

        public void Build(GameState s)
        {
            _s = s;
            if (_root != null) Destroy(_root.gameObject);
            _views.Clear(); _sites.Clear();
            _root = new GameObject("Board").transform;
            _root.SetParent(transform, false);

            var region = (Region)s.Region;
            var b = new MeshBuilder();
            int n = s.TileCount;
            _tileStart = new int[n + 1];
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                _tileStart[i] = b.VertexCount;
                var h = s.HexAt(i);
                var top = HexLayout.Surface(s, h);
                minX = Mathf.Min(minX, top.x); maxX = Mathf.Max(maxX, top.x);
                minZ = Mathf.Min(minZ, top.z); maxZ = Mathf.Max(maxZ, top.z);
                uint hash = Rng.Hash(s.Seed, i, 31);
                float shade = 0.94f + (hash & 255) / 255f * 0.12f;
                Color col = Gfx.Mul(Gfx.TerrainColor(s.Terrain[i], region), shade);
                // The top face comes first: the fog/territory pass relies on that.
                b.HexTop(top, 0.965f, Gfx.V(col));
                Color side = Gfx.Mul(col, 0.70f);
                for (int c = 0; c < 6; c++)
                {
                    Vector3 a = top + HexLayout.Corner(c, 0.965f), d = top + HexLayout.Corner((c + 1) % 6, 0.965f);
                    Vector3 a2 = new Vector3(a.x, -0.75f, a.z), d2 = new Vector3(d.x, -0.75f, d.z);
                    b.Quad(a, d, d2, a2, Gfx.V(side), (a + d) * 0.5f - top);
                }
                Models.Decorate(b, s.Terrain[i], region, top, hash);
            }
            _tileStart[n] = _tableStart = b.VertexCount;
            // A big dark "table" under the map: the miniature-tabletop look.
            var table = Gfx.V(new Color(0.20f, 0.16f, 0.14f));
            b.Quad(new Vector3(minX - 9, -0.75f, minZ - 9), new Vector3(maxX + 9, -0.75f, minZ - 9),
                   new Vector3(maxX + 9, -0.75f, maxZ + 9), new Vector3(minX - 9, -0.75f, maxZ + 9), table, Vector3.up);
            WorldBounds = Rect.MinMaxRect(minX, minZ, maxX, maxZ);

            _terrainMesh = b.ToMesh("Terrain", true);
            _baseColors = _terrainMesh.colors32;
            _colors = (Color32[])_baseColors.Clone();
            var terrain = new GameObject("Terrain");
            terrain.transform.SetParent(_root, false);
            terrain.AddComponent<MeshFilter>().sharedMesh = _terrainMesh;
            terrain.AddComponent<MeshRenderer>().sharedMaterial = Gfx.Lit;

            _overlayMesh = new Mesh { name = "Overlay" };
            _overlayMesh.MarkDynamic();
            var overlay = new GameObject("Overlay");
            overlay.transform.SetParent(_root, false);
            overlay.AddComponent<MeshFilter>().sharedMesh = _overlayMesh;
            overlay.AddComponent<MeshRenderer>().sharedMaterial = Gfx.Overlay;

            for (int i = 0; i < n; i++)
            {
                if (s.Site[i] == SiteType.None) continue;
                var go = new GameObject("Site");
                go.transform.SetParent(_root, false);
                go.transform.position = HexLayout.Surface(s, s.HexAt(i));
                go.AddComponent<MeshFilter>().sharedMesh = Models.Site(s.Site[i]);
                go.AddComponent<MeshRenderer>().sharedMaterial = Gfx.Lit;
                var flag = new GameObject("Flag");
                flag.transform.SetParent(go.transform, false);
                flag.transform.localPosition = new Vector3(-0.42f, 0.04f, -0.3f);
                var ff = flag.AddComponent<MeshFilter>();
                flag.AddComponent<MeshRenderer>().sharedMaterial = Gfx.Lit;
                _sites[i] = new SiteView { Root = go, Flag = ff };
            }
            _cam.backgroundColor = Gfx.SkyColor(region);
        }

        public void SetState(GameState s) => _s = s;

        /// <summary>Abort any running round animation (the caller re-syncs afterwards).</summary>
        public void CancelPlayback()
        {
            StopAllCoroutines();
            IsPlaying = false;
        }

        // ------------------------------------------------------------------ syncing to the state

        /// <summary>Make every view match the authoritative state exactly, from <paramref name="viewer"/>'s point of view.</summary>
        public void Sync(int viewer, bool[] visible)
        {
            _viewer = viewer;
            _visible = visible;
            _explored = viewer >= 0 ? _s.Explored[viewer] : null;

            var alive = new HashSet<int>();
            foreach (var u in _s.Units)
            {
                alive.Add(u.Id);
                var v = GetOrCreateUnit(u);
                v.Pos = u.Pos;
                v.Animating = false;
                v.Root.transform.position = HexLayout.UnitPosition(_s, u.Pos, v.IsAir);
                SetHp(v, u.Hp / (float)u.Def.MaxHp);
                v.Root.SetActive(u.Owner == viewer || viewer < 0 || IsVisible(u.Pos));
            }
            foreach (var bd in _s.Buildings)
            {
                alive.Add(bd.Id);
                var v = GetOrCreateBuilding(bd);
                if (v.Owner != bd.Owner) // seized
                {
                    v.Owner = bd.Owner;
                    v.Filter.sharedMesh = Models.Building(bd.Type, bd.Owner);
                }
                SetHp(v, bd.Hp / (float)bd.Def.MaxHp);
                bool building = !bd.IsComplete;
                v.Model.localScale = new Vector3(1f, building ? 0.45f : 1f, 1f);
                _mpb.SetColor("_Color", building ? new Color(0.62f, 0.62f, 0.62f) : Color.white);
                v.Renderer.SetPropertyBlock(_mpb);
                v.Label.text = building ? bd.Def.Short + " (" + bd.BuildRoundsLeft + ")"
                             : bd.UpgradeRoundsLeft > 0 ? bd.Def.Short + " UP"
                             : bd.Level > 1 ? bd.Def.Short + " L" + bd.Level : bd.Def.Short;
                int ti = _s.Index(bd.Pos);
                v.Root.SetActive(bd.Owner == viewer || viewer < 0 || IsVisible(bd.Pos) || (_explored != null && _explored[ti]));
            }
            var dead = new List<int>();
            foreach (var kv in _views) if (!alive.Contains(kv.Key)) dead.Add(kv.Key);
            foreach (int id in dead) { Destroy(_views[id].Root); _views.Remove(id); }

            foreach (var kv in _sites)
            {
                int owner = _s.SiteOwner[kv.Key];
                var sv = kv.Value;
                if (sv.Owner != owner) { sv.Owner = owner; sv.Flag.sharedMesh = Models.Flag(owner); }
                sv.Root.SetActive(viewer < 0 || _explored == null || _explored[kv.Key] || IsVisibleIndex(kv.Key));
            }
            ApplyFog();
        }

        bool IsVisible(Hex h) => _visible == null || (_s.InBounds(h) && _visible[_s.Index(h)]);
        bool IsVisibleIndex(int i) => _visible == null || _visible[i];

        /// <summary>Recolour the terrain: black where unexplored, dimmed where remembered, team-tinted inside territory.</summary>
        void ApplyFog()
        {
            int n = _s.TileCount;
            Color32 shroud = Gfx.V(Gfx.Shroud);
            for (int i = 0; i < n; i++)
            {
                int start = _tileStart[i], end = _tileStart[i + 1];
                bool vis = _visible == null || _visible[i];
                bool known = vis || _explored == null || _explored[i];
                int owner = _s.Territory[i];
                Color32 tint = owner >= 0 ? Gfx.V(Gfx.Team[owner]) : default;
                for (int v = start; v < end; v++)
                {
                    if (!known) { _colors[v] = shroud; continue; }
                    Color32 c = _baseColors[v];
                    if (owner >= 0 && v < start + 18) // the 18 vertices of the top face
                        c = Color32.Lerp(c, tint, 0.20f);
                    if (!vis) c = new Color32((byte)(c.r * 0.50f), (byte)(c.g * 0.52f), (byte)(c.b * 0.58f), 255);
                    _colors[v] = c;
                }
            }
            _terrainMesh.colors32 = _colors;
        }

        EntityView GetOrCreateUnit(Unit u)
        {
            if (_views.TryGetValue(u.Id, out var v)) return v;
            v = CreateView("Unit " + u.Def.Name, Models.Unit(u.Type, u.Owner), u.Def.Short, u.IsAir ? 0.62f : 0.82f);
            v.Id = u.Id; v.Owner = u.Owner; v.IsUnit = true; v.IsAir = u.IsAir; v.UnitType = u.Type;
            v.BobPhase = (u.Id * 0.73f) % 6.28f;
            // Units start out facing the enemy side of the map.
            v.Model.localRotation = Quaternion.Euler(0, u.Owner == 0 ? 135f : -45f, 0);
            var shadow = new GameObject("Shadow");
            shadow.transform.SetParent(v.Root.transform, false);
            shadow.AddComponent<MeshFilter>().sharedMesh = Models.BlobShadow();
            shadow.AddComponent<MeshRenderer>().sharedMaterial = Gfx.Overlay;
            v.Shadow = shadow.transform;
            v.Shadow.localPosition = new Vector3(0, 0.03f, 0);
            if (Models.HasRotor(u.Type))
            {
                var rotor = new GameObject("Rotor");
                rotor.transform.SetParent(v.Model, false);
                rotor.transform.localPosition = new Vector3(0, 0.32f, 0);
                rotor.AddComponent<MeshFilter>().sharedMesh = Models.Rotor();
                rotor.AddComponent<MeshRenderer>().sharedMaterial = Gfx.Lit;
                v.Rotor = rotor.transform; v.HasRotor = true;
            }
            _views[u.Id] = v;
            return v;
        }

        EntityView GetOrCreateBuilding(Building bd)
        {
            if (_views.TryGetValue(bd.Id, out var v)) return v;
            v = CreateView("Building " + bd.Def.Name, Models.Building(bd.Type, bd.Owner), bd.Def.Short,
                bd.Type == BuildingType.Headquarters ? 1.85f : 1.15f);
            v.Id = bd.Id; v.Owner = bd.Owner; v.BuildingType = bd.Type; v.Pos = bd.Pos;
            v.Root.transform.position = HexLayout.Surface(_s, bd.Pos);
            _views[bd.Id] = v;
            return v;
        }

        EntityView CreateView(string name, Mesh mesh, string label, float barHeight)
        {
            var v = new EntityView { Root = new GameObject(name) };
            v.Root.transform.SetParent(_root, false);
            var model = new GameObject("Model");
            model.transform.SetParent(v.Root.transform, false);
            v.Model = model.transform;
            v.Filter = model.AddComponent<MeshFilter>();
            v.Filter.sharedMesh = mesh;
            v.Renderer = model.AddComponent<MeshRenderer>();
            v.Renderer.sharedMaterial = Gfx.Lit;

            // Health bar + label always face the camera.
            var bill = new GameObject("Billboard");
            bill.transform.SetParent(v.Root.transform, false);
            bill.transform.localPosition = new Vector3(0, barHeight, 0);
            v.Billboard = bill.transform;
            v.BarMesh = new Mesh { name = "Bar" };
            var bar = new GameObject("Bar");
            bar.transform.SetParent(bill.transform, false);
            bar.AddComponent<MeshFilter>().sharedMesh = v.BarMesh;
            bar.AddComponent<MeshRenderer>().sharedMaterial = Gfx.Overlay;
            v.Label = MakeText(bill.transform, label, 0.045f, Color.white);
            v.Label.transform.localPosition = new Vector3(0, 0.17f, 0);
            return v;
        }

        TextMesh MakeText(Transform parent, string text, float size, Color color)
        {
            var go = new GameObject("Text");
            go.transform.SetParent(parent, false);
            var tm = go.AddComponent<TextMesh>();
            tm.font = Ui.Font; tm.fontSize = 56; tm.characterSize = size;
            tm.anchor = TextAnchor.MiddleCenter; tm.alignment = TextAlignment.Center;
            tm.color = color; tm.text = text; tm.fontStyle = FontStyle.Bold;
            go.GetComponent<MeshRenderer>().sharedMaterial = Ui.Font.material;
            return tm;
        }

        void SetHp(EntityView v, float hp01)
        {
            hp01 = Mathf.Clamp01(hp01);
            if (Mathf.Abs(hp01 - v.Hp01) < 0.001f) return;
            v.Hp01 = hp01;
            var b = new MeshBuilder();
            const float w = 0.62f, h = 0.085f;
            Color fill = hp01 > 0.6f ? new Color(0.30f, 0.90f, 0.35f) : hp01 > 0.3f ? new Color(0.98f, 0.80f, 0.20f) : new Color(0.95f, 0.25f, 0.20f);
            Color32 back = new Color32(0, 0, 0, 170);
            b.Quad(new Vector3(-w / 2 - 0.02f, -h / 2 - 0.02f, 0), new Vector3(w / 2 + 0.02f, -h / 2 - 0.02f, 0),
                   new Vector3(w / 2 + 0.02f, h / 2 + 0.02f, 0), new Vector3(-w / 2 - 0.02f, h / 2 + 0.02f, 0), back, Vector3.back);
            float x1 = -w / 2 + w * hp01;
            b.Quad(new Vector3(-w / 2, -h / 2, -0.001f), new Vector3(x1, -h / 2, -0.001f), new Vector3(x1, h / 2, -0.001f), new Vector3(-w / 2, h / 2, -0.001f),
                   Gfx.V(fill), Vector3.back);
            // A small team-coloured tab so ownership reads at a glance.
            b.Quad(new Vector3(-w / 2 - 0.12f, -h / 2 - 0.02f, 0), new Vector3(-w / 2 - 0.03f, -h / 2 - 0.02f, 0),
                   new Vector3(-w / 2 - 0.03f, h / 2 + 0.02f, 0), new Vector3(-w / 2 - 0.12f, h / 2 + 0.02f, 0), Gfx.V(Gfx.Team[v.Owner]), Vector3.back);
            b.Apply(v.BarMesh);
        }

        void LateUpdate()
        {
            if (_cam == null || _s == null) return;
            var camRot = _cam.transform.rotation;
            float t = Time.time;
            foreach (var v in _views.Values)
            {
                if (!v.Root.activeSelf) continue;
                v.Billboard.rotation = camRot;
                if (v.IsAir)
                {
                    float bob = Mathf.Sin(t * 2.2f + v.BobPhase) * 0.05f;
                    v.Model.localPosition = new Vector3(0, bob, 0);
                    // keep the blob shadow on the ground under the aircraft
                    float ground = HexLayout.TopY(_s, HexLayout.FromWorld(v.Root.transform.position));
                    v.Shadow.position = new Vector3(v.Root.transform.position.x, Mathf.Max(ground, -0.1f) + 0.04f, v.Root.transform.position.z);
                    if (v.HasRotor) v.Rotor.localRotation = Quaternion.Euler(0, t * 1400f % 360f, 0);
                }
            }
        }

        // ------------------------------------------------------------------ picking

        /// <summary>Which hex is under a screen position.</summary>
        public bool PickHex(CameraRig rig, Vector2 screen, out Hex hex)
        {
            hex = default;
            if (_s == null || !rig.GroundPoint(screen, 0f, out var p)) return false;
            hex = HexLayout.FromWorld(p);
            // Refine once using the real height of the tile we hit (hills are raised).
            if (_s.InBounds(hex) && rig.GroundPoint(screen, HexLayout.TopY(_s, hex), out var p2)) hex = HexLayout.FromWorld(p2);
            return _s.InBounds(hex);
        }

        public Vector3 WorldOf(Hex h) => HexLayout.Surface(_s, h);

        // ------------------------------------------------------------------ overlay (highlights, paths)

        public void OverlayBegin() => _overlay.Clear();
        public void OverlayEnd() => _overlay.Apply(_overlayMesh);

        Vector3 Lift(Hex h, float extra = 0.045f) => HexLayout.Surface(_s, h) + Vector3.up * extra;

        public void OverlayFill(Hex h, Color color) { if (_s.InBounds(h)) _overlay.HexTop(Lift(h), 0.9f, color); }
        public void OverlayRing(Hex h, Color color, float thickness = 0.14f) { if (_s.InBounds(h)) _overlay.HexRing(Lift(h, 0.06f), 0.95f, 0.95f - thickness, color); }

        public void OverlayPath(Hex from, List<Hex> path, Color color, float width = 0.16f)
        {
            Vector3 prev = Lift(from, 0.08f);
            for (int i = 0; i < path.Count; i++)
            {
                Vector3 next = Lift(path[i], 0.08f);
                _overlay.Ribbon(prev, next, width, color);
                prev = next;
            }
            if (path.Count > 0) _overlay.HexRing(Lift(path[path.Count - 1], 0.08f), 0.5f, 0.34f, color);
        }

        public void OverlayLine(Hex from, Hex to, Color color, float width = 0.1f)
        {
            Vector3 a = Lift(from, 0.5f), b = Lift(to, 0.5f);
            _overlay.Ribbon(a, b, width, color);
            _overlay.HexRing(Lift(to, 0.09f), 0.8f, 0.62f, color);
        }

        // ------------------------------------------------------------------ playback

        /// <summary>
        /// Animate one resolved round. <paramref name="canSee"/> tells whether the local viewer may see
        /// something on a given hex (fog of war applies to the replay too).
        /// </summary>
        public IEnumerator Play(RoundResult result, Func<Hex, bool> canSee, int viewer, Action<string> log)
        {
            IsPlaying = true;
            OverlayBegin(); OverlayEnd();
            bool Mine(int owner) => viewer < 0 || owner == viewer;

            // 1. New units and construction sites appear.
            bool any = false;
            foreach (var e in result.Events)
            {
                if (e.Type == SimEventType.UnitProduced)
                {
                    var u = _s.GetUnit(e.A);
                    if (u == null) continue; // produced and destroyed in the same round
                    var v = GetOrCreateUnit(u);
                    v.Pos = e.To;
                    v.Root.transform.position = HexLayout.UnitPosition(_s, e.To, v.IsAir);
                    SetHp(v, 1f);
                    bool show = Mine(u.Owner) || canSee(e.To);
                    v.Root.SetActive(show);
                    if (show) { StartCoroutine(Pop(v.Model)); any = true; }
                }
                else if (e.Type == SimEventType.BuildStarted)
                {
                    var bd = _s.GetBuilding(e.A);
                    if (bd == null) continue;
                    var v = GetOrCreateBuilding(bd);
                    v.Model.localScale = new Vector3(1, 0.45f, 1);
                    SetHp(v, bd.Hp / (float)bd.Def.MaxHp);
                    bool show = Mine(bd.Owner) || canSee(e.To);
                    v.Root.SetActive(show);
                    if (show) { StartCoroutine(Pop(v.Model, 0.45f)); any = true; }
                }
                else if (e.Type == SimEventType.OrderRejected && Mine(e.Player) && viewer >= 0)
                    log?.Invoke("Order cancelled: " + e.Text);
            }
            if (any) { Sfx.Play(Sfx.Id.Build); yield return new WaitForSeconds(0.35f); }

            // 2. Everybody moves at once.
            float longest = 0f;
            foreach (var e in result.Events)
            {
                if (e.Type != SimEventType.Move || !_views.TryGetValue(e.A, out var v)) continue;
                const float stepTime = 0.2f;
                longest = Mathf.Max(longest, e.Path.Count * stepTime);
                StartCoroutine(MoveAlong(v, e.From, e.Path, stepTime, Mine(e.Player) ? null : canSee));
            }
            if (longest > 0f) { Sfx.Play(Sfx.Id.Move); yield return new WaitForSeconds(longest + 0.1f); }

            // 3. Combat. Shots are staggered slightly so a big battle reads as a volley.
            int shots = 0;
            foreach (var e in result.Events) if (e.Type == SimEventType.Attack) shots++;
            float gap = shots > 0 ? Mathf.Min(0.14f, 3.5f / shots) : 0f;
            bool anyShot = false;
            foreach (var e in result.Events)
            {
                if (e.Type != SimEventType.Attack) continue;
                bool seeFrom = Mine(e.Player) || canSee(e.From), seeTo = canSee(e.To) || Mine(1 - e.Player);
                if (!seeFrom && !seeTo) continue;
                anyShot = true;
                StartCoroutine(Shot(e, seeFrom, seeTo));
                yield return new WaitForSeconds(gap);
            }
            if (anyShot) yield return new WaitForSeconds(0.45f);

            // 4. Destruction, captures, repairs, construction, income.
            foreach (var e in result.Events)
            {
                switch (e.Type)
                {
                    case SimEventType.UnitDestroyed:
                    case SimEventType.BuildingDestroyed:
                    case SimEventType.OutOfFuel:
                        if (_views.TryGetValue(e.A, out var dv))
                        {
                            if (dv.Root.activeSelf)
                            {
                                StartCoroutine(Burst(dv.Root.transform.position + Vector3.up * 0.25f, e.Type == SimEventType.BuildingDestroyed ? 1.5f : 0.9f, new Color(1f, 0.6f, 0.15f)));
                                Sfx.Play(Sfx.Id.Explosion, 0.8f);
                                if (e.Type == SimEventType.OutOfFuel) FloatText(dv.Root.transform.position, "OUT OF FUEL", Ui.Bad);
                            }
                            Destroy(dv.Root);
                            _views.Remove(e.A);
                        }
                        break;
                    case SimEventType.SiteCaptured:
                        if (canSee(e.To) || Mine(e.Player))
                        {
                            if (_sites.TryGetValue(_s.Index(e.To), out var sv)) { sv.Owner = e.Player; sv.Flag.sharedMesh = Models.Flag(e.Player); }
                            FloatText(WorldOf(e.To), "CAPTURED", Gfx.Team[e.Player]);
                            Sfx.Play(Sfx.Id.Capture);
                        }
                        break;
                    case SimEventType.BuildingSeized:
                        if (canSee(e.To) || Mine(e.Player)) { FloatText(WorldOf(e.To), "SEIZED", Gfx.Team[e.Player]); Sfx.Play(Sfx.Id.Capture); }
                        break;
                    case SimEventType.Repaired:
                        if (Mine(e.Player)) FloatText(WorldOf(e.To), "+" + e.Amount, Ui.Good);
                        break;
                    case SimEventType.ConstructionDone:
                        if (Mine(e.Player)) { FloatText(WorldOf(e.To), "READY", Ui.Good); Sfx.Play(Sfx.Id.Build); }
                        break;
                    case SimEventType.UpgradeDone:
                        if (Mine(e.Player)) { FloatText(WorldOf(e.To), "LEVEL " + e.Amount, Ui.Accent); Sfx.Play(Sfx.Id.Build); }
                        break;
                    case SimEventType.Income:
                        if (viewer >= 0 && e.Player == viewer) { log?.Invoke("Income +" + e.Amount + " Credits"); Sfx.Play(Sfx.Id.Coins, 0.6f); }
                        break;
                }
            }
            yield return new WaitForSeconds(0.55f);
            IsPlaying = false;
        }

        IEnumerator Pop(Transform t, float yScale = 1f)
        {
            float time = 0f;
            while (time < 0.25f && t != null)
            {
                time += Time.deltaTime;
                float k = Mathf.Clamp01(time / 0.25f);
                float s = 1f + Mathf.Sin(k * Mathf.PI) * 0.25f;
                t.localScale = new Vector3(k * s, k * s * yScale, k * s);
                yield return null;
            }
            if (t != null) t.localScale = new Vector3(1, yScale, 1);
        }

        IEnumerator MoveAlong(EntityView v, Hex from, List<Hex> path, float stepTime, Func<Hex, bool> canSee)
        {
            v.Animating = true;
            Vector3 a = HexLayout.UnitPosition(_s, from, v.IsAir);
            foreach (var h in path)
            {
                if (v.Root == null) yield break;
                Vector3 b = HexLayout.UnitPosition(_s, h, v.IsAir);
                if (canSee != null) v.Root.SetActive(canSee(h) || canSee(v.Pos));
                Vector3 dir = b - a; dir.y = 0;
                if (dir.sqrMagnitude > 0.001f) v.Model.localRotation = Quaternion.LookRotation(dir);
                float t = 0f;
                while (t < stepTime)
                {
                    if (v.Root == null) yield break;
                    t += Time.deltaTime;
                    v.Root.transform.position = Vector3.Lerp(a, b, Mathf.SmoothStep(0, 1, t / stepTime));
                    yield return null;
                }
                a = b; v.Pos = h;
            }
            v.Animating = false;
        }

        IEnumerator Shot(SimEvent e, bool seeFrom, bool seeTo)
        {
            _views.TryGetValue(e.A, out var attacker);
            _views.TryGetValue(e.B, out var target);
            Vector3 from = attacker != null && attacker.Root != null ? attacker.Root.transform.position + Vector3.up * 0.3f : WorldOf(e.From) + Vector3.up * 0.3f;
            Vector3 to = target != null && target.Root != null ? target.Root.transform.position + Vector3.up * 0.25f : WorldOf(e.To) + Vector3.up * 0.25f;
            if (attacker != null && attacker.IsUnit && attacker.Root != null)
            {
                Vector3 dir = to - from; dir.y = 0;
                if (dir.sqrMagnitude > 0.001f) attacker.Model.localRotation = Quaternion.LookRotation(dir);
            }
            int dist = Hex.Distance(e.From, e.To);
            bool lob = dist >= 2;
            Sfx.Play(lob ? Sfx.Id.Cannon : Sfx.Id.Shot, 0.7f);

            if (seeFrom || seeTo)
            {
                var proj = NewFx(Gfx.NewOverlay(new Color(1f, 0.92f, 0.4f)));
                proj.transform.localScale = Vector3.one * (lob ? 0.16f : 0.1f);
                float duration = 0.14f + dist * 0.05f, t = 0f;
                while (t < duration)
                {
                    t += Time.deltaTime;
                    float k = Mathf.Clamp01(t / duration);
                    Vector3 p = Vector3.Lerp(from, to, k);
                    if (lob) p.y += Mathf.Sin(k * Mathf.PI) * (0.6f + dist * 0.25f);
                    proj.transform.position = p;
                    yield return null;
                }
                Destroy(proj.GetComponent<MeshRenderer>().sharedMaterial);
                Destroy(proj);
            }
            if (seeTo)
            {
                StartCoroutine(Burst(to, 0.45f, new Color(1f, 0.85f, 0.3f)));
                FloatText(to + Vector3.up * 0.3f, "-" + e.Amount, new Color(1f, 0.35f, 0.3f));
                if (target != null && target.Root != null)
                {
                    // Show the health drop right away (the final Sync makes it exact).
                    var u = _s.GetUnit(e.B); var bd = _s.GetBuilding(e.B);
                    if (u != null) SetHp(target, u.Hp / (float)u.Def.MaxHp);
                    else if (bd != null) SetHp(target, bd.Hp / (float)bd.Def.MaxHp);
                    else SetHp(target, 0f);
                }
            }
        }

        /// <summary>A low-poly "gem" used for projectiles and explosions, tinted by its material.</summary>
        GameObject NewFx(Material material)
        {
            var go = new GameObject("Fx");
            go.transform.SetParent(_root, false);
            go.AddComponent<MeshFilter>().sharedMesh = Models.Gem();
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }

        /// <summary>Cartoon explosion: a ball that swells and fades.</summary>
        IEnumerator Burst(Vector3 at, float size, Color color)
        {
            var mat = Gfx.NewOverlay(color);
            var go = NewFx(mat);
            go.transform.position = at;
            float t = 0f; const float duration = 0.35f;
            while (t < duration)
            {
                t += Time.deltaTime;
                float k = t / duration;
                go.transform.localScale = Vector3.one * size * (0.3f + k * 1.2f);
                mat.color = new Color(color.r, color.g, color.b, 1f - k);
                yield return null;
            }
            Destroy(mat);
            Destroy(go);
        }

        public void FloatText(Vector3 at, string text, Color color) => StartCoroutine(FloatTextRoutine(at, text, color));

        IEnumerator FloatTextRoutine(Vector3 at, string text, Color color)
        {
            var tm = MakeText(_root, text, 0.07f, color);
            float t = 0f; const float duration = 1.0f;
            while (t < duration && tm != null)
            {
                t += Time.deltaTime;
                tm.transform.position = at + Vector3.up * (0.7f + t * 0.9f);
                tm.transform.rotation = _cam.transform.rotation;
                color.a = 1f - Mathf.Clamp01((t - 0.6f) / 0.4f);
                tm.color = color;
                yield return null;
            }
            if (tm != null) Destroy(tm.gameObject);
        }
    }
}
