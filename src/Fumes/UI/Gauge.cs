using System;
using System.Drawing;
using System.Globalization;
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

                Draw.Text(reading, x + w / 2f, y + 0.0005f, 0.235f,
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

        private static float Clamp01(float v)
        {
            if (v < 0f) return 0f;
            if (v > 1f) return 1f;
            return v;
        }
    }
}
