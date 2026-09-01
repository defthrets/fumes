using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using GTA;

// Both namespaces have a Control and only one of them is a game control.
using Control = GTA.Control;
using Fumes.Core;

namespace Fumes.UI
{
    /// <summary>
    /// The settings menu, and the gauge positioner inside it.
    ///
    /// BUILT OUT OF RECTANGLES, because everything else in this mod is. NativeUI and LemonUI
    /// both do this better and both are another dll a player has to find, put in the right
    /// folder and keep in step with the game -- which is the whole reason Fumes has no
    /// dependencies. A menu is a list of strings and a highlight; it is not worth a dependency.
    ///
    /// THE POSITIONER IS THE POINT OF IT. Where the minimap lands depends on the safe-zone
    /// slider and the aspect ratio, and there is no honest way for a mod to ask -- so the gauge
    /// beside it can only ever be right on the screen it was tuned on. That used to be a hidden
    /// ini switch that turned the numpad into a drag handle, which meant the answer to "it is in
    /// the wrong place on my monitor" was a setting nobody had been told about. Now it is an
    /// item in a menu with a key printed on the screen.
    ///
    /// Changes apply to the live settings the instant they are made, so the HUD moves while you
    /// are looking at it, and are written to the ini when the menu closes -- in place, keeping
    /// every comment, only the lines that actually changed.
    /// </summary>
    internal sealed class Menu
    {
        private readonly Settings _cfg;
        private readonly Gauge _gauge;

        private readonly List<Page> _pages = new List<Page>();
        private readonly HashSet<Item> _changed = new HashSet<Item>();

        private int _page;
        private int _row;
        private int _scroll;

        private bool _open;

        // ---- motion -----------------------------------------------------
        //
        // A menu that appears fully formed and jumps a row at a time is readable and dead. The
        // three things below cost a float each and are the difference between a list and an
        // instrument: the panel arrives, the highlight travels to where you sent it, and a
        // value you just changed says so.

        /// <summary>0 shut, 1 fully open. Drives the slide and the fade.</summary>
        private float _reveal;

        /// <summary>Where the highlight actually is, in rows, as opposed to where _row says.</summary>
        private float _highlight;

        /// <summary>The row that just changed, and how recently, for the flash.</summary>
        private int _flashRow = -1;
        private float _flash;

        /// <summary>Which page the tab strip is sliding from, and how far along.</summary>
        private float _tabAt;

        /// <summary>True while the gauge is being dragged rather than the menu navigated.</summary>
        public bool Placing { get; private set; }

        /// <summary>True whenever the menu is taking input, so the rest of the mod can stand off.</summary>
        public bool IsOpen => _open;

        // Where it sits and how big, as fractions of the screen.
        private const float PanelX = 0.030f;
        private const float PanelW = 0.230f;
        private const float PanelTop = 0.180f;
        private const float TitleH = 0.052f;
        private const float RowH = 0.0295f;
        private const float FootH = 0.040f;
        private const int Rows = 11;

        private const int Script = 1;   // Sign Painter, for the name
        private const int Plain = 4;    // Chalet Comprime Cologne, for everything else

        private static readonly Color Ink = Color.FromArgb(232, 228, 228, 231);
        private static readonly Color Dim = Color.FromArgb(165, 150, 150, 156);
        private static readonly Color Amber = Color.FromArgb(255, 245, 196, 60);
        private static readonly Color Panel = Color.FromArgb(234, 15, 15, 18);
        private static readonly Color Head = Color.FromArgb(242, 26, 26, 31);

        public Menu(Settings cfg, Gauge gauge)
        {
            _cfg = cfg;
            _gauge = gauge;

            Build();
        }

        // ==================================================================
        // The items
        // ==================================================================

        private sealed class Page
        {
            public string Title;
            public Icon Badge;
            public readonly List<Item> Items = new List<Item>();
        }

        private sealed class Item
        {
            public string Label;
            public string Hint;

            /// <summary>The value as it should read on screen, or null for an action row.</summary>
            public Func<string> Show;

            /// <summary>Left or right, as -1 or +1.</summary>
            public Action<int> Nudge;

            /// <summary>Enter.</summary>
            public Action Press;

            /// <summary>Where it belongs in the ini, and what to write there.</summary>
            public string Section;
            public string Key;
            public Func<string> Written;
        }

        private Page Add(string title, string icon)
        {
            var p = new Page { Title = title, Badge = new Icon(icon) };
            _pages.Add(p);
            return p;
        }

        private static Item Toggle(string label, Func<bool> get, Action<bool> set,
                                   string section, string key, string hint)
        {
            var item = new Item
            {
                Label = label,
                Hint = hint,
                Section = section,
                Key = key,
                Show = () => get() ? "ON" : "OFF",
                Written = () => get() ? "true" : "false",
            };

            item.Nudge = d => set(!get());
            item.Press = () => set(!get());
            return item;
        }

        private static Item Number(string label, Func<float> get, Action<float> set,
                                   float step, float min, float max, string format,
                                   string section, string key, string hint)
        {
            var item = new Item
            {
                Label = label,
                Hint = hint,
                Section = section,
                Key = key,
                Show = () => get().ToString(format, CultureInfo.InvariantCulture),
                Written = () => get().ToString(format, CultureInfo.InvariantCulture),
            };

            item.Nudge = d =>
            {
                var v = get() + step * d;

                // Rounded to the step, or a float that started at 0.1 and was nudged twenty
                // times reads as 0.30000004 and writes that into the ini.
                v = (float)Math.Round(v / step) * step;

                if (v < min) v = min;
                if (v > max) v = max;

                set(v);
            };

            return item;
        }

        private static Item Whole(string label, Func<int> get, Action<int> set,
                                  int min, int max, string section, string key, string hint)
        {
            var item = new Item
            {
                Label = label,
                Hint = hint,
                Section = section,
                Key = key,
                Show = () => get().ToString(CultureInfo.InvariantCulture),
                Written = () => get().ToString(CultureInfo.InvariantCulture),
            };

            item.Nudge = d =>
            {
                var v = get() + d;
                if (v < min) v = max;
                if (v > max) v = min;
                set(v);
            };

            return item;
        }

        private static Item Choice<T>(string label, Func<T> get, Action<T> set,
                                      string section, string key, string hint)
        {
            var values = Enum.GetValues(typeof(T));

            var item = new Item
            {
                Label = label,
                Hint = hint,
                Section = section,
                Key = key,
                Show = () => get().ToString().ToUpperInvariant(),
                Written = () => get().ToString(),
            };

            item.Nudge = d =>
            {
                var at = Array.IndexOf(values, get());
                at = ((at + d) % values.Length + values.Length) % values.Length;
                set((T)values.GetValue(at));
            };

            return item;
        }

        private Item Action_(string label, Action press, string hint)
        {
            return new Item { Label = label, Hint = hint, Press = press, Show = null };
        }

        private void Build()
        {
            var hud = Add("HUD", "icon_hud.png");
            hud.Items.Add(Action_("Move and size the gauge", () => Placing = true,
                                  "Arrows move it, Shift+arrows resize, Enter keeps it."));
            hud.Items.Add(Toggle("Show the gauge", () => _cfg.ShowGauge, v => _cfg.ShowGauge = v,
                                 "HUD", "ShowGauge", "The bar beside the minimap."));
            hud.Items.Add(Toggle("Hide with the game's HUD", () => _cfg.GaugeFollowsHud,
                                 v => _cfg.GaugeFollowsHud = v, "HUD", "FollowsHud",
                                 "Goes when the radar goes, whatever hid it."));
            hud.Items.Add(Toggle("Only in a vehicle", () => _cfg.GaugeOnlyInVehicle,
                                 v => _cfg.GaugeOnlyInVehicle = v, "HUD", "OnlyInVehicle",
                                 "Off shows the last car's tank while you are on foot."));
            hud.Items.Add(Number("Opacity", () => _cfg.GaugeOpacity, v => _cfg.GaugeOpacity = v,
                                 0.02f, 0.15f, 1f, "0.00", "HUD", "Opacity",
                                 "The whole gauge at once, border and fuel and all."));
            hud.Items.Add(Toggle("Liquid fuel", () => _cfg.GaugeLiquid, v => _cfg.GaugeLiquid = v,
                                 "HUD", "GaugeLiquid", "A moving surface and rising bubbles."));
            hud.Items.Add(Toggle("Pump icon", () => _cfg.ShowGaugeIcon, v => _cfg.ShowGaugeIcon = v,
                                 "HUD", "ShowGaugeIcon", "The little pump inside the bar."));
            hud.Items.Add(Number("Pump icon size", () => _cfg.GaugeIconScale,
                                 v => _cfg.GaugeIconScale = v, 0.02f, 0.2f, 2f, "0.00",
                                 "HUD", "IconScale", "As a fraction of the bar's width."));
            hud.Items.Add(Toggle("Percentage", () => _cfg.ShowNumbers, v => _cfg.ShowNumbers = v,
                                 "HUD", "ShowNumbers", "The reading inside the bar."));
            hud.Items.Add(Toggle("Hide it at full", () => _cfg.HideFullReading,
                                 v => _cfg.HideFullReading = v, "HUD", "HideFullReading",
                                 "A full bar already says full, and 100 does not fit."));
            hud.Items.Add(Choice("Units", () => _cfg.Units, v => _cfg.Units = v, "HUD", "Units",
                                 "Gallons converts the prices too."));
            hud.Items.Add(Choice("Prompts", () => _cfg.Prompts, v => _cfg.Prompts = v,
                                 "HUD", "Prompts", "Help box top left, or the button bar."));

            var fuel = Add("FUEL", "drop.png");
            fuel.Items.Add(Number("Consumption", () => _cfg.ConsumptionMultiplier,
                                  v => _cfg.ConsumptionMultiplier = v, 0.05f, 0f, 5f, "0.00",
                                  "Fuel", "ConsumptionMultiplier",
                                  "1.0 is about half an hour of driving to a tank."));
            fuel.Items.Add(Number("Idling, litres an hour", () => _cfg.IdleLitresPerHour,
                                  v => _cfg.IdleLitresPerHour = v, 0.1f, 0f, 20f, "0.0",
                                  "Fuel", "IdleLitresPerHour", "What it burns going nowhere."));
            fuel.Items.Add(Number("Reserve mark", () => _cfg.ReserveFraction,
                                  v => _cfg.ReserveFraction = v, 0.01f, 0.02f, 0.5f, "0.00",
                                  "Fuel", "ReserveFraction", "Where the gauge starts warning."));
            fuel.Items.Add(Toggle("Boats", () => _cfg.AffectBoats, v => _cfg.AffectBoats = v,
                                  "Fuel", "AffectBoats", null));
            fuel.Items.Add(Toggle("Aircraft", () => _cfg.AffectAircraft,
                                  v => _cfg.AffectAircraft = v, "Fuel", "AffectAircraft",
                                  "Off by default: a helicopter running dry is a long fall."));
            fuel.Items.Add(Toggle("Traffic", () => _cfg.AffectTraffic, v => _cfg.AffectTraffic = v,
                                  "Fuel", "AffectTraffic", "Everyone else runs out too."));
            fuel.Items.Add(Toggle("Mission cars start full", () => _cfg.MissionTanksFull,
                                  v => _cfg.MissionTanksFull = v, "Fuel", "MissionTanksFull",
                                  "Only cars handed to you mid-mission. Not trainer spawns."));
            fuel.Items.Add(Toggle("Cars left running", () => _cfg.AbandonedIdle,
                                  v => _cfg.AbandonedIdle = v, "Fuel", "AbandonedIdle",
                                  "One you drove off and left idling keeps drinking."));
            fuel.Items.Add(Toggle("Low fuel chime", () => _cfg.LowFuelChime,
                                  v => _cfg.LowFuelChime = v, "Fuel", "LowFuelChime",
                                  "Once when it drops into reserve, not on a loop."));
            fuel.Items.Add(Toggle("Tanks leak when shot", () => _cfg.TankLeaks,
                                  v => _cfg.TankLeaks = v, "Fuel", "TankLeaks", null));
            fuel.Items.Add(Toggle("Stall when empty", () => _cfg.StallWhenEmpty,
                                  v => _cfg.StallWhenEmpty = v, "Engine", "StallWhenEmpty",
                                  "Off leaves you driving on an empty tank."));

            var station = Add("STATION", "icon_station.png");
            station.Items.Add(Number("Price a litre", () => _cfg.PricePerLitre,
                                     v => _cfg.PricePerLitre = v, 0.05f, 0f, 20f, "0.00",
                                     "Station", "PricePerLitre",
                                     "Before each station's own variance."));
            station.Items.Add(Choice("Grade", () => _cfg.Grade, v => _cfg.Grade = v,
                                     "Station", "Grade",
                                     "Premium costs a quarter more and goes a tenth further."));
            station.Items.Add(Toggle("Take the money", () => _cfg.ChargeMoney,
                                     v => _cfg.ChargeMoney = v, "Station", "ChargeMoney",
                                     "Off fills for nothing."));
            station.Items.Add(Number("Litres a second", () => _cfg.LitresPerSecond,
                                     v => _cfg.LitresPerSecond = v, 0.1f, 0.2f, 20f, "0.0",
                                     "Station", "LitresPerSecond", "How fast the pump runs."));
            station.Items.Add(Number("Reach: taking the nozzle", () => _cfg.PumpReach,
                                     v => _cfg.PumpReach = v, 0.1f, 0.5f, 12f, "0.0",
                                     "Station", "PumpReach", null));
            station.Items.Add(Number("Reach: the filler", () => _cfg.CapReach,
                                     v => _cfg.CapReach = v, 0.1f, 0.5f, 12f, "0.0",
                                     "Station", "CapReach",
                                     "Raise it if you are hunting for the exact spot."));
            station.Items.Add(Number("Reach: hanging up", () => _cfg.HangUpReach,
                                     v => _cfg.HangUpReach = v, 0.1f, 0.4f, 6f, "0.0",
                                     "Station", "HangUpReach", null));
            station.Items.Add(Toggle("Map blips", () => _cfg.ShowBlips, v => _cfg.ShowBlips = v,
                                     "Station", "ShowBlips", null));
            station.Items.Add(Toggle("Learn stations", () => _cfg.LearnStations,
                                     v => _cfg.LearnStations = v, "Station", "LearnStations",
                                     "Corrects the shipped list from the pumps it finds."));
            station.Items.Add(Toggle("Forecourt hazard", () => _cfg.ForecourtHazard,
                                     v => _cfg.ForecourtHazard = v, "Hazard", "ForecourtHazard",
                                     "Gunfire at the pumps can set the vapour off."));

            var hose = Add("HOSE", "icon_hose.png");
            hose.Items.Add(Whole("Rope", () => _cfg.HoseRopeType, v =>
                                 {
                                     // Through the probe, so a type known to crash this install
                                     // cannot be reached from the menu either.
                                     var next = v;
                                     if (RopeProbe.IsBad(next)) next = RopeProbe.Next(_cfg.HoseRopeType);
                                     _cfg.HoseRopeType = next;
                                 }, 0, RopeProbe.MaxType, "Nozzle", "HoseRopeType",
                                 "Eight ropes; that is all the game has."));
            hose.Items.Add(Toggle("Rope keys while carrying", () => _cfg.RopePicker,
                                  v => _cfg.RopePicker = v, "Nozzle", "RopePicker",
                                  "NumPad * to cycle, NumPad 0 to keep."));
            hose.Items.Add(Number("Hose length", () => _cfg.HoseMaxMetres,
                                  v => _cfg.HoseMaxMetres = v, 0.5f, 2f, 40f, "0.0",
                                  "Nozzle", "HoseMaxMetres", "How far you can walk with it."));
            hose.Items.Add(Number("Sag", () => _cfg.HoseSag, v => _cfg.HoseSag = v,
                                  0.02f, 1f, 2.5f, "0.00", "Nozzle", "HoseSag",
                                  "1.0 is a taut wire."));
            hose.Items.Add(Toggle("Hose snaps if you walk off", () => _cfg.HoseSnaps,
                                  v => _cfg.HoseSnaps = v, "Nozzle", "HoseSnaps", null));
            hose.Items.Add(Toggle("Filling sound", () => _cfg.FillSound, v => _cfg.FillSound = v,
                                  "Nozzle", "FillSound", null));
            hose.Items.Add(Toggle("Nozzle in the left hand", () => _cfg.LeftHand,
                                  v => _cfg.LeftHand = v, "Nozzle", "LeftHand",
                                  "Match it to whichever arm the fill animation reaches with."));
            hose.Items.Add(Toggle("Marker on the filler", () => _cfg.ShowFillerMarker,
                                  v => _cfg.ShowFillerMarker = v, "Nozzle", "ShowFillerMarker",
                                  "A ring on the spot the nozzle has to reach."));
        }

        // ==================================================================
        // Input
        // ==================================================================

        private bool _openKey, _up, _down, _left, _right, _enter, _back, _tab;

        public void Update()
        {
            if (Edge(_cfg.MenuKey, ref _openKey) && Modifier())
            {
                if (Placing)
                {
                    // REMEMBER FIRST. Leaving the positioner by the menu key is the same act as
                    // leaving it by Enter, and it used to be the one that threw the placement
                    // away: the gauge stayed where you put it for the rest of the session and
                    // the ini never heard about it, so it sprang back on the next reload with
                    // nothing to explain why.
                    Remember();
                    Placing = false;
                }
                else if (_open) Close();
                else
                {
                    _open = true;

                    // From scratch each time. Left where it was, a menu reopened a second later
                    // would appear already in place and the arrival would only ever be seen once.
                    _reveal = 0f;
                    _highlight = _row;
                    _tabAt = _page;
                    _flash = 0f;
                }
            }

            if (!_open) return;

            // The game keeps its own input while a menu is up otherwise -- arrow keys steer,
            // Enter answers the phone, and the player shoots whatever is in front of them.
            Deafen();

            Animate();

            if (Placing) { Place(); return; }

            Navigate();
            Render();
        }

        /// <summary>
        /// Moves everything that moves, once a frame.
        ///
        /// EXPONENTIAL, not linear, and framerate-independent for the same reason the fuel is:
        /// a fixed step per frame runs at half speed on a machine doing thirty and twice on one
        /// doing a hundred and twenty, which is a menu that feels different on every PC.
        /// </summary>
        private void Animate()
        {
            var dt = 0f;

            try { dt = Game.LastFrameTime; }
            catch { dt = 0f; }

            if (dt <= 0f || dt > 0.5f) dt = dt > 0.5f ? 0.5f : 0f;

            // Once. The tab badges are sized as a fraction of the screen's WIDTH and drawn with
            // a height that has to undo the screen's shape, or a square icon comes out a third
            // wider than it is tall on anything ultrawide -- the same trap the pump icon in the
            // gauge falls into, and the same fix.
            if (!_measured)
            {
                _measured = true;

                try
                {
                    var res = GTA.UI.Screen.Resolution;
                    if (res.Height > 0) _aspect = res.Width / (float)res.Height;
                }
                catch
                {
                    // 16:9 stands, and the badges are a little wide. Nothing else cares.
                }
            }

            Ease(ref _reveal, 1f, dt, 0.085f);
            Ease(ref _highlight, _row, dt, 0.055f);
            Ease(ref _tabAt, _page, dt, 0.070f);

            // The flash decays rather than easing to a target: it is a one-off, not a position.
            if (_flash > 0f)
            {
                _flash -= dt / 0.45f;
                if (_flash < 0f) _flash = 0f;
            }
        }

        private static void Ease(ref float value, float target, float dt, float seconds)
        {
            if (dt <= 0f || seconds <= 0.001f) { value = target; return; }

            value += (target - value) * (1f - (float)Math.Exp(-dt / seconds));

            // Snap when it is close enough to see, or a highlight spends forever approaching a
            // row it is already sitting on and the scroll maths never quite settles.
            if (Math.Abs(target - value) < 0.002f) value = target;
        }

        private bool Modifier()
        {
            try
            {
                switch (_cfg.MenuModifier)
                {
                    case MenuModifier.None: return true;
                    case MenuModifier.Shift: return Game.IsKeyPressed(Keys.ShiftKey);
                    case MenuModifier.Control: return Game.IsKeyPressed(Keys.ControlKey);
                    case MenuModifier.Alt: return Game.IsKeyPressed(Keys.Menu);
                }
            }
            catch
            {
                // Treated as not held, so the menu simply does not open.
            }

            return false;
        }

        private static void Deafen()
        {
            try
            {
                Game.DisableControlThisFrame(Control.Attack);
                Game.DisableControlThisFrame(Control.Attack2);
                Game.DisableControlThisFrame(Control.Aim);
                Game.DisableControlThisFrame(Control.MoveLeftRight);
                Game.DisableControlThisFrame(Control.MoveUpDown);
                Game.DisableControlThisFrame(Control.Jump);
                Game.DisableControlThisFrame(Control.Enter);
                Game.DisableControlThisFrame(Control.Context);
                Game.DisableControlThisFrame(Control.Phone);
                Game.DisableControlThisFrame(Control.SelectWeapon);
                Game.DisableControlThisFrame(Control.VehicleExit);
            }
            catch
            {
                // Worst case the game hears the same key we did.
            }
        }

        private void Navigate()
        {
            var page = _pages[_page];

            if (Edge(Keys.Tab, ref _tab))
            {
                _page = (_page + 1) % _pages.Count;
                _row = 0;
                _scroll = 0;
                return;
            }

            if (Edge(Keys.Up, ref _up)) _row--;
            if (Edge(Keys.Down, ref _down)) _row++;

            if (_row < 0) _row = page.Items.Count - 1;
            if (_row >= page.Items.Count) _row = 0;

            // Keep the highlight on screen with a margin, so the next row is visible before
            // you get to it rather than appearing as you land on it.
            if (_row < _scroll) _scroll = _row;
            if (_row >= _scroll + Rows) _scroll = _row - Rows + 1;

            var item = page.Items[_row];

            if (Edge(Keys.Left, ref _left) && item.Nudge != null) Touch(item, -1);
            if (Edge(Keys.Right, ref _right) && item.Nudge != null) Touch(item, 1);

            if (Edge(Keys.Return, ref _enter))
            {
                if (item.Press != null)
                {
                    item.Press();
                    if (item.Section != null) _changed.Add(item);
                }
            }

            if (Edge(Keys.Back, ref _back)) Close();
        }

        private void Touch(Item item, int direction)
        {
            item.Nudge(direction);
            if (item.Section != null) _changed.Add(item);

            _flashRow = _row;
            _flash = 1f;
        }

        /// <summary>
        /// Writes the changed settings back, in place, and shuts.
        ///
        /// Only what actually moved. IniFile.SetValue rewrites one line and leaves every
        /// comment where it was, so a menu that saved everything would still be correct -- it
        /// would just churn a hundred lines to record two.
        /// </summary>
        private void Close()
        {
            _open = false;
            Placing = false;

            if (_changed.Count == 0) return;

            var written = 0;

            foreach (var item in _changed)
            {
                try
                {
                    if (IniFile.SetValue(Paths.Ini, item.Section, item.Key, item.Written())) written++;
                    else Log.Warn("Could not write [" + item.Section + "] " + item.Key + " to Fumes.ini.");
                }
                catch (Exception ex)
                {
                    Log.Once("menu-save", "Could not write the settings: " + ex.Message);
                }
            }

            Log.Info("Menu: " + written + " of " + _changed.Count + " setting(s) written to Fumes.ini.");

            try
            {
                GTA.UI.Notification.PostTicker(written == _changed.Count
                    ? "~g~" + written + " setting(s) saved~s~ to Fumes.ini."
                    : "~y~Only " + written + " of " + _changed.Count +
                      " settings saved~s~ - see Fumes.log.", false, false);
            }
            catch { /* the log already has it */ }

            _changed.Clear();
        }

        // ==================================================================
        // The positioner
        // ==================================================================

        private void Place()
        {
            var fine = 0.0004f;
            var fast = 0.0035f;

            var shift = Held(Keys.ShiftKey);
            var step = Held(Keys.ControlKey) ? fine : fast;

            if (Edge(Keys.Left, ref _left)) { if (shift) _cfg.GaugeWidth -= step; else _cfg.GaugeX -= step; }
            if (Edge(Keys.Right, ref _right)) { if (shift) _cfg.GaugeWidth += step; else _cfg.GaugeX += step; }
            if (Edge(Keys.Up, ref _up)) { if (shift) _cfg.GaugeHeight -= step * 2f; else _cfg.GaugeY -= step; }
            if (Edge(Keys.Down, ref _down)) { if (shift) _cfg.GaugeHeight += step * 2f; else _cfg.GaugeY += step; }

            _cfg.GaugeX = Clamp(_cfg.GaugeX, 0f, 0.98f);
            _cfg.GaugeY = Clamp(_cfg.GaugeY, 0f, 0.99f);
            _cfg.GaugeWidth = Clamp(_cfg.GaugeWidth, 0.0015f, 0.8f);
            _cfg.GaugeHeight = Clamp(_cfg.GaugeHeight, 0.004f, 0.6f);

            // BOTH EDGES ARE READ, every frame, and that is not style. || short-circuits, so
            // whenever Return produced an edge the Back edge was never evaluated -- and Edge is
            // what UPDATES the remembered key state. Backspace held through that frame would
            // stay recorded as up, and register a fresh press the next time it was looked at.
            var accept = Edge(Keys.Return, ref _enter);
            var cancel = Edge(Keys.Back, ref _back);

            if (accept || cancel)
            {
                Placing = false;
                Remember();
                return;
            }

            // The gauge itself, drawn whatever the game thinks, so there is something to aim.
            _gauge.Preview();

            Readout();
        }

        /// <summary>Stages the four numbers so Close writes them.</summary>
        private void Remember()
        {
            Stage("HUD", "X", () => _cfg.GaugeX.ToString("0.0000", CultureInfo.InvariantCulture));
            Stage("HUD", "Y", () => _cfg.GaugeY.ToString("0.0000", CultureInfo.InvariantCulture));
            Stage("HUD", "Width", () => _cfg.GaugeWidth.ToString("0.0000", CultureInfo.InvariantCulture));
            Stage("HUD", "Height", () => _cfg.GaugeHeight.ToString("0.0000", CultureInfo.InvariantCulture));
        }

        private void Stage(string section, string key, Func<string> value)
        {
            _changed.Add(new Item { Section = section, Key = key, Written = value, Label = key });
        }

        /// <summary>
        /// What the gauge is, in fractions AND in real pixels.
        ///
        /// The pixels are the half that settles arguments. A bar can be described as five
        /// thousandths wide and still be reported as too thick, and neither side of that can
        /// check the other -- but "16 px" is the same number on both ends of the conversation.
        /// </summary>
        private void Readout()
        {
            int w = 1920, h = 1080;

            try
            {
                var res = GTA.UI.Screen.Resolution;
                if (res.Width > 0 && res.Height > 0) { w = res.Width; h = res.Height; }
            }
            catch
            {
                // The fractions still read; only the pixel figures are a guess.
            }

            const float left = 0.5f;
            var top = 0.055f;

            Draw.Bar(left - 0.155f, top - 0.012f, 0.310f, 0.086f, Panel);
            Draw.Bar(left - 0.155f, top - 0.012f, 0.310f, 0.0022f, Amber);

            Draw.Text("PLACING THE GAUGE", left, top, 0.36f, Amber, Plain, true);

            Draw.Text(
                _cfg.GaugeWidth.ToString("0.0000", CultureInfo.InvariantCulture) + " x " +
                _cfg.GaugeHeight.ToString("0.0000", CultureInfo.InvariantCulture) + "   at   " +
                _cfg.GaugeX.ToString("0.0000", CultureInfo.InvariantCulture) + ", " +
                _cfg.GaugeY.ToString("0.0000", CultureInfo.InvariantCulture),
                left, top + 0.026f, 0.30f, Ink, Plain, true);

            Draw.Text(
                (_cfg.GaugeWidth * w).ToString("0", CultureInfo.InvariantCulture) + " x " +
                (_cfg.GaugeHeight * h).ToString("0", CultureInfo.InvariantCulture) + " px   on " +
                w + "x" + h,
                left, top + 0.046f, 0.28f, Dim, Plain, true);

            Draw.Text("ARROWS move    SHIFT+ARROWS resize    CTRL fine    ENTER done",
                      left, top + 0.066f, 0.26f, Dim, Plain, true);
        }

        // ==================================================================
        // Drawing
        // ==================================================================

        /// <summary>Everything the panel draws, faded and slid by how open it is.</summary>
        private Color A(Color c)
        {
            var a = (int)(c.A * _reveal);
            if (a < 0) a = 0;
            if (a > 255) a = 255;
            return Color.FromArgb(a, c.R, c.G, c.B);
        }

        private void Render()
        {
            var page = _pages[_page];
            var shown = Math.Min(Rows, page.Items.Count);

            var bodyH = shown * RowH;
            var totalH = TitleH + bodyH + FootH;

            // IT ARRIVES FROM THE LEFT rather than appearing. Three hundredths of a screen over
            // about a tenth of a second -- far enough to register as movement, short enough that
            // nobody waiting to change a setting is kept waiting by it.
            var left = PanelX - (1f - _reveal) * 0.030f;

            Draw.Bar(left, PanelTop, PanelW, totalH, A(Panel));
            Draw.Bar(left, PanelTop, PanelW, TitleH, A(Head));
            Draw.Bar(left, PanelTop + TitleH - 0.0022f, PanelW, 0.0022f, A(Amber));

            Draw.Text("Fumes", left + 0.012f, PanelTop + 0.004f, 0.62f, A(Amber), Script);

            Tabs(left);

            Rows_(page, left, shown, bodyH);
            Footer(page, left, bodyH);
        }

        /// <summary>The rows, with the highlight riding between them.</summary>
        private void Rows_(Page page, float left, int shown, float bodyH)
        {
            var top = PanelTop + TitleH;

            // THE HIGHLIGHT IS DRAWN FROM _highlight, NOT _row, and that is the whole of the
            // travelling effect: _row jumps the instant you press a key, _highlight is chasing
            // it, and the bar is drawn wherever the chase has got to. Scrolling still uses _row,
            // because the list must show the row you are ON, not the one the animation is
            // passing over.
            var at = _highlight - _scroll;

            if (at > -1f && at < shown)
            {
                var hy = top + at * RowH;

                Draw.Bar(left, hy, PanelW, RowH, A(Color.FromArgb(38, 245, 196, 60)));
                Draw.Bar(left, hy, 0.0022f, RowH, A(Amber));
            }

            for (var i = 0; i < shown; i++)
            {
                var index = _scroll + i;
                if (index >= page.Items.Count) break;

                var item = page.Items[index];
                var y = top + i * RowH;
                var selected = index == _row;

                var label = item.Show == null ? item.Label.ToUpperInvariant() : item.Label;

                Draw.Text(label, left + 0.012f, y + 0.0044f, 0.295f,
                          A(selected ? Ink : Color.FromArgb(200, 205, 205, 208)), Plain);

                if (item.Show == null)
                {
                    if (selected)
                    {
                        Draw.Text("ENTER", left + PanelW - 0.010f, y + 0.0044f, 0.295f,
                                  A(Amber), Plain, false, true);
                    }

                    continue;
                }

                // A VALUE THAT JUST CHANGED SAYS SO. Without it, holding left on a number is a
                // column of digits quietly replacing themselves and the only way to know the
                // key registered is to read them. The flash is on the value alone, not the row:
                // the row did not change, one number did.
                var ink = selected ? Amber : Dim;

                if (index == _flashRow && _flash > 0f)
                {
                    ink = Mix(ink, Color.FromArgb(255, 255, 255, 255), _flash * 0.85f);
                }

                Draw.Text(item.Show(), left + PanelW - 0.010f, y + 0.0044f, 0.295f,
                          A(ink), Plain, false, true);
            }

            if (page.Items.Count > Rows)
            {
                var track = bodyH;
                var thumb = track * Rows / page.Items.Count;

                // From the HIGHLIGHT rather than the scroll offset, so the thumb glides with
                // the selection instead of stepping only when the list happens to scroll.
                var span = Math.Max(1, page.Items.Count - 1);
                var slide = (track - thumb) * _highlight / span;

                Draw.Bar(left + PanelW - 0.0018f, PanelTop + TitleH, 0.0018f, track,
                         A(Color.FromArgb(50, 255, 255, 255)));
                Draw.Bar(left + PanelW - 0.0018f, PanelTop + TitleH + slide, 0.0018f, thumb,
                         A(Amber));
            }
        }

        private void Footer(Page page, float left, float bodyH)
        {
            var foot = PanelTop + TitleH + bodyH;

            Draw.Bar(left, foot, PanelW, 0.0016f, A(Color.FromArgb(70, 255, 255, 255)));

            var hint = page.Items[_row].Hint;

            Draw.Text(string.IsNullOrEmpty(hint) ? "" : hint,
                      left + 0.012f, foot + 0.008f, 0.26f, A(Dim), Plain);

            Draw.Text("TAB page    ARROWS change    BACKSPACE save & close",
                      left + 0.012f, foot + 0.024f, 0.24f,
                      A(Color.FromArgb(120, 150, 150, 156)), Plain);
        }

        /// <summary>
        /// The page names across the head of the panel, with an icon each, and a marker that
        /// slides between them.
        ///
        /// LAID OUT BY MEASUREMENT rather than in fixed columns: four names of different lengths
        /// across a fifth of the screen, so even columns would either crowd STATION or strand
        /// HUD. The icon's width is included in each name's slot, or the badge on the longest
        /// name would push it into its neighbour.
        /// </summary>
        private void Tabs(float left)
        {
            const float scale = 0.26f;

            var x0 = left + 0.012f;
            var x1 = left + PanelW - 0.012f;
            var y = PanelTop + 0.030f;

            var badge = 0.0055f;                 // icon width, as a fraction of the screen
            var badgeH = badge * _aspect;
            var pad = 0.0022f;

            var widths = new float[_pages.Count];
            var total = 0f;

            for (var i = 0; i < _pages.Count; i++)
            {
                widths[i] = Draw.Width(_pages[i].Title, scale, Plain) + badge + pad;
                total += widths[i];
            }

            var gap = _pages.Count > 1 ? (x1 - x0 - total) / (_pages.Count - 1) : 0f;
            if (gap < 0.003f) gap = 0.003f;

            // Where each tab starts, kept so the marker can be put between two of them.
            var starts = new float[_pages.Count];
            var x = x0;

            for (var i = 0; i < _pages.Count; i++)
            {
                starts[i] = x;
                x += widths[i] + gap;
            }

            // THE MARKER IS INTERPOLATED BETWEEN TABS, which is why _tabAt is a float. Drawn
            // under the current tab it would jump the width of a word; slid, it carries the eye
            // from the page you left to the one you are on.
            var from = (int)Math.Floor(_tabAt);
            if (from < 0) from = 0;
            if (from > _pages.Count - 1) from = _pages.Count - 1;

            var to = Math.Min(from + 1, _pages.Count - 1);
            var f = _tabAt - from;

            var markX = starts[from] + (starts[to] - starts[from]) * f;
            var markW = widths[from] + (widths[to] - widths[from]) * f;

            Draw.Bar(markX, y + 0.0165f, markW, 0.0016f, A(Amber));

            for (var i = 0; i < _pages.Count; i++)
            {
                var on = i == _page;
                var tint = on ? Amber : Color.FromArgb(120, 150, 150, 156);

                if (_pages[i].Badge != null)
                {
                    _pages[i].Badge.DrawSized(starts[i] + badge / 2f, y + badgeH / 2f + 0.0015f,
                                              badge, badgeH, A(tint));
                }

                Draw.Text(_pages[i].Title, starts[i] + badge + pad, y, scale, A(tint), Plain);
            }
        }

        /// <summary>Screen width over height, for keeping the tab badges square. See Gauge.</summary>
        private float _aspect = 16f / 9f;
        private bool _measured;

        private static Color Mix(Color a, Color b, float t)
        {
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;

            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t),
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        // ==================================================================
        // Small print
        // ==================================================================

        private static bool Held(Keys key)
        {
            try { return Game.IsKeyPressed(key); }
            catch { return false; }
        }

        private static bool Edge(Keys key, ref bool wasDown)
        {
            var down = Held(key);
            var edge = down && !wasDown;
            wasDown = down;
            return edge;
        }

        private static float Clamp(float v, float lo, float hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }
}
