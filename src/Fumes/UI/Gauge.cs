using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using GTA;
using GTA.Native;
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

            // Gone when the game's own HUD or radar is. A bar beside a minimap that is not
            // there is worse than no bar, and this way a screenshot key or a cinematic mod
            // takes it with them without knowing Fumes exists.
            // TWO TESTS, NOT ONE. Gone is the set of moments no HUD of ours belongs on screen
            // at all -- dead, a cutscene, a fade, a character switch, the pause menu -- and it
            // is not a preference. HudHidden is the game's own HUD being off, which somebody
            // may want to ignore, and it stays behind the setting it always had.
            if (Gone()) return;
            if (_cfg.GaugeFollowsHud && HudHidden()) return;

            Measure();

            // On foot the gauge is only shown while actually putting fuel in something --
            // otherwise it is a permanent readout of a car you are not in.
            if (_cfg.GaugeOnlyInVehicle && !refuelling && !InThisVehicle(vehicle)) return;

            // HOW HARD THE FUEL IS BEING THROWN ABOUT, from the car's own speed.
            //
            // Fuel in a tank does what the tank does. Standing still it settles and the surface
            // barely moves; at speed it is being pushed around a box and the bubbles go with
            // it. Tying the animation to the one number that says how much of that is happening
            // costs nothing and means the gauge is never quite the same twice.
            //
            // Read here rather than inside the drawing so it is one lookup a frame, and so the
            // preview and a stalled car both get the resting value without a special case.
            _tickFraction = Clamp01(tank.Fraction);
            _tickFilling = refuelling;
            Tick(vehicle);

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

                // BARE MINIMUM'S ROW, TO THE NUMBER. Width is its BarWidth, Height is its
                // BarLength, and the bar's own column comes out of them the same way it does
                // there: the plate under the bar is the outline's width, square-cornered on
                // screen, a breath of one edge between the two, and the column is whatever is
                // left of the length after those. Computed from the same expressions from the
                // same inputs, so the five bars beside this one and this one are one row.
                var plateW = w + edge * 2f;
                var plateH = plateW * _aspect;
                var breath = edge * _aspect;

                float iconW = 0f, iconH = 0f;

                if (_cfg.Vertical && _cfg.ShowGaugeIcon)
                {
                    h -= plateH + breath;
                    if (h < 0.004f) h = 0.004f;

                    // A SQUARE, THE WAY BARE MINIMUM'S BADGE DRAWS EVERY MARK: the plate's width
                    // times IconScale, and as tall as that is wide on screen. The pump's art sits
                    // inside a square canvas at the same proportions as the heart, the bolt and
                    // the shield -- see tools/make_icons.py, which fits it to theirs -- so no
                    // per-icon aspect is needed here, and having one was the difference.
                    iconW = plateW * Clamp(_cfg.GaugeIconScale, 0.2f, 1f);
                    iconH = iconW * _aspect;
                }

                // The surround is one edge all round -- an edge tall as well as wide, which is
                // the aspect on the vertical sides, so the corners are square on screen.
                Draw.Bar(x - edge, y - breath, w + edge * 2f, h + breath * 2f, Fade(Color.FromArgb(205, 0, 0, 0)));
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

                    if (_cfg.ShowGaugeIcon && iconH > 0f)
                    {
                        // Below the bar, on its own dark plate.
                        //
                        // The plate is not decoration. Inside the bar the pump was black on the
                        // fuel and had a lit background guaranteed; out here it sits on whatever
                        // the world happens to be, and a light icon on concrete at midday is
                        // nothing at all. The same border the bar wears, wrapped round the icon,
                        // ties the two into one instrument and settles the contrast in every
                        // scene rather than most of them.
                        // THE PLATE IS THE WIDTH OF THE BAR'S OUTLINE, not of the icon.
                        // Sized to the icon it came out narrower than the thing above it, and
                        // two dark shapes in a column that ALMOST line up read as a mistake in
                        // a way that either lining up or plainly differing does not. Same left
                        // edge, same right edge, same border thickness.
                        // SEAMLESS. The bar's outline ends at y + h + edge and the plate's
                        // begins there -- one border thickness doing duty for both, so the two
                        // read as one column rather than two things stacked near each other.
                        // THE PLATE STARTS WHERE THE SURROUND ENDS, one breath below the foot,
                        // which is the line Bare Minimum draws its badges on.
                        var plateY = y + h + breath;
                        var cx = x + w / 2f;

                        Draw.Bar(cx - plateW / 2f, plateY, plateW, plateH,
                                 Fade(Color.FromArgb(205, 0, 0, 0)));

                        _pump.DrawSized(cx, plateY + plateH / 2f, iconW, iconH,
                                        Fade(Color.FromArgb(240, 242, 246, 252)));
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

        // ==================================================================
        // The level: Bare Minimum's bars, exactly
        // ==================================================================
        //
        // THIS IS THE FOOD BAR'S ANIMATION, CARRIED OVER WHOLE. The two mods sit next to each
        // other beside the minimap and were tuned against each other by eye for a week, and a
        // fuel column that moves differently from the five beside it reads as the odd one out
        // however good it is on its own. So the surface, the spring under it, the banded body,
        // the crest and the crumbs are Bare Minimum's, at Bare Minimum's numbers -- the one
        // thing kept from the old gauge is that the CLOCK runs faster with road speed, because
        // fuel in a moving car has a reason to be livelier that hunger does not.

        /// <summary>
        /// A damped spring the surface rides on. Kicked by a fill or a drain; leaned on by
        /// braking. Under a second a swing, lightly damped, so a kick rings three or four times
        /// before it is gone -- which reads as liquid rather than a cursor being nudged.
        /// </summary>
        private sealed class Slosh
        {
            public float S;
            public float V;

            private const float Omega = 7.4f;    // 2 pi over 0.85 seconds
            private const float Damping = 2.1f;  // 2 zeta omega, zeta about 0.14
            private const float Reach = 0.09f;

            public void Kick(float velocity) { V += velocity; }

            public void Step(float dt, float force)
            {
                V += (-Omega * Omega * S - Damping * V + force) * dt;
                S += V * dt;

                if (S > Reach) { S = Reach; if (V > 0f) V = 0f; }
                if (S < -Reach) { S = -Reach; if (V < 0f) V = 0f; }
            }
        }

        private readonly Slosh _spring = new Slosh();

        /// <summary>This frame's level and whether a pump is running, for the spring's kicks.</summary>
        private float _tickFraction;
        private bool _tickFilling;

        /// <summary>Last frame's level, for the kicks. Below zero until there has been one.</summary>
        private float _lastFraction = -1f;

        /// <summary>Fuel that has gone in since the last little bounce while filling.</summary>
        private float _fillSince;

        /// <summary>How hard he is being pushed along his own length, and upward, m/s², eased.</summary>
        private float _accel;
        private float _heave;

        private float _lastAlong;
        private float _lastUp;
        private bool _haveAlong;
        private int _lastBody;

        /// <summary>The column tops of the surface. One buffer, reused.</summary>
        private float[] _tops = new float[0];

        private int _screenW;
        private int _screenH;

        /// <summary>The accumulated clock. Advanced by Tick at the pace, times the road-speed lift.</summary>
        private float _clock;

        /// <summary>Set once a frame by Tick. See Motion.</summary>
        private float _motion = 1f;

        /// <summary>The slow clock the body's shading drifts on: this many seconds per unit.</summary>
        private const float InsideSlow = 7f;

        /// <summary>
        /// The equilibrium throw per m/s² of acceleration, as a share of the bar, times the
        /// spring's stiffness -- so a steady 10 m/s² of braking lifts the fuel about three per
        /// cent of the way up the bar and holds it there until the braking stops.
        /// </summary>
        private const float LeanGain = 0.003f * 7.4f * 7.4f;

        /// <summary>The spring, moved on by this frame's step. Called from Tick.</summary>
        private void Momentum(float fraction, bool filling, float dt)
        {
            if (dt < 0f) dt = 0f;
            if (dt > 0.1f) dt = 0.1f;

            var live = _cfg.GaugeLiquid;
            var force = live ? Lean(dt) : 0f;

            if (!live)
            {
                _haveAlong = false;
                _accel = 0f;
                _heave = 0f;
            }

            Kicks(fraction, filling, live);

            _spring.Step(dt, force);
        }

        /// <summary>
        /// The jolts: what changed since last frame, thrown at the spring.
        ///
        /// A top-up throws the level up a little; a loss slaps it down harder. THE DRAIN DOES
        /// NOT KICK -- a tank emptying over a drive moves a few millionths a frame, far under
        /// the threshold -- so this is a can poured in, a siphon, a save load. Filling from a
        /// pump is under the threshold too, so it gets its own small bounce every per cent, which
        /// is what pouring into a container looks like.
        /// </summary>
        private void Kicks(float fraction, bool filling, bool live)
        {
            var k = live ? Clamp01(_cfg.GaugeSlosh) : 0f;

            if (_lastFraction >= 0f)
            {
                var delta = fraction - _lastFraction;

                if (delta > 0.001f) _spring.Kick(0.32f * Math.Min(1f, delta * 4f) * k);
                else if (delta < -0.001f) _spring.Kick(-(0.28f + 0.52f * Math.Min(1f, -delta * 4f)) * k);
                else if (filling && delta > 0f)
                {
                    _fillSince += delta;
                    if (_fillSince >= 0.01f) { _spring.Kick(0.18f * k); _fillSince = 0f; }
                }
            }

            if (!filling) _fillSince = 0f;
            _lastFraction = fraction;
        }

        /// <summary>
        /// Braking, accelerating and landing, as a force on the spring.
        ///
        /// From whatever he is riding in, or him on foot. Eased, so a physics tick that stutters
        /// is a lean rather than a rattle; reset on a change of body or a long frame, because
        /// the difference between two velocities only means something across one step.
        /// </summary>
        private float Lean(float dt)
        {
            var motion = Clamp01(_cfg.GaugeLean);

            if (motion <= 0f || dt <= 0.0001f)
            {
                _accel = 0f;
                _heave = 0f;
                return 0f;
            }

            try
            {
                var ped = Game.Player.Character;
                Entity body = ped;

                if (ped.IsInVehicle())
                {
                    var veh = ped.CurrentVehicle;
                    if (veh != null && veh.Exists()) body = veh;
                }

                var v = body.Velocity;
                var f = body.ForwardVector;

                var along = v.X * f.X + v.Y * f.Y + v.Z * f.Z;
                var up = v.Z;

                if (body.Handle != _lastBody || !_haveAlong || dt > 0.09f)
                {
                    _lastBody = body.Handle;
                    _lastAlong = along;
                    _lastUp = up;
                    _haveAlong = true;
                    _accel = 0f;
                    _heave = 0f;
                    return 0f;
                }

                var a = Clamp((along - _lastAlong) / dt, -30f, 30f);
                var az = Clamp((up - _lastUp) / dt, -30f, 30f);

                _lastAlong = along;
                _lastUp = up;

                // Footsteps bob the ped a little; that is not a landing.
                if (Math.Abs(az) < 2f) az = 0f;

                _accel += (a - _accel) * Math.Min(1f, dt * 12f);
                _heave += (az - _heave) * Math.Min(1f, dt * 12f);

                // BRAKING DROPS IT, ACCELERATING LIFTS IT, and a landing drops it -- the same
                // way round as Bare Minimum's bars beside this one, which were felt as backwards
                // the other way. All through the spring, so they ring down rather than snap.
                return (_accel * 0.6f - _heave) * LeanGain * motion;
            }
            catch
            {
                _haveAlong = false;
                return 0f;
            }
        }

        /// <summary>
        /// The tops of the surface, column by column: a slow bow and lift on their own
        /// periods, a tilt, the spring's throw bending it, and braking leaning it up a wall.
        /// </summary>
        private float[] Surface(float x, float y, float w, float h, float surfaceY, float t,
                                float swing, float speed, float capH, float phase)
        {
            var floor = y + h;
            var wave = Clamp01(_cfg.GaugeWave);

            // The width as a HEIGHT fraction, for anything measured across the bar in the same
            // unit as along it.
            var thickH = w * _aspect;

            var bow = (float)Math.Sin(t * (2.0 * Math.PI / 144.0)) * swing;
            var lift = (float)Math.Sin(t * (2.0 * Math.PI / 208.0)) * swing * 0.40f;

            // BARELY MOVING AT REST, the same as Bare Minimum's five beside it: a fifth of the
            // idle tilt it had, and the swing above is a quarter of its first figure. The
            // movement you see is the spring -- a fill, a brake, a landing.
            var tilt = (float)Math.Sin((t + phase) * (2.0 * Math.PI / 298.0)) * 0.04f * thickH * wave;

            // Up is negative on screen: a surface thrown upward heaps in the middle.
            bow -= speed * 0.40f * thickH;
            tilt += speed * 0.25f * thickH;

            // Braking leans the liquid up one wall; accelerating, the other. The same way round
            // as the throw, so a stop reads as one motion and not two.
            tilt -= Clamp(_accel / 10f, -1f, 1f) * 0.30f * thickH * Clamp01(_cfg.GaugeLean);

            var columns = Columns(w);
            if (_tops.Length != columns) _tops = new float[columns];

            for (var i = 0; i < columns; i++)
            {
                // -1 at one wall, +1 at the other; 1 in the middle, 0 at both walls.
                var across = ((i + 0.5f) / columns - 0.5f) * 2f;
                var curve = 1f - across * across;

                var topY = surfaceY + lift + bow * curve + tilt * across;

                if (topY < y) topY = y;
                if (topY > floor - capH) topY = floor - capH;

                _tops[i] = topY;
            }

            return _tops;
        }

        /// <summary>One column per two pixels, eight to forty. Sub-pixel strips boil; see Bare Minimum.</summary>
        private int Columns(float w)
        {
            if (_screenW <= 0)
            {
                try { _screenW = GTA.UI.Screen.Resolution.Width; }
                catch { _screenW = 1920; }
            }

            // THE VITALS' RULE, NOT THE FOOD BAR'S. One column per two pixels, FOUR to ten. The
            // food bar's floor of eight put eight strips across a nine-pixel bar -- 1.1 px each,
            // and a sub-pixel rectangle is a lottery with the rasteriser: each one rounded to
            // one or two pixels on its own, every frame, and the surface boiled. That is the
            // glitch. Four strips of two pixels move; eight strips of one flicker.
            var n = (int)Math.Round(w * _screenW / 2f);

            if (n < 4) n = 4;
            if (n > 10) n = 10;

            return n;
        }

        /// <summary>One band per four pixels of height, twelve to sixty-four.</summary>
        private int Bands(float h)
        {
            if (_screenH <= 0)
            {
                try { _screenH = GTA.UI.Screen.Resolution.Height; }
                catch { _screenH = 1080; }
            }

            var n = (int)(h * _screenH / 4f);

            if (n < 12) n = 12;
            if (n > 64) n = 64;

            return n;
        }

        private static float Lowest(float[] tops, float floor)
        {
            var low = 0f;

            for (var i = 0; i < tops.Length; i++)
            {
                if (tops[i] > low) low = tops[i];
            }

            return low > floor ? floor : low;
        }

        /// <summary>
        /// A soft hump, 1 at its centre and 0 past its width, wrapped so it goes round the bar
        /// rather than off the end. Cosine rather than a triangle: a linear falloff has a corner.
        /// </summary>
        private static float Pulse(float u, float width)
        {
            u -= (float)Math.Floor(u);

            var d = Math.Abs(u - 0.5f) * 2f;
            if (d >= width) return 0f;

            return 0.5f + 0.5f * (float)Math.Cos(d / width * Math.PI);
        }

        /// <summary>
        /// Bubbles rising through the fuel. The original, back.
        ///
        /// THE THREE PALE SPECKS THAT STOOD IN FOR THESE WERE INVISIBLE. They were Bare
        /// Minimum's crumbs turned upward -- three, tiny, on a clock that took half a minute to
        /// cross the bar, and pale on a pale body. These are the bubbles the bar had before the
        /// port: quicker, a shade warmer than the fuel, and quicker still while it is filling,
        /// which is the one moment a tank has a reason to fizz.
        ///
        /// Deterministic rather than random: each one's position comes from the clock and its
        /// own index, so there is no state to keep and no Random being pumped sixty times a
        /// second. They fade in off the floor and out at the surface instead of appearing and
        /// popping.
        ///
        /// ON THE OLD CLOCK. This was written when the clock advanced a unit a second; it now
        /// advances Pace units a second, so the caller divides it back down. Drift is how
        /// visible they are, and 0 removes them.
        /// </summary>
        private void Bubbles(float x, float y, float w, float h, float level, float t,
                             bool filling)
        {
            const int count = 3;

            if (level < 0.014f) return;

            var drift = Clamp01(_cfg.GaugeDrift);
            if (drift <= 0.001f) return;

            var floor = y + h;

            // Square ON SCREEN. A rectangle given equal width and height fractions is as wide
            // as the screen is wider than it is tall -- on this one that is a bubble half again
            // wider than it is high, which at three pixels reads as a dash.
            var size = w * 0.24f;
            var tall = size * _aspect;

            for (var i = 0; i < count; i++)
            {
                var lane = 0.22f + i * (0.56f / (count - 1));

                // Quicker while fuel is actually going in. NOT multiplied by the motion here:
                // t is already the accelerated clock, and multiplying twice would square it.
                var speed = (0.30f + (i % 3) * 0.08f) * (filling ? 2.1f : 1f);
                var phase = (t * speed + i * 0.41f) % 1f;

                var by = floor - level * phase;

                var edge = Math.Min(phase * 4f, Math.Min((1f - phase) * 3f, 1f));
                var alpha = (int)(135 * Math.Max(edge, 0f) * drift);
                if (alpha <= 4) continue;

                Draw.Bar(x + w * lane - size / 2f, by, size, tall,
                         Fade(Color.FromArgb(alpha, 255, 245, 210)));
            }
        }

        /// <summary>
        /// The fuel in the bar, drawn the way Bare Minimum draws a need: the surface first, then
        /// the body under the lowest point of it in slow-shifting bands, then the crest, then
        /// the bubbles.
        /// </summary>
        private void Liquid(float x, float y, float w, float h, float fraction, Color body,
                            bool filling)
        {
            var t = _clock;
            var inside = t / InsideSlow;

            var level = h * fraction;
            var empty = 1f - fraction;

            // THE THROW. The spring's offset is a share of the bar's length, up when positive,
            // and it moves the whole surface; its speed bends the surface as well.
            var thrown = _spring.S * h;
            var speed = Clamp(_spring.V * 1.8f, -1f, 1f);

            var surfaceY = Clamp(y + h - level - thrown, y, y + h);

            var swing = h * 0.004f * Clamp01(_cfg.GaugeWave) * (0.35f + 0.65f * empty);

            var floor = y + h;

            // The rate climbs as the tank empties, so running it down makes it visibly livelier.
            var hurry = 1f + 0.85f * empty;
            var capH = Math.Max(1.6f / (_screenH > 0 ? _screenH : 1080f), h * 0.007f);
            var tops = Surface(x, y, w, h, surfaceY, t * hurry, swing, speed, capH, 0f);

            var bodyTop = Lowest(tops, floor);

            // ---- the contents: two broad humps drifting up the column, very slowly ----
            var bands = Bands(h);

            for (var i = 0; i < bands; i++)
            {
                var bTop = bodyTop + (floor - bodyTop) * i / bands;
                var bBot = bodyTop + (floor - bodyTop) * (i + 1) / bands;

                if (bBot - bTop <= 0f) continue;

                var u = (i + 0.5f) / bands;

                var a = Pulse(u - inside * 0.00138f, 0.58f);
                var b = Pulse(u - inside * 0.00085f + 0.5f, 0.76f);

                var warm = (a * 0.6f + b * 0.4f) * (0.09f + 0.13f * empty);

                Draw.Bar(x, bTop, w, bBot - bTop, Mix(body, Color.FromArgb(body.A, 255, 245, 220), warm));
            }

            if (level <= 0.002f) return;

            // ---- the surface, column by column, from the tops worked out above ----
            // A crest thinner than a pixel is drawn on some frames and not others. The vitals
            // hold theirs at a pixel and a half at least; so does this one now.
            var crestH = Math.Max(1.6f / (_screenH > 0 ? _screenH : 1080f), h * 0.007f);
            var crest = Mix(body, Color.FromArgb(body.A, 255, 240, 205), 0.55f);

            for (var i = 0; i < tops.Length; i++)
            {
                var left = x + w * i / tops.Length;
                var right = x + w * (i + 1) / tops.Length;
                var topY = tops[i];

                if (topY < bodyTop) Draw.Bar(left, topY, right - left, bodyTop - topY, body);

                Draw.Bar(left, topY, right - left, crestH, crest);
            }

            // Back on the clock they were written for: a unit a second, not Pace of them.
            var pace = _cfg.GaugePace < 0.05f ? 0.05f : _cfg.GaugePace;
            Bubbles(x, y, w, h, level, _clock / pace, filling);
        }

        private static float Clamp(float v, float lo, float hi)
        {
            return v < lo ? lo : v > hi ? hi : v;
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

        /// <summary>
        /// Whether this is a moment the gauge should not exist: the wasted screen, a cutscene,
        /// a fade, a character switch, the pause menu.
        ///
        /// ASKED OUTRIGHT RATHER THAN INFERRED FROM THE RADAR. The radar going away covers most
        /// of these, but not every cutscene hides it and the wasted screen takes a beat to --
        /// and in that beat a fuel gauge sat over "WASTED" in a film-grain frame, which is the
        /// report. Bare Minimum's bars ask the same set, so the row goes and comes back as one.
        /// </summary>
        private static bool Gone()
        {
            try
            {
                if (Game.IsPaused) return true;
                if (Function.Call<bool>(Hash.IS_PAUSE_MENU_ACTIVE)) return true;
                if (!Function.Call<bool>(Hash.IS_SCREEN_FADED_IN)) return true;
                if (Function.Call<bool>(Hash.IS_PLAYER_SWITCH_IN_PROGRESS)) return true;
                if (Function.Call<bool>(Hash.IS_CUTSCENE_ACTIVE)) return true;
                if (Function.Call<bool>(Hash.IS_PLAYER_DEAD, Game.Player.Handle)) return true;

                var me = Game.Player.Character;
                if (me == null || !me.Exists() || !me.IsAlive) return true;

                return false;
            }
            catch
            {
                // If it cannot be asked, draw -- same rule as HudHidden.
                return false;
            }
        }

        /// <summary>Whether the game is currently hiding its own HUD or radar.</summary>
        private static bool HudHidden()
        {
            try
            {
                return Function.Call<bool>(Hash.IS_HUD_HIDDEN) ||
                       Function.Call<bool>(Hash.IS_RADAR_HIDDEN);
            }
            catch
            {
                // If it cannot be asked, draw. A gauge that vanishes for an unknown reason is
                // a bug report; one that stays is at worst untidy.
                return false;
            }
        }

        /// <summary>
        /// A multiplier on how fast the fuel moves, from the vehicle's speed.
        ///
        /// One everywhere below MotionFromKmh, climbing to GaugeMotionMax by MotionFullKmh,
        /// then flat. Flat because past a point more speed does not make a liquid slosh faster,
        /// it makes it slosh harder -- and an animation that keeps accelerating with the
        /// speedometer stops reading as fuel and starts reading as a loading spinner.
        /// </summary>
        private float Motion(Vehicle v)
        {
            if (v == null) return 1f;

            try
            {
                // NOTHING HAPPENS BELOW THE THRESHOLD, and that is the point of having one.
                //
                // Scaling from a standstill meant the animation was different at every speed,
                // which is a lot of change to spend on something nobody is looking at while
                // they drive. Held at normal until it is worth remarking on, the speed-up
                // becomes an event -- the fuel starts moving when you are actually moving.
                var speed = Math.Abs(v.Speed) * 3.6f;   // to km/h, which is what the settings say

                // STANDING STILL IS ITS OWN STATE, and it gets a slower clock. Fuel in a parked
                // car is settling rather than being pushed about, and the difference is worth
                // a glance -- but only a little, because the last attempt at this ran the idle
                // at quarter speed and the bar looked broken rather than calm.
                //
                // Eased out over the first few km/h rather than snapped at the first
                // millimetre of movement: a car rolling at walking pace is not parked, and a
                // gauge that changes gear the instant the handbrake comes off draws the eye to
                // the wrong thing.
                var rest = _cfg.GaugeMotionRestKmh;

                if (speed < rest && rest > 0.1f)
                {
                    var idle = _cfg.GaugeMotionIdle;
                    return idle + (1f - idle) * (speed / rest);
                }

                var from = _cfg.GaugeMotionFromKmh;
                var full = _cfg.GaugeMotionFullKmh;

                if (speed <= from || full <= from) return 1f;

                var t = (speed - from) / (full - from);
                if (t > 1f) t = 1f;

                // Linear across the band, and the band is what sets how hard it comes on:
                // same climb, fewer km/h to do it in, so each one counts for more. Seventy wide
                // from 80 to 150 puts the whole change inside the speeds a car is actually
                // driven hard at, rather than spreading it out to a top speed most vehicles in
                // this game will never see.
                return 1f + (_cfg.GaugeMotionMax - 1f) * t;
            }
            catch
            {
                return 1f;
            }
        }





        /// <summary>
        /// Advances the clock, and eases the rate toward what the car is doing.
        ///
        /// ASYMMETRIC ON PURPOSE. Winding up follows the throttle closely, because that is a
        /// thing you did; winding down takes several seconds, because fuel that has been thrown
        /// about does not stop the moment you lift off. It also means dropping under 120 for a
        /// corner does not slam the animation back to walking pace and out again.
        /// </summary>
        private void Tick(Vehicle vehicle)
        {
            var dt = 0f;

            try { dt = Game.LastFrameTime; }
            catch { dt = 0f; }

            // A paused or hitching game hands back nonsense; neither is a frame's worth of time.
            if (dt <= 0f || dt > 0.5f) dt = dt > 0.5f ? 0.5f : 0f;

            var target = Motion(vehicle);

            var seconds = target > _motion ? _cfg.GaugeMotionRiseSeconds
                                           : _cfg.GaugeMotionFallSeconds;

            if (dt > 0f && seconds > 0.01f)
            {
                // Exponential easing, so the approach is the same on any framerate rather than
                // however many times a second this happens to be called.
                _motion += (target - _motion) * (1f - (float)Math.Exp(-dt / seconds));
            }
            else
            {
                _motion = target;
            }

            // THE SPRING AND THE CLOCK, on the same step. The clock runs at Bare Minimum's pace
            // -- forty-two of its units a second -- times the road-speed lift, which is the one
            // thing kept from the old gauge: fuel in a moving car is livelier than a parked one.
            Momentum(_tickFraction, _tickFilling, dt);

            var pace = _cfg.GaugePace < 0.05f ? 0.05f : _cfg.GaugePace;
            _clock += dt * pace * _motion;

            if (_clock > 1000000f) _clock -= 1000000f;
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
