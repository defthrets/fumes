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
        /// Both effects live in "core", which every install has loaded. veh_backfire is the
        /// game's own exhaust pop; ent_sht_electrical_box is a burst of electrical sparks,
        /// nothing to do with vehicles at all -- there for anybody who wants the warning to
        /// look like nothing the game does on its own.
        /// </summary>
        private const string PtfxAsset = "core";
        private const string Backfire = "veh_backfire";
        private const string ElectricSparks = "ent_sht_electrical_box";

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

                    Sparks(v);
                    return;
                }

                // Back above the sputter line: everything resets, including the warnings, so
                // a tank filled and run down again warns again.
                _nextCough = 0;
                _coughLogged = false;
                _toldEmpty = false;

                if (tank.Fraction > _cfg.ReserveFraction) { _warned = false; return; }

                if (_warned) return;
                _warned = true;

                Say(tank.Electric
                        ? "~y~Low charge.~s~ Find somewhere to plug in."
                        : "~y~Low fuel.~s~ Next station is on the map.");
                Beep();
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

            _handle = v.Handle;
            _nextCough = 0;
            _warned = _toldEmpty = _coughLogged = false;
            Stalled = false;
        }

        /// <summary>
        /// The last half-litre: the exhaust backfires, and the engine is not touched.
        ///
        /// IT USED TO CUT THE ENGINE AND BRING IT BACK, for the feel of one catching and
        /// dropping -- and every version of that, gentle or instant, locked the rear wheels
        /// and threw the car into reverse for a moment, because SET_VEHICLE_ENGINE_ON on a
        /// moving car is a gearbox event before it is an audio one. So the driving is left
        /// alone entirely: sparks out of the exhaust every second or two say the tank is on
        /// its last litre, and the only thing that ever stops the car is Dry, when it is empty.
        /// </summary>
        private void Sparks(Vehicle v)
        {
            var now = Game.GameTime;
            if (now < _nextCough) return;

            _nextCough = now + 900 + _rng.Next(2200);

            if (!v.IsEngineRunning) return;
            if (_cfg.LowFuelEffect == LowFuelEffect.None) return;

            var fx = _cfg.LowFuelEffect == LowFuelEffect.Sparks ? ElectricSparks : Backfire;

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
                    var ok = Function.Call<bool>(Hash.START_PARTICLE_FX_NON_LOOPED_ON_ENTITY, fx, v.Handle,
                                                 off.X, off.Y, off.Z, 0f, 0f, 0f, 1f, false, false, false);

                    // SAID ONCE EITHER WAY. A wrong effect name fails in silence, and "they
                    // aren't showing" cannot be told from "never fired" without this.
                    Log.Once("fx-" + fx + (ok ? "-ok" : "-fail"),
                             (ok ? "Low-fuel effect " : "Low-fuel effect REFUSED: ") + PtfxAsset + "/" + fx +
                             " at " + bone + " of " + v.LocalizedName + ".");

                    if (++popped >= 2) break;
                }

                if (popped == 0)
                {
                    // No exhaust bone -- some add-ons -- so out of the back, low down.
                    Function.Call(Hash.USE_PARTICLE_FX_ASSET, PtfxAsset);
                    var ok = Function.Call<bool>(Hash.START_PARTICLE_FX_NON_LOOPED_ON_ENTITY, fx, v.Handle,
                                                 0f, -2.2f, 0.2f, 0f, 0f, 0f, 1f, false, false, false);
                    Log.Once("fx-" + fx + "-nobone",
                             (ok ? "Low-fuel effect " : "Low-fuel effect REFUSED: ") + PtfxAsset + "/" + fx +
                             " behind " + v.LocalizedName + ", which has no exhaust bone.");
                }
            }
            catch (Exception ex)
            {
                Log.Once("sparks-fail", "Could not backfire: " + ex.Message);
            }
        }

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
