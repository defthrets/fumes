using System;
using System.Drawing;
using System.Globalization;
using GTA;
using Fumes.Core;
using Fumes.Fuel;

// Draw the CLASS is shadowed by Draw the METHOD below, in expression position, and the error
// that produces reads as if the class does not exist. The alias is the fix, and it is the same
// one Hoodrich carries for the same reason.
using Hud = Fumes.UI.Draw;

namespace Fumes.UI
{
    /// <summary>
    /// The pump's own display: what has gone in, what it costs, and the tank filling up.
    ///
    /// Modelled on a real forecourt display rather than a game HUD, because that is the thing
    /// it is -- numbers that only exist while the trigger is held, in the middle of the screen
    /// where you cannot miss the total climbing.
    ///
    /// The tank bar is the part worth the effort. A litre count going up is information; a tank
    /// visibly filling toward full is the thing you are actually doing, and it is the only part
    /// of the display that tells you when to stop without doing arithmetic.
    /// </summary>
    internal sealed class Meter
    {
        private readonly Settings _cfg;
        private readonly Gauge _gauge;
        private readonly Icon _pump = new Icon("fuel.png");

        // Panel geometry, all fractions of the screen. Written out rather than inlined because
        // every one of them appears in two or three places below and a panel whose parts drift
        // apart by a thousandth looks broken in a way that is very hard to see.
        private const float X = 0.5f;
        private const float Top = 0.715f;
        private const float W = 0.235f;
        private const float H = 0.145f;

        public Meter(Settings cfg, Gauge gauge)
        {
            _cfg = cfg;
            _gauge = gauge;
        }

        public void Draw(string station, float litres, float pricePerLitre, float owed, bool free, Tank tank)
        {
            try
            {
                var left = X - W / 2f;

                // Panel, then the amber rule across the top of it.
                Hud.Rect(X, Top + H / 2f, W, H, Color.FromArgb(212, 8, 8, 10));
                Hud.Rect(X, Top + 0.0016f, W, 0.0032f, Color.FromArgb(235, 245, 175, 55));

                Hud.Text(string.IsNullOrEmpty(station) ? "PUMP" : station.ToUpperInvariant(),
                         X, Top + 0.007f, 0.28f, Color.FromArgb(220, 235, 200, 120), 4, true);

                // The pump, bobbing away to the left of the litre count. It only moves while
                // fuel is moving -- a logo swaying on a stopped pump reads as a stuck animation
                // rather than as life.
                var flowing = tank != null && tank.Litres < tank.Capacity - 0.05f;
                _pump.Draw(left + 0.036f, Top + 0.049f, flowing);

                Hud.Text(_gauge.Volume(litres),
                         X + 0.014f, Top + 0.029f, 0.62f, Color.FromArgb(240, 245, 175, 55), 4, true);

                // The unit price is converted along with the volume, or the display contradicts
                // itself: gallons going in at a price per litre does not multiply out to the
                // total sitting next to it, and that reads as the mod overcharging.
                var unit = _cfg.Units == Units.Gallons
                    ? "$" + (pricePerLitre / Gauge.GallonsPerLitre).ToString("0.00", CultureInfo.InvariantCulture) + "/gal"
                    : "$" + pricePerLitre.ToString("0.00", CultureInfo.InvariantCulture) + "/L";

                var money = free
                    ? "ON THE HOUSE"
                    : "$" + owed.ToString("0.00", CultureInfo.InvariantCulture) + "   @ " + unit;

                Hud.Text(money, X, Top + 0.066f, 0.30f, Color.FromArgb(220, 225, 225, 225), 4, true);

                FillBar(left, tank);
            }
            catch (Exception ex)
            {
                Log.Once("meter", "The pump display could not be drawn: " + ex.Message);
            }
        }

        /// <summary>
        /// The tank filling up.
        ///
        /// A well, the fuel in it, a highlight travelling along the fuel while it flows, and
        /// the percentage. The travelling highlight is doing real work: at a couple of litres a
        /// second the bar grows about a pixel a frame, which is not visible as MOVEMENT, and
        /// without something moving the display looks frozen while it is in fact working.
        /// </summary>
        private void FillBar(float left, Tank tank)
        {
            if (tank == null) return;

            const float barH = 0.017f;
            var barY = Top + 0.094f;
            var barW = W - 0.028f;
            var barX = left + 0.014f;

            var fraction = tank.Capacity > 0.01f ? tank.Litres / tank.Capacity : 0f;
            if (fraction < 0f) fraction = 0f;
            if (fraction > 1f) fraction = 1f;

            var full = fraction >= 0.999f;

            // Well, then a hairline border so the empty part still reads as a container.
            Hud.Bar(barX, barY, barW, barH, Color.FromArgb(190, 26, 26, 30));
            Hud.Bar(barX, barY, barW, 0.0012f, Color.FromArgb(120, 90, 90, 96));
            Hud.Bar(barX, barY + barH - 0.0012f, barW, 0.0012f, Color.FromArgb(120, 90, 90, 96));

            var fuelColour = full
                ? Color.FromArgb(235, 120, 225, 135)
                : Color.FromArgb(235, 245, 175, 55);

            var filled = barW * fraction;
            if (filled > 0.0004f) Hud.Bar(barX, barY, filled, barH, fuelColour);

            // The travelling highlight, while there is still room to fill.
            if (!full && filled > 0.006f)
            {
                var phase = (Environment.TickCount % 1100) / 1100f;
                var shine = 0.010f;
                var shineX = barX + (filled + shine) * phase - shine;

                // Clipped to the fuel, not to the well -- a highlight running on past the fuel
                // into the empty part of the tank looks like a second, wrong fill bar.
                var visibleLeft = shineX < barX ? barX : shineX;
                var visibleRight = shineX + shine > barX + filled ? barX + filled : shineX + shine;

                if (visibleRight > visibleLeft)
                {
                    Hud.Bar(visibleLeft, barY, visibleRight - visibleLeft, barH,
                            Color.FromArgb(90, 255, 245, 200));
                }
            }

            var label = full
                ? "TANK FULL"
                : "TANK   " + (fraction * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";

            Hud.Text(label, X, barY + 0.0195f, 0.27f,
                     full ? Color.FromArgb(235, 150, 235, 160) : Color.FromArgb(210, 215, 215, 215),
                     4, true);
        }
    }
}
