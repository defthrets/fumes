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
    /// The pump's own display.
    ///
    /// Laid out the way a real forecourt display is, which is not the way a game HUD usually
    /// is. On a real pump the money is the biggest thing on the machine, the volume sits under
    /// it, and the unit price is small print at the bottom -- because what you are watching is
    /// what you are spending. That hierarchy is the whole layout: three rows, right-aligned so
    /// the digits line up in a column and the eye can read the total climbing without hunting
    /// for it.
    ///
    /// The tank anchors the left, upright and tall. A horizontal bar filling left to right is a
    /// progress bar, and a progress bar is a thing you read; liquid rising in a glass is a thing
    /// you watch, and it is the only part of the display that says when to stop without doing
    /// arithmetic.
    /// </summary>
    internal sealed class Meter
    {
        private readonly Settings _cfg;
        private readonly Gauge _gauge;

        private readonly Icon _pump = new Icon("fuel.png");
        private readonly Icon _drip = new Icon("drop.png");

        // ---- panel -------------------------------------------------------
        // All fractions of the screen, written out rather than inlined: every one appears in
        // two or three places below, and a panel whose parts drift apart by a thousandth looks
        // broken in a way that is very hard to see.
        private const float X = 0.5f;
        private const float Top = 0.700f;
        private const float W = 0.300f;
        private const float H = 0.160f;

        // ---- the glass ---------------------------------------------------
        private const float TankX = 0.020f;     // from the panel's left edge
        private const float TankY = 0.036f;     // from the panel's top
        private const float TankW = 0.042f;
        private const float TankH = 0.088f;

        // ---- the numbers column ------------------------------------------
        private const float ColLeft = 0.078f;   // labels start here
        private const float ColRight = 0.238f;  // values end here

        /// <summary>
        /// GTA's font 1 is Sign Painter House Script -- the hand-lettered signwriter's script
        /// the game uses for area names, and what a forecourt brand is written in on a real
        /// sign. That is the entire reason it is here.
        /// </summary>
        private const int Script = 1;

        /// <summary>Chalet Comprime Cologne. The block font everything else uses.</summary>
        private const int Plain = 4;

        private const int Columns = 22;
        private const int Bubbles = 5;

        /// <summary>How many pieces the chase light is sampled in. See Border().</summary>
        private const int BorderSegments = 120;

        /// <summary>When the tank last reached full, so the flourish fires once.</summary>
        private int _filledAt;
        private bool _wasFull;

        public Meter(Settings cfg, Gauge gauge)
        {
            _cfg = cfg;
            _gauge = gauge;
        }

        public void Draw(string brand, string place, float litres, float pricePerLitre,
                         float owed, bool free, Tank tank)
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

                Hud.Rect(X, Top + H / 2f, W, H, Color.FromArgb(214, 8, 8, 10));

                Header(left, Top + 0.011f, brand, place);

                // The pump, top right, bobbing and dripping while fuel moves. It goes still at
                // full: a logo swaying on a finished pump reads as a stuck animation, not life.
                _pump.Scale = 0.050f;
                _pump.Draw(left + W - 0.034f, Top + 0.064f, !full);

                // The drip hangs ABOVE the bowser rather than under it. Under, it fell into
                // the numbers column and read as a stray dot on the price; above, the pump has
                // clear panel over it and the drop has somewhere to be.
                Drip(left + W - 0.034f, Top + 0.024f, !full);

                Numbers(left, litres, pricePerLitre, owed, free);

                var tankX = left + TankX;
                TankGlass(tankX, Top + TankY, fraction, full);

                // The reading sits UNDER THE THING IT DESCRIBES, and says only the number --
                // the tank is directly above it, so a word like "TANK" would be labelling
                // something the picture has already said.
                var status = full
                    ? "FULL"
                    : (fraction * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";

                Hud.Text(status, tankX + TankW / 2f, Top + TankY + TankH + 0.004f, 0.32f,
                         full ? Flourish() : Color.FromArgb(220, 225, 225, 225), Plain, true);

                // Last, so the light runs over the top of everything rather than under it.
                Border(left, Top, full);
            }
            catch (Exception ex)
            {
                Log.Once("meter", "The pump display could not be drawn: " + ex.Message);
            }
        }

        // ==================================================================
        // The border
        // ==================================================================

        /// <summary>
        /// A light chasing round the frame, the way a lit forecourt sign does.
        ///
        /// Built as a ring of short segments rather than four sliding rectangles, and that is
        /// the trick that makes it simple: a travelling highlight drawn as a moving rectangle
        /// has to be split by hand every time it crosses a corner, and gets the maths wrong at
        /// exactly the four moments anybody is looking at it. A ring of fixed segments, each
        /// brightened by how near the chase is, turns corners for free.
        ///
        /// Segment lengths are weighted by the screen's ASPECT. Fractions of width and
        /// fractions of height are not the same physical distance, so an unweighted ring runs
        /// the light along the top edge at nearly twice the speed it climbs the sides -- which
        /// reads as a stutter rather than as a circuit.
        /// </summary>
        private void Border(float left, float top, bool full)
        {
            const float thick = 0.0022f;
            const float chaseSeconds = 2.8f;
            const float halo = 0.10f;       // how much of the ring each light lifts

            var dim = full ? Color.FromArgb(165, 42, 112, 58) : Color.FromArgb(165, 108, 72, 24);
            var lit = full ? Color.FromArgb(255, 165, 250, 180) : Color.FromArgb(255, 255, 220, 140);

            // THE UNLIT FRAME IS FOUR SOLID RECTANGLES, drawn first and in one piece.
            //
            // It used to be the same ring of segments as the light, every one of them drawn
            // whether it was lit or not -- and a ring of abutting rectangles does not abut
            // once each edge is rounded to whole pixels. The frame came out as a row of gold
            // blocks with gaps between them, which reads as a broken border rather than a dim
            // one. Now the frame is continuous by construction and the ring only ever draws
            // the part that is actually glowing.
            Hud.Bar(left, top, W, thick, dim);
            Hud.Bar(left, top + H - thick, W, thick, dim);
            Hud.Bar(left, top, thick, H, dim);
            Hud.Bar(left + W - thick, top, thick, H, dim);

            var aspect = Aspect();
            var wide = W * aspect;          // top and bottom, in height-equivalent units
            var perimeter = 2f * (wide + H);
            if (perimeter <= 0.0001f) return;

            var step = perimeter / BorderSegments;
            var chase = (Environment.TickCount % (int)(chaseSeconds * 1000)) / (chaseSeconds * 1000f);

            for (var i = 0; i < BorderSegments; i++)
            {
                var u = (float)i / BorderSegments;

                // Two lights, opposite each other. One reads as a stray pixel; two read as a
                // sign that is meant to be doing this.
                var glow = Math.Max(Ring(u, chase, halo), Ring(u, (chase + 0.5f) % 1f, halo));
                if (glow <= 0.02f) continue;

                var colour = Blend(dim, lit, glow * glow);
                var d = u * perimeter;

                // Each piece is drawn a shade longer than its spacing, so neighbours overlap
                // rather than leaving a hairline of frame showing between them.
                //
                // AND EVERY PIECE IS CLAMPED TO ITS OWN EDGE. That overlap is the reason: the
                // last segment before a corner starts almost at the corner and is 15% longer
                // than the gap it has left, so it ran out past the end of the frame -- a gold
                // spur sticking off the top right, and another off the bottom left, four times
                // every circuit. The frame looked broken rather than lit.
                var run = step * 1.15f;

                if (d < wide)
                {
                    // Top, left to right.
                    var x = left + (d / wide) * W;
                    var len = Math.Min(run / aspect, left + W - x);
                    if (len > 0f) Hud.Bar(x, top, len, thick, colour);
                }
                else if (d < wide + H)
                {
                    // Right, top to bottom.
                    var y = top + (d - wide);
                    var len = Math.Min(run, top + H - y);
                    if (len > 0f) Hud.Bar(left + W - thick, y, thick, len, colour);
                }
                else if (d < 2f * wide + H)
                {
                    // Bottom, right to left.
                    var x = left + W - ((d - wide - H) / wide) * W;
                    var from = Math.Max(left, x - run / aspect);
                    if (x > from) Hud.Bar(from, top + H - thick, x - from, thick, colour);
                }
                else
                {
                    // Left, bottom to top.
                    var y = top + H - (d - 2f * wide - H);
                    var from = Math.Max(top, y - run);
                    if (y > from) Hud.Bar(left, from, thick, y - from, colour);
                }
            }
        }

        /// <summary>How lit a point on the ring is, given where the chase is. Wraps at the seam.</summary>
        private static float Ring(float u, float chase, float halo)
        {
            var d = Math.Abs(u - chase);
            if (d > 0.5f) d = 1f - d;

            var g = 1f - d / halo;
            return g < 0f ? 0f : g;
        }

        private static Color Blend(Color a, Color b, float t)
        {
            if (t <= 0f) return a;
            if (t >= 1f) return b;

            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t),
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        private static float Aspect()
        {
            try
            {
                var a = GTA.UI.Screen.AspectRatio;
                if (a > 0.5f && a < 6f) return a;
            }
            catch
            {
                // Fall through to the safe default.
            }

            return 1.7778f;
        }

        // ==================================================================
        // The words and the numbers
        // ==================================================================

        /// <summary>
        /// The station line: brand in signwriter's script, location in the block font.
        ///
        /// Two fonts on one line means the pair has to be centred as a UNIT, so both runs are
        /// measured first and each placed from the left -- there is no way to centre a mixed
        /// line in one call.
        ///
        /// The script run is set larger and lifted slightly: a script face at the same nominal
        /// scale reads smaller than a block face, and GTA positions text by the TOP of its line
        /// box rather than by a baseline, so the taller run has to start higher or the two sit
        /// on different lines.
        /// </summary>
        private void Header(float left, float y, string brand, string place)
        {
            const float brandScale = 0.42f;
            const float placeScale = 0.28f;
            const float scriptLift = 0.0062f;

            if (string.IsNullOrEmpty(brand)) brand = "PUMP";

            var tail = string.IsNullOrEmpty(place) ? "" : "  -  " + place.ToUpperInvariant();

            var brandW = Hud.Width(brand, brandScale, Script);
            var tailW = Hud.Width(tail, placeScale, Plain);

            var startX = X - (brandW + tailW) / 2f;

            Hud.Text(brand, startX, y - scriptLift, brandScale,
                     Color.FromArgb(240, 250, 200, 110), Script);

            if (tail.Length == 0) return;

            Hud.Text(tail, startX + brandW, y, placeScale,
                     Color.FromArgb(210, 220, 205, 175), Plain);
        }

        /// <summary>
        /// TOTAL, VOLUME and PRICE, in that order and that hierarchy.
        ///
        /// Labels hard left, values hard right, so the digits line up in a column no matter how
        /// many of them there are -- a right-aligned money column is the reason a total ticking
        /// from 9.90 to 10.05 does not jump sideways as it gains a digit.
        /// </summary>
        private void Numbers(float left, float litres, float pricePerLitre, float owed, bool free)
        {
            var lx = left + ColLeft;
            var rx = left + ColRight;

            var label = Color.FromArgb(180, 175, 175, 180);
            var value = Color.FromArgb(238, 240, 240, 240);

            // TOTAL -- the big one, because it is what you are actually watching.
            Hud.Text("TOTAL", lx, Top + 0.048f, 0.25f, label, Plain);
            Hud.Text(free ? "FREE" : "$" + owed.ToString("0.00", CultureInfo.InvariantCulture),
                     rx, Top + 0.036f, 0.62f, Color.FromArgb(245, 245, 175, 55), Plain, false, true);

            Hud.Text("VOLUME", lx, Top + 0.085f, 0.25f, label, Plain);
            Hud.Text(_gauge.Volume(litres), rx, Top + 0.079f, 0.38f, value, Plain, false, true);

            var unit = _cfg.Units == Units.Gallons
                ? "$" + (pricePerLitre / Gauge.GallonsPerLitre).ToString("0.00", CultureInfo.InvariantCulture) + "/gal"
                : "$" + pricePerLitre.ToString("0.00", CultureInfo.InvariantCulture) + "/L";

            Hud.Text("PRICE", lx, Top + 0.112f, 0.25f, label, Plain);
            Hud.Text(unit, rx, Top + 0.109f, 0.30f, Color.FromArgb(215, 215, 215, 218),
                     Plain, false, true);
        }

        // ==================================================================
        // The tank
        // ==================================================================

        /// <summary>An upright sight glass with fuel rising in it.</summary>
        private void TankGlass(float x, float y, float fraction, bool full)
        {
            Hud.Bar(x - 0.0024f, y - 0.0024f, TankW + 0.0048f, TankH + 0.0048f,
                    Color.FromArgb(215, 62, 62, 68));
            Hud.Bar(x, y, TankW, TankH, Color.FromArgb(224, 20, 20, 24));

            Liquid(x, y, fraction, full);

            // Two hairline highlights down the glass, which is what stops a flat rectangle of
            // colour reading as a flat rectangle of colour.
            Hud.Bar(x + 0.0030f, y + 0.005f, 0.0013f, TankH - 0.010f, Color.FromArgb(38, 255, 255, 255));
            Hud.Bar(x + TankW - 0.0040f, y + 0.005f, 0.0010f, TankH - 0.010f, Color.FromArgb(22, 255, 255, 255));
        }

        /// <summary>
        /// The fuel itself: a rising level with a surface that will not sit still.
        ///
        /// DRAWN AS COLUMNS, which is the whole technique. There is no way to draw a wavy shape
        /// in this HUD -- DRAW_RECT is the only primitive there is -- so the liquid is sliced
        /// into upright strips and each is given its own surface height from a pair of sine
        /// waves. Two waves rather than one, at frequencies that do not divide into each other,
        /// because a single sine reads as a machine and two read as a liquid.
        ///
        /// The waves die away as the tank approaches full, so it settles rather than sloshing
        /// forever under a finished pump.
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

            var level = TankH * fraction;
            var surfaceY = y + TankH - level;

            var settle = full ? 0f : Math.Min(fraction * 6f, 1f) * (1f - fraction * 0.55f);
            var a1 = 0.0017f * settle;
            var a2 = 0.0010f * settle;

            for (var i = 0; i < Columns; i++)
            {
                var u = (float)i / (Columns - 1);

                var wave = (float)(Math.Sin(t * 3.3f + u * 7.1f) * a1 +
                                   Math.Sin(t * 5.1f - u * 11.7f) * a2);

                var top = surfaceY + wave;
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
        /// second. They fade near the surface instead of popping out of existence.
        /// </summary>
        private void BubbleTrail(float x, float y, float level, bool full, float t)
        {
            if (full || level < 0.012f) return;

            var floor = y + TankH;

            for (var i = 0; i < Bubbles; i++)
            {
                var lane = 0.16f + (i * 0.68f / (Bubbles - 1));
                var speed = 0.42f + (i % 3) * 0.11f;
                var phase = (t * speed + i * 0.37f) % 1f;

                var by = floor - level * phase;
                var size = 0.0016f + (i % 2) * 0.0007f;

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
        /// A droplet falling from the nozzle and fading, over and over.
        ///
        /// Accelerating rather than linear -- p squared -- because a drop that falls at a
        /// constant speed reads as a lift rather than as gravity.
        /// </summary>
        private void Drip(float x, float y, bool flowing)
        {
            if (!flowing || _drip.Missing) return;

            const float period = 1.25f;
            const float fall = 0.022f;

            var p = (Environment.TickCount % (int)(period * 1000)) / (period * 1000f);

            var alpha = (int)(210 * Math.Min(p * 6f, Math.Min((1f - p) * 3.5f, 1f)));
            if (alpha <= 4) return;

            _drip.DrawRaw(x, y + p * p * fall, 0.016f, Color.FromArgb(alpha, 245, 185, 70));
        }

        /// <summary>
        /// FULL, pulsing for a couple of seconds and then holding steady.
        ///
        /// It settles deliberately: a label that flashes forever is a label you learn to stop
        /// seeing, and this one has already done its job by the time it stops.
        /// </summary>
        private Color Flourish()
        {
            var since = Environment.TickCount - _filledAt;

            if (since > 2200) return Color.FromArgb(235, 150, 235, 160);

            var pulse = (float)Math.Abs(Math.Sin(since / 190f));
            return Color.FromArgb(255, (int)(120 + 110 * pulse), (int)(205 + 50 * pulse),
                                  (int)(130 + 70 * pulse));
        }
    }
}
