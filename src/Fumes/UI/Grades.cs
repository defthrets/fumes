using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using GTA;
using GTA.Native;
using Fumes.Core;

// Both namespaces have a Control and only one of them is a game control.
using Control = GTA.Control;
using Hud = Fumes.UI.Draw;

namespace Fumes.UI
{
    /// <summary>
    /// The card you pick a grade on before the fuel starts moving.
    ///
    /// A PANEL RATHER THAN A PROMPT, because the choice has numbers attached. Cycling a grade
    /// on a help-text line would fit, and it would ask you to remember what Plus costs and what
    /// it returns while you press a button to find out. Three cards side by side answer the
    /// question the choice actually is -- what does each one cost me and what do I get -- and
    /// the answer is different at every station, since the price is the forecourt's.
    ///
    /// It is only ever shown for petrol. A diesel vehicle has no choice to make, so it is not
    /// offered one; the pump display names the fuel while it goes in.
    /// </summary>
    internal sealed class Grades
    {
        internal enum Result
        {
            /// <summary>Still open.</summary>
            Waiting,

            /// <summary>A grade was chosen. Read Picked.</summary>
            Chosen,

            /// <summary>Backed out. Nothing was filled.</summary>
            Dropped
        }

        private static readonly FuelGrade[] Petrol =
        {
            FuelGrade.Regular, FuelGrade.Plus, FuelGrade.Premium
        };

        /// <summary>
        /// One accent per grade, the way a forecourt colour-codes its own pumps.
        ///
        /// Restrained on purpose: the cards are the same otherwise, and the colour is a hint
        /// rather than the label. The selected card is what the eye is meant to find, and it
        /// finds it by size and brightness, not by hue.
        /// </summary>
        private static Color Accent(FuelGrade grade)
        {
            if (grade == FuelGrade.Plus) return Color.FromArgb(255, 120, 190, 235);
            if (grade == FuelGrade.Premium) return Color.FromArgb(255, 245, 200, 90);
            return Color.FromArgb(255, 200, 205, 212);
        }

        private const float PanelW = 0.430f;
        private const float PanelH = 0.230f;
        private const float PanelY = 0.500f;

        private const float CardW = 0.126f;
        private const float CardH = 0.132f;
        private const float CardGap = 0.010f;

        /// <summary>Chalet Comprime Cologne, the block font the rest of the mod uses.</summary>
        private const int Plain = 4;
        private const int Script = 1;

        private readonly Settings _cfg;
        private readonly Icon _pump = new Icon("fuel.png");

        private int _at;
        private float _reveal;
        private float _slide;
        private float _pulse;

        private bool _left, _right, _accept, _cancel;

        private float _price;
        private string _brand = "PUMP";

        /// <summary>
        /// The screen's own shape, so a "square" icon is square.
        ///
        /// Sprites are laid out in a fixed 1280x720 canvas whatever the monitor is, so a width
        /// and a height in those units are not the same distance on a 21:9 panel -- an icon
        /// given equal numbers comes out a third too wide. Read once; a resolution does not
        /// change between frames.
        /// </summary>
        private float _aspect = 16f / 9f;

        public FuelGrade Picked { get; private set; }

        public bool Open { get; private set; }

        public Grades(Settings cfg)
        {
            _cfg = cfg;
        }

        /// <summary>
        /// Opens the card, starting on the grade already selected.
        ///
        /// Starting there rather than at Regular because the setting is a habit -- somebody who
        /// always buys Premium should be one button from buying Premium, not two.
        /// </summary>
        public void Show(float basePrice, string brand)
        {
            Open = true;
            _price = basePrice;
            _brand = string.IsNullOrEmpty(brand) ? "PUMP" : brand;

            _at = Array.IndexOf(Petrol, _cfg.Grade);
            if (_at < 0) _at = 0;

            _reveal = 0f;
            _slide = _at;
            _pulse = 0f;

            try
            {
                var res = GTA.UI.Screen.Resolution;
                if (res.Height > 0) _aspect = res.Width / (float)res.Height;
            }
            catch
            {
                // 16:9 is the safe assumption and the common one.
            }

            // The edges start held, so a button still down from the press that opened this
            // cannot also choose on the first frame.
            _left = _right = _accept = _cancel = true;
        }

        public void Close()
        {
            Open = false;
        }

        public Result Update()
        {
            if (!Open) return Result.Waiting;

            try
            {
                var dt = Game.LastFrameTime;

                Ease(ref _reveal, 1f, dt, 0.075f);
                Ease(ref _slide, _at, dt, 0.055f);

                _pulse += dt;

                Deafen();

                var result = Read();

                Panel();

                return result;
            }
            catch (Exception ex)
            {
                Log.Once("grades", "The grade card fell over: " + ex.Message);
                Open = false;
                Picked = _cfg.Grade;
                return Result.Chosen;
            }
        }

        // ==================================================================
        // Input
        // ==================================================================

        /// <summary>
        /// Keyboard and pad through one edge each, the way the settings menu does it.
        ///
        /// One detector fed by "key OR button" rather than two joined together: two would leave
        /// the device you did not touch holding a stale "was down" and swallow the next press
        /// on the other one.
        /// </summary>
        private Result Read()
        {
            if (Edge(Keys.Left, Control.FrontendLeft, ref _left)) Move(-1);
            if (Edge(Keys.Right, Control.FrontendRight, ref _right)) Move(1);

            if (Edge(Keys.Return, Control.FrontendAccept, ref _accept))
            {
                Picked = Petrol[_at];
                Open = false;
                return Result.Chosen;
            }

            if (Edge(Keys.Back, Control.FrontendCancel, ref _cancel))
            {
                Open = false;
                return Result.Dropped;
            }

            return Result.Waiting;
        }

        private void Move(int by)
        {
            _at += by;

            // STOPS AT THE ENDS rather than wrapping. Three cards are all on screen at once, so
            // wrapping would jump the highlight the full width of the panel to reach a card
            // sitting right next to where it started.
            if (_at < 0) _at = 0;
            if (_at >= Petrol.Length) _at = Petrol.Length - 1;

            _pulse = 0f;
        }

        // ==================================================================
        // The card
        // ==================================================================

        private void Panel()
        {
            var lift = (1f - _reveal) * 0.020f;
            var y = PanelY + lift;

            var fade = (int)(235 * _reveal);

            Hud.Rect(0.5f, y, PanelW, PanelH, Color.FromArgb((int)(214 * _reveal), 8, 8, 10));

            // A rule under the header rather than a box round everything: the cards below have
            // their own edges and a second frame around them reads as clutter.
            Hud.Rect(0.5f, y - PanelH / 2f + 0.048f, PanelW - 0.024f, 0.0015f,
                     Color.FromArgb((int)(90 * _reveal), 250, 200, 110));

            Hud.Text(_brand, 0.5f, y - PanelH / 2f + 0.010f, 0.46f,
                     Color.FromArgb(fade, 250, 200, 110), Script, true);

            Hud.Text("CHOOSE A GRADE", 0.5f, y - PanelH / 2f + 0.055f, 0.28f,
                     Color.FromArgb((int)(190 * _reveal), 200, 200, 205), Plain, true);

            Cards(y);

            Hud.Text(OnKeyboard()
                         ? "ARROWS choose    ENTER fill    BACKSPACE cancel"
                         : "DPAD choose    A fill    B cancel",
                     0.5f, y + PanelH / 2f - 0.026f, 0.27f,
                     Color.FromArgb((int)(180 * _reveal), 195, 195, 200), Plain, true);
        }

        private void Cards(float panelY)
        {
            var span = Petrol.Length * CardW + (Petrol.Length - 1) * CardGap;
            var first = 0.5f - span / 2f + CardW / 2f;
            var top = panelY - 0.012f;

            // THE HIGHLIGHT IS DRAWN FIRST AND AT THE EASED POSITION, so it slides between
            // cards instead of jumping. It sits behind them, which is why it is a wash rather
            // than an outline -- an outline drawn under a card would only show on three sides.
            var glideX = first + _slide * (CardW + CardGap);

            Hud.Rect(glideX, top, CardW + 0.008f, CardH + 0.008f,
                     Color.FromArgb((int)(200 * _reveal), 250, 200, 110));

            for (var i = 0; i < Petrol.Length; i++)
            {
                Card(Petrol[i], first + i * (CardW + CardGap), top, i == _at);
            }
        }

        private void Card(FuelGrade grade, float x, float y, bool on)
        {
            var accent = Accent(grade);
            var alpha = (int)(255 * _reveal);

            // A HEARTBEAT ON THE CHOSEN CARD, and only on that one. It settles rather than
            // running forever -- a panel that never stops moving is a panel you cannot read.
            var beat = on ? (float)Math.Exp(-_pulse * 3.2f) * 0.006f : 0f;

            var w = CardW - 0.006f + beat;
            var h = CardH - 0.006f + beat;

            Hud.Rect(x, y, w, h, Color.FromArgb((int)((on ? 250 : 220) * _reveal),
                                                on ? 20 : 13, on ? 20 : 13, on ? 24 : 16));

            // The pump, tinted to the grade. Sized off the file's own aspect so it is not
            // stretched -- the icon is 1:1.36 and a square would squash it.
            var iconH = 0.052f;
            var iconW = iconH / (_aspect * Math.Max(0.2f, _pump.Aspect));

            _pump.DrawSized(x, y - 0.030f, iconW, iconH,
                            Color.FromArgb((int)((on ? 255 : 150) * _reveal),
                                           accent.R, accent.G, accent.B));

            Hud.Text(Fumes.Fuel.Diesel.Name(grade), x, y + 0.004f, 0.34f,
                     Color.FromArgb(alpha, accent.R, accent.G, accent.B), Plain, true);

            var per = _price * _cfg.PriceFor(grade);

            Hud.Text("$" + per.ToString("0.00", CultureInfo.InvariantCulture) + "/L",
                     x, y + 0.028f, 0.30f,
                     Color.FromArgb((int)((on ? 250 : 190) * _reveal), 240, 240, 240), Plain, true);

            // What it BUYS you, as a percentage rather than a multiplier. 0.90 means nothing at
            // a glance; "10% further" is the reason to pay for it.
            var economy = _cfg.EconomyFor(grade);
            var further = (int)Math.Round((1f - economy) * 100f);

            Hud.Text(further <= 0 ? "standard" : further + "% further",
                     x, y + 0.048f, 0.26f,
                     Color.FromArgb((int)((on ? 210 : 150) * _reveal), 190, 195, 200), Plain, true);
        }

        // ==================================================================
        // Small print
        // ==================================================================

        private static void Deafen()
        {
            try
            {
                Game.DisableControlThisFrame(Control.Attack);
                Game.DisableControlThisFrame(Control.Attack2);
                Game.DisableControlThisFrame(Control.Aim);
                Game.DisableControlThisFrame(Control.Jump);
                Game.DisableControlThisFrame(Control.Enter);
                Game.DisableControlThisFrame(Control.Context);
                Game.DisableControlThisFrame(Control.Phone);
                Game.DisableControlThisFrame(Control.SelectWeapon);
                Game.DisableControlThisFrame(Control.MoveLeftRight);
                Game.DisableControlThisFrame(Control.MoveUpDown);
            }
            catch
            {
                // Worst case the game hears the same button we did.
            }
        }

        private static bool OnKeyboard()
        {
            try { return Function.Call<bool>(Hash.IS_USING_KEYBOARD_AND_MOUSE, 2); }
            catch { return true; }
        }

        private static bool Held(Keys key)
        {
            try { return Game.IsKeyPressed(key); }
            catch { return false; }
        }

        private static bool Pad(Control control)
        {
            // IsControlPressed reports a DISABLED control and IsEnabledControlPressed does not,
            // which matters because Deafen has just turned half of these off.
            try { return Game.IsControlPressed(control); }
            catch { return false; }
        }

        private static bool Edge(Keys key, Control pad, ref bool wasDown)
        {
            var down = Held(key) || Pad(pad);
            var edge = down && !wasDown;
            wasDown = down;
            return edge;
        }

        private static void Ease(ref float value, float target, float dt, float seconds)
        {
            if (dt <= 0f || seconds <= 0.001f) { value = target; return; }

            value += (target - value) * (1f - (float)Math.Exp(-dt / seconds));

            if (Math.Abs(target - value) < 0.002f) value = target;
        }
    }
}
