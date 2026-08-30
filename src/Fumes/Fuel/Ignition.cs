using System;
using System.Windows.Forms;
using GTA;

// Both namespaces have a Control and only one of them is a game control.
using Control = GTA.Control;
using GTA.Native;
using Fumes.Core;

namespace Fumes.Fuel
{
    /// <summary>
    /// The ignition, taken off the game and given to the player.
    ///
    /// Three rules, and they are one idea: THE ENGINE IS A THING YOU OPERATE, not a side effect
    /// of being sat in the seat.
    ///
    ///   Hold the exit key and the engine stops. You do not get out.
    ///   Tap it and you get out, and the car is left exactly as it stands -- running if it was
    ///   running, dead if you turned it off first.
    ///   Get in and nothing happens. It starts when you touch the throttle.
    ///
    /// Silent by design. No prompt, no notification, no help text: a car that keeps running
    /// when you leave it is not an event, it is how a car works, and telling somebody about it
    /// every time would make it into a mechanic.
    ///
    /// THE EXIT CONTROL IS TAKEN OVER RATHER THAN WATCHED. Both meanings live on one key and
    /// the game already has its own idea about it -- hold to exit at speed -- so leaving the
    /// control enabled would have the game acting on the same press we are interpreting, and
    /// the player would be out of the car before the hold ever registered as a hold. It is
    /// disabled every frame and read through IsControlPressed, which reads a disabled control;
    /// what happens next is entirely ours.
    /// </summary>
    internal sealed class Ignition
    {
        private readonly Settings _cfg;
        private readonly Tanks _tanks;

        /// <summary>The car being driven, so getting into a different one is noticed.</summary>
        private Vehicle _car;

        /// <summary>Engine held off until the throttle is touched.</summary>
        private bool _waitingForThrottle;

        /// <summary>When the exit key went down, and whether this press has already stopped the engine.</summary>
        private int _downAt;
        private bool _stopped;

        /// <summary>
        /// A car just stepped out of, and the state it is to be left in.
        ///
        /// Held for a few seconds because the game turns the engine off ITSELF as the driver
        /// gets out, and it does it after the task starts rather than when it finishes. One
        /// call at the moment of leaving is overwritten a frame later and the car dies on the
        /// forecourt with no explanation.
        /// </summary>
        private Vehicle _leaving;
        private bool _leavingRunning;
        private int _leavingUntil;

        /// <summary>Set once the exit control has been seen through the disable. See ExitKey.</summary>
        private bool _controlReadable;

        /// <summary>The station that was playing as he got out, and whether it has been put back on.</summary>
        private string _station;
        private bool _radioSet;

        public Ignition(Settings cfg, Tanks tanks)
        {
            _cfg = cfg;
            _tanks = tanks;
        }

        public void Update(Ped me)
        {
            if (!_cfg.ManualIgnition) return;

            try
            {
                var car = me == null ? null : me.CurrentVehicle;

                // BACK IN THE SAME CAR ENDS THE ENFORCEMENT, and it has to happen before
                // Settle runs. Otherwise the four-second window that keeps a car as you left
                // it goes on holding its engine off while you sit in it -- so getting out and
                // straight back in gave a car that would not start for four seconds however
                // hard the throttle was pressed, and nothing on screen to say why.
                if (_leaving != null && Same(car, _leaving)) _leaving = null;

                Settle(me);

                var driving = car != null && car.Exists() && !car.IsDead &&
                              Driving(car, me) && Covered(car);

                if (!driving)
                {
                    _car = null;
                    _downAt = 0;
                    _stopped = false;
                    return;
                }

                if (!Same(car, _car))
                {
                    _car = car;
                    _downAt = 0;
                    _stopped = false;

                    // ONLY IF IT WAS ALREADY OFF. Getting into something you left running is
                    // not an ignition problem -- it is still running, and stopping it so it can
                    // be started again would be the mod inventing work.
                    _waitingForThrottle = !Running(car);
                }

                Choke(car);
                ExitKey(me, car);
            }
            catch (Exception ex)
            {
                Log.Once("ignition", "The ignition handling fell over: " + ex.Message +
                                     " - the game's own behaviour is back.");
            }
        }

        /// <summary>
        /// Aircraft are left alone.
        ///
        /// Not squeamishness: the same gesture that parks a car is, in a helicopter at a
        /// thousand feet, the one that kills you -- and it is the SAME KEY the player uses to
        /// get out on the ground. A feature nobody asked to be lethal should not be.
        /// </summary>
        private bool Covered(Vehicle v)
        {
            if (_cfg.ManualIgnitionAircraft) return true;

            try
            {
                var m = v.Model;
                return !m.IsPlane && !m.IsHelicopter;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>Holds the engine off until the throttle is touched.</summary>
        private void Choke(Vehicle car)
        {
            if (!_waitingForThrottle) return;

            if (Game.IsControlPressed(Control.VehicleAccelerate))
            {
                _waitingForThrottle = false;

                // Not on an empty tank. Starvation would kill it again within the frame, and
                // the pair of them would fight over the engine once a frame forever -- which
                // reads as a car that will not catch rather than one with no fuel in it.
                var tank = _tanks.For(car);
                if (tank != null && tank.Empty) return;

                Engine(car, true);
                return;
            }

            // Every frame, and with auto-start disabled: the game restarts an engine under a
            // seated driver on its own, and it does it more than once.
            Engine(car, false);
        }

        private void ExitKey(Ped me, Vehicle car)
        {
            // ABOVE WALKING PACE THE KEY GOES BACK TO THE GAME, whole and untouched.
            //
            // A tap that ejects you at sixty is not what a tap should do, and it was only doing
            // it because tapping had been given a new meaning. But simply refusing the tap
            // would have left NO way out of a moving car -- hold means "stop the engine" here,
            // and vanilla's hold-to-bail would have been quietly deleted along with it.
            //
            // So the control is not disabled at all above the limit. Vanilla is not
            // approximated, it is handed back: hold to bail out, exactly as the game does it,
            // because at speed that is the only one of the two meanings worth having. Below the
            // limit, where you are parking rather than driving, it is ours again.
            float speed;
            try { speed = car.Speed; }
            catch { speed = 0f; }

            if (speed > _cfg.ManualIgnitionMaxSpeed)
            {
                _downAt = 0;
                _stopped = false;
                return;
            }

            Game.DisableControlThisFrame(Control.VehicleExit);

            // READ THROUGH THE DISABLE, with a way out if that turns out to be wrong.
            //
            // SHVDN's IsControlPressed reads a control whether or not it is disabled -- that is
            // what separates it from IsEnabledControlPressed, which is the pair's whole reason
            // for existing. Everything here rests on that: the control is disabled every frame
            // so the game cannot act on it, and read anyway so we can.
            //
            // If it were wrong, the failure would not be a feature that does nothing. It would
            // be a player sealed inside a car with the exit key doing nothing at all and no way
            // to find out why. That is worth a belt as well as braces: until the control has
            // been seen to read true at least once, the default exit key is watched directly as
            // well. After that it never is, so a rebound exit control behaves properly.
            bool down;

            try
            {
                var viaControl = Game.IsControlPressed(Control.VehicleExit);
                if (viaControl) _controlReadable = true;

                down = viaControl || (!_controlReadable && Game.IsKeyPressed(Keys.F));
            }
            catch
            {
                down = false;
            }

            if (down)
            {
                if (_downAt == 0)
                {
                    _downAt = Game.GameTime;
                    return;
                }

                if (_stopped) return;

                if (Game.GameTime - _downAt < (int)(_cfg.ExitHoldSeconds * 1000f)) return;

                // Held long enough. The engine stops and the player stays put -- and the wait
                // for the throttle is armed, so it does not simply start itself again while
                // they are still sitting there.
                _stopped = true;
                _waitingForThrottle = true;

                Engine(car, false);
                return;
            }

            var wasDown = _downAt != 0;
            var held = _stopped;

            _downAt = 0;
            _stopped = false;

            // Released without ever becoming a hold: a tap, which is the only thing that gets
            // anybody out of a car.
            if (wasDown && !held) Leave(me, car);
        }

        private void Leave(Ped me, Vehicle car)
        {
            try
            {
                _leaving = car;
                _leavingRunning = Running(car);
                _leavingUntil = Game.GameTime + 4000;
                _radioSet = false;

                // THE STATION HAS TO BE READ NOW, from inside. This native answers "what is the
                // PLAYER listening to", and the player stops listening to a car radio the moment
                // they are not in the car -- ask afterwards and the answer is nothing, and the
                // car would be left running in silence.
                _station = null;

                if (_leavingRunning && _cfg.RadioKeepsPlaying)
                {
                    try { _station = Function.Call<string>(Hash.GET_PLAYER_RADIO_STATION_NAME); }
                    catch { _station = null; }
                }

                // The game's OWN way of keeping an abandoned engine running, which is a better
                // instrument than holding the engine on by force every frame -- that only lasts
                // as long as the window, and this lasts as long as the car.
                try
                {
                    Function.Call(Hash.SET_VEHICLE_KEEP_ENGINE_ON_WHEN_ABANDONED,
                                  car.Handle, _leavingRunning);
                }
                catch { /* the enforcement window still covers it */ }

                Function.Call(Hash.TASK_LEAVE_VEHICLE, me.Handle, car.Handle, 0);
            }
            catch (Exception ex)
            {
                Log.Once("ignition-leave", "Could not get out: " + ex.Message);
                _leaving = null;
            }
        }

        /// <summary>Keeps a car just left in the state it was left in, while the game argues.</summary>
        private void Settle(Ped me)
        {
            if (_leaving == null) return;

            if (Game.GameTime > _leavingUntil || !_leaving.Exists() || _leaving.IsDead)
            {
                _leaving = null;
                return;
            }

            Engine(_leaving, _leavingRunning);
            Radio(me);
        }

        /// <summary>
        /// The radio, still playing, and audible from outside.
        ///
        /// ONCE, AND ONLY ONCE HE IS ACTUALLY OUT. Not every frame, because SET_VEH_RADIO_STATION
        /// is a station CHANGE -- called sixty times a second it restarts the track sixty times
        /// a second, and a car left running would sit there stuttering the first half-second of
        /// a song forever. And not at the moment the exit task starts either: he is still in the
        /// seat then, the game still owns the radio, and it turns it off behind us on the way
        /// out.
        ///
        /// SET_VEHICLE_RADIO_LOUD is the one that carries it past the windows. Without it the
        /// radio does play, at the volume it has for somebody sitting inside, which from the
        /// forecourt is silence.
        /// </summary>
        private void Radio(Ped me)
        {
            if (_radioSet || !_cfg.RadioKeepsPlaying) return;

            // Still climbing out. The radio is the game's until he is clear of the seat.
            try
            {
                if (me == null) return;

                var still = me.CurrentVehicle;
                if (still != null && still.Handle == _leaving.Handle) return;
            }
            catch
            {
                return;
            }

            _radioSet = true;

            if (!_leavingRunning || string.IsNullOrEmpty(_station) || _station == "OFF")
            {
                // Engine off, or nothing was playing. A dead car with a radio on is a flat
                // battery, not a feature.
                try
                {
                    Function.Call(Hash.SET_VEHICLE_RADIO_ENABLED, _leaving.Handle, false);
                    Function.Call(Hash.SET_VEHICLE_RADIO_LOUD, _leaving.Handle, false);
                }
                catch { /* nothing worth reporting */ }

                return;
            }

            try
            {
                Function.Call(Hash.SET_VEHICLE_RADIO_ENABLED, _leaving.Handle, true);
                Function.Call(Hash.SET_VEH_RADIO_STATION, _leaving.Handle, _station);
                Function.Call(Hash.SET_VEHICLE_RADIO_LOUD, _leaving.Handle, true);
            }
            catch (Exception ex)
            {
                Log.Once("radio", "Could not leave the radio playing: " + ex.Message);
            }
        }

        /// <summary>
        /// Two wrappers for the same thing.
        ///
        /// NOT ReferenceEquals, which is what this was and why none of it worked. Every one of
        /// these properties BUILDS A NEW WRAPPER on each call -- Vehicle.Driver has a getter and
        /// no backing field, and so does Ped.CurrentVehicle -- so two reads a frame apart are
        /// two different objects around the same handle. ReferenceEquals was false every single
        /// frame: the driver never matched the player, so the feature never ran at all, and the
        /// car never matched the last car, so the hold timer was reset before it could count.
        ///
        /// The handle is the identity. SHVDN's own == does exactly this.
        /// </summary>
        private static bool Same(Entity a, Entity b)
        {
            if (a == null || b == null) return false;

            try { return a.Handle == b.Handle; }
            catch { return false; }
        }

        private static bool Driving(Vehicle car, Ped me)
        {
            try
            {
                var driver = car.Driver;
                return driver != null && driver.Exists() && Same(driver, me);
            }
            catch
            {
                return false;
            }
        }

        private static bool Running(Vehicle v)
        {
            try { return v.IsEngineRunning; }
            catch { return false; }
        }

        /// <summary>
        /// SET_VEHICLE_ENGINE_ON(vehicle, on, instantly, disableAutoStart).
        ///
        /// The fourth argument is the one that matters and the one that is easy to leave out:
        /// without it the game is free to start the engine again by itself the moment a driver
        /// is seated, which is exactly the behaviour being replaced.
        /// </summary>
        private static void Engine(Vehicle v, bool on)
        {
            try { Function.Call(Hash.SET_VEHICLE_ENGINE_ON, v.Handle, on, true, true); }
            catch { /* the next frame will try again */ }
        }
    }
}
