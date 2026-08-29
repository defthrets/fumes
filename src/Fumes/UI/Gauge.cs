using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using GTA;
using Fumes.Core;
using Fumes.Fuel;

namespace Fumes.UI
{
    /// <summary>
    /// The fuel gauge, bottom right by default, above where the minimap sits.
    ///
    /// A bar rather than a dial. A dial would look better and would have to be a texture, and
    /// a texture means shipping a .ytd into the game's archives -- which turns a script mod
    /// anybody can drop into scripts\ into an asset mod that needs OpenIV, a limit adjuster
    /// and a different install per game edition. Not worth it for a number between 0 and 1.
    /// </summary>
    internal sealed class Gauge
    {
        private readonly Settings _cfg;

        /// <summary>Flash phase for the empty warning. Wall clock, so it blinks at the same rate always.</summary>
        private int _blinkSince;

        public Gauge(Settings cfg)
        {
            _cfg = cfg;
        }

        /// <summary>US gallons, for anybody who would rather read the game's own units.</summary>
        public const float GallonsPerLitre = 0.2641720524f;

        public string Volume(float litres)
        {
            return _cfg.Units == Units.Gallons
                ? (litres * GallonsPerLitre).ToString("0.0", CultureInfo.InvariantCulture) + " gal"
                : litres.ToString("0.0", CultureInfo.InvariantCulture) + " L";
        }

        public void Update(Vehicle vehicle, Tank tank, bool refuelling, bool stalled = false)
        {
            if (!_cfg.ShowGauge || tank == null) return;

            // On foot the gauge is only shown while actually putting fuel in something --
            // otherwise it is a permanent readout of a car you are not in.
            if (_cfg.GaugeOnlyInVehicle && !refuelling && !InThisVehicle(vehicle)) return;

            try
            {
                var x = _cfg.GaugeX;
                var y = _cfg.GaugeY;
                var w = _cfg.GaugeWidth;
                var h = _cfg.GaugeHeight;

                var fraction = Clamp01(tank.Fraction);

                // Border first, then the well, then the fill. Drawn as three rectangles
                // because there is no rounded-rect primitive and nobody has ever noticed.
                Draw.Bar(x - 0.0022f, y - 0.0022f, w + 0.0044f, h + 0.0044f, Color.FromArgb(190, 0, 0, 0));
                Draw.Bar(x, y, w, h, Color.FromArgb(150, 30, 30, 32));

                if (fraction > 0.001f)
                {
                    Draw.Bar(x, y, w * fraction, h, Level(fraction));
                }

                // The reserve mark, so "low" is a place on the gauge rather than a message
                // that has already gone.
                var reserveX = x + w * Clamp01(_cfg.ReserveFraction);
                Draw.Bar(reserveX, y - 0.0016f, 0.0012f, h + 0.0032f, Color.FromArgb(220, 235, 180, 60));

                if (!_cfg.ShowNumbers) return;

                // THE READOUT SITS INSIDE THE BAR, which is not where it started. It used to be
                // a label row above -- fine in the bottom-right corner, impossible under the
                // minimap: there is about two hundredths of a screen between the minimap and
                // the bottom edge, which fits a bar or a line of text but not both. Overlaid on
                // the bar it needs no room of its own, and the outline the text already carries
                // is enough to keep it readable over any fill colour.
                //
                // The stall state takes the label's place rather than adding a line, for the
                // same reason -- and it reads better anyway, because an empty bar and a bar
                // with a drop left in it look identical at a glance.
                var label = stalled ? (tank.Electric ? "FLAT" : "DRY") : tank.Noun;

                var reading = label + "   " + Volume(tank.Litres) + " / " + Volume(tank.Capacity);

                Draw.Text(reading, x + w / 2f, y - 0.0008f, 0.215f,
                          stalled ? Color.FromArgb(240, 255, 120, 110)
                                  : Color.FromArgb(230, 245, 245, 245),
                          4, true);
            }
            catch (Exception ex)
            {
                Log.Once("gauge", "The gauge could not be drawn: " + ex.Message);
            }
        }

        private static bool InThisVehicle(Vehicle v)
        {
            try
            {
                var me = Game.Player.Character;
                return v != null && v.Exists() && me != null && me.Exists() && me.CurrentVehicle == v;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Green down to the reserve mark, amber below it, red and flashing on empty.
        ///
        /// The flash is on wall-clock time rather than a frame counter so it blinks at the
        /// same speed whatever the framerate is doing.
        /// </summary>
        private Color Level(float fraction)
        {
            if (fraction > _cfg.ReserveFraction * 2f) return Color.FromArgb(225, 105, 205, 120);
            if (fraction > _cfg.ReserveFraction) return Color.FromArgb(230, 235, 190, 70);

            if (_blinkSince == 0) _blinkSince = Environment.TickCount;
            var on = ((Environment.TickCount - _blinkSince) / 420) % 2 == 0;

            return on ? Color.FromArgb(235, 220, 70, 60) : Color.FromArgb(150, 130, 45, 40);
        }

        // ==================================================================
        // The tuner
        // ==================================================================

        private bool _upDown, _downDown, _leftDown, _rightDown;
        private bool _narrowDown, _wideDown, _thinDown, _fatDown, _dumpDown;

        /// <summary>
        /// Drags the gauge into place with the game running.
        ///
        /// This exists because the one number nobody can compute is where the minimap actually
        /// is: it moves with the player's safe-zone slider and with their aspect ratio, and the
        /// natives that claim to tell you are easy to get subtly wrong in a way that puts the
        /// bar somewhere random rather than somewhere obviously broken. A number in an ini that
        /// anybody can nudge while looking at it beats a formula nobody can check.
        ///
        /// It writes into the live Settings object, never to disk -- a tuner that silently
        /// rewrote somebody's ini would be a tuner that lost their edits. NumPad0 prints the
        /// result ready to paste.
        /// </summary>
        public void Tune()
        {
            if (!_cfg.TuneGauge) return;

            try
            {
                const float move = 0.0015f;
                const float size = 0.0020f;

                if (Edge(Keys.NumPad8, ref _upDown)) _cfg.GaugeY -= move;
                if (Edge(Keys.NumPad2, ref _downDown)) _cfg.GaugeY += move;
                if (Edge(Keys.NumPad4, ref _leftDown)) _cfg.GaugeX -= move;
                if (Edge(Keys.NumPad6, ref _rightDown)) _cfg.GaugeX += move;

                if (Edge(Keys.NumPad7, ref _narrowDown)) _cfg.GaugeWidth -= size;
                if (Edge(Keys.NumPad9, ref _wideDown)) _cfg.GaugeWidth += size;
                if (Edge(Keys.NumPad1, ref _thinDown)) _cfg.GaugeHeight -= size * 0.4f;
                if (Edge(Keys.NumPad3, ref _fatDown)) _cfg.GaugeHeight += size * 0.4f;

                _cfg.GaugeX = Clamp(_cfg.GaugeX, 0f, 0.95f);
                _cfg.GaugeY = Clamp(_cfg.GaugeY, 0f, 0.99f);
                _cfg.GaugeWidth = Clamp(_cfg.GaugeWidth, 0.02f, 0.8f);
                _cfg.GaugeHeight = Clamp(_cfg.GaugeHeight, 0.004f, 0.2f);

                if (Edge(Keys.NumPad0, ref _dumpDown))
                {
                    Log.Info("Gauge placement:" + Environment.NewLine + IniBlock());
                    Say("Gauge numbers written to Fumes.log");
                }

                Draw.Text("GAUGE TUNER   [8/2] up down   [4/6] left right   " +
                          "[7/9] width   [1/3] height   [0] log",
                          0.5f, 0.055f, 0.30f, Color.FromArgb(230, 245, 200, 90), 4, true);

                Draw.Text(IniLine(), 0.5f, 0.085f, 0.34f,
                          Color.FromArgb(240, 255, 255, 255), 4, true);
            }
            catch (Exception ex)
            {
                Log.Once("gauge-tune", "The gauge tuner fell over: " + ex.Message);
            }
        }

        private string IniLine()
        {
            return "X " + _cfg.GaugeX.ToString("0.0000", CultureInfo.InvariantCulture) +
                   "   Y " + _cfg.GaugeY.ToString("0.0000", CultureInfo.InvariantCulture) +
                   "   W " + _cfg.GaugeWidth.ToString("0.0000", CultureInfo.InvariantCulture) +
                   "   H " + _cfg.GaugeHeight.ToString("0.0000", CultureInfo.InvariantCulture);
        }

        /// <summary>The placement, formatted so it can be pasted straight under [HUD].</summary>
        private string IniBlock()
        {
            var nl = Environment.NewLine;
            return "X = " + _cfg.GaugeX.ToString("0.0000", CultureInfo.InvariantCulture) + nl +
                   "Y = " + _cfg.GaugeY.ToString("0.0000", CultureInfo.InvariantCulture) + nl +
                   "Width = " + _cfg.GaugeWidth.ToString("0.0000", CultureInfo.InvariantCulture) + nl +
                   "Height = " + _cfg.GaugeHeight.ToString("0.0000", CultureInfo.InvariantCulture);
        }

        /// <summary>Rising edge for a key, since the input API only reports held.</summary>
        private static bool Edge(Keys key, ref bool wasDown)
        {
            bool down;
            try { down = Game.IsKeyPressed(key); }
            catch { down = false; }

            var edge = down && !wasDown;
            wasDown = down;
            return edge;
        }

        private static void Say(string text)
        {
            try { GTA.UI.Notification.PostTicker(text, false, false); }
            catch { /* not worth a crash */ }
        }

        private static float Clamp(float v, float lo, float hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        private static float Clamp01(float v)
        {
            if (v < 0f) return 0f;
            if (v > 1f) return 1f;
            return v;
        }
    }
}
