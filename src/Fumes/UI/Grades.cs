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

        /// <summary>
        /// THE SAME RECTANGLE THE PUMP DISPLAY USES, near enough, and in the same place.
        ///
        /// The card is shown instead of the meter and the meter appears the moment it closes,
        /// so putting them anywhere near each other on screen would read as two panels arguing.
        /// Sharing the footprint makes it one panel that changes what it is showing -- which is
        /// what it is. Meter sits at 0.5 wide by 0.300 across from 0.700 down; this is a shade
        /// taller because a row of cards needs more height than a column of numbers.
        /// </summary>
        private const float PanelW = 0.320f;
        private const float PanelH = 0.192f;
        private const float PanelLeft = 0.5f - PanelW / 2f;
        private const float PanelTop = 0.678f;

        private const float Margin = 0.012f;
        private const float CardGap = 0.006f;
        private const float CardTop = PanelTop + 0.046f;
        private const float CardH = 0.104f;
        private const float CardW = (PanelW - Margin * 2f - CardGap * 2f) / 3f;

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
            // Slid up into place, so it arrives rather than appears. The same easing the
            // settings panel uses, for the same reason.
            var top = PanelTop + (1f - _reveal) * 0.022f;

            Hud.Bar(PanelLeft, top, PanelW, PanelH, Fade(214, 8, 8, 10));

            Hud.Text(_brand, 0.5f, top + 0.006f, 0.42f, Fade(240, 250, 200, 110), Script, true);

            Hud.Bar(PanelLeft + Margin, top + 0.038f, PanelW - Margin * 2f, 0.0015f,
                    Fade(110, 250, 200, 110));

            Cards(top);

            Hud.Text(OnKeyboard()
                         ? "ARROWS choose     ENTER fill     BACKSPACE cancel"
                         : "DPAD choose     A fill     B cancel",
                     0.5f, top + PanelH - 0.024f, 0.26f,
                     Fade(180, 195, 195, 200), Plain, true);

            // LAST, so the light runs over the top of everything rather than under it. The
            // same frame the pump display wears, because this is the same panel a moment
            // earlier -- see Draw.ChaseFrame.
            Hud.ChaseFrame(PanelLeft, top, PanelW, PanelH,
                           Fade(165, 108, 72, 24), Fade(255, 255, 220, 140));
        }

        private void Cards(float panelTop)
        {
            var top = panelTop + (CardTop - PanelTop);

            for (var i = 0; i < Petrol.Length; i++)
            {
                Card(Petrol[i], PanelLeft + Margin + i * (CardW + CardGap), top, i == _at);
            }

            // THE OUTLINE IS DRAWN LAST AND AT THE EASED POSITION, so it slides between cards
            // instead of jumping -- and over them, so it reads as a frame around the chosen one
            // rather than a block behind it. Behind was what the first version did and it made
            // the selection look like a thick gold border with a hole in it.
            var x = PanelLeft + Margin + _slide * (CardW + CardGap);

            Outline(x, top, CardW, CardH, Fade(255, 250, 200, 110));
        }

        /// <summary>Four thin bars. A rectangle with a hole in it is four rectangles.</summary>
        private static void Outline(float left, float top, float w, float h, Color colour)
        {
            const float t = 0.0016f;

            Hud.Bar(left, top, w, t, colour);
            Hud.Bar(left, top + h - t, w, t, colour);
            Hud.Bar(left, top, t, h, colour);
            Hud.Bar(left + w - t, top, t, h, colour);
        }

        private void Card(FuelGrade grade, float left, float top, bool on)
        {
            var accent = Accent(grade);

            // A POP THAT SETTLES. It runs off an exponential from the moment the selection
            // moved, so it is a knock rather than a pulse -- a card that never stops breathing
            // is a card you cannot read the price off.
            var pop = on ? (float)Math.Exp(-_pulse * 4.5f) : 0f;
            var grow = pop * 0.004f;

            var x = left - grow;
            var y = top - grow;
            var w = CardW + grow * 2f;
            var h = CardH + grow * 2f;

            Hud.Bar(x, y, w, h, on ? Fade(250, 26, 24, 20) : Fade(210, 14, 14, 17));

            // The unselected cards keep a dim edge of their own, or they float on the panel
            // with nothing to say where one stops and the next starts.
            if (!on) Outline(x, y, w, h, Fade(70, 120, 120, 128));

            var centre = x + w / 2f;

            // Sized off the file's own shape so the pump is not squashed -- width from height
            // through the screen's aspect, since a fraction of width and a fraction of height
            // are not the same distance.
            var iconH = 0.030f + pop * 0.002f;
            var iconW = iconH / (Hud.Aspect() * Math.Max(0.2f, _pump.Aspect));

            _pump.DrawSized(centre, y + 0.024f, iconW, iconH,
                            Fade(on ? 255 : 130, accent.R, accent.G, accent.B));

            Hud.Text(Fumes.Fuel.Diesel.Name(grade), centre, y + 0.040f, 0.30f,
                     Fade(on ? 255 : 190, accent.R, accent.G, accent.B), Plain, true);

            var per = _price * _cfg.PriceFor(grade);

            Hud.Text("$" + per.ToString("0.00", CultureInfo.InvariantCulture) + "/L",
                     centre, y + 0.059f, 0.30f,
                     Fade(on ? 250 : 175, 240, 240, 240), Plain, true);

            // What it BUYS you, as a percentage rather than a multiplier. 0.90 means nothing at
            // a glance; "10% further" is the reason to pay for it.
            var further = (int)Math.Round((1f - _cfg.EconomyFor(grade)) * 100f);

            Hud.Text(further <= 0 ? "standard" : further + "% further",
                     centre, y + 0.078f, 0.25f,
                     Fade(on ? 215 : 140, 190, 195, 200), Plain, true);
        }

        /// <summary>A colour dimmed by however far the panel has come in.</summary>
        private Color Fade(int a, int r, int g, int b)
        {
            var alpha = (int)(a * _reveal);
            if (alpha < 0) alpha = 0;
            if (alpha > 255) alpha = 255;
            return Color.FromArgb(alpha, r, g, b);
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
