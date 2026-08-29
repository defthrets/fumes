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
        private readonly Glyphs _glyphs = new Glyphs();

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

                // Border, well, fill. Three rectangles, because there is no rounded-rect
                // primitive and nobody has ever noticed.
                Draw.Bar(x - 0.0022f, y - 0.0022f, w + 0.0044f, h + 0.0044f, Color.FromArgb(200, 0, 0, 0));
                Draw.Bar(x, y, w, h, Color.FromArgb(165, 28, 28, 32));

                var colour = Level(fraction);

                if (_cfg.Vertical)
                {
                    // FILLED FROM THE BOTTOM, which is the only way up is up. A vertical bar
                    // that fills downward from the top is a loading bar stood on its end; one
                    // that fills from the floor is a level in a container, and that is what a
                    // tank is. It also matches the glass on the pump display exactly, so the
                    // two things that show the same number look like the same instrument.
                    if (fraction > 0.001f)
                    {
                        var fill = h * fraction;
                        var top = y + h - fill;

                        Draw.Bar(x, top, w, fill, colour);

                        // A highlight sliding up the fuel. The bar moves about a pixel a
                        // minute on its own, which is not movement anybody can see -- without
                        // something travelling, a working gauge looks like a painted one.
                        if (fill > 0.012f)
                        {
                            var band = 0.010f;
                            var phase = (Environment.TickCount % 2400) / 2400f;
                            var bandY = y + h - (fill + band) * phase;

                            var lo = bandY < top ? top : bandY;
                            var hi = bandY + band > y + h ? y + h : bandY + band;

                            if (hi > lo)
                            {
                                Draw.Bar(x, lo, w, hi - lo, Color.FromArgb(60, 255, 250, 225));
                            }
                        }
                    }

                    // The reserve mark, measured from the bottom for the same reason.
                    var reserveY = y + h * (1f - Clamp01(_cfg.ReserveFraction));
                    Draw.Bar(x - 0.0016f, reserveY, w + 0.0032f, 0.0011f,
                             Color.FromArgb(225, 235, 180, 60));
                }
                else
                {
                    if (fraction > 0.001f) Draw.Bar(x, y, w * fraction, h, colour);

                    var reserveX = x + w * Clamp01(_cfg.ReserveFraction);
                    Draw.Bar(reserveX, y - 0.0016f, 0.0012f, h + 0.0032f,
                             Color.FromArgb(225, 235, 180, 60));
                }

                if (!_cfg.ShowNumbers) return;

                if (_cfg.Vertical)
                {
                    // TURNED ON ITS SIDE, because a bar this narrow cannot hold upright text
                    // and GTA cannot rotate any. The letters are pictures -- see Glyphs -- and
                    // they run up the bar, which is the only direction there is room in.
                    var ink = Color.FromArgb(215, 18, 18, 20);

                    var pct = (int)Math.Round(fraction * 100f);
                    if (pct > 99) pct = 99;

                    var pitch = w * 1.25f;
                    var text = pct.ToString(CultureInfo.InvariantCulture) + "%";

                    _glyphs.Number(text, x + w / 2f, y + h - 0.004f, w * 0.85f, pitch, ink);
                    _glyphs.Label(x + w / 2f, y + 0.026f, w * 0.85f, 0.040f, ink);
                    return;
                }

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
        /// Where the ramp turns, from empty on the left to full on the right.
        ///
        /// YELLOW AT FULL THROUGH TO RED AT EMPTY -- no green in it anywhere. Green reads as
        /// "fine, ignore me", and a fuel gauge is never saying that: even a full tank is a thing
        /// you are spending. Yellow to red is one hue sliding down its own scale, which the eye
        /// reads as a quantity rather than as three separate verdicts.
        ///
        /// Six stops rather than a straight two-colour blend, because a blend from yellow to red
        /// runs through a muddy brown at the halfway point. Putting real gold, amber and orange
        /// in the middle keeps every part of the range a colour somebody would name.
        /// </summary>
        private static readonly float[] Stops = { 0f, 0.12f, 0.30f, 0.50f, 0.72f, 1f };

        private static readonly Color[] Ramp =
        {
            Color.FromArgb(235, 212,  52,  46),   // empty      red
            Color.FromArgb(235, 224,  84,  48),   // reserve    red-orange
            Color.FromArgb(235, 233, 126,  48),   // a third    orange
            Color.FromArgb(235, 240, 166,  54),   // half       amber
            Color.FromArgb(235, 245, 196,  60),   // most       gold
            Color.FromArgb(235, 248, 220,  74)    // full       yellow
        };

        /// <summary>
        /// The bar's colour at a given level: a continuous ramp, flashing under the reserve.
        ///
        /// It used to be three steps -- green, amber, red -- which meant half a tank and a full
        /// one were the same green, and the gauge said nothing at all until it was nearly too
        /// late. A ramp is reading the number for you.
        ///
        /// The flash is on wall-clock time rather than a frame counter, so it blinks at the
        /// same speed whatever the framerate is doing.
        /// </summary>
        private Color Level(float fraction)
        {
            var colour = OnRamp(Clamp01(fraction));

            if (fraction > _cfg.ReserveFraction) return colour;

            // Below the reserve mark it also pulses, because by then the colour alone has
            // nowhere left to go -- it is already as red as it gets.
            if (_blinkSince == 0) _blinkSince = Environment.TickCount;

            var on = ((Environment.TickCount - _blinkSince) / 420) % 2 == 0;
            if (on) return colour;

            return Color.FromArgb(150, colour.R / 2, colour.G / 2, colour.B / 2);
        }

        private static Color OnRamp(float t)
        {
            if (t <= Stops[0]) return Ramp[0];
            if (t >= Stops[Stops.Length - 1]) return Ramp[Ramp.Length - 1];

            for (var i = 1; i < Stops.Length; i++)
            {
                if (t > Stops[i]) continue;

                var span = Stops[i] - Stops[i - 1];
                var u = span <= 0.0001f ? 0f : (t - Stops[i - 1]) / span;

                return Mix(Ramp[i - 1], Ramp[i], u);
            }

            return Ramp[Ramp.Length - 1];
        }

        private static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t),
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
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

                if (Edge(Keys.NumPad0, ref _dumpDown)) Keep();

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

        /// <summary>
        /// Writes where you have dragged it straight into Fumes.ini.
        ///
        /// This is the point of the tuner and it took too long to get here. Printing the
        /// numbers to a log and asking somebody to copy four of them into a file by hand is
        /// most of the work still left undone -- and every one of those four is a chance to
        /// mistype a decimal. The edit is surgical: it finds the key, replaces what is after
        /// the equals sign, and leaves every comment and blank line exactly where it was.
        /// </summary>
        private void Keep()
        {
            var ok = Write("X", _cfg.GaugeX)
                   & Write("Y", _cfg.GaugeY)
                   & Write("Width", _cfg.GaugeWidth)
                   & Write("Height", _cfg.GaugeHeight);

            Log.Info("Gauge placement saved:" + Environment.NewLine + IniBlock());

            Say(ok
                ? "~g~Gauge position saved~s~ to Fumes.ini."
                : "~y~Could not write Fumes.ini~s~ - numbers are in Fumes.log.");
        }

        private static bool Write(string key, float value)
        {
            return IniFile.SetValue(Paths.Ini, "HUD", key,
                                    value.ToString("0.0000", CultureInfo.InvariantCulture));
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
