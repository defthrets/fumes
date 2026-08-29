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

            if (_mode == HoseMode.Line) { DrawLine(from, to); return; }

            if (!Live && !Spawn(from, to))
            {
                // Rope could not be had. In Auto that is a decision, not a failure.
                if (_mode == HoseMode.Auto && _gaveUpOnRopeAt != 0 &&
                    Game.GameTime - _gaveUpOnRopeAt > 3000)
                {
                    _mode = HoseMode.Line;
                    Log.Warn("Rope hose is not coming up after three seconds - drawing the hose instead. " +
                             "Set [Nozzle] Hose = Line to make that permanent, or Rope to keep waiting.");
                }

                if (_mode != HoseMode.Rope) DrawLine(from, to);
                return;
            }

            Pin(from, to);
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
                if (_mode == HoseMode.Auto) _mode = HoseMode.Line;
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
                if (_mode == HoseMode.Auto) _mode = HoseMode.Line;
            }
        }

        /// <summary>
        /// The drawn hose: a hanging curve, thickened by drawing it more than once.
        ///
        /// DRAW_LINE is one pixel wide at any distance, which reads as a wire and not a hose,
        /// so the same curve is drawn three times a couple of centimetres apart across its own
        /// width. Three is enough to look like a tube and cheap enough not to matter.
        /// </summary>
        private void DrawLine(Vector3 from, Vector3 to)
        {
            const int segments = 16;

            try
            {
                var span = from.DistanceTo(to);
                if (span < 0.05f) return;

                // How far the middle hangs below the straight line. Comes out of the same
                // slack figure the rope uses, so both hoses have the same shape.
                var sag = Clamp(span * (_cfg.HoseSag - 1f) * 1.6f, 0.08f, 1.4f);

                var across = Vector3.Cross(to - from, Vector3.WorldUp);
                if (across.Length() > 0.001f) across.Normalize();
                across *= 0.022f;

                var hose = Color.FromArgb(235, 24, 24, 26);

                for (var pass = -1; pass <= 1; pass++)
                {
                    var shift = across * pass;
                    var previous = from + shift;

                    for (var i = 1; i <= segments; i++)
                    {
                        var t = (float)i / segments;
                        var point = Vector3.Lerp(from, to, t) + shift;

                        // A parabola, not a real catenary. Over three metres of hose the two
                        // are the same picture and one of them needs a cosh.
                        point.Z -= 4f * sag * t * (1f - t);

                        Function.Call(Hash.DRAW_LINE,
                                      previous.X, previous.Y, previous.Z,
                                      point.X, point.Y, point.Z,
                                      hose.R, hose.G, hose.B, hose.A);

                        previous = point;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Once("hose-draw", "Could not draw the hose: " + ex.Message);
            }
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
