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

        /// <summary>The pump that sits above the reading. A white PNG; the colour is the tint.</summary>
        private readonly Icon _pump = new Icon("fuel_bar.png");

        // The pump's height on screen comes from the file's own shape -- see Icon.Aspect.
        // It was a constant here, copied by hand from the crop in make_icons.py, which is a
        // pairing nobody would remember to keep in step and whose failure is a silently
        // squashed icon rather than anything that complains.

        /// <summary>
        /// Screen width over height, for keeping the pump square.
        ///
        /// CustomSprite positions and sizes in a FIXED 1280x720 canvas, which is 16:9 -- so on
        /// anything wider the canvas is stretched horizontally and a sprite given equal width
        /// and height comes out visibly wider than it is tall. On this 3440x1440 screen that is
        /// a third again too wide. The width is divided by this to undo it.
        /// </summary>
        private float _aspect = 16f / 9f;

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

        /// <summary>Whether the one-off measurement below has been written to the log.</summary>
        private bool _measured;

        /// <summary>
        /// Says, once, how big the gauge actually IS in real pixels.
        ///
        /// Everything here is written in fractions of the screen, and a fraction tells you
        /// nothing about how thick a line looks on somebody else's monitor -- which is exactly
        /// the argument that kept going round: a bar can be described as three thousandths wide
        /// and still be reported as too thick, and neither side of that can check the other.
        /// A line in the log with the resolution and the pixel width settles it in one reading.
        /// </summary>
        private void Measure()
        {
            if (_measured) return;
            _measured = true;

            try
            {
                var res = GTA.UI.Screen.Resolution;

                if (res.Height > 0) _aspect = res.Width / (float)res.Height;

                Log.Info("Gauge: " + (_cfg.GaugeWidth * res.Width).ToString("0.0") + " x " +
                         (_cfg.GaugeHeight * res.Height).ToString("0.0") + " px" +
                         " at " + (_cfg.GaugeX * res.Width).ToString("0") + "," +
                         (_cfg.GaugeY * res.Height).ToString("0") +
                         "  (screen " + res.Width + "x" + res.Height +
                         ", ini " + _cfg.GaugeWidth.ToString("0.0000") + " x " +
                         _cfg.GaugeHeight.ToString("0.0000") + ")");
            }
            catch (Exception ex)
            {
                Log.Once("gauge-measure", "Could not read the screen size: " + ex.Message);
            }
        }

        /// <summary>
        /// The scale at which a string comes out exactly as wide as the gauge.
        ///
        /// Measured at a reference scale and rescaled by the ratio, because text width is
        /// linear in scale. A hand-picked number would only ever be right for one string at one
        /// bar width, and both of those have changed repeatedly.
        /// </summary>
        private static float Fit(float probe, string text, float width)
        {
            var probeWidth = Draw.Width(text, probe, 4);
            return probeWidth > 0.0001f ? probe * (width / probeWidth) : 0.20f;
        }

        private bool _numberMeasured;

        /// <summary>
        /// Says, once, how big the number in the bar actually came out.
        ///
        /// The same argument as Measure. The height comes from a native this codebase has not
        /// used before, and if it returns something odd the digits sit wrong and the only
        /// evidence is a screenshot somebody has to interpret. One line in the log says whether
        /// they fit the bar or overhang it, in pixels, with nothing to interpret.
        /// </summary>
        private void MeasureNumber(string text, float scale, float height)
        {
            if (_numberMeasured) return;
            _numberMeasured = true;

            try
            {
                var res = GTA.UI.Screen.Resolution;

                Log.Info("Gauge number " + text + ": " +
                         (Draw.Width(text, scale, 4) * res.Width).ToString("0.0") + " x " +
                         (height * res.Height).ToString("0.0") + " px at scale " +
                         scale.ToString("0.000") + ", inside a bar " +
                         (_cfg.GaugeWidth * res.Width).ToString("0.0") + " px wide.");
            }
            catch (Exception ex)
            {
                Log.Once("gauge-number-measure", "Could not measure the gauge number: " + ex.Message);
            }
        }

        /// <summary>
        /// Draws the gauge with a made-up tank, for the positioner in the menu.
        ///
        /// It has to draw whatever the game thinks: the gauge is normally only shown from
        /// inside a vehicle, and the one moment you most want to place it is standing in front
        /// of the minimap on foot with the menu open. refuelling = true is what waives that
        /// check, and ShowGauge is forced because positioning a gauge you have switched off is
        /// otherwise an empty screen and no explanation.
        /// </summary>
        public void Preview()
        {
            var was = _cfg.ShowGauge;
            _cfg.ShowGauge = true;

            try { Update(null, _previewTank, true); }
            finally { _cfg.ShowGauge = was; }
        }

        /// <summary>A tank that does not exist, at a level that shows the fill and the icon.</summary>
        private readonly Tank _previewTank = new Tank { Capacity = 65f, Litres = 41f, Known = true };

        public void Update(Vehicle vehicle, Tank tank, bool refuelling, bool stalled = false)
        {
            if (!_cfg.ShowGauge || tank == null) return;

            Measure();

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
                //
                // THE BORDER IS A FRACTION OF THE BAR, not a fixed size. It used to be a flat
                // 0.0022 a side, which was a hairline around a wide strip and a frame thicker
                // than the bar itself once the bar came down to three thousandths -- most of
                // what read as "still too thick" at the end was black edge, not gauge.
                var edge = w * 0.22f;
                if (edge < 0.0005f) edge = 0.0005f;

                Draw.Bar(x - edge, y - edge, w + edge * 2f, h + edge * 2f, Fade(Color.FromArgb(205, 0, 0, 0)));
                Draw.Bar(x, y, w, h, Fade(Color.FromArgb(165, 28, 28, 32)));

                var colour = Fade(Level(fraction));

                if (_cfg.Vertical)
                {
                    // FILLED FROM THE BOTTOM, which is the only way up is up. A vertical bar
                    // that fills downward from the top is a loading bar stood on its end; one
                    // that fills from the floor is a level in a container, and that is what a
                    // tank is. It also matches the glass on the pump display exactly, so the
                    // two things that show the same number look like the same instrument.
                    if (fraction > 0.001f)
                    {
                        if (_cfg.GaugeLiquid)
                        {
                            Liquid(x, y, w, h, fraction, colour, refuelling);
                        }
                        else
                        {
                            var fill = h * fraction;
                            Draw.Bar(x, y + h - fill, w, fill, colour);
                        }
                    }

                    if (_cfg.ShowReserveMark)
                    {
                        // Measured from the bottom, for the same reason the fill is.
                        var reserveY = y + h * (1f - Clamp01(_cfg.ReserveFraction));
                        Draw.Bar(x - 0.0016f, reserveY, w + 0.0032f, 0.0011f,
                                 Fade(Color.FromArgb(225, 235, 180, 60)));
                    }
                }
                else
                {
                    if (fraction > 0.001f) Draw.Bar(x, y, w * fraction, h, colour);

                    if (_cfg.ShowReserveMark)
                    {
                        var reserveX = x + w * Clamp01(_cfg.ReserveFraction);
                        Draw.Bar(reserveX, y - 0.0016f, 0.0012f, h + 0.0032f,
                                 Fade(Color.FromArgb(225, 235, 180, 60)));
                    }
                }

                if (_cfg.Vertical)
                {
                    // ShowNumbers USED TO RETURN OUT OF THIS WHOLE BLOCK, which was right when
                    // the reading was the only thing in it. The pump is in here now, and "no
                    // number" is not "nothing" -- an early return would have taken the pump with
                    // it and left a bar with nothing in it at all.
                    var pct = (int)Math.Round(fraction * 100f);
                    if (pct < 0) pct = 0;
                    if (pct > 100) pct = 100;

                    // Off at a full tank as well as off by setting. A full bar already says
                    // full, and 100 is the one reading that does not fit: the digits are sized
                    // so that TWO of them span the bar, so a third only gets in by shrinking all
                    // three, and it was the only number in the set drawn at a different size.
                    var showReading = _cfg.ShowNumbers && !(_cfg.HideFullReading && pct >= 100);

                    var inset = edge;

                    // Stacked up from the foot of the bar, each thing taking its own height off
                    // what is left. Written this way so the pump lands correctly whether or not
                    // there is a number under it -- with the reading off it simply sits in the
                    // slot the reading would have had, rather than floating above a gap.
                    var used = inset;

                    if (showReading)
                    {
                        var text = pct.ToString(CultureInfo.InvariantCulture);

                        // SIZED OFF "88", NOT OFF THE READING. Fitting each number to the bar in
                        // turn makes the digits change size as the tank drains -- "9" would be
                        // drawn nearly twice the size of "45", because one character has the
                        // whole width to itself. Two digits' worth is the size; the actual
                        // string can only pull it DOWN, which is what makes room for 100.
                        const float probe = 0.30f;

                        var scale = Fit(probe, "88", w);
                        var actual = Fit(probe, text, w);
                        if (actual < scale) scale = actual;

                        scale *= _cfg.GaugeTextScale;
                        if (scale < 0.10f) scale = 0.10f;
                        if (scale > 0.60f) scale = 0.60f;

                        // INSIDE THE BAR, AT ITS FOOT. The line is drawn from its top edge, so
                        // the bottom only lands where it should once its own height comes off --
                        // and that height is measured, not assumed. See Draw.Height.
                        var textH = Draw.Height(scale, 4);

                        // BLACK ONLY WHILE THERE IS FUEL UNDERNEATH IT.
                        //
                        // The number sits in the bottom few pixels, which are filled at any
                        // level worth reading -- but below about three per cent the fill drops
                        // past the digits and black ink lands on the empty channel, which is
                        // near-black. The one reading you cannot afford to lose is the last one.
                        var lit = h * fraction >= textH + inset;

                        var ink = Fade(stalled ? Color.FromArgb(245, 255, 120, 110)
                                     : lit     ? Color.FromArgb(240, 10, 10, 12)
                                               : Color.FromArgb(235, 240, 240, 240));

                        Draw.Text(text, x + w / 2f, y + h - used - textH, scale, ink,
                                  4, true, false, !lit);

                        MeasureNumber(text, scale, textH);

                        used += textH + inset;
                    }

                    if (_cfg.ShowGaugeIcon)
                    {
                        // Square ON SCREEN, which is not the same as square in the sprite canvas
                        // -- see _aspect. Width is the bar's width, so it fits exactly.
                        var iconW = w * _cfg.GaugeIconScale;
                        var iconH = iconW * _aspect * _pump.Aspect;

                        var iconLit = h * fraction >= used + iconH;

                        _pump.DrawSized(x + w / 2f, y + h - used - iconH / 2f, iconW, iconH,
                                        Fade(iconLit ? Color.FromArgb(240, 10, 10, 12)
                                                     : Color.FromArgb(215, 235, 235, 238)));
                    }

                    // FUEL, when it is asked for. It takes the same ink rule as the number
                    // and needs it more: it sits at the TOP of the bar, so black on the fill is
                    // black on empty channel at anything under about half a tank.
                    if (_cfg.ShowGaugeLabel)
                    {
                        var glyph = w * _cfg.GaugeTextScale;
                        var labelH = glyph * 5.6f;
                        if (labelH > h * 0.45f) labelH = h * 0.45f;

                        var labelLit = h * fraction >= labelH + 0.006f;

                        _glyphs.Label(x + w / 2f, y + labelH / 2f + 0.006f, glyph, labelH,
                                      Fade(labelLit ? Color.FromArgb(215, 18, 18, 20)
                                                    : Color.FromArgb(160, 225, 225, 228)));
                    }

                    return;
                }

                if (!_cfg.ShowNumbers) return;

                var label = stalled ? (tank.Electric ? "FLAT" : "DRY") : tank.Noun;
                var reading = label + "   " + Volume(tank.Litres) + " / " + Volume(tank.Capacity);

                Draw.Text(reading, x + w / 2f, y - 0.0008f, 0.215f,
                          Fade(stalled ? Color.FromArgb(240, 255, 120, 110)
                                       : Color.FromArgb(230, 245, 245, 245)),
                          4, true);
            }
            catch (Exception ex)
            {
                Log.Once("gauge", "The gauge could not be drawn: " + ex.Message);
            }
        }

        /// <summary>
        /// The fuel in the bar: a moving surface, and bubbles rising through it.
        ///
        /// THE BODY IS ONE RECTANGLE AND ONLY THE SURFACE IS COLUMNS. That is the whole fix for
        /// the lines running down the bar, and they were not a rounding artefact -- they were
        /// alpha.
        ///
        /// Every column used to be drawn from its own wavy top ALL THE WAY DOWN to the foot of
        /// the bar, each overlapping its neighbour by a hair to keep a gap from opening between
        /// them. But the fuel is translucent, and two translucent rectangles over the same
        /// pixel do not come out the same as one: 1-(1-a)^2, not a. So every seam was a bright
        /// stripe two hundred pixels long, and the fix that was supposed to hide the seams was
        /// what drew them.
        ///
        /// Now the body is filled once, in a single rectangle, up to the LOWEST the surface can
        /// swing. Only the few pixels of wave above that line are columns, they tile exactly
        /// instead of overlapping, and there is nothing left to double up. Eight columns rather
        /// than six as well, since a two-pixel strip is fine for a surface profile when it is
        /// not also painting the whole bar.
        /// </summary>
        private void Liquid(float x, float y, float w, float h, float fraction, Color body,
                            bool filling)
        {
            const int columns = 8;

            var t = Environment.TickCount / 1000f;

            var level = h * fraction;
            var surfaceY = y + h - level;

            // The waves die away as it fills, so a finished tank settles rather than sloshing
            // forever -- and lift while fuel is going in, the one moment a surface has a reason
            // to be disturbed.
            var settle = Math.Min(fraction * 6f, 1f) * (1f - fraction * 0.55f);
            if (filling) settle = Math.Min(settle * 2.2f + 0.35f, 1.5f);

            var a1 = 0.0013f * settle;
            var a2 = 0.0008f * settle;

            var amplitude = a1 + a2;

            // The body, once, up to the deepest the surface can go. No seams because there is
            // nothing to seam: it is one rectangle the full width of the bar.
            var bodyTop = surfaceY + amplitude;
            if (bodyTop < y) bodyTop = y;

            if (bodyTop < y + h) Draw.Bar(x, bodyTop, w, y + h - bodyTop, body);

            if (level <= 0.002f) return;

            // A brighter line riding the surface, so the top is a surface and not just where
            // the colour stops. Mixed off the body rather than fixed, because the body runs the
            // whole ramp from yellow to red and a fixed crest would come loose from it.
            var crest = Mix(body, Color.FromArgb(body.A, 255, 240, 195), 0.55f);

            if (amplitude < 0.00004f)
            {
                Draw.Bar(x, surfaceY, w, 0.0011f, crest);
            }
            else
            {
                for (var i = 0; i < columns; i++)
                {
                    // EXACT TILING, not overlapping. One column's right edge is the next one's
                    // left edge, computed from the same expression, so no pixel is covered
                    // twice and no seam can brighten.
                    var left = x + w * i / columns;
                    var right = x + w * (i + 1) / columns;

                    var u = (float)i / (columns - 1);

                    // Two waves rather than one, at frequencies that do not divide into each
                    // other: a single sine reads as a machine and two read as a liquid.
                    var wave = (float)(Math.Sin(t * 3.3f + u * 7.1f) * a1 +
                                       Math.Sin(t * 5.1f - u * 11.7f) * a2);

                    var top = surfaceY + wave;
                    if (top < y) top = y;

                    // Only the sliver above the body line. A handful of pixels, not the bar.
                    if (top < bodyTop) Draw.Bar(left, top, right - left, bodyTop - top, body);

                    Draw.Bar(left, top, right - left, 0.0011f, crest);
                }
            }

            Bubbles(x, y, w, h, level, t, filling);
        }

        /// <summary>
        /// Bubbles rising through the fuel.
        ///
        /// Deterministic rather than random: each one's position comes from the clock and its
        /// own index, so there is no state to keep and no Random being pumped sixty times a
        /// second. They fade in off the floor and out at the surface instead of appearing and
        /// popping.
        ///
        /// Three, not the pump's five. Across sixteen pixels, five lanes put them close enough
        /// to read as a row rather than as separate bubbles.
        /// </summary>
        private void Bubbles(float x, float y, float w, float h, float level, float t,
                             bool filling)
        {
            const int count = 3;

            if (level < 0.014f) return;

            var floor = y + h;

            // Square ON SCREEN. A rectangle given equal width and height fractions is as wide
            // as the screen is wider than it is tall -- on this one that is a bubble half again
            // wider than it is high, which at three pixels reads as a dash.
            var size = w * 0.24f;
            var tall = size * _aspect;

            for (var i = 0; i < count; i++)
            {
                var lane = 0.22f + i * (0.56f / (count - 1));
                // Quicker while fuel is actually going in. refuelling reached this code and
                // went unused for its whole life; with the waves gone the bubbles are the only
                // thing left that can show the difference between filling and standing still.
                var speed = (0.30f + (i % 3) * 0.08f) * (filling ? 2.1f : 1f);
                var phase = (t * speed + i * 0.41f) % 1f;

                var by = floor - level * phase;

                var edge = Math.Min(phase * 4f, Math.Min((1f - phase) * 3f, 1f));
                var alpha = (int)(135 * Math.Max(edge, 0f));
                if (alpha <= 4) continue;

                Draw.Bar(x + w * lane - size / 2f, by, size, tall,
                         Fade(Color.FromArgb(alpha, 255, 245, 210)));
            }
        }

        /// <summary>
        /// The same colour, at the gauge's opacity.
        ///
        /// Every colour the gauge draws goes through here, which is the point: the alphas in
        /// the drawing code stay as the RATIOS between the parts -- border darker than channel,
        /// shimmer barely there -- and one setting moves the lot without disturbing any of them.
        /// </summary>
        private Color Fade(Color c)
        {
            var a = (int)(c.A * _cfg.GaugeOpacity + 0.5f);

            if (a < 0) a = 0;
            if (a > 255) a = 255;

            return Color.FromArgb(a, c.R, c.G, c.B);
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

        /// <summary>Nothing outside 0..1, for anything that is a fraction of a tank.</summary>
        private static float Clamp01(float v)
        {
            if (v < 0f) return 0f;
            if (v > 1f) return 1f;
            return v;
        }
    }
}
