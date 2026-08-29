using System;
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

        /// <summary>Which way this instance settled, once it has had to decide.</summary>
        private HoseMode _mode;

        public Hose(Settings cfg)
        {
            _cfg = cfg;
            _mode = cfg.Hose;
        }

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
        public void Update(Vector3 from, Vector3 to)
        {
            if (_mode == HoseMode.None) return;

            if (_mode == HoseMode.Line) { DrawCatenary(from, to); return; }

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
            if (!Ready())
            {
                if (_gaveUpOnRopeAt == 0) _gaveUpOnRopeAt = Game.GameTime;
                return false;
            }

            try
            {
                var span = from.DistanceTo(to);
                var initial = Clamp(span * _cfg.HoseSag, 1.5f, _cfg.HoseMaxMetres);

                // ADD_ROPE(x, y, z, rotX, rotY, rotZ, maxLength, ropeType, initLength,
                //          minLength, windingSpeed, p11, p12, rigid, p14, breakWhenShot, unkPtr)
                //
                // maxLength is given slack over HoseMaxMetres on purpose: HoseMaxMetres is the
                // gameplay leash, enforced by Refuel, and a rope whose physical maximum is the
                // same number goes taut and starts fighting the pin a moment before the leash
                // ever fires.
                var handle = Function.Call<int>(Hash.ADD_ROPE,
                    from.X, from.Y, from.Z,
                    0f, 0f, 0f,
                    _cfg.HoseMaxMetres * 1.6f,
                    _cfg.HoseRopeType,
                    initial,
                    0.5f,
                    1f,
                    false, false,
                    false,
                    1f,
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

                Log.Debug("Hose out: rope " + handle + ", " + _rope.VertexCount + " vertices.");
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
                _rope.Length = Clamp(span * _cfg.HoseSag, 1.2f, _cfg.HoseMaxMetres * 1.5f);

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
        private void DrawCatenary(Vector3 from, Vector3 to)
        {
            const int segments = 16;

            try
            {
                var span = from.DistanceTo(to);
                if (span < 0.05f) return;

                // Slack from the same figure the rope uses, so both hoses hang alike.
                var sag = Clamp(span * (_cfg.HoseSag - 1f) * 1.6f, 0.08f, 1.4f);

                var points = new Vector3[segments + 1];
                for (var i = 0; i <= segments; i++)
                {
                    var t = (float)i / segments;
                    var point = Vector3.Lerp(from, to, t);
                    point.Z -= 4f * sag * t * (1f - t);
                    points[i] = point;
                }

                Stroke(points);
            }
            catch (Exception ex)
            {
                Log.Once("hose-draw", "Could not draw the hose: " + ex.Message);
            }
        }

        /// <summary>
        /// Draws a run of points as something that reads as a TUBE rather than a wire.
        ///
        /// DRAW_LINE is one pixel wide at any distance, so a single pass looks like fishing
        /// line however dark it is. Each segment is therefore drawn five times: once down the
        /// middle and once at each of four offsets around it, forming a cross-section. Four
        /// rather than two because two only look thick from one side, and the player walks all
        /// the way round this thing.
        /// </summary>
        private void Stroke(Vector3[] points)
        {
            if (points == null || points.Length < 2) return;

            var colour = Color.FromArgb(240,
                                        Clamp255(_cfg.HoseRed),
                                        Clamp255(_cfg.HoseGreen),
                                        Clamp255(_cfg.HoseBlue));

            var radius = _cfg.HoseThickness * 0.5f;

            for (var i = 0; i < points.Length - 1; i++)
            {
                var a = points[i];
                var b = points[i + 1];

                var dir = b - a;
                if (dir.Length() < 0.0005f) continue;

                var side = Vector3.Cross(dir, Vector3.WorldUp);
                if (side.Length() < 0.0005f) side = Vector3.Cross(dir, Vector3.RelativeFront);
                if (side.Length() < 0.0005f) continue;

                side.Normalize();
                side *= radius;

                var up = Vector3.Cross(dir, side);
                if (up.Length() < 0.0005f) continue;

                up.Normalize();
                up *= radius;

                Segment(a, b, colour);
                Segment(a + side, b + side, colour);
                Segment(a - side, b - side, colour);
                Segment(a + up, b + up, colour);
                Segment(a - up, b - up, colour);
            }
        }

        private static void Segment(Vector3 a, Vector3 b, Color colour)
        {
            Function.Call(Hash.DRAW_LINE, a.X, a.Y, a.Z, b.X, b.Y, b.Z,
                          colour.R, colour.G, colour.B, colour.A);
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
