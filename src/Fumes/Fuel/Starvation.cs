using System;
using System.Globalization;
using GTA;
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

        /// <summary>Which vehicle these timers belong to, so a new car starts clean.</summary>
        private int _handle;

        /// <summary>While the game time is under this, the engine is deliberately dead.</summary>
        private int _coughUntil;

        /// <summary>When the next cough is due.</summary>
        private int _nextCough;

        /// <summary>Whether the engine being off right now is our doing. See Cough.</summary>
        private bool _cutByUs;

        /// <summary>While under this, the starter is turning over on an empty tank.</summary>
        private int _crankUntil;

        /// <summary>The starter will not engage again before this.</summary>
        private int _crankAgainAt;

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

                    Cough(v);
                    return;
                }

                // Back above the sputter line: everything resets, including the warnings, so
                // a tank filled and run down again warns again.
                _coughUntil = 0;
                _nextCough = 0;
                _coughLogged = false;
                _cutByUs = false;
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

            _handle = v.Handle;
            _coughUntil = _nextCough = _crankUntil = _crankAgainAt = 0;
            _warned = _toldEmpty = _cutByUs = _coughLogged = false;
            Stalled = false;
        }

        /// <summary>
        /// The last half-litre: an engine that keeps catching and dropping.
        ///
        /// The cut is NOT instant. SET_VEHICLE_ENGINE_ON with instantly=false lets the engine
        /// die down through its own audio, which is the whole effect -- instant off is a car
        /// that switches off, and this is a car that is running out.
        /// </summary>
        private void Cough(Vehicle v)
        {
            var now = Game.GameTime;

            if (now < _coughUntil)
            {
                Engine(v, false, false);
                return;
            }

            if (_cutByUs)
            {
                // We put it out, so we put it back -- and ONLY then. The earlier version of
                // this restarted any engine that was off between coughs, which meant a car
                // parked at the kerb on its last half-litre started itself, ran the tank out
                // and would not stay switched off. Never start an engine you did not stop.
                _cutByUs = false;
                Engine(v, true, false);
                return;
            }

            if (now < _nextCough) return;
            if (!v.IsEngineRunning) return;

            _coughUntil = now + 260 + _rng.Next(320);
            _nextCough = _coughUntil + 700 + _rng.Next(1800);
            _cutByUs = true;
            Engine(v, false, false);
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

            var now = Game.GameTime;

            if (now < _crankUntil)
            {
                // Turning over, and that is all it is going to do.
                Engine(v, true, false);
                return;
            }

            Engine(v, false, true);

            if (!_toldEmpty)
            {
                _toldEmpty = true;
                _warned = true;
                Say(tank.Electric ? "~r~Flat battery.~s~" : "~r~Out of fuel.~s~");
                Beep();
            }

            if (!IsPlayerDriving(v)) return;
            if (now < _crankAgainAt) return;
            if (!Game.IsControlPressed(Control.VehicleAccelerate)) return;

            _crankUntil = now + (int)(_cfg.DryRestartSeconds * 1000f);
            _crankAgainAt = _crankUntil + 1400;
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
