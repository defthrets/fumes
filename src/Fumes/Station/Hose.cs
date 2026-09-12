using System;
using System.Collections.Generic;
using System.Drawing;
using GTA;
using GTA.Math;
using GTA.Native;
using Fumes.Core;

namespace Fumes.Station
{
    /// <summary>
    /// The line between the pump and whatever is holding the nozzle.
    ///
    /// Two implementations of the same idea, and the mod picks whichever actually works on
    /// the machine it is running on.
    ///
    /// ROPE is the real thing: one of the game's own physics ropes, pinned at both ends every
    /// frame and given slightly more length than the straight-line distance so it hangs. It
    /// swings when you walk, sags when you stand still and drapes over a wing when you lean
    /// across a car -- and none of that is written here, it is the rope solver doing it.
    ///
    /// LINE is a drawn catenary. No physics, no textures, cannot fail. It is here because
    /// ropes have a real prerequisite that can genuinely not be met -- see Ready() -- and a
    /// mod whose central prop is invisible on some machines is worse than one that draws a
    /// convincing black line on all of them.
    ///
    /// The pinning is the interesting part. The obvious approach is ATTACH_ENTITIES_TO_ROPE
    /// between the pump and the nozzle, but the nozzle is bolted to a hand bone, which means
    /// it is not physically simulated and there is nothing for a rope end to pull on. Pinning
    /// named vertices to two positions we compute ourselves sidesteps that entirely: the ends
    /// go exactly where we say, and the solver is left to do only the part it is good at,
    /// which is the shape in between.
    /// </summary>
    internal sealed class Hose
    {
        private readonly Settings _cfg;

        private Rope _rope;
        private bool _texturesAsked;
        private int _gaveUpOnRopeAt;

        private int _spawnedAt;

        /// <summary>Which way this instance settled, once it has had to decide.</summary>
        private HoseMode _mode;

        /// <summary>Lengthways bands the hose is shaded in. See Stroke.</summary>
        private const int Bands = 5;

        /// <summary>
        /// Most segments the hose is drawn in, however many points it is handed.
        ///
        /// A SETTING NOW, AND IT WAS FOURTEEN. The note below said a dozen is smooth over
        /// three or four metres and nobody can tell -- true, and this hose runs to nine, where
        /// fourteen is a segment every sixty-five centimetres and every one of them is
        /// straight. That is the jaggedness. The band shading multiplies the cost by five, so
        /// it is not free, but DRAW_POLY does not come out of the rectangle budget that the
        /// HUD is fighting over.
        /// </summary>
        private int MaxSegments => _cfg.HoseSegments;

        /// <summary>
        /// Whether this is the siphon line rather than the pump hose.
        ///
        /// They are the same object doing the same job and they still want different paint: the
        /// pump hose is the game's own rope, which carries its own texture and looks right, and
        /// the siphon line is a length of black tube somebody keeps in a boot. One flag rather
        /// than a second class, because everything about the geometry is identical -- only the
        /// numbers differ.
        /// </summary>
        private readonly bool _siphon;

        public Hose(Settings cfg, bool siphon = false)
        {
            _cfg = cfg;
            _siphon = siphon;
            _mode = siphon ? cfg.SiphonHose : cfg.Hose;
        }

        // Which set of numbers this hose draws itself with.
        private int Red => _siphon ? _cfg.SiphonHoseRed : _cfg.HoseRed;
        private int Green => _siphon ? _cfg.SiphonHoseGreen : _cfg.HoseGreen;
        private int Blue => _siphon ? _cfg.SiphonHoseBlue : _cfg.HoseBlue;
        private int Sheen => _siphon ? _cfg.SiphonHoseSheen : _cfg.HoseSheen;
        private float Thickness => _siphon ? _cfg.SiphonHoseThickness : _cfg.HoseThickness;
        private float Sag => _siphon ? _cfg.SiphonHoseSag : _cfg.HoseSag;
        private int RopeType => _siphon ? _cfg.SiphonHoseRopeType : _cfg.HoseRopeType;
        private int Sides => _siphon ? _cfg.SiphonHoseSides : _cfg.HoseSides;

        public bool Live => _rope != null && _rope.Exists();

        /// <summary>
        /// Rope textures.
        ///
        /// THE ONE MANDATORY STEP, and the one that produces the most convincing wrong result
        /// when it is skipped: a rope created before its textures are loaded exists, simulates,
        /// has vertices you can read -- and draws nothing at all. Every symptom says the rope
        /// failed to spawn, and it did not.
        /// </summary>
        private bool Ready()
        {
            try
            {
                if (Function.Call<bool>(Hash.ROPE_ARE_TEXTURES_LOADED)) return true;

                if (!_texturesAsked)
                {
                    _texturesAsked = true;
                    Function.Call(Hash.ROPE_LOAD_TEXTURES);
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Runs the hose out from a pump. Call every frame while the nozzle is out.
        ///
        /// from = where the hose leaves the pump, to = the hand holding the nozzle.
        /// </summary>
        /// <summary>Runs the hose from one point to another by way of a third.</summary>
        public void Update(Vector3 from, Vector3 via, Vector3 to)
        {
            if (_mode == HoseMode.None) return;

            // Only the drawn modes can be told where to go. A rope is a physics object with two
            // ends and no opinion about the middle, so it gets the straight run.
            if (_mode == HoseMode.Line || _mode == HoseMode.Tube)
            {
                DrawCatenary(from, via, to);
                return;
            }

            Update(from, to);
        }

        public void Update(Vector3 from, Vector3 to)
        {
            if (_mode == HoseMode.None) return;

            if (_mode == HoseMode.Line || _mode == HoseMode.Tube) { DrawCatenary(from, to); return; }

            if (!Live && !Spawn(from, to))
            {
                // Rope could not be had. In Auto and Painted that is a decision, not a failure.
                if ((_mode == HoseMode.Auto || _mode == HoseMode.Painted) && _gaveUpOnRopeAt != 0 &&
                    Game.GameTime - _gaveUpOnRopeAt > 3000)
                {
                    _mode = HoseMode.Line;
                    Log.Warn("Rope hose is not coming up after three seconds - drawing the hose instead. " +
                             "Set [Nozzle] Hose = Line to make that permanent, or Rope to keep waiting.");
                }

                if (_mode != HoseMode.Rope) DrawCatenary(from, to);
                return;
            }

            // Survived. A rope that has existed for a second and a half did not crash the
            // game, so the note naming its type comes off the disk.
            if (_spawnedAt != 0 && Game.GameTime - _spawnedAt > 1500)
            {
                _spawnedAt = 0;
                RopeProbe.Disarm();
            }

            Pin(from, to);

            // Painted mode: the rope did the physics, we do the colour. See HoseMode.Painted.
            if (_mode == HoseMode.Painted) PaintRope(from, to);
        }

        /// <summary>
        /// Draws the hose along the rope's OWN vertices.
        ///
        /// This is what makes a black hose possible at all. There is no native to tint a rope;
        /// the colour is baked into one of nine authored textures and the choice of texture is
        /// the only control there is. But the rope will tell you where every one of its
        /// vertices ended up, and a line through those points is the rope's exact shape -- sag,
        /// swing, drape over a wing and all -- in whatever colour we like.
        /// </summary>
        private void PaintRope(Vector3 from, Vector3 to)
        {
            try
            {
                var count = _rope.VertexCount;
                if (count < 2) { DrawCatenary(from, to); return; }

                var points = new Vector3[count];
                for (var i = 0; i < count; i++) points[i] = _rope.GetVertexCoord(i);

                Stroke(points);
            }
            catch (Exception ex)
            {
                Log.Once("hose-paint", "Could not read the rope's shape: " + ex.Message +
                                       " - drawing a plain hose instead.");
                DrawCatenary(from, to);
            }
        }

        private bool Spawn(Vector3 from, Vector3 to)
        {
            // PAINTED MODE LOADS THE TEXTURES TOO, AND HAS TO.
            //
            // It tried not to. The reasoning looked sound: Painted does not LOOK at the rope, it
            // reads the rope's vertices and draws its own hose along them, and Ready()'s own
            // comment says a rope created before its textures are loaded simulates fine and
            // draws nothing -- which would be a free invisible rope with real physics.
            //
            // It came out as a rigid straight line from the pump to the hand. That comment is
            // about a rope whose textures have not arrived YET, and the solver picks up when
            // they do; a rope that never gets them never simulates at all, so every vertex stays
            // on the straight line between the two pinned ends and the hose reads as a pole. The
            // texture is load-bearing for the physics, not only for the picture.
            //
            // So the beige rope is drawn underneath and the black ribbon is drawn over it. That
            // is what Painted was always for, and covering a rope is a smaller problem than not
            // having one.
            if (!Ready())
            {
                if (_gaveUpOnRopeAt == 0) _gaveUpOnRopeAt = Game.GameTime;
                return false;
            }

            try
            {
                var span = from.DistanceTo(to);

                // BORN AT FULL LENGTH, WHATEVER THE SPAN IS. This was span * Sag, which is
                // about a metre and a half at the moment the nozzle leaves the pump -- and
                // a rope's SEGMENT COUNT is fixed when it is created, from the length it is
                // created at. A metre and a half of rope is a handful of vertices, and Pin
                // then stretches those same few vertices out to nine metres as you walk: long
                // straight runs with a corner at each joint, which is exactly the "stiff and
                // jagged" of it. Created at the full reach it gets the full count, and Pin
                // shortening it afterwards keeps every one of them -- so the same hose has
                // several times the joints to bend at, at every length.
                var initial = _cfg.HoseMaxMetres;

                // ADD_ROPE(x, y, z, rotX, rotY, rotZ, maxLength, ropeType, initLength,
                //          minLength, windingSpeed, p11, p12, rigid, p14, breakWhenShot, unkPtr)
                //
                // maxLength is given slack over HoseMaxMetres on purpose: HoseMaxMetres is the
                // gameplay leash, enforced by Refuel, and a rope whose physical maximum is the
                // same number goes taut and starts fighting the pin a moment before the leash
                // ever fires.
                // WRITTEN DOWN FIRST. ADD_ROPE with a type past the end of the game's rope
                // table does not throw, it ends the process -- so the only way to learn which
                // types those are is to record the attempt somewhere that outlives the attempt.
                RopeProbe.Arm(RopeType);

                var handle = Function.Call<int>(Hash.ADD_ROPE,
                    from.X, from.Y, from.Z,
                    0f, 0f, 0f,
                    _cfg.HoseMaxMetres * 1.6f,
                    RopeType,
                    initial,
                    0.5f,
                    1f,
                    false, false,
                    false,
                    _cfg.HoseFlex,
                    false,
                    0);

                if (handle == 0)
                {
                    if (_gaveUpOnRopeAt == 0) _gaveUpOnRopeAt = Game.GameTime;
                    return false;
                }

                _rope = new Rope(handle);
                _rope.ActivatePhysics();
                _gaveUpOnRopeAt = 0;
                _spawnedAt = Game.GameTime;

                Log.Once(_siphon ? "hose-mode-siphon" : "hose-mode", (_siphon ? "Siphon line: rope type " : "Hose: rope type ") + RopeType + ", " + _mode +
                                      ", drawn at " + Red + "," + Green + "," +
                                      Blue + " with sheen " + Sheen +
                                      ", ribbon " + Thickness.ToString("0.000") + "m.");

                // AT INFO, because the vertex count is the whole of how a rope moves and it
                // is the one number that explains a stiff one. Once per hose.
                Log.Once("hose-verts-" + RopeType,
                         "Hose out: rope type " + RopeType + ", " + _rope.VertexCount +
                         " vertices over " + initial.ToString("0.0") + "m.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Once("rope-spawn", "Could not create the hose rope: " + ex.Message +
                                       " - falling back to a drawn hose.");
                if (_mode == HoseMode.Auto || _mode == HoseMode.Painted) _mode = HoseMode.Line;
                return false;
            }
        }

        /// <summary>
        /// Nails both ends where they belong and gives the middle enough length to hang.
        ///
        /// Every frame, and it has to be: the hand end moves with the player, and a rope end
        /// pinned once is a rope that stays where the player was standing when they picked it
        /// up.
        /// </summary>
        private void Pin(Vector3 from, Vector3 to)
        {
            try
            {
                var count = _rope.VertexCount;
                if (count < 2)
                {
                    // A rope with no vertices is a rope that did not really spawn.
                    Retract();
                    return;
                }

                var span = from.DistanceTo(to);
                _rope.Length = Clamp(span * Sag, 1.2f, _cfg.HoseMaxMetres * 1.5f);

                _rope.PinVertex(0, from);
                _rope.PinVertex(count - 1, to);
            }
            catch (Exception ex)
            {
                Log.Once("rope-pin", "Hose pinning failed: " + ex.Message + " - drawing it instead.");
                Retract();
                if (_mode == HoseMode.Auto || _mode == HoseMode.Painted) _mode = HoseMode.Line;
            }
        }

        /// <summary>
        /// The hose with no rope behind it: a hanging curve worked out here.
        ///
        /// Used when ropes are switched off or will not load. The shape is a parabola rather
        /// than a real catenary -- over three metres of hose the two are the same picture, and
        /// only one of them needs a cosh.
        /// </summary>
        /// <summary>
        /// One hanging leg, as points. Sag is worked out per LEG rather than across the whole
        /// run -- a short leg between two hands should barely dip while a long one to the floor
        /// droops properly, and one figure for both gives you a hose that either sags between
        /// his hands or runs dead straight to the can.
        /// </summary>
        private Vector3[] Leg(Vector3 from, Vector3 to, int segments)
        {
            var span = from.DistanceTo(to);
            var sag = Clamp(span * (Sag - 1f) * 1.6f, 0.02f, 1.4f);

            var points = new Vector3[segments + 1];

            for (var i = 0; i <= segments; i++)
            {
                var t = (float)i / segments;
                var point = Vector3.Lerp(from, to, t);
                point.Z -= 4f * sag * t * (1f - t);
                points[i] = point;
            }

            return points;
        }

        /// <summary>
        /// Tube draws real geometry, everything else draws the billboarded ribbon.
        ///
        /// THIS LINE WAS IN THE WRONG METHOD. It was put into PaintRope, which only runs for
        /// Painted mode over a live rope -- and Tube never gets there, because it returns to
        /// DrawCatenary long before. So Sleeve was written, compiled, shipped, and never once
        /// called: everything drawn under the name Tube has actually been Stroke.
        ///
        /// The patch that placed it reported success, because the text it wrote was in the file
        /// afterwards. It was in the wrong function. A string being present is not the same
        /// question as a string being where it was meant to go, and only the first one was
        /// being asked.
        /// </summary>
        private void Draw(Vector3[] points)
        {
            if (_mode == HoseMode.Tube) Sleeve(points);
            else Stroke(points);
        }

        private void DrawCatenary(Vector3 from, Vector3 to)
        {
            // Sampled at least as finely as it will be drawn, or the stride below has nothing
            // to choose from and the extra segments buy nothing.
            var segments = _cfg.HoseSegments + 4;

            try
            {
                if (from.DistanceTo(to) < 0.05f) return;

                Draw(Leg(from, to, segments));
            }
            catch (Exception ex)
            {
                Log.Once("hose-draw", "Could not draw the hose: " + ex.Message);
            }
        }

        /// <summary>
        /// The same hose, but running THROUGH a point on its way.
        ///
        /// Two legs joined into ONE array rather than drawn as two hoses. Drawn separately a
        /// tube would start and end at the join, and a tube's end is an open ring -- you would
        /// see the hole, and the halves would shade independently either side of it. Joined,
        /// the rings carry through and the bend is just a bend.
        ///
        /// The join point appears once, not twice: a repeated point has no direction between
        /// itself and itself, so the frame carried along the tube would have nothing to
        /// re-square against there.
        /// </summary>
        private void DrawCatenary(Vector3 from, Vector3 via, Vector3 to)
        {
            const int segments = 9;

            try
            {
                if (from.DistanceTo(via) < 0.02f) { DrawCatenary(from, to); return; }
                if (via.DistanceTo(to) < 0.02f) { DrawCatenary(from, via); return; }

                var first = Leg(from, via, segments);
                var second = Leg(via, to, segments);

                var all = new Vector3[first.Length + second.Length - 1];

                Array.Copy(first, all, first.Length);
                Array.Copy(second, 1, all, first.Length, second.Length - 1);

                Draw(all);
            }
            catch (Exception ex)
            {
                Log.Once("hose-draw-via", "Could not draw the hose: " + ex.Message);
            }
        }

        /// <summary>
        /// Draws a run of points as a SOLID hose, out of triangles.
        ///
        /// This used to be a bundle of parallel DRAW_LINEs, and that approach cannot be made
        /// to work however many you add. DRAW_LINE is ONE PIXEL WIDE AT ANY DISTANCE -- it does
        /// not get thicker as you walk up to it -- so lines offset by real-world centimetres
        /// converge into one hairline at range and separate into visibly distinct wires up
        /// close. There is no thickness at which it reads as a hose from both.
        ///
        /// DRAW_POLY draws a filled triangle in world space, which does scale with distance. So
        /// the hose is a ribbon: two triangles per segment, running down the whole length.
        ///
        /// The ribbon is BILLBOARDED. Its width is laid out along the axis perpendicular to
        /// both the hose and the line to the camera, so it always presents its full width to
        /// the viewer and reads as a round tube from every angle. A ribbon with a fixed
        /// orientation vanishes to nothing the moment you look at it edge-on, which on a hose
        /// you walk all the way around is most of the time.
        /// </summary>
        private void Stroke(Vector3[] points)
        {
            if (points == null || points.Length < 2) return;

            var radius = Thickness * 0.5f;
            if (radius < 0.002f) radius = 0.002f;

            Vector3 eye;
            try { eye = GameplayCamera.Position; }
            catch { eye = points[0]; }

            // A stride, so a rope with forty vertices does not cost forty times the polygons.
            // Over three or four metres of hose the curve is smooth at a dozen segments and
            // nobody can tell; the band shading below multiplies whatever this costs by five.
            var stride = 1 + points.Length / MaxSegments;

            var centres = new List<Vector3>();
            var sides = new List<Vector3>();

            for (var i = 0; i < points.Length; i += stride)
            {
                // Always include the very last point, or the hose stops short of the hand.
                var index = i;
                if (i + stride >= points.Length) index = points.Length - 1;

                Vector3 dir;
                if (index == 0) dir = points[1] - points[0];
                else if (index == points.Length - 1) dir = points[index] - points[index - 1];
                else dir = points[Math.Min(index + stride, points.Length - 1)] - points[index - 1];

                if (dir.Length() < 0.00001f) continue;

                var side = Vector3.Cross(dir, eye - points[index]);
                if (side.Length() < 0.00001f) side = Vector3.Cross(dir, Vector3.WorldUp);
                if (side.Length() < 0.00001f) continue;

                side.Normalize();

                centres.Add(points[index]);
                sides.Add(side * radius);

                if (index == points.Length - 1) break;
            }

            if (centres.Count < 2) return;

            // ROUNDNESS IS SHADING, NOT GEOMETRY.
            //
            // The ribbon is one flat strip facing the camera, and a flat strip of one colour
            // looks exactly like what it is: a flat strip. Building an actual tube out of
            // triangles would be six or eight times the polygons for something nobody can see
            // the far side of anyway.
            //
            // So the strip is split lengthways into bands and each is shaded as if it were a
            // cylinder: dark at the rims, lifting through the middle, with a narrow sheen down
            // the centre. That is the whole trick behind every drawn cable in every game, and
            // at this size it is indistinguishable from the real thing.
            for (var b = 0; b < Bands; b++)
            {
                // Across the width, -1 at one rim to +1 at the other.
                var u0 = -1f + 2f * b / Bands;
                var u1 = -1f + 2f * (b + 1) / Bands;
                var mid = (u0 + u1) * 0.5f;

                var colour = Rubber(mid);

                for (var i = 0; i < centres.Count - 1; i++)
                {
                    var a0 = centres[i] + sides[i] * u0;
                    var a1 = centres[i] + sides[i] * u1;
                    var b0 = centres[i + 1] + sides[i + 1] * u0;
                    var b1 = centres[i + 1] + sides[i + 1] * u1;

                    Quad(a0, a1, b0, b1, colour);
                }
            }
        }

        /// <summary>
        /// The colour of the hose at a point across its width, u running -1 rim to +1 rim.
        ///
        /// sqrt(1 - u squared) is the height of a circle at that width -- which, for a cylinder
        /// lit from the viewer's side, is also how square-on its surface is to the light. So it
        /// doubles as the shading term and costs one square root. The power term on top of it
        /// is the sheen: narrow, because a wide one turns rubber into chrome.
        /// </summary>
        private Color Rubber(float u)
        {
            var round = (float)Math.Sqrt(Math.Max(0f, 1f - u * u));

            var tone = 0.40f + 0.60f * round;
            var sheen = (float)Math.Pow(round, 9) * 0.85f;

            var lift = Clamp255(Sheen);

            return Color.FromArgb(255,
                                  Shade(Red, tone, sheen, lift),
                                  Shade(Green, tone, sheen, lift),
                                  Shade(Blue, tone, sheen, lift));
        }

        /// <summary>
        /// One channel, shaded for roundness and then lifted along the centre line.
        ///
        /// THE LIFT IS THE THING THAT WAS KEEPING THE HOSE GREY. It used to be a hardcoded 58,
        /// which is most of a mid-grey added on top of whatever colour was asked for -- so a
        /// hose set to 20,20,23 drew at nearly 70 up its middle and no amount of turning the
        /// colour down would make it black. It is HoseSheen now.
        /// </summary>
        private static int Shade(int channel, float tone, float sheen, int lift)
        {
            return Clamp255((int)(Clamp255(channel) * tone + lift * sheen));
        }

        /// <summary>
        /// One quad of the ribbon, as two triangles -- and each of those drawn both ways round.
        ///
        /// The reversed winding is not waste. A hose you carry around a car is seen from both
        /// sides within a few seconds, and a back-face-culled triangle is simply not there from
        /// behind: the hose would vanish in halves as you walked past it. Four polygons a
        /// segment is nothing next to that.
        /// </summary>
        /// <summary>
        /// Draws the run of points as an actual TUBE -- rings of vertices around the curve with
        /// triangles between them.
        ///
        /// WHY THE POWER LINES LOOK BETTER THAN ANYTHING A SCRIPT DRAWS, and what can be done
        /// about it. Those cables are map geometry: modelled, textured and lit by the people who
        /// built the world, baked into the props. No script can add map geometry. What a script
        /// has is three things, and only three:
        ///
        ///   ADD_ROPE      a real physics rope, flexible, but its thickness is fixed per type
        ///                 in ropedata.xml and NO native changes it. All eight types are tow
        ///                 ropes and winch cables. That is the tiny rope.
        ///   billboards    a strip turned to face the camera. Flat, and the eye knows. That is
        ///                 the flat planes.
        ///   DRAW_POLY     raw triangles in world space. Full control of the shape.
        ///
        /// The first two are what has been tried. This is the third, and it is the only one that
        /// can be round, because it is the only one where the roundness is geometry rather than
        /// a picture of geometry. It will not be lit or textured the way the power lines are --
        /// DRAW_POLY is flat-shaded -- so each face is tinted by how square-on it sits to the
        /// camera, which is what lighting would have done for it anyway on a matt black cable.
        ///
        /// The frame is CARRIED from ring to ring rather than rebuilt from a fixed up-vector.
        /// Rebuilt, the tube spins on its axis wherever the curve passes through vertical, and a
        /// hose that rotates as it hangs is worse than a flat one.
        /// </summary>
        private void Sleeve(Vector3[] points)
        {
            if (points == null || points.Length < 2) return;

            var radius = Thickness * 0.5f;
            if (radius < 0.002f) radius = 0.002f;

            var sides = Sides;
            if (sides < 3) sides = 3;
            if (sides > 16) sides = 16;

            Vector3 eye;
            try { eye = GameplayCamera.Position; }
            catch { eye = points[0]; }

            var tangent = Unit(points[1] - points[0]);

            // Any vector not along the tangent will do to start; the carry keeps it honest
            // from there.
            var seed = Math.Abs(tangent.Z) > 0.9f ? new Vector3(1f, 0f, 0f) : new Vector3(0f, 0f, 1f);

            var normal = Unit(Vector3.Cross(seed, tangent));

            var ring = new Vector3[sides];
            var last = new Vector3[sides];
            var started = false;

            for (var i = 0; i < points.Length; i++)
            {
                Vector3 t;
                if (i == 0) t = Unit(points[1] - points[0]);
                else if (i == points.Length - 1) t = Unit(points[i] - points[i - 1]);
                else t = Unit(points[i + 1] - points[i - 1]);

                // Carried, not rebuilt: the old normal pushed back square to the new tangent.
                normal = Unit(normal - t * Vector3.Dot(normal, t));
                var binormal = Unit(Vector3.Cross(t, normal));

                for (var s = 0; s < sides; s++)
                {
                    var a = (float)(2.0 * Math.PI * s / sides);
                    ring[s] = points[i]
                              + normal * ((float)Math.Cos(a) * radius)
                              + binormal * ((float)Math.Sin(a) * radius);
                }

                if (started)
                {
                    for (var s = 0; s < sides; s++)
                    {
                        var n = (s + 1) % sides;

                        var mid = (last[s] + last[n] + ring[s] + ring[n]) * 0.25f;
                        var out_ = Unit(mid - points[i]);
                        var toEye = Unit(eye - mid);

                        var facing = Vector3.Dot(out_, toEye);

                        // The far side of the tube. Drawing it costs the same as drawing the
                        // near side and is covered by it.
                        if (facing <= 0f) continue;

                        Quad(last[s], last[n], ring[s], ring[n], Curve(facing));
                    }
                }

                Array.Copy(ring, last, sides);
                started = true;
            }
        }

        /// <summary>
        /// A face's colour from how square-on it is to the camera.
        ///
        /// This is the shading the billboard was faking. On real geometry it is honest: the
        /// faces along the silhouette are steeply angled and go dark, the ones facing you are
        /// lit, and the gradient between them is the tube being round rather than a picture of
        /// a tube being round.
        /// </summary>
        private Color Curve(float facing)
        {
            var tone = 0.35f + 0.65f * facing;
            var gloss = (float)Math.Pow(facing, 6) * 0.9f;
            var lift = Clamp255(Sheen);

            return Color.FromArgb(255,
                                  Shade(Red, tone, gloss, lift),
                                  Shade(Green, tone, gloss, lift),
                                  Shade(Blue, tone, gloss, lift));
        }

        private static Vector3 Unit(Vector3 v)
        {
            var len = v.Length();
            return len < 0.0001f ? new Vector3(0f, 0f, 1f) : v / len;
        }

        private static void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color colour)
        {
            try
            {
                World.DrawPolygon(a, b, c, colour);
                World.DrawPolygon(b, d, c, colour);

                World.DrawPolygon(c, b, a, colour);
                World.DrawPolygon(c, d, b, colour);
            }
            catch (Exception ex)
            {
                Log.Once("hose-poly", "Could not draw the hose body: " + ex.Message);
            }
        }

        private static int Clamp255(int v)
        {
            if (v < 0) return 0;
            if (v > 255) return 255;
            return v;
        }

        /// <summary>Reels it in. Safe whether or not anything is out.</summary>
        public void Retract()
        {
            try
            {
                if (_rope != null && _rope.Exists()) _rope.Delete();
            }
            catch (Exception ex)
            {
                Log.Debug("Rope would not delete: " + ex.Message);
            }

            _rope = null;
        }

        /// <summary>
        /// Lets go of the rope textures.
        ///
        /// Only on shutdown. Unloading them while any rope in the game is drawn -- ours or
        /// another mod's, or the game's own tow truck -- takes the texture out from under it.
        /// </summary>
        public void Release()
        {
            Retract();

            if (!_texturesAsked) return;
            _texturesAsked = false;

            try { Function.Call(Hash.ROPE_UNLOAD_TEXTURES); }
            catch { /* nothing worth reporting on the way out */ }
        }

        private static float Clamp(float v, float lo, float hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }
}
