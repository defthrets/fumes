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
    /// The pump's own display: what has gone in, what it costs, and a tank visibly filling.
    ///
    /// Modelled on a real forecourt display rather than a game HUD, because that is the thing
    /// it is -- numbers that only exist while the trigger is held, in the middle of the screen
    /// where you cannot miss the total climbing.
    ///
    /// The tank is the part worth the effort, and it is drawn UPRIGHT for a reason: a
    /// horizontal bar filling left to right is a progress bar, and a progress bar is a thing
    /// you read. Liquid rising in a glass is a thing you WATCH, and it is the only part of the
    /// display that tells you when to stop without doing arithmetic. Everything about the
    /// animation follows from that -- the surface sloshes, bubbles rise through it, and both
    /// settle when the tank is full.
    /// </summary>
    internal sealed class Meter
    {
        private readonly Settings _cfg;
        private readonly Gauge _gauge;

        private readonly Icon _pump = new Icon("fuel.png");
        private readonly Icon _drip = new Icon("drop.png");

        // Panel geometry, all fractions of the screen. Written out rather than inlined because
        // every one appears in two or three places below, and a panel whose parts drift apart
        // by a thousandth looks broken in a way that is very hard to see.
        private const float X = 0.5f;
        private const float Top = 0.715f;
        private const float W = 0.25f;
        private const float H = 0.145f;

        /// <summary>The upright tank, in the same fractions.</summary>
        private const float TankW = 0.034f;
        private const float TankH = 0.088f;

        /// <summary>How many columns the liquid is drawn in. See Liquid().</summary>
        private const int Columns = 22;

        /// <summary>How many bubbles rise through it.</summary>
        private const int Bubbles = 5;

        /// <summary>When the tank last reached full, so the flourish can fire once.</summary>
        private int _filledAt;
        private bool _wasFull;

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

                var fraction = tank == null || tank.Capacity <= 0.01f ? 0f : tank.Litres / tank.Capacity;
                if (fraction < 0f) fraction = 0f;
                if (fraction > 1f) fraction = 1f;

                var full = fraction >= 0.999f;

                if (full && !_wasFull) _filledAt = Environment.TickCount;
                _wasFull = full;

                // Panel, then the amber rule across the top of it.
                Hud.Rect(X, Top + H / 2f, W, H, Color.FromArgb(212, 8, 8, 10));
                Hud.Rect(X, Top + 0.0016f, W, 0.0032f, Color.FromArgb(235, 245, 175, 55));

                Hud.Text(string.IsNullOrEmpty(station) ? "PUMP" : station.ToUpperInvariant(),
                         X, Top + 0.0065f, 0.28f, Color.FromArgb(220, 235, 200, 120), 4, true);

                // The pump, bobbing away on the left, dripping while fuel moves. It goes still
                // when the tank is full -- a logo swaying on a finished pump reads as a stuck
                // animation rather than as life.
                _pump.Scale = 0.052f;
                _pump.Draw(left + 0.036f, Top + 0.0545f, !full);
                Drip(left + 0.036f, Top + 0.070f, !full);

                Hud.Text(_gauge.Volume(litres),
                         left + 0.108f, Top + 0.027f, 0.60f,
                         Color.FromArgb(240, 245, 175, 55), 4, true);

                // The unit price is converted along with the volume, or the display contradicts
                // itself: gallons going in at a price per litre does not multiply out to the
                // total sitting next to it, and that reads as the mod overcharging.
                var unit = _cfg.Units == Units.Gallons
                    ? "$" + (pricePerLitre / Gauge.GallonsPerLitre).ToString("0.00", CultureInfo.InvariantCulture) + "/gal"
                    : "$" + pricePerLitre.ToString("0.00", CultureInfo.InvariantCulture) + "/L";

                var money = free
                    ? "ON THE HOUSE"
                    : "$" + owed.ToString("0.00", CultureInfo.InvariantCulture) + "   @ " + unit;

                Hud.Text(money, left + 0.108f, Top + 0.069f, 0.30f,
                         Color.FromArgb(220, 225, 225, 225), 4, true);

                TankGlass(left + 0.198f, Top + 0.019f, fraction, full);

                var status = full
                    ? "TANK FULL"
                    : "TANK   " + (fraction * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";

                Hud.Text(status, X, Top + 0.114f, 0.29f,
                         full ? Flourish() : Color.FromArgb(210, 215, 215, 215), 4, true);
            }
            catch (Exception ex)
            {
                Log.Once("meter", "The pump display could not be drawn: " + ex.Message);
            }
        }

        // ==================================================================
        // The tank
        // ==================================================================

        /// <summary>
        /// An upright sight glass with fuel rising in it.
        ///
        /// The glass and its frame are three flat rectangles; everything that moves is in
        /// Liquid and BubbleTrail below.
        /// </summary>
        private void TankGlass(float x, float y, float fraction, bool full)
        {
            // Frame, then the empty glass behind the fuel.
            Hud.Bar(x - 0.0022f, y - 0.0022f, TankW + 0.0044f, TankH + 0.0044f,
                    Color.FromArgb(210, 60, 60, 66));
            Hud.Bar(x, y, TankW, TankH, Color.FromArgb(220, 20, 20, 24));

            Liquid(x, y, fraction, full);

            // Two hairline highlights down the left of the glass, which is what stops a flat
            // rectangle of colour reading as a flat rectangle of colour.
            Hud.Bar(x + 0.0026f, y + 0.004f, 0.0012f, TankH - 0.008f, Color.FromArgb(38, 255, 255, 255));
            Hud.Bar(x + TankW - 0.0034f, y + 0.004f, 0.0009f, TankH - 0.008f, Color.FromArgb(22, 255, 255, 255));
        }

        /// <summary>
        /// The fuel itself: a rising level with a surface that will not sit still.
        ///
        /// DRAWN AS COLUMNS, which is the whole technique. There is no way to draw a wavy shape
        /// in this HUD -- DRAW_RECT is the only primitive there is -- so the liquid is sliced
        /// into twenty-two upright strips and each one is given its own surface height from a
        /// pair of sine waves. Two waves rather than one, at frequencies that do not divide
        /// into each other, because a single sine reads as a machine and two read as a liquid.
        ///
        /// The waves also die away as the tank approaches full, so it settles rather than
        /// sloshing forever under a finished pump.
        /// </summary>
        private void Liquid(float x, float y, float fraction, bool full)
        {
            if (fraction <= 0.0005f) return;

            var t = Environment.TickCount / 1000f;

            var body = full
                ? Color.FromArgb(238, 120, 225, 135)
                : Color.FromArgb(238, 235, 160, 45);

            var crest = full
                ? Color.FromArgb(250, 175, 245, 185)
                : Color.FromArgb(250, 255, 210, 120);

            var columnW = TankW / Columns;

            // Level measured from the BOTTOM of the glass, which is where liquid lives.
            var level = TankH * fraction;
            var surfaceY = y + TankH - level;

            // Waves shrink as it fills and vanish when it is full, and they are also damped in
            // a nearly-empty tank -- a puddle does not roll.
            var settle = full ? 0f : Math.Min(fraction * 6f, 1f) * (1f - fraction * 0.55f);
            var a1 = 0.0016f * settle;
            var a2 = 0.0009f * settle;

            for (var i = 0; i < Columns; i++)
            {
                var u = (float)i / (Columns - 1);

                var wave = (float)(Math.Sin(t * 3.3f + u * 7.1f) * a1 +
                                   Math.Sin(t * 5.1f - u * 11.7f) * a2);

                var top = surfaceY + wave;

                // Never above the glass, never below its floor.
                if (top < y) top = y;
                var height = y + TankH - top;
                if (height <= 0.0002f) continue;

                Hud.Bar(x + i * columnW, top, columnW + 0.0002f, height, body);

                // A brighter line riding the surface, so the top edge is a surface and not
                // just where the colour stops.
                Hud.Bar(x + i * columnW, top, columnW + 0.0002f, 0.0016f, crest);
            }

            BubbleTrail(x, y, level, full, t);
        }

        /// <summary>
        /// Bubbles rising through the fuel.
        ///
        /// Deterministic rather than random: each bubble's position comes from the clock and
        /// its own index, so there is no state to keep and no Random being pumped sixty times a
        /// second. They fade as they near the surface instead of popping out of existence.
        /// </summary>
        private void BubbleTrail(float x, float y, float level, bool full, float t)
        {
            if (full || level < 0.012f) return;

            var floor = y + TankH;

            for (var i = 0; i < Bubbles; i++)
            {
                // Spread across the width and staggered in time, both from the index alone.
                var lane = 0.16f + (i * 0.68f / (Bubbles - 1));
                var speed = 0.42f + (i % 3) * 0.11f;
                var phase = (t * speed + i * 0.37f) % 1f;

                var height = level * phase;
                var by = floor - height;

                var size = 0.0016f + (i % 2) * 0.0007f;

                // Faded in at the bottom and out at the top, so nothing appears or vanishes.
                var edge = Math.Min(phase * 4f, Math.Min((1f - phase) * 3f, 1f));
                var alpha = (int)(150 * Math.Max(edge, 0f));
                if (alpha <= 4) continue;

                Hud.Bar(x + TankW * lane, by, size, size, Color.FromArgb(alpha, 255, 240, 200));
            }
        }

        // ==================================================================
        // Trimmings
        // ==================================================================

        /// <summary>
        /// A droplet falling from the nozzle and fading out, over and over.
        ///
        /// Accelerating rather than linear -- p squared -- because a drop that falls at a
        /// constant speed reads as a lift rather than as gravity.
        /// </summary>
        private void Drip(float x, float y, bool flowing)
        {
            if (!flowing || _drip.Missing) return;

            const float period = 1.25f;
            const float fall = 0.030f;

            var p = (Environment.TickCount % (int)(period * 1000)) / (period * 1000f);

            var alpha = (int)(210 * Math.Min(p * 6f, Math.Min((1f - p) * 3.5f, 1f)));
            if (alpha <= 4) return;

            _drip.DrawRaw(x, y + p * p * fall, 0.016f,
                          Color.FromArgb(alpha, 245, 185, 70));
        }

        /// <summary>
        /// TANK FULL, pulsing for a couple of seconds and then holding steady.
        ///
        /// It settles deliberately: a label that flashes forever is a label you learn to stop
        /// seeing, and this one has already done its job by the time it stops.
        /// </summary>
        private Color Flourish()
        {
            var since = Environment.TickCount - _filledAt;

            if (since > 2200) return Color.FromArgb(235, 150, 235, 160);

            var pulse = (float)Math.Abs(Math.Sin(since / 190f));
            var g = (int)(205 + 50 * pulse);
            return Color.FromArgb(255, (int)(120 + 110 * pulse), g, (int)(130 + 70 * pulse));
        }
    }
}
