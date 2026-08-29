using System;
using System.Drawing;
using System.Globalization;
using Fumes.Core;

// Draw the CLASS is shadowed by Draw the METHOD below, in expression position, and the error
// that produces reads as if the class does not exist. The alias is the fix, and it is the same
// one Hoodrich carries for the same reason.
using Hud = Fumes.UI.Draw;

namespace Fumes.UI
{
    /// <summary>
    /// The pump's own display: what has gone in, what it costs a litre, and what you owe.
    ///
    /// Modelled on a real forecourt display rather than a game HUD, because that is the thing
    /// it is -- three numbers that only exist while the trigger is held, in the middle of the
    /// screen where you cannot miss the total climbing.
    /// </summary>
    internal sealed class Meter
    {
        private readonly Settings _cfg;
        private readonly Gauge _gauge;

        public Meter(Settings cfg, Gauge gauge)
        {
            _cfg = cfg;
            _gauge = gauge;
        }

        public void Draw(string station, float litres, float pricePerLitre, float owed, bool free)
        {
            try
            {
                const float x = 0.5f;
                const float y = 0.74f;
                const float w = 0.20f;
                const float h = 0.088f;

                Hud.Rect(x, y + h / 2f, w, h, Color.FromArgb(205, 8, 8, 10));
                Hud.Rect(x, y + 0.0016f, w, 0.0032f, Color.FromArgb(230, 235, 150, 40));

                Hud.Text(string.IsNullOrEmpty(station) ? "PUMP" : station.ToUpperInvariant(),
                             x, y + 0.006f, 0.28f, Color.FromArgb(220, 235, 200, 120), 4, true);

                Hud.Text(_gauge.Volume(litres),
                             x, y + 0.028f, 0.62f, Color.FromArgb(240, 245, 175, 55), 4, true);

                // The unit price is converted along with the volume, or the display contradicts
                // itself: gallons going in at a price per litre does not multiply out to the
                // total sitting next to it, and that reads as the mod overcharging.
                var unit = _cfg.Units == Units.Gallons
                    ? "$" + (pricePerLitre / Gauge.GallonsPerLitre).ToString("0.00", CultureInfo.InvariantCulture) + "/gal"
                    : "$" + pricePerLitre.ToString("0.00", CultureInfo.InvariantCulture) + "/L";

                var money = free
                    ? "ON THE HOUSE"
                    : "$" + owed.ToString("0.00", CultureInfo.InvariantCulture) + "   @ " + unit;

                Hud.Text(money, x, y + 0.064f, 0.30f,
                             Color.FromArgb(220, 225, 225, 225), 4, true);
            }
            catch (Exception ex)
            {
                Log.Once("meter", "The pump display could not be drawn: " + ex.Message);
            }
        }
    }
}
