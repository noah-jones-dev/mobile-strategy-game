using System;
using System.Collections.Generic;
using Hexfront.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Hexfront
{
    /// <summary>Main menu, match setup, saved maps and the LAN lobby screens.</summary>
    public sealed class MenuUi : MonoBehaviour
    {
        enum Page { None, Main, Setup, Maps, HostLobby, Join, JoinWaiting, Help }

        App _app;
        RectTransform _root;
        Page _screen = Page.None;
        readonly List<Action> _refreshers = new List<Action>();
        float _nextRefresh;

        // setup state (kept between visits so settings are remembered)
        MatchMode _mode = MatchMode.VsAi;
        MapConfig _cfg = new MapConfig { Premade = 0, Seed = 1 };
        AiDifficulty _ai = AiDifficulty.Normal;
        int _timerIndex, _sizeIndex = 1, _roundLimitIndex;
        string _mapName = "", _codeInput = "", _ipInput = "";
        Texture2D _preview;

        static readonly int[] Timers = { 0, 30, 60, 90, 120 };
        static readonly int[] RoundLimits = { 0, 20, 30, 40, 60 };
        static readonly int[][] Sizes = { new[] { 18, 12 }, new[] { 24, 16 }, new[] { 32, 20 }, new[] { 40, 26 } };
        static readonly string[] SizeNames = { "Small 18x12", "Medium 24x16", "Large 32x20", "Huge 40x26" };
        static readonly string[] DistanceNames = { "Close", "Medium", "Far" };

        public void Init(App app, RectTransform root)
        {
            _app = app; _root = root;
            _cfg.Seed = (uint)UnityEngine.Random.Range(1, 999999);
        }

        public void Hide()
        {
            _screen = Page.None;
            _root.gameObject.SetActive(false);
            _app.Discovery.Stop();
        }

        void Update()
        {
            if (_screen == Page.None || Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.25f;
            if (_screen == Page.Join && _app.Discovery.Poll()) ShowJoin();
            for (int i = 0; i < _refreshers.Count; i++) _refreshers[i]();
        }

        /// <summary>Android back button / Escape.</summary>
        public void Back()
        {
            if (_screen == Page.Main || _screen == Page.None) return;
            if (_screen == Page.Maps) { ShowSetup(_mode); return; }
            if (_screen == Page.HostLobby || _screen == Page.JoinWaiting || _screen == Page.Join) _app.StopNetworking();
            ShowMain();
        }

        /// <summary>Clears the screen and returns a centred panel to fill.</summary>
        RectTransform Begin(Page screen, float width, float height)
        {
            _screen = screen;
            _refreshers.Clear();
            _root.gameObject.SetActive(true);
            Ui.Clear(_root);
            var panel = Ui.Box("Panel", _root, Ui.PanelColor).rectTransform;
            panel.Pin(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(width, height));
            return panel;
        }

        // ------------------------------------------------------------------ main menu

        public void ShowMain()
        {
            _app.Discovery.Stop();
            var panel = Begin(Page.Main, 460, 640);
            var col = Ui.Column(panel, 10, 24);
            col.Stretch();
            Ui.Label(col, "HEXFRONT", 60, TextAnchor.MiddleCenter, Ui.Accent).Size(-1, 78).fontStyle = FontStyle.Bold;
            Ui.Label(col, "Simultaneous-turn tactics", 19, TextAnchor.MiddleCenter, Ui.Dim).Size(-1, 26);
            if (SaveSystem.HasMatch)
                Ui.Button(col, "Continue Match", () => { if (!_app.ContinueSavedMatch()) { SaveSystem.DeleteMatch(); ShowMain(); } }, 24, Ui.Good).Size(-1, 60);
            Ui.Button(col, "Skirmish vs AI", () => ShowSetup(MatchMode.VsAi), 24).Size(-1, 60);
            Ui.Button(col, "Host LAN Game", () => ShowSetup(MatchMode.LanHost), 24).Size(-1, 60);
            Ui.Button(col, "Join LAN Game", ShowJoin, 24).Size(-1, 60);
            Ui.Button(col, "Pass and Play", () => ShowSetup(MatchMode.PassAndPlay), 24).Size(-1, 60);
            Ui.Button(col, "How to Play", ShowHelp, 22).Size(-1, 54);
            var row = Ui.Row(col, 8, 46);
            Ui.Label(row, "Your name", 18, TextAnchor.MiddleLeft, Ui.Dim).Size(110, -1);
            var nameField = Ui.Input(row, "Commander", App.PlayerName, v => App.PlayerName = v).Size(-1, -1, 1);
            nameField.characterLimit = 16;
        }

        // ------------------------------------------------------------------ match setup

        public void ShowSetup(MatchMode mode)
        {
            _mode = mode;
            _app.Discovery.Stop();
            var panel = Begin(Page.Setup, 940, 660);
            var cols = Ui.Row(panel, 18);
            cols.Stretch(20, 18, 20, 18);
            cols.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.UpperLeft;

            // ---- left: settings
            var left = Ui.Column(cols, 6).Size(-1, -1, 1);
            string title = mode == MatchMode.VsAi ? "Skirmish vs AI" : mode == MatchMode.LanHost ? "Host LAN Game" : "Pass and Play";
            Ui.Label(left, title, 30, TextAnchor.MiddleLeft, Ui.Accent).Size(-1, 44);
            var scroll = Ui.Scroll(left, out var list, 4);
            scroll.Size(-1, 500);

            int mapCount = PremadeMaps.All.Length + 1;
            Ui.Stepper(list, "Map", () => _cfg.Premade >= 0 ? PremadeMaps.All[_cfg.Premade].Name : "Random map", d =>
            {
                int index = _cfg.Premade >= 0 ? _cfg.Premade : PremadeMaps.All.Length;
                index = (index + d + mapCount) % mapCount;
                _cfg.Premade = index < PremadeMaps.All.Length ? index : -1;
                ShowSetup(_mode); // the rows below depend on this choice
            }, _refreshers);

            if (_cfg.Premade < 0)
            {
                Ui.Stepper(list, "Region", () => MapConfig.RegionNames[(int)_cfg.Region], d => { _cfg.Region = (Region)(((int)_cfg.Region + d + 6) % 6); Changed(); }, _refreshers);
                Ui.Stepper(list, "Map size", () => SizeNames[_sizeIndex], d =>
                {
                    _sizeIndex = (_sizeIndex + d + Sizes.Length) % Sizes.Length;
                    _cfg.Width = Sizes[_sizeIndex][0]; _cfg.Height = Sizes[_sizeIndex][1];
                    Changed();
                }, _refreshers);
                Ui.Stepper(list, "Terrain density", () => _cfg.TerrainDensity + "%", d => { _cfg.TerrainDensity = Mathf.Clamp(_cfg.TerrainDensity + d * 10, 0, 100); Changed(); }, _refreshers);
                Ui.Stepper(list, "Water coverage", () => _cfg.WaterCoverage + "%", d => { _cfg.WaterCoverage = Mathf.Clamp(_cfg.WaterCoverage + d * 5, 0, 50); Changed(); }, _refreshers);
                Ui.Stepper(list, "Resource richness", () => _cfg.ResourceDensity + "%", d => { _cfg.ResourceDensity = Mathf.Clamp(_cfg.ResourceDensity + d * 10, 0, 100); Changed(); }, _refreshers);
                Ui.Stepper(list, "Objectives", () => (_cfg.Objectives * 2) + " sites", d => { _cfg.Objectives = Mathf.Clamp(_cfg.Objectives + d, 1, 8); Changed(); }, _refreshers);
                Ui.Stepper(list, "Starting distance", () => DistanceNames[_cfg.StartDistance], d => { _cfg.StartDistance = (_cfg.StartDistance + d + 3) % 3; Changed(); }, _refreshers);
                Ui.Stepper(list, "Seed", () => _cfg.Seed.ToString(), d => { _cfg.Seed = (uint)Mathf.Max(1, (int)_cfg.Seed + d); Changed(); }, _refreshers);
                Ui.Button(list, "New random seed", () => { _cfg.Seed = (uint)UnityEngine.Random.Range(1, 999999); Changed(); }, 19).Size(-1, 42);
            }

            Ui.Stepper(list, "Starting Credits", () => _cfg.StartingCredits.ToString(), d => _cfg.StartingCredits = Mathf.Clamp(_cfg.StartingCredits + d * 100, 300, 2000), _refreshers);
            Ui.Stepper(list, "Fog of war", () => _cfg.FogOfWar ? "On" : "Off", d => _cfg.FogOfWar = !_cfg.FogOfWar, _refreshers);
            Ui.Stepper(list, "Round limit", () => RoundLimits[_roundLimitIndex] == 0 ? "None" : RoundLimits[_roundLimitIndex] + " rounds", d =>
            {
                _roundLimitIndex = (_roundLimitIndex + d + RoundLimits.Length) % RoundLimits.Length;
                _cfg.MaxRounds = RoundLimits[_roundLimitIndex];
            }, _refreshers);
            Ui.Stepper(list, "Turn timer", () => Timers[_timerIndex] == 0 ? "Off" : Timers[_timerIndex] + " seconds", d => _timerIndex = (_timerIndex + d + Timers.Length) % Timers.Length, _refreshers);
            if (mode == MatchMode.VsAi)
                Ui.Stepper(list, "AI difficulty", () => _ai.ToString(), d => _ai = (AiDifficulty)(((int)_ai + d + 3) % 3), _refreshers);

            // ---- right: preview and actions
            var right = Ui.Column(cols, 8).Size(330, -1);
            var img = Ui.Rect("Preview", right).gameObject.AddComponent<RawImage>();
            img.raycastTarget = false;
            _previewImage = img;
            _previewInfo = Ui.Label(right, "", 16, TextAnchor.UpperLeft, Ui.Dim).Size(-1, 92);
            var saveRow = Ui.Row(right, 6, 44);
            Ui.Input(saveRow, "Map name", _mapName, v => _mapName = v).Size(-1, -1, 1);
            Ui.Button(saveRow, "Save", SaveCurrentMap, 18).Size(80, -1);
            Ui.Button(right, "Saved Maps / Enter Code", ShowMaps, 19).Size(-1, 46);
            Ui.Button(right, mode == MatchMode.LanHost ? "Open Lobby" : "Start Match", StartFromSetup, 26, Ui.Good).Size(-1, 70);
            Ui.Button(right, "Back", ShowMain, 20).Size(-1, 46);
            Changed();
        }

        RawImage _previewImage;
        Text _previewInfo;

        /// <summary>Called whenever a map setting changes: regenerate the preview.</summary>
        void Changed()
        {
            if (_previewImage == null) return;
            var copy = CloneConfig();
            var s = MapGenerator.CreateMatch(copy);
            if (_preview != null) Destroy(_preview);
            _preview = RenderPreview(s);
            _previewImage.texture = _preview;
            _previewImage.Size(-1, Mathf.Min(300f, 330f * _preview.height / _preview.width));
            string description = _cfg.Premade >= 0 ? PremadeMaps.All[_cfg.Premade].Description : MapConfig.RegionNames[(int)_cfg.Region] + ", " + s.Width + " x " + s.Height + " hexes.";
            _previewInfo.text = description + "\nShare code: <color=#FFC040>" + _cfg.ToCode() + "</color>";
        }

        MapConfig CloneConfig()
        {
            return new MapConfig
            {
                Premade = _cfg.Premade, Width = _cfg.Width, Height = _cfg.Height, Region = _cfg.Region, TerrainDensity = _cfg.TerrainDensity,
                WaterCoverage = _cfg.WaterCoverage, ResourceDensity = _cfg.ResourceDensity, Objectives = _cfg.Objectives, StartDistance = _cfg.StartDistance,
                Seed = _cfg.Seed, StartingCredits = _cfg.StartingCredits, FogOfWar = _cfg.FogOfWar, MaxRounds = _cfg.MaxRounds,
            };
        }

        /// <summary>A tiny top-down picture of the map: 4x4 pixels per hex, odd rows shifted like the real grid.</summary>
        static Texture2D RenderPreview(GameState s)
        {
            const int px = 4;
            int w = (s.Width * 2 + 1) * (px / 2), h = s.Height * px;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[w * h];
            var region = (Region)s.Region;
            for (int i = 0; i < s.TileCount; i++)
            {
                int col = i % s.Width, row = i / s.Width;
                Color c = Gfx.TerrainColor(s.Terrain[i], region);
                if (s.Terrain[i] == TerrainType.Forest) c = Gfx.Mul(c, 0.72f);
                if (s.Terrain[i] == TerrainType.Mountain) c = Gfx.Mul(c, 0.8f);
                if (s.Site[i] != SiteType.None) c = new Color(1f, 0.85f, 0.1f);
                var b = s.BuildingAt(s.HexAt(i));
                if (b != null) c = Gfx.Team[b.Owner];
                int x0 = (col * 2 + (row & 1)) * (px / 2), y0 = (s.Height - 1 - row) * px;
                for (int y = 0; y < px; y++)
                    for (int x = 0; x < px; x++)
                        pixels[(y0 + y) * w + x0 + x] = c;
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            return tex;
        }

        void StartFromSetup()
        {
            var cfg = CloneConfig();
            int timer = Timers[_timerIndex];
            if (_mode == MatchMode.LanHost)
            {
                if (!_app.OpenHostLobby(cfg, timer)) ShowMessage("Could not open a network port. Check that Wi-Fi is on.", ShowMain);
            }
            else _app.StartLocalMatch(cfg, _mode, _ai, timer);
        }

        void ShowMessage(string message, Action onOk)
        {
            var panel = Begin(Page.Help, 560, 260);
            var col = Ui.Column(panel, 12, 24);
            col.Stretch();
            Ui.Label(col, message, 22, TextAnchor.MiddleCenter).Size(-1, 130);
            Ui.Button(col, "OK", onOk, 22, Ui.Good).Size(-1, 56);
        }

        // ------------------------------------------------------------------ saved maps

        void SaveCurrentMap()
        {
            var maps = SaveSystem.LoadMaps();
            string name = string.IsNullOrWhiteSpace(_mapName) ? App.DescribeConfig(_cfg) + " #" + _cfg.Seed : _mapName.Trim();
            maps.RemoveAll(m => m.Code == _cfg.ToCode());
            maps.Add(new SavedMap { Name = name, Code = _cfg.ToCode() });
            SaveSystem.SaveMaps(maps);
            ShowMaps();
        }

        void ShowMaps()
        {
            var panel = Begin(Page.Maps, 700, 640);
            var col = Ui.Column(panel, 8, 20);
            col.Stretch();
            Ui.Label(col, "Saved Maps", 30, TextAnchor.MiddleLeft, Ui.Accent).Size(-1, 44);

            var codeRow = Ui.Row(col, 8, 48);
            Ui.Input(codeRow, "Enter a shared map code (HF-...)", _codeInput, v => _codeInput = v).Size(-1, -1, 1);
            Ui.Button(codeRow, "Load", () =>
            {
                if (MapConfig.TryParse(_codeInput, out var parsed)) { LoadMap(parsed); ShowSetup(_mode); }
                else ShowMessage("That is not a valid map code.", ShowMaps);
            }, 19, Ui.Good).Size(100, -1);

            var scroll = Ui.Scroll(col, out var list);
            scroll.Size(-1, 400);
            var maps = SaveSystem.LoadMaps();
            maps.Sort((a, b) => a.Favorite != b.Favorite ? (a.Favorite ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            if (maps.Count == 0)
                Ui.Label(list, "No saved maps yet. Set up a map you like, give it a name and press Save.", 19, TextAnchor.MiddleCenter, Ui.Dim).Size(-1, 80);
            foreach (var map in maps)
            {
                var m = map;
                var row = Ui.Row(list, 6, 56);
                Ui.Button(row, m.Favorite ? "FAV" : "fav", () => { m.Favorite = !m.Favorite; SaveSystem.SaveMaps(maps); ShowMaps(); }, 15, m.Favorite ? Ui.Accent : Ui.ButtonColor).Size(56, -1);
                var play = Ui.Button(row, "", () => { if (MapConfig.TryParse(m.Code, out var parsed)) { LoadMap(parsed); _mapName = m.Name; ShowSetup(_mode); } }, 17).Size(-1, -1, 1);
                var label = Ui.LabelOf(play);
                label.alignment = TextAnchor.MiddleLeft;
                label.text = "<b>" + m.Name + "</b>\n<size=13>" + m.Code + "</size>";
                Ui.Button(row, "DEL", () => { maps.Remove(m); SaveSystem.SaveMaps(maps); ShowMaps(); }, 15, Ui.Bad).Size(60, -1);
            }
            Ui.Button(col, "Back", () => ShowSetup(_mode), 20).Size(-1, 48);
        }

        /// <summary>Take the map part of a parsed code but keep the current match rules.</summary>
        void LoadMap(MapConfig parsed)
        {
            parsed.StartingCredits = _cfg.StartingCredits; parsed.FogOfWar = _cfg.FogOfWar; parsed.MaxRounds = _cfg.MaxRounds;
            _cfg = parsed;
            for (int i = 0; i < Sizes.Length; i++) if (Sizes[i][0] == _cfg.Width && Sizes[i][1] == _cfg.Height) _sizeIndex = i;
        }

        // ------------------------------------------------------------------ LAN lobby

        public void ShowHostLobby()
        {
            var panel = Begin(Page.HostLobby, 620, 520);
            var col = Ui.Column(panel, 10, 24);
            col.Stretch();
            Ui.Label(col, "LAN Lobby", 32, TextAnchor.MiddleLeft, Ui.Accent).Size(-1, 46);
            Ui.Label(col, _app.LobbyText, 21).Size(-1, 30);
            string addresses = "";
            foreach (var a in LanDiscovery.LocalAddresses()) addresses += (addresses.Length > 0 ? ", " : "") + a;
            Ui.Label(col, "Both devices must be on the same Wi-Fi network.\nIf the game does not show up on the other device, join by address:\n<color=#FFC040>" +
                          (addresses.Length > 0 ? addresses : "no network found") + "</color>   port " + (_app.Host != null ? _app.Host.Port : 0),
                18, TextAnchor.UpperLeft, Ui.Dim).Size(-1, 96);
            var status = Ui.Label(col, "", 22, TextAnchor.MiddleCenter).Size(-1, 60);
            var start = Ui.Button(col, "Start Match", () => _app.HostStartMatch(), 26, Ui.Good).Size(-1, 70);
            Ui.Button(col, "Cancel", () => { _app.StopNetworking(); ShowMain(); }, 20).Size(-1, 48);
            Action refresh = () =>
            {
                if (status == null) return;
                bool has = _app.HostHasClient;
                status.text = has ? "<color=#58D070>" + _app.HostClientName + " has joined</color>" : "Waiting for a player to join...";
                start.interactable = has;
            };
            refresh();
            _refreshers.Add(refresh);
        }

        public void ShowJoin()
        {
            if (_screen != Page.Join) _app.Discovery.StartListening();
            var panel = Begin(Page.Join, 620, 580);
            var col = Ui.Column(panel, 10, 24);
            col.Stretch();
            Ui.Label(col, "Join LAN Game", 32, TextAnchor.MiddleLeft, Ui.Accent).Size(-1, 46);
            Ui.Label(col, "Games found on this Wi-Fi network:", 19, TextAnchor.MiddleLeft, Ui.Dim).Size(-1, 26);
            var scroll = Ui.Scroll(col, out var list);
            scroll.Size(-1, 250);
            if (_app.Discovery.Lobbies.Count == 0)
                Ui.Label(list, "Searching...", 20, TextAnchor.MiddleCenter, Ui.Dim).Size(-1, 60);
            foreach (var lobby in _app.Discovery.Lobbies)
            {
                var l = lobby;
                var b = Ui.Button(list, "", () => _app.JoinLobby(l.Address, l.Port), 19).Size(-1, 62);
                var label = Ui.LabelOf(b);
                label.alignment = TextAnchor.MiddleLeft;
                label.text = "<b>" + l.Name + "'s game</b>\n<size=14>" + l.Info + "   (" + l.Address + ")</size>";
            }
            Ui.Label(col, "Or connect directly to the host's address:", 19, TextAnchor.MiddleLeft, Ui.Dim).Size(-1, 26);
            var row = Ui.Row(col, 8, 48);
            Ui.Input(row, "192.168.x.x", _ipInput, v => _ipInput = v).Size(-1, -1, 1);
            Ui.Button(row, "Connect", () =>
            {
                string address = (_ipInput ?? "").Trim(); int port = NetHost.DefaultPort;
                int colon = address.LastIndexOf(':');
                if (colon > 0 && int.TryParse(address.Substring(colon + 1), out int parsedPort)) { port = parsedPort; address = address.Substring(0, colon); }
                if (address.Length > 0) _app.JoinLobby(address, port);
            }, 20, Ui.Good).Size(130, -1);
            Ui.Button(col, "Back", () => { _app.StopNetworking(); ShowMain(); }, 20).Size(-1, 48);
        }

        public void ShowJoinWaiting()
        {
            _app.Discovery.Stop();
            var panel = Begin(Page.JoinWaiting, 600, 360);
            var col = Ui.Column(panel, 10, 24);
            col.Stretch();
            Ui.Label(col, "Join LAN Game", 32, TextAnchor.MiddleLeft, Ui.Accent).Size(-1, 46);
            var status = Ui.Label(col, "", 22, TextAnchor.MiddleCenter).Size(-1, 150);
            Ui.Button(col, "Cancel", () => { _app.StopNetworking(); ShowMain(); }, 20).Size(-1, 52);
            Action refresh = () =>
            {
                if (status == null) return;
                bool connected = _app.Client != null && _app.Client.IsConnected;
                status.text = (_app.NetProblem ? "<color=#E05040>" + _app.NetStatus + "</color>" : _app.NetStatus) +
                              (connected ? "\n\n" + _app.LobbyText + "\nWaiting for the host to start the match..." : "");
            };
            refresh();
            _refreshers.Add(refresh);
        }

        /// <summary>The app tells us when lobby state changed (player joined, connection failed...).</summary>
        public void OnLobbyChanged()
        {
            for (int i = 0; i < _refreshers.Count; i++) _refreshers[i]();
        }

        // ------------------------------------------------------------------ help

        void ShowHelp()
        {
            var panel = Begin(Page.Help, 760, 640);
            var col = Ui.Column(panel, 8, 22);
            col.Stretch();
            Ui.Label(col, "How to Play", 30, TextAnchor.MiddleLeft, Ui.Accent).Size(-1, 44);
            var scroll = Ui.Scroll(col, out var list, 16);
            scroll.Size(-1, 480);
            foreach (string page in Hud.HelpPages)
            {
                Ui.Label(list, page, 18, TextAnchor.UpperLeft); // the layout group sizes it to fit its text
            }
            Ui.Button(col, "Back", ShowMain, 20).Size(-1, 48);
        }
    }
}
