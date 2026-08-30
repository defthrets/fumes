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
        /// The fuel in the bar: a flat level, a surface, and bubbles rising through it.
        ///
        /// THE SURFACE IS FLAT. It was sliced into six columns each riding its own pair of sine
        /// waves -- the pump display's technique, which works there because that glass is four
        /// times as wide. At sixteen pixels a column is two and a half, so a wave of a couple of
        /// pixels across six of them is not read as a moving liquid; it is read as the top edge
        /// being ragged. The effect needed width the bar does not have.
        ///
        /// What survives is what worked: a brighter line riding the surface, so the top is a
        /// surface and not just where the colour stops, and the bubbles. Those do not need
        /// width -- they need height, and the bar is two hundred pixels of it.
        /// </summary>
        private void Liquid(float x, float y, float w, float h, float fraction, Color body,
                            bool filling)
        {
            var level = h * fraction;
            var top = y + h - level;

            Draw.Bar(x, top, w, level, body);

            // Mixed off the body rather than fixed, because the body runs the whole ramp from
            // yellow to red and a fixed crest would come loose from it near empty.
            if (level > 0.004f)
            {
                Draw.Bar(x, top, w, 0.0011f, Mix(body, Color.FromArgb(body.A, 255, 240, 195), 0.55f));
            }

            Bubbles(x, y, w, h, level, Environment.TickCount / 1000f, filling);
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
                // A STEP HAS TO SUIT WHAT IT IS MOVING. The width of this bar is now three
                // thousandths of the screen, so the old two-thousandth size step changed it by
                // most of itself in one press -- a tuner you cannot creep up on is not a tuner.
                const float move = 0.0010f;
                const float wide = 0.0004f;
                const float tall = 0.0020f;

                if (Edge(Keys.NumPad8, ref _upDown)) _cfg.GaugeY -= move;
                if (Edge(Keys.NumPad2, ref _downDown)) _cfg.GaugeY += move;
                if (Edge(Keys.NumPad4, ref _leftDown)) _cfg.GaugeX -= move;
                if (Edge(Keys.NumPad6, ref _rightDown)) _cfg.GaugeX += move;

                if (Edge(Keys.NumPad7, ref _narrowDown)) _cfg.GaugeWidth -= wide;
                if (Edge(Keys.NumPad9, ref _wideDown)) _cfg.GaugeWidth += wide;
                if (Edge(Keys.NumPad1, ref _thinDown)) _cfg.GaugeHeight -= tall;
                if (Edge(Keys.NumPad3, ref _fatDown)) _cfg.GaugeHeight += tall;

                _cfg.GaugeX = Clamp(_cfg.GaugeX, 0f, 0.95f);
                _cfg.GaugeY = Clamp(_cfg.GaugeY, 0f, 0.99f);
                _cfg.GaugeWidth = Clamp(_cfg.GaugeWidth, 0.0015f, 0.8f);
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
