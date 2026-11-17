using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Hexfront.Core;
using UnityEngine;

namespace Hexfront
{
    /// <summary>
    /// The conductor. Owns the camera, board, UI and network objects, and runs the round loop:
    ///   Planning -> (both players locked in) -> Simulation.Resolve -> playback -> Planning ...
    /// All game rules live in Hexfront.Core; this class only moves data between the pieces.
    /// </summary>
    public sealed class App : MonoBehaviour
    {
        public static App Instance { get; private set; }

        public enum Phase { Menu, Planning, PassDevice, Waiting, Executing, GameOver }

        public CameraRig Rig { get; private set; }
        public BoardView Board { get; private set; }
        public Hud Hud { get; private set; }
        public MenuUi Menu { get; private set; }

        // ---- current match
        public GameState State { get; private set; }
        public MatchMode Mode { get; private set; }
        public AiDifficulty Ai { get; private set; }
        public int TurnTimer { get; private set; }
        public int LocalPlayer { get; private set; }
        public Planner Planner { get; private set; }
        public Phase CurrentPhase { get; private set; } = Phase.Menu;
        public float TimerLeft { get; private set; }
        public bool OpponentReady { get; private set; }
        public string NetStatus { get; private set; } = "";
        public bool NetProblem { get; private set; }
        public Hex? InspectedHex { get; private set; }
        public int InspectedEnemyId { get; private set; }

        // ---- networking
        public NetHost Host { get; private set; }
        public NetClient Client { get; private set; }
        public readonly LanDiscovery Discovery = new LanDiscovery();
        public string LobbyText { get; private set; } = "";
        public bool HostHasClient => Host != null && Host.HasClient;
        public string HostClientName => Host != null ? Host.ClientName : "";

        readonly PlayerOrders[] _submitted = new PlayerOrders[2];
        Camera _cam;
        Coroutine _playback;
        bool _matchStarted;
        MapConfig _hostConfig;
        GameState _hostResumeState;

        public static string PlayerName
        {
            get => PlayerPrefs.GetString("hf_name", "Commander");
            set { PlayerPrefs.SetString("hf_name", string.IsNullOrWhiteSpace(value) ? "Commander" : value.Trim()); PlayerPrefs.Save(); }
        }

        // ------------------------------------------------------------------ lifecycle

        void Awake()
        {
            Instance = this;
            Application.targetFrameRate = 60;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
            Screen.autorotateToPortrait = false;
            Screen.autorotateToPortraitUpsideDown = false;
            Screen.autorotateToLandscapeLeft = true;
            Screen.autorotateToLandscapeRight = true;
            Screen.orientation = ScreenOrientation.AutoRotation;

            Gfx.Init();
            Ui.Init();

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            camGo.transform.SetParent(transform, false);
            _cam = camGo.AddComponent<Camera>();
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.fieldOfView = 34f;
            _cam.nearClipPlane = 0.5f;
            _cam.farClipPlane = 220f;
            camGo.AddComponent<AudioListener>();
            Rig = camGo.AddComponent<CameraRig>();
            Rig.Cam = _cam;
            Rig.Tapped = OnTap;

            gameObject.AddComponent<Sfx>();
            Board = new GameObject("BoardView").AddComponent<BoardView>();
            Board.transform.SetParent(transform, false);
            Board.Init(_cam);

            Hud = new GameObject("Hud").AddComponent<Hud>();
            Hud.transform.SetParent(transform, false);
            Hud.Init(this, Ui.CreateCanvas("HudCanvas", 0, transform));
            Menu = new GameObject("Menu").AddComponent<MenuUi>();
            Menu.transform.SetParent(transform, false);
            Menu.Init(this, Ui.CreateCanvas("MenuCanvas", 10, transform));

            GoToMenu();
        }

        void OnDestroy()
        {
            StopNetworking();
            if (Instance == this) Instance = null;
        }

        void OnApplicationPause(bool paused)
        {
            if (paused) AutoSave();
        }

        void Update()
        {
            GameInput.Update();
            Ui.RefreshSafeAreas();
            Host?.Poll();
            Client?.Poll();

            if (CurrentPhase == Phase.Menu)
            {
                Rig.RotateBy(4f * Time.unscaledDeltaTime); // slowly orbit the backdrop battlefield
                if (GameInput.Back) Menu.Back();
                return;
            }
            if (GameInput.Back) Hud.TogglePause();

            if (CurrentPhase == Phase.Planning && TurnTimer > 0 && !Hud.IsPaused)
            {
                TimerLeft -= Time.unscaledDeltaTime;
                if (TimerLeft <= 0f) { Hud.Toast("Time is up - orders locked in", false); LockIn(); }
            }
        }

        // ------------------------------------------------------------------ menu / backdrop

        /// <summary>Leave any match and show the main menu over a slowly rotating random battlefield.</summary>
        public void GoToMenu()
        {
            if (_playback != null) { StopCoroutine(_playback); _playback = null; }
            Board.CancelPlayback();
            StopNetworking();
            CurrentPhase = Phase.Menu;
            Planner = null; _matchStarted = false; _hostResumeState = null;
            _submitted[0] = _submitted[1] = null;
            NetStatus = ""; NetProblem = false;

            var cfg = new MapConfig
            {
                Region = (Region)UnityEngine.Random.Range(0, 6), Seed = (uint)UnityEngine.Random.Range(1, 999999),
                Width = 22, Height = 14, FogOfWar = false,
            };
            State = MapGenerator.CreateMatch(cfg);
            Board.Build(State);
            Board.Sync(-1, null);
            Rig.SetBounds(Board.WorldBounds);
            Rig.Snap(new Vector3(Board.WorldBounds.center.x, 0, Board.WorldBounds.center.y), 20f, 24f);
            Rig.InputEnabled = false;
            Hud.Hide();
            Menu.ShowMain();
        }

        // ------------------------------------------------------------------ starting matches

        public void StartLocalMatch(MapConfig cfg, MatchMode mode, AiDifficulty ai, int turnTimer)
        {
            StopNetworking();
            StartMatch(MapGenerator.CreateMatch(cfg), mode, ai, turnTimer, 0);
        }

        public bool ContinueSavedMatch()
        {
            var saved = SaveSystem.LoadMatch();
            if (saved == null || saved.State == null || saved.State.IsOver) return false;
            if (saved.Mode == MatchMode.LanHost)
            {
                OpenHostLobby(null, saved.TurnTimer, saved.State);
                return true;
            }
            StopNetworking();
            StartMatch(saved.State, saved.Mode == MatchMode.LanClient ? MatchMode.VsAi : saved.Mode, saved.Ai, saved.TurnTimer, 0);
            return true;
        }

        void StartMatch(GameState state, MatchMode mode, AiDifficulty ai, int turnTimer, int localPlayer)
        {
            if (_playback != null) { StopCoroutine(_playback); _playback = null; }
            Board.CancelPlayback();
            State = state; Mode = mode; Ai = ai; TurnTimer = turnTimer; LocalPlayer = localPlayer;
            _submitted[0] = _submitted[1] = null;
            OpponentReady = false;
            _matchStarted = true;
            Board.Build(State);
            Rig.SetBounds(Board.WorldBounds);
            Rig.InputEnabled = true;
            FocusHeadquarters(LocalPlayer, true);
            Menu.Hide();
            Hud.Show();
            if (State.IsOver) { EnterGameOver(); return; }
            BeginPlanning();
            if (State.Round == 1 && PlayerPrefs.GetInt("hf_tutorial_seen", 0) == 0)
            {
                PlayerPrefs.SetInt("hf_tutorial_seen", 1);
                Hud.ShowHelp();
            }
        }

        public void FocusHeadquarters(int player, bool snap = false)
        {
            var hq = State.FindHeadquarters(player);
            Vector3 p = hq != null ? Board.WorldOf(hq.Pos) : new Vector3(Board.WorldBounds.center.x, 0, Board.WorldBounds.center.y);
            // Player 0 starts at the "top" of the map, so turn the camera to put your own base nearest.
            if (snap) Rig.Snap(p, LocalPlayer == 0 ? 180f : 0f, 17f);
            else Rig.Focus(p);
        }

        /// <summary>Where we think the enemy base is: its HQ if we have seen it, else the mirror of ours.</summary>
        public void FocusEnemyHeadquarters()
        {
            int enemy = 1 - LocalPlayer;
            var hq = State.FindHeadquarters(enemy);
            if (hq != null && (!State.FogOfWar || State.Explored[LocalPlayer][State.Index(hq.Pos)])) { Rig.Focus(Board.WorldOf(hq.Pos)); return; }
            var mine = State.FindHeadquarters(LocalPlayer);
            if (mine != null) Rig.Focus(Board.WorldOf(State.Mirror(mine.Pos)));
        }

        public void FocusSelection()
        {
            if (Planner == null) return;
            var u = State.GetUnit(Planner.SelectedUnit);
            if (u != null) { Rig.Focus(Board.WorldOf(u.Pos)); return; }
            var b = State.GetBuilding(Planner.SelectedBuilding);
            if (b != null) Rig.Focus(Board.WorldOf(b.Pos));
        }

        // ------------------------------------------------------------------ planning phase

        void BeginPlanning()
        {
            CurrentPhase = Phase.Planning;
            Planner = new Planner(State, LocalPlayer);
            InspectedHex = null; InspectedEnemyId = 0;
            TimerLeft = TurnTimer;
            Board.SetState(State);
            Board.Sync(LocalPlayer, Planner.Visible);
            Hud.HideOverlays();
            RefreshPlanning();
        }

        /// <summary>Redraw highlights and refresh the HUD after any change to the draft.</summary>
        public void RefreshPlanning()
        {
            DrawOverlay();
            Hud.Refresh();
        }

        void OnTap(Vector2 screen)
        {
            if (CurrentPhase != Phase.Planning || Planner == null || Hud.IsPaused) return;
            if (!Board.PickHex(Rig, screen, out Hex hex)) return;
            HandleTap(hex);
            RefreshPlanning();
        }

        void HandleTap(Hex hex)
        {
            var p = Planner;
            int tile = State.Index(hex);
            InspectedHex = hex; InspectedEnemyId = 0;

            if (p.BuildMode)
            {
                string err = p.Add(Order.Build(p.BuildType, hex));
                if (err != null) { Hud.Toast(err, true); return; }
                p.BuildMode = false;
                Sfx.Play(Sfx.Id.Order);
                return;
            }

            // What is on this hex?
            var ground = p.Occ.Ground[tile]; var air = p.Occ.Air[tile]; var building = p.Occ.Buildings[tile];
            bool visible = p.Visible[tile];
            var own = new List<int>();
            if (ground != null && ground.Owner == LocalPlayer) own.Add(ground.Id);
            if (air != null && air.Owner == LocalPlayer) own.Add(air.Id);
            if (building != null && building.Owner == LocalPlayer) own.Add(building.Id);

            var selected = State.GetUnit(p.SelectedUnit);
            if (selected != null && own.Count == 0)
            {
                // Enemy here? Attack it.
                int targetId = PickEnemy(selected, ground, air, building, visible, tile);
                if (targetId != 0)
                {
                    string err = p.Target(selected, targetId, hex);
                    if (err != null) Hud.Toast(err, true); else Sfx.Play(Sfx.Id.Order);
                    return;
                }
                // Otherwise it is a move order.
                if (selected.Def.Move > 0)
                {
                    string err = p.SetMove(selected, hex);
                    if (err != null) Hud.Toast(err, true); else Sfx.Play(Sfx.Id.Order);
                }
                return;
            }

            if (own.Count > 0)
            {
                // Tapping a stack again cycles through it (ground unit -> aircraft -> building -> nothing).
                int current = own.IndexOf(p.SelectedUnit != 0 ? p.SelectedUnit : p.SelectedBuilding);
                int next = current + 1;
                p.SelectedUnit = 0; p.SelectedBuilding = 0;
                if (next < own.Count)
                {
                    int id = own[next];
                    if (State.GetUnit(id) != null) p.SelectedUnit = id; else p.SelectedBuilding = id;
                    Sfx.Play(Sfx.Id.Click);
                }
                Hud.CloseList();
                return;
            }

            // Nothing of ours: just inspect.
            p.SelectedUnit = 0; p.SelectedBuilding = 0;
            Hud.CloseList();
            if (visible && ground != null) InspectedEnemyId = ground.Id;
            else if (visible && air != null) InspectedEnemyId = air.Id;
            else if (building != null && (visible || State.Explored[LocalPlayer][tile])) InspectedEnemyId = building.Id;
        }

        int PickEnemy(Unit attacker, Unit ground, Unit air, Building building, bool visible, int tile)
        {
            var def = attacker.Def;
            int fallback = 0;
            if (visible && ground != null && ground.Owner != LocalPlayer)
            {
                if (def.AttackVs(ground.Def.Category) > 0) return ground.Id;
                fallback = ground.Id;
            }
            if (visible && air != null && air.Owner != LocalPlayer)
            {
                if (def.AttackVs(UnitCategory.Air) > 0) return air.Id;
                if (fallback == 0) fallback = air.Id;
            }
            if (building != null && building.Owner != LocalPlayer && (visible || State.Explored[LocalPlayer][tile]))
            {
                if (def.AtkBuilding > 0 || fallback == 0) return building.Id;
            }
            return fallback;
        }

        void DrawOverlay()
        {
            var p = Planner;
            Board.OverlayBegin();
            if (p == null || CurrentPhase != Phase.Planning) { Board.OverlayEnd(); return; }
            Color team = Gfx.Team[LocalPlayer];

            if (p.BuildMode)
                for (int i = 0; i < State.TileCount; i++)
                    if (Rules.CheckBuildSite(State, p.Occ, LocalPlayer, State.HexAt(i)) == null)
                        Board.OverlayFill(State.HexAt(i), new Color(0.3f, 0.95f, 0.4f, 0.38f));

            var sel = State.GetUnit(p.SelectedUnit);
            if (sel != null && !p.BuildMode)
            {
                if (sel.Def.Move > 0)
                {
                    var field = Pathfinding.ReachableThisRound(State, p.Occ, sel, p.Visible);
                    for (int i = 0; i < State.TileCount; i++)
                        if (field.Cost[i] != Pathfinding.Unreachable && field.Cost[i] > 0)
                            Board.OverlayFill(State.HexAt(i), new Color(team.r, team.g, team.b, 0.26f));
                }
                // Enemies this unit could hit from its planned position.
                Hex from = p.PlannedPos(sel);
                foreach (var e in State.Units)
                    if (e.Owner != LocalPlayer && p.Visible[State.Index(e.Pos)] && Rules.PreviewAttack(State, sel, from, e.Id, out _, out _))
                        Board.OverlayRing(e.Pos, new Color(1f, 0.25f, 0.2f, 0.9f));
                foreach (var b in State.Buildings)
                    if (b.Owner != LocalPlayer && (p.Visible[State.Index(b.Pos)] || State.Explored[LocalPlayer][State.Index(b.Pos)]) &&
                        Rules.PreviewAttack(State, sel, from, b.Id, out _, out _))
                        Board.OverlayRing(b.Pos, new Color(1f, 0.25f, 0.2f, 0.9f));
                Board.OverlayRing(sel.Pos, Color.white);
            }
            var selB = State.GetBuilding(p.SelectedBuilding);
            if (selB != null) Board.OverlayRing(selB.Pos, Color.white);
            if (InspectedHex.HasValue && sel == null && selB == null && !p.BuildMode)
                Board.OverlayRing(InspectedHex.Value, new Color(1, 1, 1, 0.55f), 0.08f);

            // Every planned order stays drawn on the board so the whole plan can be reviewed.
            foreach (var o in p.Orders)
            {
                var u = State.GetUnit(o.ActorId);
                switch (o.Type)
                {
                    case OrderType.Move:
                        if (u != null) Board.OverlayPath(u.Pos, p.PlannedPath(u), new Color(team.r, team.g, team.b, u.Id == p.SelectedUnit ? 0.95f : 0.6f));
                        break;
                    case OrderType.Attack:
                    case OrderType.Seize:
                        if (u != null)
                        {
                            var tu = State.GetUnit(o.TargetId); var tb = State.GetBuilding(o.TargetId);
                            if (tu != null || tb != null)
                                Board.OverlayLine(p.PlannedPos(u), tu != null ? tu.Pos : tb.Pos,
                                    o.Type == OrderType.Attack ? new Color(1f, 0.2f, 0.15f, 0.85f) : new Color(0.8f, 0.4f, 1f, 0.85f));
                        }
                        break;
                    case OrderType.Defend:
                        if (u != null) Board.OverlayRing(u.Pos, new Color(0.9f, 0.9f, 1f, 0.9f), 0.22f);
                        break;
                    case OrderType.Repair:
                    {
                        var b = State.GetBuilding(o.ActorId);
                        Board.OverlayRing(u != null ? u.Pos : b != null ? b.Pos : default, new Color(0.3f, 0.95f, 0.4f, 0.9f), 0.22f);
                        break;
                    }
                    case OrderType.Build:
                        Board.OverlayFill(o.Target, new Color(1f, 0.8f, 0.2f, 0.5f));
                        Board.OverlayRing(o.Target, new Color(1f, 0.8f, 0.2f, 0.95f));
                        break;
                    case OrderType.Upgrade:
                    case OrderType.Purchase:
                    {
                        var b = State.GetBuilding(o.ActorId);
                        if (b != null) Board.OverlayRing(b.Pos, new Color(1f, 0.8f, 0.2f, 0.95f), 0.1f);
                        break;
                    }
                }
            }
            Board.OverlayEnd();
        }

        // ------------------------------------------------------------------ locking in and resolving

        /// <summary>The Ready button: the local player's orders become final.</summary>
        public void LockIn()
        {
            if (CurrentPhase != Phase.Planning || Planner == null) return;
            var orders = Planner.ToPlayerOrders();
            _submitted[LocalPlayer] = orders;
            Planner.SelectedUnit = Planner.SelectedBuilding = 0; Planner.BuildMode = false;
            Sfx.Play(Sfx.Id.Ready);
            Hud.CloseList();

            switch (Mode)
            {
                case MatchMode.VsAi:
                    _submitted[1] = AiPlayer.Plan(State, 1, Ai);
                    ResolveRound();
                    break;
                case MatchMode.PassAndPlay:
                    if (LocalPlayer == 0) EnterPassDevice(1);
                    else ResolveRound();
                    break;
                case MatchMode.LanHost:
                    Host?.Send(Msg.OpponentLocked);
                    if (_submitted[1] != null) ResolveRound(); else EnterWaiting();
                    break;
                case MatchMode.LanClient:
                    Client?.Send(Msg.Orders, orders.Serialize());
                    EnterWaiting();
                    break;
            }
        }

        void EnterWaiting()
        {
            CurrentPhase = Phase.Waiting;
            DrawOverlay();
            Hud.Refresh();
        }

        void EnterPassDevice(int nextPlayer)
        {
            CurrentPhase = Phase.PassDevice;
            Board.OverlayBegin(); Board.OverlayEnd();
            Hud.Refresh();
            Hud.ShowPassDevice(nextPlayer, () =>
            {
                LocalPlayer = nextPlayer;
                FocusHeadquarters(nextPlayer, true);
                BeginPlanning();
            });
        }

        /// <summary>Authoritative resolution (single player, pass and play, or LAN host).</summary>
        void ResolveRound()
        {
            var o0 = _submitted[0]; var o1 = _submitted[1];
            _submitted[0] = _submitted[1] = null;
            OpponentReady = false;
            int viewer = Mode == MatchMode.PassAndPlay ? -1 : LocalPlayer;
            bool[] before = VisibilityFor(viewer);
            int round = State.Round;
            var result = Simulation.Resolve(State, o0, o1);

            if (Mode == MatchMode.LanHost && Host != null)
            {
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms))
                {
                    byte[] a = (o0 ?? new PlayerOrders(0, round)).Serialize(), b = (o1 ?? new PlayerOrders(1, round)).Serialize();
                    w.Write(round); w.Write(a.Length); w.Write(a); w.Write(b.Length); w.Write(b); w.Write(result.StateHash);
                    w.Flush();
                    Host.Send(Msg.Round, ms.ToArray());
                }
            }
            AutoSave();
            _playback = StartCoroutine(Playback(result, before, viewer));
        }

        bool[] VisibilityFor(int viewer)
        {
            if (viewer >= 0) return Visibility.Compute(State, viewer);
            var a = Visibility.Compute(State, 0); var b = Visibility.Compute(State, 1);
            for (int i = 0; i < a.Length; i++) a[i] |= b[i];
            return a;
        }

        IEnumerator Playback(RoundResult result, bool[] before, int viewer)
        {
            CurrentPhase = Phase.Executing;
            Hud.HideOverlays();
            Hud.Refresh();
            bool[] after = VisibilityFor(viewer);
            Func<Hex, bool> canSee = h => State.InBounds(h) && (before[State.Index(h)] || after[State.Index(h)]);
            yield return Board.Play(result, canSee, viewer, msg => Hud.Log(msg));
            _playback = null;

            if (State.IsOver) { Board.Sync(viewer, null); EnterGameOver(); yield break; }
            if (Mode == MatchMode.PassAndPlay)
            {
                Board.Sync(-1, VisibilityFor(-1));
                EnterPassDevice(0);
            }
            else BeginPlanning();
        }

        void EnterGameOver()
        {
            CurrentPhase = Phase.GameOver;
            SaveSystem.DeleteMatch();
            Board.OverlayBegin(); Board.OverlayEnd();
            bool won = State.Winner == LocalPlayer;
            if (Mode != MatchMode.PassAndPlay) Sfx.Play(won ? Sfx.Id.Victory : Sfx.Id.Defeat);
            else Sfx.Play(Sfx.Id.Victory);
            Hud.Refresh();
            Hud.ShowGameOver();
        }

        void AutoSave()
        {
            if (!_matchStarted || State == null || State.IsOver || CurrentPhase == Phase.Menu) return;
            if (Mode == MatchMode.LanClient) return; // the host owns the authoritative save
            SaveSystem.SaveMatch(Mode, Ai, TurnTimer, State);
        }

        // ------------------------------------------------------------------ LAN: hosting

        /// <summary>Open a lobby and start advertising it. Pass a resume state to continue a saved LAN match.</summary>
        public bool OpenHostLobby(MapConfig cfg, int turnTimer, GameState resume = null)
        {
            StopNetworking();
            _hostConfig = cfg; _hostResumeState = resume; TurnTimer = turnTimer;
            _matchStarted = false;
            Mode = MatchMode.LanHost;
            Host = new NetHost
            {
                WelcomeProvider = BuildWelcome,
                ClientJoined = name =>
                {
                    NetProblem = false;
                    NetStatus = name + " connected";
                    if (_matchStarted) Hud.Toast(name + " reconnected", false);
                    Menu.OnLobbyChanged();
                },
                ClientLeft = () =>
                {
                    NetProblem = true;
                    NetStatus = "Opponent disconnected - waiting for them to rejoin";
                    Menu.OnLobbyChanged();
                },
                OrdersReceived = OnClientOrders,
                ResyncRequested = () => Host?.Send(Msg.Welcome, BuildWelcome()),
            };
            if (!Host.Start()) { Host = null; return false; }
            string mapName = resume != null ? resume.MapName + " (round " + resume.Round + ")" : DescribeConfig(cfg);
            LobbyText = mapName + (turnTimer > 0 ? "  -  " + turnTimer + "s turns" : "");
            string name = PlayerName;
            int port = Host.Port;
            Discovery.StartBroadcast(() => port + "|" + Clean(name) + "|" + Clean(LobbyText));
            NetStatus = "Waiting for a player to join";
            Menu.ShowHostLobby();
            return true;
        }

        static string Clean(string s) => (s ?? "").Replace('|', '/');

        public static string DescribeConfig(MapConfig cfg)
        {
            if (cfg.Premade >= 0) return PremadeMaps.All[cfg.Premade].Name;
            return MapConfig.RegionNames[(int)cfg.Region] + " " + cfg.Width + "x" + cfg.Height;
        }

        public void HostStartMatch()
        {
            if (Host == null || !Host.HasClient) return;
            var state = _hostResumeState ?? MapGenerator.CreateMatch(_hostConfig);
            StartMatch(state, MatchMode.LanHost, AiDifficulty.Normal, TurnTimer, 0);
            Host.Send(Msg.Welcome, BuildWelcome());
            NetStatus = "Connected";
        }

        byte[] BuildWelcome()
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                bool started = _matchStarted && State != null;
                w.Write((byte)1);                      // the joining player is always player 1
                w.Write(started);
                w.Write(_submitted[1] != null);        // "we already have your orders for this round"
                w.Write(_submitted[0] != null);        // "the host is ready"
                w.Write(TurnTimer);
                w.Write(LobbyText ?? "");
                byte[] state = started ? State.Serialize() : Array.Empty<byte>();
                w.Write(state.Length); w.Write(state);
                w.Flush();
                return ms.ToArray();
            }
        }

        void OnClientOrders(byte[] payload)
        {
            if (!_matchStarted || State == null || State.IsOver) return;
            PlayerOrders po;
            try { po = PlayerOrders.Deserialize(payload); } catch (Exception) { return; }
            if (po.Round != State.Round) return;     // stale or early: ignore
            if (_submitted[1] != null) return;        // duplicate: the first submission stands
            po.Player = 1;                            // a client can only ever command player 1
            _submitted[1] = po;
            OpponentReady = true;
            if (_submitted[0] != null && CurrentPhase == Phase.Waiting) ResolveRound();
            else Hud.Refresh();
        }

        // ------------------------------------------------------------------ LAN: joining

        public void JoinLobby(string address, int port)
        {
            StopNetworking();
            Mode = MatchMode.LanClient;
            NetStatus = "Connecting to " + address + "...";
            NetProblem = false;
            Client = new NetClient
            {
                Connected = () =>
                {
                    NetProblem = false; NetStatus = "Connected";
                    Client.AutoReconnect = true;
                    if (_matchStarted) Hud.Toast("Reconnected", false);
                    Menu.OnLobbyChanged();
                },
                ConnectFailed = () =>
                {
                    NetProblem = true; NetStatus = "Could not connect to " + address;
                    Menu.OnLobbyChanged();
                },
                Disconnected = () =>
                {
                    NetProblem = true;
                    NetStatus = _matchStarted ? "Connection lost - match paused, reconnecting..." : "Disconnected from host";
                    if (_matchStarted) Hud.Refresh(); else Menu.OnLobbyChanged();
                },
                Received = OnHostMessage,
            };
            Client.Connect(address, port, PlayerName);
            Menu.ShowJoinWaiting();
        }

        void OnHostMessage(NetMessage m)
        {
            try
            {
                switch (m.Type)
                {
                    case Msg.Welcome:
                        using (var r = new BinaryReader(new MemoryStream(m.Payload)))
                        {
                            r.ReadByte();
                            bool started = r.ReadBoolean(), ordersIn = r.ReadBoolean(), hostReady = r.ReadBoolean();
                            int timer = r.ReadInt32();
                            LobbyText = r.ReadString();
                            int length = r.ReadInt32();
                            byte[] state = r.ReadBytes(length);
                            TurnTimer = timer;
                            if (started) ApplyHostState(GameState.Deserialize(state), ordersIn, hostReady);
                            else Menu.OnLobbyChanged();
                        }
                        break;
                    case Msg.Lobby:
                        LobbyText = Encoding.UTF8.GetString(m.Payload);
                        Menu.OnLobbyChanged();
                        break;
                    case Msg.OpponentLocked:
                        OpponentReady = true;
                        Hud.Refresh();
                        break;
                    case Msg.Round:
                        OnHostRound(m.Payload);
                        break;
                    case Msg.Reject:
                        NetProblem = true;
                        NetStatus = Encoding.UTF8.GetString(m.Payload);
                        Client?.Stop();
                        Menu.OnLobbyChanged();
                        break;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("Bad message from host: " + e.Message);
                Client?.Send(Msg.ResyncRequest);
            }
        }

        /// <summary>The host's state always wins: used at match start, on reconnect and on any mismatch.</summary>
        void ApplyHostState(GameState state, bool ordersAlreadyIn, bool hostReady)
        {
            bool sameMatch = _matchStarted && Mode == MatchMode.LanClient && State != null &&
                             State.Width == state.Width && State.Height == state.Height && State.Seed == state.Seed && State.MapName == state.MapName;
            List<Order> draft = sameMatch && Planner != null && State.Round == state.Round ? Planner.Orders : null;

            if (!sameMatch) StartMatch(state, MatchMode.LanClient, AiDifficulty.Normal, TurnTimer, 1);
            else
            {
                if (_playback != null) { StopCoroutine(_playback); _playback = null; }
                Board.CancelPlayback();
                State = state;
                if (State.IsOver) { Board.SetState(State); Board.Sync(LocalPlayer, null); EnterGameOver(); return; }
                BeginPlanning();
            }
            if (State.IsOver) return;
            if (draft != null) { Planner.Orders = new List<Order>(draft); Planner.Rebuild(); }
            OpponentReady = hostReady;
            if (ordersAlreadyIn) { _submitted[1] = Planner.ToPlayerOrders(); EnterWaiting(); }
            else RefreshPlanning();
        }

        void OnHostRound(byte[] payload)
        {
            int round; PlayerOrders o0, o1; uint hash;
            using (var r = new BinaryReader(new MemoryStream(payload)))
            {
                round = r.ReadInt32();
                o0 = PlayerOrders.Deserialize(r.ReadBytes(r.ReadInt32()));
                o1 = PlayerOrders.Deserialize(r.ReadBytes(r.ReadInt32()));
                hash = r.ReadUInt32();
            }
            if (State == null || round != State.Round) { Client?.Send(Msg.ResyncRequest); return; }

            // Run the very same deterministic simulation locally, then check we agree with the host.
            _submitted[0] = _submitted[1] = null;
            OpponentReady = false;
            bool[] before = VisibilityFor(LocalPlayer);
            var result = Simulation.Resolve(State, o0, o1);
            if (result.StateHash != hash)
            {
                Hud.Toast("Out of sync - reloading from host", true);
                Client?.Send(Msg.ResyncRequest);
                return;
            }
            if (_playback != null) StopCoroutine(_playback);
            _playback = StartCoroutine(Playback(result, before, LocalPlayer));
        }

        public void StopNetworking()
        {
            Discovery.Stop();
            Host?.Stop(); Host = null;
            Client?.Stop(); Client = null;
        }

        /// <summary>Save (if we own the match) and return to the main menu.</summary>
        public void QuitToMenu()
        {
            AutoSave();
            GoToMenu();
        }
    }
}
