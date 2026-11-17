using System;
using System.Collections.Generic;
using Hexfront.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Hexfront
{
    /// <summary>The in-match interface: credits, selection panel, build/production lists, Ready button, overlays.</summary>
    public sealed class Hud : MonoBehaviour
    {
        enum ListMode { None, Build, Produce, Orders }

        App _app;
        RectTransform _root, _selPanel, _selButtons, _listPanel, _listContent, _overlay, _banner;
        Text _roundText, _creditsText, _statusText, _timerText, _selTitle, _selBody, _listTitle, _toast, _log, _bannerText;
        Button _readyButton, _buildButton, _ordersButton;
        ListMode _list = ListMode.None;
        float _toastUntil;
        readonly List<string> _logLines = new List<string>();
        float _logUntil;
        int _helpPage;

        public bool IsPaused { get; private set; }

        public void Init(App app, RectTransform root)
        {
            _app = app; _root = root;

            // ---- top bar
            var bar = Ui.Box("TopBar", root, Ui.PanelColor, false).rectTransform;
            bar.anchorMin = new Vector2(0, 1); bar.anchorMax = new Vector2(1, 1); bar.pivot = new Vector2(0.5f, 1);
            bar.offsetMin = new Vector2(0, -54); bar.offsetMax = Vector2.zero;
            var row = Ui.Row(bar, 14);
            row.Stretch(14, 4, 10, 4);
            _roundText = Ui.Label(row, "", 22).Size(150, -1);
            _creditsText = Ui.Label(row, "", 24, TextAnchor.MiddleLeft, Ui.Accent).Size(300, -1);
            _statusText = Ui.Label(row, "", 19, TextAnchor.MiddleCenter, Ui.Dim).Size(-1, -1, 1);
            _timerText = Ui.Label(row, "", 24, TextAnchor.MiddleRight).Size(70, -1);
            Ui.Button(row, "?", ShowHelp, 24).Size(52, -1);
            Ui.Button(row, "MENU", TogglePause, 18).Size(86, -1);

            // ---- camera buttons (accessibility: everything a gesture does also has a button)
            var cam = Ui.Column(root, 6);
            cam.Pin(new Vector2(0, 0.5f), new Vector2(10, 40), new Vector2(64, 330));
            Ui.Button(cam, "<", () => _app.Rig.RotateBy(-60f), 26).Size(-1, 50);
            Ui.Button(cam, ">", () => _app.Rig.RotateBy(60f), 26).Size(-1, 50);
            Ui.Button(cam, "+", () => _app.Rig.ZoomBy(0.75f), 26).Size(-1, 50);
            Ui.Button(cam, "-", () => _app.Rig.ZoomBy(1.33f), 26).Size(-1, 50);
            Ui.Button(cam, "HQ", () => _app.FocusHeadquarters(_app.LocalPlayer), 18, Gfx.Mul(Gfx.Team[0], 0.6f)).Size(-1, 50);
            Ui.Button(cam, "FOE", () => _app.FocusEnemyHeadquarters(), 16, Gfx.Mul(Gfx.Team[1], 0.6f)).Size(-1, 50);

            // ---- selection panel (bottom-left)
            _selPanel = Ui.Box("Selection", root, Ui.PanelColor).rectTransform;
            _selPanel.Pin(new Vector2(0, 0), new Vector2(84, 10), new Vector2(480, 232));
            var selCol = Ui.Column(_selPanel, 4, 10);
            selCol.Stretch();
            _selTitle = Ui.Label(selCol, "", 23, TextAnchor.MiddleLeft, Ui.Accent).Size(-1, 28);
            _selBody = Ui.Label(selCol, "", 16, TextAnchor.UpperLeft).Size(-1, 124);
            _selButtons = Ui.Row(selCol, 6, 46);

            // ---- right column (bottom-right)
            var right = Ui.Column(root, 8);
            right.Pin(new Vector2(1, 0), new Vector2(-10, 10), new Vector2(190, 214));
            _ordersButton = Ui.Button(right, "ORDERS", () => ToggleList(ListMode.Orders), 19).Size(-1, 48);
            _buildButton = Ui.Button(right, "BUILD", () => ToggleList(ListMode.Build), 21).Size(-1, 54);
            _readyButton = Ui.Button(right, "READY", () => _app.LockIn(), 28, Ui.Good).Size(-1, 88);

            // ---- list panel (build menu / production / orders)
            _listPanel = Ui.Box("List", root, Ui.PanelColor).rectTransform;
            _listPanel.Pin(new Vector2(1, 0), new Vector2(-10, 234), new Vector2(390, 410));
            var listCol = Ui.Column(_listPanel, 6, 10);
            listCol.Stretch();
            var head = Ui.Row(listCol, 6, 40);
            _listTitle = Ui.Label(head, "", 22, TextAnchor.MiddleLeft, Ui.Accent).Size(-1, -1, 1);
            Ui.Button(head, "X", CloseList, 20, Ui.Bad).Size(46, -1);
            var scroll = Ui.Scroll(listCol, out _listContent);
            scroll.Size(-1, 320);
            _listPanel.gameObject.SetActive(false);

            // ---- messages
            _toast = Ui.Label(root, "", 22, TextAnchor.MiddleCenter);
            _toast.rectTransform.Pin(new Vector2(0.5f, 1), new Vector2(0, -112), new Vector2(760, 40));
            AddOutline(_toast);
            _log = Ui.Label(root, "", 17, TextAnchor.UpperLeft);
            _log.rectTransform.Pin(new Vector2(0, 1), new Vector2(86, -62), new Vector2(520, 90));
            AddOutline(_log);

            _banner = Ui.Box("Banner", root, Ui.PanelColor).rectTransform;
            _banner.Pin(new Vector2(0.5f, 1), new Vector2(0, -62), new Vector2(440, 44));
            _bannerText = Ui.Label(_banner, "", 21, TextAnchor.MiddleCenter);
            _bannerText.rectTransform.Stretch();

            // ---- full-screen overlay (pause, pass the device, game over, help)
            var dim = Ui.Box("Overlay", root, new Color(0, 0, 0, 0.72f), false);
            _overlay = dim.rectTransform;
            _overlay.Stretch(-200, -200, -200, -200); // also cover the area outside the safe zone
            _overlay.gameObject.SetActive(false);

            Hide();
        }

        static void AddOutline(Text t)
        {
            var o = t.gameObject.AddComponent<Outline>();
            o.effectColor = new Color(0, 0, 0, 0.85f);
            o.effectDistance = new Vector2(1.5f, -1.5f);
        }

        public void Show() { _root.gameObject.SetActive(true); _logLines.Clear(); _log.text = ""; _toast.text = ""; }
        public void Hide() { _root.gameObject.SetActive(false); IsPaused = false; _overlay.gameObject.SetActive(false); }

        // ------------------------------------------------------------------ per-frame

        void Update()
        {
            if (_app == null || !_root.gameObject.activeSelf || _app.State == null) return;
            if (_toast.text.Length > 0 && Time.unscaledTime > _toastUntil) _toast.text = "";
            if (_log.text.Length > 0 && Time.unscaledTime > _logUntil) { _logLines.Clear(); _log.text = ""; }

            var s = _app.State;
            _roundText.text = "Round " + s.Round + (s.MaxRounds > 0 ? " / " + s.MaxRounds : "");
            var me = s.Players[_app.LocalPlayer];
            int income = s.ComputeIncome(_app.LocalPlayer);
            if (_app.Planner != null && _app.CurrentPhase == App.Phase.Planning)
            {
                int spending = me.Credits - _app.Planner.CreditsLeft;
                _creditsText.text = "$ " + _app.Planner.CreditsLeft + (spending > 0 ? "  (-" + spending + ")" : "") + "   +" + income + "/round";
            }
            else _creditsText.text = "$ " + me.Credits + "   +" + income + "/round";

            _statusText.text = _app.NetStatus;
            _statusText.color = _app.NetProblem ? Ui.Bad : Ui.Dim;
            if (_app.TurnTimer > 0 && _app.CurrentPhase == App.Phase.Planning)
            {
                int t = Mathf.CeilToInt(Mathf.Max(0, _app.TimerLeft));
                _timerText.text = t.ToString();
                _timerText.color = t <= 10 ? Ui.Bad : Ui.TextColor;
            }
            else _timerText.text = "";
        }

        // ------------------------------------------------------------------ refresh

        /// <summary>Rebuild everything that depends on the phase, selection or the draft orders.</summary>
        public void Refresh()
        {
            if (_app.State == null) return;
            var phase = _app.CurrentPhase;
            bool planning = phase == App.Phase.Planning && _app.Planner != null;

            _readyButton.gameObject.SetActive(planning);
            _buildButton.gameObject.SetActive(planning);
            _ordersButton.gameObject.SetActive(planning);
            if (planning) Ui.LabelOf(_ordersButton).text = "ORDERS (" + _app.Planner.Orders.Count + ")";

            string banner = "";
            if (phase == App.Phase.Waiting) banner = _app.NetProblem ? "Match paused - connection lost" : "Orders locked. Waiting for opponent...";
            else if (phase == App.Phase.Executing) banner = "Executing round " + (_app.State.Round - 1);
            else if (planning && _app.OpponentReady) banner = "Opponent is ready";
            _banner.gameObject.SetActive(banner.Length > 0);
            _bannerText.text = banner;

            if (!planning) { _selPanel.gameObject.SetActive(false); CloseList(); return; }
            BuildSelection();
            if (_list != ListMode.None) PopulateList();
        }

        void SelButton(string text, Action onClick, Color? color = null, bool enabled = true)
        {
            var b = Ui.Button(_selButtons, text, () => { onClick(); _app.RefreshPlanning(); }, 17, color).Size(-1, -1, 1);
            b.interactable = enabled;
            if (!enabled) Ui.LabelOf(b).color = Ui.Dim;
        }

        void BuildSelection()
        {
            Ui.Clear(_selButtons);
            var p = _app.Planner; var s = _app.State;
            string title = "", body = "";

            var unit = s.GetUnit(p.SelectedUnit);
            var building = s.GetBuilding(p.SelectedBuilding);
            if (p.BuildMode)
            {
                var def = BuildingDefs.Get(p.BuildType);
                title = "Place " + def.Name;
                body = "Tap a green hex inside your territory.\n" + def.Description;
                SelButton("Cancel", () => p.BuildMode = false, Ui.Bad);
            }
            else if (unit != null)
            {
                var d = unit.Def;
                title = d.Name;
                body = "HP " + unit.Hp + "/" + d.MaxHp + "   Armor " + d.Armor + "   Move " + d.Move +
                       "   Range " + (d.MaxRange == 0 ? "-" : d.MinRange == d.MaxRange ? d.MaxRange.ToString() : d.MinRange + "-" + d.MaxRange) + "   Sight " + d.Vision +
                       "\nAttack  Inf " + d.AtkInfantry + "  Veh " + d.AtkVehicle + "  Air " + d.AtkAir + "  Bld " + d.AtkBuilding +
                       (d.Fuel > 0 ? "   Fuel " + unit.Fuel + "/" + d.Fuel : "") +
                       "\n" + d.Description + "\n" + PlanSummary(unit);
                p.TryGetAction(unit.Id, out var act);
                bool hasAny = p.TryGetMove(unit.Id, out _) || p.TryGetAction(unit.Id, out _);
                SelButton("Defend", () => Report(p.SetAction(unit, Order.Defend(unit.Id))), act.Type == OrderType.Defend && hasAny ? Ui.Accent : (Color?)null);
                if (unit.Hp < d.MaxHp)
                    SelButton("Repair " + Rules.RepairCost(unit), () => Report(p.SetAction(unit, Order.Repair(unit.Id))), null, Rules.HasRepairSupport(s, unit));
                if (d.Has(UnitFlags.Engineer))
                {
                    Building seizable = null;
                    foreach (var b in s.Buildings)
                        if (b.Owner != _app.LocalPlayer && b.Type != BuildingType.Headquarters && Hex.Distance(b.Pos, p.PlannedPos(unit)) <= 1 &&
                            b.Hp * 100 <= b.Def.MaxHp * Balance.SeizeThresholdPercent && p.Visible[s.Index(b.Pos)]) { seizable = b; break; }
                    if (seizable != null) SelButton("Seize", () => Report(p.SetAction(unit, Order.Seize(unit.Id, seizable.Id))), new Color(0.6f, 0.3f, 0.8f));
                }
                SelButton("Clear", () => p.ClearUnit(unit.Id), null, hasAny);
                SelButton("Find", () => _app.FocusSelection());
            }
            else if (building != null)
            {
                var d = building.Def;
                title = d.Name + "  Lv " + building.Level + (d.MaxLevel > 1 ? "/" + d.MaxLevel : "");
                body = "HP " + building.Hp + "/" + d.MaxHp + "   Armor " + d.Armor + (building.Income > 0 ? "   Income +" + building.Income : "") +
                       "\n" + (!building.IsComplete ? "Under construction: " + building.BuildRoundsLeft + " round(s) left"
                              : building.UpgradeRoundsLeft > 0 ? "Upgrading (offline this round)" : d.Description);
                bool hasOrder = p.TryGetBuildingOrder(building.Id, out var bo);
                if (hasOrder) body += "\nPlan: " + p.Describe(bo);
                if (d.IsProducer) SelButton("Units", () => OpenList(ListMode.Produce), Ui.Accent, building.IsOperational);
                if (building.Level < d.MaxLevel)
                    SelButton("Upgrade " + Rules.UpgradeCost(building), () => { p.ClearBuilding(building.Id); Report(p.Add(Order.Upgrade(building.Id))); }, null, building.IsOperational);
                if (building.Hp < d.MaxHp && building.IsComplete)
                    SelButton("Repair " + Rules.RepairCost(building), () => Report(p.Add(Order.Repair(building.Id))));
                if (hasOrder) SelButton("Cancel", () => p.ClearBuilding(building.Id), Ui.Bad);
            }
            else if (_app.InspectedHex.HasValue)
            {
                var h = _app.InspectedHex.Value; int i = s.Index(h);
                bool known = !s.FogOfWar || s.Explored[_app.LocalPlayer][i];
                var enemyUnit = s.GetUnit(_app.InspectedEnemyId); var enemyBuilding = s.GetBuilding(_app.InspectedEnemyId);
                if (enemyUnit != null)
                {
                    var d = enemyUnit.Def;
                    title = "Enemy " + d.Name;
                    body = "HP " + enemyUnit.Hp + "/" + d.MaxHp + "   Armor " + d.Armor + "   Move " + d.Move + "   Range " + d.MaxRange +
                           "\nAttack  Inf " + d.AtkInfantry + "  Veh " + d.AtkVehicle + "  Air " + d.AtkAir + "  Bld " + d.AtkBuilding + "\n" + d.Description +
                           "\nSelect one of your units, then tap this enemy to attack it.";
                }
                else if (enemyBuilding != null)
                {
                    title = "Enemy " + enemyBuilding.Def.Name;
                    body = "HP " + enemyBuilding.Hp + "/" + enemyBuilding.Def.MaxHp + "   Armor " + enemyBuilding.Def.Armor + "   Lv " + enemyBuilding.Level + "\n" + enemyBuilding.Def.Description;
                }
                else if (!known) { title = "Unexplored"; body = "Send a unit closer to reveal this area."; }
                else
                {
                    var t = s.Terrain[i];
                    title = TerrainDefs.Name(t);
                    int cover = TerrainDefs.DefensePercent(t);
                    body = "Cover " + (cover >= 0 ? "+" : "") + cover + "%   " +
                           "Move cost: foot " + Cost(t, MoveClass.Foot) + ", wheels " + Cost(t, MoveClass.Wheeled) + ", tracks " + Cost(t, MoveClass.Tracked) +
                           (s.Elevation[i] >= 2 ? "\nHigh ground: +10% damage downhill, +1 sight" : "");
                    if (s.Site[i] != SiteType.None)
                        body += "\n" + SiteDefs.Name(s.Site[i]) + ": +" + SiteDefs.Income(s.Site[i]) + " Credits/round. " +
                                (s.SiteOwner[i] == _app.LocalPlayer ? "Yours." : s.SiteOwner[i] < 0 ? "Neutral - move infantry here to capture." : "Enemy held - move infantry here to capture.");
                    if (s.Territory[i] == _app.LocalPlayer) body += "\nYour territory (you can build here).";
                }
            }

            bool show = title.Length > 0;
            _selPanel.gameObject.SetActive(show);
            _selTitle.text = title; _selBody.text = body;
        }

        static string Cost(TerrainType t, MoveClass c)
        {
            int cost = TerrainDefs.MoveCost(t, c);
            return cost < 0 ? "X" : cost.ToString();
        }

        string PlanSummary(Unit unit)
        {
            var p = _app.Planner;
            string plan = "";
            if (p.TryGetMove(unit.Id, out var mv)) plan = p.Describe(mv).Replace(unit.Def.Name + ": ", "");
            if (p.TryGetAction(unit.Id, out var act))
            {
                plan += (plan.Length > 0 ? ", then " : "") + p.Describe(act).Replace(unit.Def.Name + ": ", "");
                if (act.Type == OrderType.Attack && Rules.PreviewAttack(_app.State, unit, p.PlannedPos(unit), act.TargetId, out int min, out int max))
                    plan += " (est. " + min + "-" + max + " damage)";
                else if (act.Type == OrderType.Attack) plan += " (out of range from there - will fire at will)";
            }
            return plan.Length > 0 ? "<color=#FFC040>Plan: " + plan + "</color>" : "No orders: holds position and fires at will. Tap a hex to move, an enemy to attack.";
        }

        void Report(string error)
        {
            if (error != null) Toast(error, true); else Sfx.Play(Sfx.Id.Order);
        }

        // ------------------------------------------------------------------ lists

        void ToggleList(ListMode mode)
        {
            if (_list == mode) { CloseList(); return; }
            OpenList(mode);
        }

        void OpenList(ListMode mode)
        {
            _list = mode;
            _listPanel.gameObject.SetActive(true);
            PopulateList();
        }

        public void CloseList()
        {
            _list = ListMode.None;
            if (_listPanel != null) _listPanel.gameObject.SetActive(false);
        }

        void ListRow(string title, string detail, Action onClick, bool enabled, Color? color = null)
        {
            var b = Ui.Button(_listContent, "", onClick, 18, color ?? Ui.ButtonColor).Size(-1, detail.Length > 0 ? 66 : 44);
            var label = Ui.LabelOf(b);
            label.alignment = TextAnchor.MiddleLeft;
            label.text = "<b>" + title + "</b>" + (detail.Length > 0 ? "\n<size=14>" + detail + "</size>" : "");
            b.interactable = enabled;
            if (!enabled) label.color = Ui.Dim;
        }

        void PopulateList()
        {
            Ui.Clear(_listContent);
            var p = _app.Planner; var s = _app.State;
            if (p == null) { CloseList(); return; }
            switch (_list)
            {
                case ListMode.Build:
                    _listTitle.text = "Build";
                    foreach (var def in BuildingDefs.All)
                    {
                        var type = def.Type;
                        ListRow(def.Name + "   $" + def.Cost, def.Description + " (" + def.BuildRounds + " round" + (def.BuildRounds > 1 ? "s" : "") + ")", () =>
                        {
                            p.BuildMode = true; p.BuildType = type; p.SelectedUnit = 0; p.SelectedBuilding = 0;
                            CloseList();
                            _app.RefreshPlanning();
                        }, def.Cost <= p.CreditsLeft);
                    }
                    break;

                case ListMode.Produce:
                {
                    var b = s.GetBuilding(p.SelectedBuilding);
                    if (b == null || !b.Def.IsProducer) { CloseList(); return; }
                    _listTitle.text = b.Def.Name + " - one unit per round";
                    bool hasOrder = p.TryGetBuildingOrder(b.Id, out var current);
                    // Choosing a unit replaces this building's current order, which frees up its cost again.
                    int refund = !hasOrder ? 0
                               : current.Type == OrderType.Purchase ? UnitDefs.Get((UnitType)current.Param).Cost
                               : current.Type == OrderType.Upgrade ? Rules.UpgradeCost(b) : Rules.RepairCost(b);
                    foreach (var def in Rules.ProductionOptions(b))
                    {
                        var type = def.Type;
                        bool locked = def.ProducerLevel > b.Level;
                        bool chosen = hasOrder && current.Type == OrderType.Purchase && current.Param == (int)type;
                        bool affordable = def.Cost <= p.CreditsLeft + refund;
                        ListRow(def.Name + "   $" + def.Cost + (locked ? "   (needs Lv " + def.ProducerLevel + ")" : chosen ? "   [ordered]" : ""), def.Description, () =>
                        {
                            p.ClearBuilding(b.Id);
                            string err = p.Add(Order.Purchase(b.Id, type));
                            Report(err);
                            _app.RefreshPlanning();
                        }, !locked && affordable, chosen ? Gfx.Mul(Ui.Good, 0.7f) : (Color?)null);
                    }
                    break;
                }

                case ListMode.Orders:
                    _listTitle.text = "Planned orders";
                    if (p.Orders.Count == 0)
                        Ui.Label(_listContent, "No orders yet.\nSelect a unit and tap a hex to move it, or tap an enemy to attack.", 17, TextAnchor.UpperLeft, Ui.Dim).Size(-1, 80);
                    for (int i = 0; i < p.Orders.Count; i++)
                    {
                        int index = i;
                        var row = Ui.Row(_listContent, 6, 40);
                        Ui.Label(row, p.Describe(p.Orders[i]), 17).Size(-1, -1, 1);
                        Ui.Button(row, "X", () => { p.RemoveAt(index); _app.RefreshPlanning(); }, 18, Ui.Bad).Size(44, -1);
                    }
                    if (p.Orders.Count > 0)
                        Ui.Button(_listContent, "Clear all orders", () => { p.Clear(); _app.RefreshPlanning(); }, 18, Ui.Bad).Size(-1, 44);
                    break;
            }
        }

        // ------------------------------------------------------------------ messages

        public void Toast(string message, bool isError)
        {
            _toast.text = message;
            _toast.color = isError ? new Color(1f, 0.5f, 0.45f) : Ui.TextColor;
            _toastUntil = Time.unscaledTime + 2.6f;
            if (isError) Sfx.Play(Sfx.Id.Error);
        }

        public void Log(string message)
        {
            _logLines.Add(message);
            while (_logLines.Count > 4) _logLines.RemoveAt(0);
            _log.text = string.Join("\n", _logLines.ToArray());
            _logUntil = Time.unscaledTime + 6f;
        }

        // ------------------------------------------------------------------ overlays

        RectTransform OpenOverlay(float width, float height)
        {
            Ui.Clear(_overlay);
            _overlay.gameObject.SetActive(true);
            var panel = Ui.Box("Panel", _overlay, Ui.PanelColor).rectTransform;
            panel.Pin(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(width, height));
            var col = Ui.Column(panel, 10, 22);
            col.Stretch();
            return col;
        }

        public void HideOverlays()
        {
            IsPaused = false;
            _overlay.gameObject.SetActive(false);
        }

        public void TogglePause()
        {
            if (_app.CurrentPhase == App.Phase.GameOver || _app.CurrentPhase == App.Phase.PassDevice) return;
            if (IsPaused) { HideOverlays(); return; }
            IsPaused = true;
            var col = OpenOverlay(420, 400);
            Ui.Label(col, "Paused", 32, TextAnchor.MiddleCenter, Ui.Accent).Size(-1, 50);
            Ui.Button(col, "Resume", HideOverlays, 24, Ui.Good).Size(-1, 60);
            Ui.Button(col, "How to Play", ShowHelp, 22).Size(-1, 54);
            Button sound = null;
            sound = Ui.Button(col, "Sound: " + (Sfx.Muted ? "Off" : "On"), () =>
            {
                Sfx.Muted = !Sfx.Muted;
                Ui.LabelOf(sound).text = "Sound: " + (Sfx.Muted ? "Off" : "On");
            }, 22).Size(-1, 54);
            bool lan = _app.Mode == MatchMode.LanHost || _app.Mode == MatchMode.LanClient;
            Ui.Button(col, lan ? "Leave Match" : "Save and Quit", () => _app.QuitToMenu(), 22, Ui.Bad).Size(-1, 54);
            Ui.Label(col, _app.Mode == MatchMode.LanClient ? "The host keeps the match. Rejoin from Join LAN Game."
                         : "The match is saved automatically after every round.", 15, TextAnchor.MiddleCenter, Ui.Dim).Size(-1, 40);
        }

        public void ShowPassDevice(int player, Action onContinue)
        {
            IsPaused = false;
            var col = OpenOverlay(560, 300);
            string name = player == 0 ? "PLAYER 1 (Blue)" : "PLAYER 2 (Red)";
            Ui.Label(col, "Pass the device to", 24, TextAnchor.MiddleCenter).Size(-1, 40);
            Ui.Label(col, name, 40, TextAnchor.MiddleCenter, Gfx.Team[player]).Size(-1, 60);
            Ui.Label(col, "Orders stay secret until both players are ready.", 18, TextAnchor.MiddleCenter, Ui.Dim).Size(-1, 40);
            Ui.Button(col, "I am " + (player == 0 ? "Player 1" : "Player 2") + " - start planning", () => { HideOverlays(); onContinue(); }, 22, Ui.Good).Size(-1, 70);
        }

        public void ShowGameOver()
        {
            IsPaused = false;
            var s = _app.State;
            var col = OpenOverlay(520, 400);
            string title; Color color;
            if (s.Winner == GameState.Draw) { title = "DRAW"; color = Ui.TextColor; }
            else if (_app.Mode == MatchMode.PassAndPlay) { title = (s.Winner == 0 ? "BLUE" : "RED") + " WINS"; color = Gfx.Team[s.Winner]; }
            else if (s.Winner == _app.LocalPlayer) { title = "VICTORY"; color = Ui.Good; }
            else { title = "DEFEAT"; color = Ui.Bad; }
            Ui.Label(col, title, 54, TextAnchor.MiddleCenter, color).Size(-1, 80);
            bool byScore = s.FindHeadquarters(0) != null && s.FindHeadquarters(1) != null;
            Ui.Label(col, byScore ? "Round limit reached - decided on score (" + s.Score(0) + " vs " + s.Score(1) + ")" : "A Headquarters has fallen.",
                19, TextAnchor.MiddleCenter, Ui.Dim).Size(-1, 34);
            var me = s.Players[_app.LocalPlayer];
            Ui.Label(col, "Rounds played: " + (s.Round - 1) + "\nEnemy units destroyed: " + me.UnitsKilled + "\nUnits lost: " + me.UnitsLost,
                21, TextAnchor.MiddleCenter).Size(-1, 100);
            Ui.Button(col, "Main Menu", () => _app.GoToMenu(), 24, Ui.Good).Size(-1, 64);
        }

        public static readonly string[] HelpPages =
        {
            "<b>WELCOME TO HEXFRONT</b>\n\nBoth players plan in secret at the same time. When both press READY, every order plays out together.\n\n" +
            "Destroy the enemy Headquarters to win.\n\nDrag to pan. Pinch to zoom. Twist two fingers to rotate (or use the buttons on the left).",
            "<b>GIVING ORDERS</b>\n\nTap one of your units to select it. Blue hexes show where it can go this round.\n\n" +
            "- Tap a hex to MOVE there.\n- Tap an enemy (red ring = in range) to ATTACK it. The unit moves into range if it has to.\n" +
            "- A unit can move and then attack in the same round. Artillery cannot.\n- Units with no orders hold position and fire at will.\n\n" +
            "Open ORDERS to review or cancel anything before you lock in.",
            "<b>SIMULTANEOUS TURNS</b>\n\nYou cannot see what your opponent is planning. Think about where they WILL be, not where they are.\n\n" +
            "- Two enemies heading for the same hex: the heavier unit takes it.\n- Units never enter a hex an enemy is standing in.\n" +
            "- All shooting happens at once, so a unit destroyed this round still fires this round.\n\nDEFEND digs a unit in: it takes 40% less damage but returns only half fire.",
            "<b>ECONOMY AND BASES</b>\n\nCredits come from your Headquarters, Generators, Supply Depots and captured sites (oil fields, industry, supply caches). " +
            "Move infantry onto a site to capture it.\n\nPress BUILD to construct buildings inside your territory (tinted in your colour). " +
            "Tap a Barracks, Vehicle Factory or Airfield to buy units - one per building per round. Upgrade buildings to unlock stronger units.",
            "<b>COMBINED ARMS</b>\n\n- Tanks crush infantry and vehicles.\n- Anti-Tank Squads beat armor.\n- Artillery out-ranges everything but is helpless up close.\n" +
            "- Aircraft ignore terrain but need Anti-Air kept away, and must return to an Airfield before fuel runs out.\n" +
            "- Recon units and Radar see far through the fog of war.\n- Forests, towns, rocks and mountains give cover. High ground hits harder.\n\nGood luck, Commander.",
        };

        public void ShowHelp()
        {
            IsPaused = true;
            var col = OpenOverlay(640, 470);
            var text = Ui.Label(col, HelpPages[_helpPage], 19, TextAnchor.UpperLeft).Size(-1, 340);
            var row = Ui.Row(col, 10, 56);
            Ui.Button(row, "Back", () => { _helpPage = (_helpPage + HelpPages.Length - 1) % HelpPages.Length; ShowHelp(); }, 20).Size(120, -1);
            Ui.Label(row, (_helpPage + 1) + " / " + HelpPages.Length, 18, TextAnchor.MiddleCenter, Ui.Dim).Size(-1, -1, 1);
            Ui.Button(row, "Next", () => { _helpPage = (_helpPage + 1) % HelpPages.Length; ShowHelp(); }, 20).Size(120, -1);
            Ui.Button(row, "Close", HideOverlays, 20, Ui.Good).Size(140, -1);
        }
    }
}
