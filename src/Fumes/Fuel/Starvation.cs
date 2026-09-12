using System;
using System.Globalization;
using GTA;
using GTA.Math;
using GTA.Native;
using Fumes.Core;

namespace Fumes.Fuel
{
    /// <summary>
    /// What a car does on the last of its fuel, and what it does on none.
    ///
    /// Only ever applied to the vehicle the player is actually in. Reaching into ambient
    /// traffic to switch engines off is a per-frame native call per car for something nobody
    /// is looking at, and it is how a fuel mod becomes a stutter.
    /// </summary>
    internal sealed class Starvation
    {
        private readonly Settings _cfg;
        private readonly Random _rng = new Random();

        /// <summary>
        /// Everything here lives in "core", which every install has loaded. veh_backfire is
        /// the game's own exhaust pop. The smoke is LOOPED and a LADDER: a low trail out of
        /// each exhaust that stays lit for as long as the tank is in reserve, thickening
        /// toward empty -- a puff every second read as a geyser, and steam shot upward is
        /// not smoke. Tried in order the first time it is wanted and the first effect the
        /// game accepts kept: there is no list of particle names on disk to check against,
        /// the game refuses a wrong one in silence, and the log says which rung it took.
        /// </summary>
        private const string PtfxAsset = "core";
        private const string Backfire = "veh_backfire";

        private static readonly string[] SmokeLadder = { "veh_exhaust", "ent_amb_smoke_general", "ent_sht_steam" };

        /// <summary>
        /// Scale at the reserve mark, and in the last litre, per rung.
        ///
        /// SMALL, AND THE FIRST GO WAS NOT. Two of these are ambient effects built to drift
        /// across a whole street, so at the scale that looked right in a still they trailed
        /// the length of a block behind the car. A wisp off the pipe is what a starving
        /// engine gives you. [Engine] LowFuelSmokeScale multiplies them for anybody who
        /// wants more, or less.
        /// </summary>
        private static readonly float[] SmokeAtMark = { 0.30f, 0.07f, 0.04f };
        private static readonly float[] SmokeAtEmpty = { 0.70f, 0.18f, 0.09f };

        /// <summary>The rung of SmokeLadder the game accepted, or -1 while unknown.</summary>
        private int _smoke = -1;

        /// <summary>The looped effects, one per exhaust, and the vehicle they are on.</summary>
        private readonly int[] _plumes = new int[2];
        private int _plumeVehicle;

        private static readonly string[] ExhaustBones = { "exhaust", "exhaust_2", "exhaust_3", "exhaust_4" };

        /// <summary>Which vehicle these timers belong to, so a new car starts clean.</summary>
        private int _handle;

        /// <summary>When the next cough is due.</summary>
        private int _nextCough;

        /// <summary>Whether the reserve warning has already been given for this tankful.</summary>
        private bool _warned;

        /// <summary>Whether the "you are out" line has been said for this tankful.</summary>
        private bool _toldEmpty;

        public Starvation(Settings cfg)
        {
            _cfg = cfg;
        }

        /// <summary>True while the engine is being held off for want of fuel.</summary>
        public bool Stalled { get; private set; }

        public void Update(Vehicle v, Tank tank)
        {
            if (v == null || !v.Exists() || tank == null)
            {
                Stalled = false;
                return;
            }

            try
            {
                Retarget(v);

                if (tank.Empty && _cfg.StallWhenEmpty) { Dry(v, tank); return; }

                Stalled = false;
                Unground();

                if (tank.Litres <= SputterAt(tank))
                {
                    // WRITTEN ONCE PER EPISODE, because four rounds of reading the code have
                    // not explained a report of spluttering at half a tank. Everything here is
                    // plain arithmetic on two numbers, the gauge divides the same two, and both
                    // are handed the same vehicle -- so either the tank really is low and the
                    // gauge is lying, or this line never fires and the noise is something else.
                    //
                    // One line in the log tells those apart. Guessing has not.
                    if (!_coughLogged)
                    {
                        _coughLogged = true;

                        Log.Info("Spluttering " + v.LocalizedName + ": " +
                                 tank.Litres.ToString("0.00", CultureInfo.InvariantCulture) + " / " +
                                 tank.Capacity.ToString("0.0", CultureInfo.InvariantCulture) + " L = " +
                                 (tank.Fraction * 100f).ToString("0", CultureInfo.InvariantCulture) +
                                 "%, threshold " +
                                 SputterAt(tank).ToString("0.00", CultureInfo.InvariantCulture) + " L.");
                    }

                    Sparks(v, 1f);
                    return;
                }

                // IN RESERVE: the effect starts here, with the chime, and not in the last
                // litre only. The last litre is where it gets dense; from the reserve mark
                // down to there it is an occasional pop that gets less occasional. Depth is
                // how far into the reserve the tank is, nought at the mark and one at the
                // dense zone.
                var reserve = tank.Capacity * _cfg.ReserveFraction;

                if (tank.Litres <= reserve)
                {
                    _toldEmpty = false;

                    if (!_warned)
                    {
                        _warned = true;

                        Say(tank.Electric
                                ? "~y~Low charge.~s~ Find somewhere to plug in."
                                : "~y~Low fuel.~s~ Next station is on the map.");
                        Beep();

                        // The map already has the blips; this puts a line on it. Main owns
                        // the station list, so it owns the route -- see Reserve.
                        if (!tank.Electric && Reserve != null) Reserve();
                    }

                    var span = reserve - SputterAt(tank);
                    var depth = span > 0.01f ? 1f - (tank.Litres - SputterAt(tank)) / span : 1f;

                    Sparks(v, depth);
                    Cutout(v, depth);
                    return;
                }

                // Above the reserve mark: everything resets, including the warnings, so a
                // tank filled and run down again warns again.
                Quench();
                Restore();
                _nextCut = 0;
                _nextCough = 0;
                _coughLogged = false;
                _toldEmpty = false;
                _warned = false;
            }
            catch (Exception ex)
            {
                Log.Once("starve-throw", "Running-dry handling failed: " + ex.Message +
                                         " - the engine will not be cut for want of fuel.");
                Stalled = false;
            }
        }

        /// <summary>
        /// The litres at which it starts catching and dropping.
        ///
        /// A SHARE OF THE TANK, not a fixed number of litres, because a fixed number is a
        /// different warning on every vehicle: 0.6 L is 0.9 per cent of a saloon and 3.8 of a
        /// motorbike, so the smaller the tank the less notice you get -- which is backwards,
        /// since the small tank is the one that empties soonest.
        ///
        /// CAPPED, because a share alone is worse the other way. Ten per cent of a saloon is
        /// six litres, and spluttering for the last six litres is spluttering for seventy
        /// kilometres. The cap is the honest quantity: however big the tank, you get about two
        /// litres of warning that it is nearly done.
        ///
        /// And floored by the old absolute, so a five-litre moped keeps a warning long enough
        /// to notice at all.
        /// </summary>
        private float SputterAt(Tank tank)
        {
            var share = tank.Capacity * _cfg.SputterFraction;

            if (share > _cfg.SputterMaxLitres) share = _cfg.SputterMaxLitres;

            return share > _cfg.SputterLitres ? share : _cfg.SputterLitres;
        }

        private bool _coughLogged;

        /// <summary>A different car means somebody else's timers. Drop them.</summary>
        private void Retarget(Vehicle v)
        {
            if (v.Handle == _handle) return;

            // The vehicle he got out of keeps nothing of ours.
            Unground();
            Quench();
            Restore();
            _nextCut = 0;

            _handle = v.Handle;
            _nextCough = 0;
            _warned = _toldEmpty = _coughLogged = false;
            Stalled = false;
        }

        /// <summary>
        /// The reserve: smoke trails out of the exhausts, or a backfire now and then, and the
        /// engine is not touched.
        ///
        /// IT USED TO CUT THE ENGINE AND BRING IT BACK, for the feel of one catching and
        /// dropping -- and every version of that, gentle or instant, locked the rear wheels
        /// and threw the car into reverse for a moment, because SET_VEHICLE_ENGINE_ON on a
        /// moving car is a gearbox event before it is an audio one. So the driving is left
        /// alone entirely, and the only thing that ever stops the car is Dry, when it is
        /// empty. Depth is how far into the reserve the tank is: nought at the mark, one in
        /// the last litre.
        /// </summary>
        private void Sparks(Vehicle v, float depth)
        {
            if (depth < 0f) depth = 0f;
            if (depth > 1f) depth = 1f;

            if (_cfg.LowFuelEffect == LowFuelEffect.None || !v.IsEngineRunning)
            {
                Quench();
                return;
            }

            if (_cfg.LowFuelEffect == LowFuelEffect.Smoke)
            {
                Plume(v, depth);
                return;
            }

            Quench();

            // The backfire: every five or six seconds at the mark, every one to three in the
            // last litre, with a little jitter so it never reads as a metronome.
            var now = Game.GameTime;
            if (now < _nextCough) return;

            var gap = 5500f - depth * 4600f;
            _nextCough = now + (int)gap + _rng.Next((int)(gap * 0.7f));

            try
            {
                if (!Function.Call<bool>(Hash.HAS_NAMED_PTFX_ASSET_LOADED, PtfxAsset))
                {
                    Function.Call(Hash.REQUEST_NAMED_PTFX_ASSET, PtfxAsset);
                    return;
                }

                var popped = 0;

                foreach (var bone in ExhaustBones)
                {
                    var index = Function.Call<int>(Hash.GET_ENTITY_BONE_INDEX_BY_NAME, v.Handle, bone);
                    if (index < 0) continue;

                    var world = Function.Call<Vector3>(Hash.GET_WORLD_POSITION_OF_ENTITY_BONE, v.Handle, index);
                    var off = Function.Call<Vector3>(Hash.GET_OFFSET_FROM_ENTITY_GIVEN_WORLD_COORDS, v.Handle,
                                                     world.X, world.Y, world.Z);

                    Function.Call(Hash.USE_PARTICLE_FX_ASSET, PtfxAsset);
                    var ok = Function.Call<bool>(Hash.START_PARTICLE_FX_NON_LOOPED_ON_ENTITY, Backfire, v.Handle,
                                                 off.X, off.Y, off.Z, 0f, 0f, 0f, 1f, false, false, false);
                    Log.Once("fx-backfire" + (ok ? "-ok" : "-fail"),
                             (ok ? "Low-fuel effect " : "Low-fuel effect REFUSED: ") + PtfxAsset + "/" + Backfire +
                             " at " + bone + " of " + v.LocalizedName + ".");

                    if (ok && ++popped >= 2) break;
                }
            }
            catch (Exception ex)
            {
                Log.Once("sparks-fail", "Could not backfire: " + ex.Message);
            }
        }

        /// <summary>
        /// The smoke: looped on each exhaust bone, made once per vehicle, scaled every frame
        /// by how deep into the reserve the tank is.
        /// </summary>
        private void Plume(Vehicle v, float depth)
        {
            try
            {
                if (_plumeVehicle != v.Handle)
                {
                    Quench();

                    if (!Function.Call<bool>(Hash.HAS_NAMED_PTFX_ASSET_LOADED, PtfxAsset))
                    {
                        Function.Call(Hash.REQUEST_NAMED_PTFX_ASSET, PtfxAsset);
                        return;
                    }

                    var lit = 0;

                    foreach (var bone in ExhaustBones)
                    {
                        var index = Function.Call<int>(Hash.GET_ENTITY_BONE_INDEX_BY_NAME, v.Handle, bone);
                        if (index < 0) continue;

                        var handle = Light(v, index, bone);
                        if (handle == 0) continue;

                        _plumes[lit++] = handle;
                        if (lit >= _plumes.Length) break;
                    }

                    // No exhaust bone -- some add-ons -- so out of the back, low down.
                    if (lit == 0)
                    {
                        var handle = Light(v, -1, "no exhaust bone");
                        if (handle != 0) _plumes[0] = handle;
                    }

                    _plumeVehicle = v.Handle;
                }

                if (_smoke < 0) return;

                var scale = (SmokeAtMark[_smoke] + (SmokeAtEmpty[_smoke] - SmokeAtMark[_smoke]) * depth)
                            * _cfg.LowFuelSmokeScale;

                foreach (var handle in _plumes)
                {
                    if (handle != 0) Function.Call(Hash.SET_PARTICLE_FX_LOOPED_SCALE, handle, scale);
                }
            }
            catch (Exception ex)
            {
                Log.Once("plume-fail", "Could not smoke the exhaust: " + ex.Message);
            }
        }

        /// <summary>
        /// One looped effect at one bone, walking the ladder if the rung is not yet known.
        /// Returns the handle, or nought when nothing was accepted.
        /// </summary>
        private int Light(Vehicle v, int bone, string where)
        {
            var first = _smoke >= 0 ? _smoke : 0;
            var last = _smoke >= 0 ? _smoke : SmokeLadder.Length - 1;

            for (var i = first; i <= last; i++)
            {
                Function.Call(Hash.USE_PARTICLE_FX_ASSET, PtfxAsset);

                var handle = bone >= 0
                    ? Function.Call<int>(Hash.START_PARTICLE_FX_LOOPED_ON_ENTITY_BONE, SmokeLadder[i], v.Handle,
                                         0f, 0f, 0f, 0f, 0f, 0f, bone, SmokeAtMark[i], false, false, false)
                    : Function.Call<int>(Hash.START_PARTICLE_FX_LOOPED_ON_ENTITY, SmokeLadder[i], v.Handle,
                                         0f, -2.2f, 0.2f, 0f, 0f, 0f, SmokeAtMark[i], false, false, false);

                if (handle == 0)
                {
                    if (_smoke < 0)
                    {
                        Log.Info("Low-fuel smoke: " + PtfxAsset + "/" + SmokeLadder[i] + " refused; trying the next.");
                    }
                    continue;
                }

                if (_smoke < 0)
                {
                    _smoke = i;
                    Log.Info("Low-fuel smoke: " + PtfxAsset + "/" + SmokeLadder[i] + " at " + where + " of " +
                             v.LocalizedName + ".");
                }

                return handle;
            }

            Log.Once("fx-smoke-none", "Low-fuel smoke: none of " + SmokeLadder.Length + " effects was accepted.");
            return 0;
        }

        /// <summary>When the current stumble ends, and when the next one is due.</summary>
        private int _cutUntil;
        private int _nextCut;

        /// <summary>The vehicle whose torque this took away, so it can always be given back.</summary>
        private int _cutVehicle;

        /// <summary>
        /// A FUEL STARVATION STUMBLE, and not an ignition switch.
        ///
        /// The distinction is the whole of it. SET_VEHICLE_ENGINE_ON on a moving car is a
        /// GEARBOX event before it is an audio one -- the rear wheels lock and it lurches into
        /// reverse -- which is why every previous attempt at a low-fuel cough had to be taken
        /// out again. A real engine starved of fuel does not switch off: it stops making
        /// power for a quarter of a second and then picks up.
        ///
        /// So that is what this does. The torque goes to nothing, the throttle is ignored for
        /// as long as it lasts, and the exhaust pops. Nothing touches the engine's state, the
        /// gearbox or the brakes, so there is nothing here that can lock a wheel.
        ///
        /// They get more frequent the emptier it is: about one every twenty seconds at the
        /// reserve mark, one every four or five in the last litre.
        /// </summary>
        private void Cutout(Vehicle v, float depth)
        {
            if (!_cfg.LowFuelCutouts || !v.IsEngineRunning)
            {
                Restore();
                return;
            }

            var now = Game.GameTime;

            if (now < _cutUntil)
            {
                Starve(v);
                return;
            }

            if (_cutVehicle != 0) Restore();

            if (_nextCut == 0)
            {
                // Not the instant the mark is crossed: the chime has only just gone.
                _nextCut = now + 6000 + _rng.Next(6000);
                return;
            }

            if (now < _nextCut) return;

            var gap = 20000f - depth * 15500f;
            _cutUntil = now + (int)(_cfg.LowFuelCutoutSeconds * 1000f);
            _nextCut = _cutUntil + (int)gap + _rng.Next((int)(gap * 0.6f));

            Pop(v);
            Starve(v);
        }

        /// <summary>Holds the engine's power at nothing for this frame of the stumble.</summary>
        private void Starve(Vehicle v)
        {
            try
            {
                // The player's foot, ignored. On its own this is already most of the feel,
                // and unlike anything physical it cannot go wrong.
                Game.DisableControlThisFrame(Control.VehicleAccelerate);

                // And the engine itself makes none, so a car already rolling loses drive
                // rather than merely stopping gaining it. A MULTIPLIER, not a state: the
                // gearbox is not told anything.
                v.EngineTorqueMultiplier = 0.01f;
                _cutVehicle = v.Handle;
            }
            catch (Exception ex)
            {
                Log.Once("cutout-fail", "Could not starve the engine: " + ex.Message +
                                        " - low fuel will not stumble.");
                _cutVehicle = 0;
            }
        }

        /// <summary>
        /// Gives the torque back. Called on every way out, and safe when nothing was taken --
        /// a multiplier left at nothing is a car that will not move again.
        /// </summary>
        private void Restore()
        {
            _cutUntil = 0;

            if (_cutVehicle == 0) return;

            var handle = _cutVehicle;
            _cutVehicle = 0;

            try
            {
                var v = Entity.FromHandle(handle) as Vehicle;
                if (v != null && v.Exists()) v.EngineTorqueMultiplier = 1f;
            }
            catch (Exception ex)
            {
                Log.Once("restore-fail", "Could not give the engine its torque back: " + ex.Message);
            }
        }

        /// <summary>One backfire out of the exhausts, for the stumble.</summary>
        private void Pop(Vehicle v)
        {
            try
            {
                if (!Function.Call<bool>(Hash.HAS_NAMED_PTFX_ASSET_LOADED, PtfxAsset))
                {
                    Function.Call(Hash.REQUEST_NAMED_PTFX_ASSET, PtfxAsset);
                    return;
                }

                var popped = 0;

                foreach (var bone in ExhaustBones)
                {
                    var index = Function.Call<int>(Hash.GET_ENTITY_BONE_INDEX_BY_NAME, v.Handle, bone);
                    if (index < 0) continue;

                    var world = Function.Call<Vector3>(Hash.GET_WORLD_POSITION_OF_ENTITY_BONE, v.Handle, index);
                    var off = Function.Call<Vector3>(Hash.GET_OFFSET_FROM_ENTITY_GIVEN_WORLD_COORDS, v.Handle,
                                                     world.X, world.Y, world.Z);

                    Function.Call(Hash.USE_PARTICLE_FX_ASSET, PtfxAsset);
                    var ok = Function.Call<bool>(Hash.START_PARTICLE_FX_NON_LOOPED_ON_ENTITY, Backfire, v.Handle,
                                                 off.X, off.Y, off.Z, 0f, 0f, 0f, 1f, false, false, false);
                    Log.Once("fx-backfire" + (ok ? "-ok" : "-fail"),
                             (ok ? "Low-fuel backfire " : "Low-fuel backfire REFUSED: ") + PtfxAsset + "/" +
                             Backfire + " at " + bone + " of " + v.LocalizedName + ".");

                    if (ok && ++popped >= 2) break;
                }
            }
            catch (Exception ex)
            {
                Log.Once("pop-fail", "Could not backfire: " + ex.Message);
            }
        }

        /// <summary>Puts the smoke out. Safe to call when there is none.</summary>
        private void Quench()
        {
            for (var i = 0; i < _plumes.Length; i++)
            {
                if (_plumes[i] == 0) continue;

                try
                {
                    if (Function.Call<bool>(Hash.DOES_PARTICLE_FX_LOOPED_EXIST, _plumes[i]))
                    {
                        Function.Call(Hash.STOP_PARTICLE_FX_LOOPED, _plumes[i], false);
                    }
                }
                catch
                {
                    // It is gone either way.
                }

                _plumes[i] = 0;
            }

            _plumeVehicle = 0;
        }

        /// <summary>For the script going down: nothing of ours left burning on a car.</summary>
        public void Quiet()
        {
            Quench();
            Unground();
            Restore();
        }

        /// <summary>
        /// Called once when the tank crosses into reserve, for whoever wants to do something
        /// about it. Main routes the GPS to the nearest station with it.
        /// </summary>
        public Action Reserve;

        /// <summary>
        /// Nothing left.
        ///
        /// The engine is held off every frame rather than switched off once, because the game
        /// starts it again on its own the moment the player touches the throttle. Pressing the
        /// throttle DOES do something though: it turns the starter, for as long as somebody
        /// would keep trying before believing the gauge.
        /// </summary>
        private void Dry(Vehicle v, Tank tank)
        {
            Stalled = true;

            // A dead engine does not smoke, and does not need its torque taken away.
            Quench();
            Restore();

            // HELD OFF, AND NOTHING ELSE. This used to turn the starter over for a second or
            // two whenever the throttle was pressed, for the feel of somebody trying -- but
            // SET_VEHICLE_ENGINE_ON(true, instantly=false) is not a starter, it is a start:
            // the engine caught at the end of the crank, ran until the next frame cut it, and
            // the car crept forward or back on that moment of power while the on-off-on
            // backfired sparks out of the exhaust. Reported as "sparks from the exhaust and
            // it starts moving" by GrandHaven and, earlier, AliG_15. A tank with nothing in
            // it now does exactly what they asked for: shuts down and stays shut down.
            Engine(v, false, true);

            // A HOVER BIKE DOES NOT RUN ON ITS ENGINE. The Oppressor Mk II kept flying with
            // the engine held off, because its flight is a thruster the engine flag does not
            // touch. Hover flight is switched off for as long as the tank is dry, and given
            // back the moment it is not. Reported by AliG_15.
            Ground(v);

            if (!_toldEmpty)
            {
                _toldEmpty = true;
                _warned = true;
                Say(tank.Electric ? "~r~Flat battery.~s~" : "~r~Out of fuel.~s~");
                Beep();
            }
        }

        /// <summary>The vehicle whose hover flight this took away, so it can be given back.</summary>
        private int _grounded;

        private void Ground(Vehicle v)
        {
            if (_grounded == v.Handle) return;

            Unground();

            try
            {
                Function.Call(Hash.SET_DISABLE_HOVER_MODE_FLIGHT, v.Handle, true);
                Function.Call(Hash.SET_SPECIAL_FLIGHT_MODE_ALLOWED, v.Handle, false);
                _grounded = v.Handle;
            }
            catch (Exception ex)
            {
                Log.Once("ground-fail", "Could not take hover flight away from a dry vehicle: " + ex.Message);
            }
        }

        /// <summary>Gives hover flight back, if it was taken. Safe to call when it was not.</summary>
        private void Unground()
        {
            if (_grounded == 0) return;

            var handle = _grounded;
            _grounded = 0;

            try
            {
                var v = Entity.FromHandle(handle) as Vehicle;
                if (v == null || !v.Exists()) return;

                Function.Call(Hash.SET_DISABLE_HOVER_MODE_FLIGHT, v.Handle, false);
                Function.Call(Hash.SET_SPECIAL_FLIGHT_MODE_ALLOWED, v.Handle, true);
            }
            catch (Exception ex)
            {
                Log.Once("unground-fail", "Could not give hover flight back: " + ex.Message);
            }
        }

        private static bool IsPlayerDriving(Vehicle v)
        {
            try
            {
                var me = Game.Player.Character;
                return me != null && me.Exists() && me.CurrentVehicle == v;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// SET_VEHICLE_ENGINE_ON(vehicle, on, instantly, disableAutoStart).
        ///
        /// disableAutoStart is the argument that matters here and it is easy to miss: without
        /// it the game helpfully starts the engine again a frame later and the car runs on an
        /// empty tank forever.
        /// </summary>
        private static void Engine(Vehicle v, bool on, bool instantly)
        {
            Function.Call(Hash.SET_VEHICLE_ENGINE_ON, v.Handle, on, instantly, true);
        }

        private static void Say(string text)
        {
            try { GTA.UI.Notification.Show(Lang.T(text), false); }
            catch { /* a missing ticker is not worth a crash */ }
        }

        private static void Beep()
        {
            try
            {
                Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "Beep_Red",
                              "DLC_HEIST_HACKING_SNAKE_SOUNDS", true);
            }
            catch
            {
                // Silence is an acceptable outcome for a warning noise.
            }
        }
    }
}
