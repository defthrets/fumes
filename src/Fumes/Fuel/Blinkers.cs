using System;
using GTA;
using Fumes.Core;

// Both namespaces have a Control and only one of them is a game control.
using Control = GTA.Control;

namespace Fumes.Fuel
{
    /// <summary>
    /// Indicators worked by the steering wheel, the way a real one self-cancels.
    ///
    /// Hold the wheel over for a second and that side comes on. It goes off when you steer the
    /// OTHER way -- which is what straightening out of a turn is -- or when you have been
    /// driving without steering that way for a second.
    ///
    /// LETTING GO DOES NOT CANCEL IT, and that is the whole point of the design rather than a
    /// detail. Sitting at a red light you turn the wheel, let it centre, and the indicator
    /// stays on; that is how you signal before you have moved. The straight-ahead cancel only
    /// applies while the car is actually MOVING, so a centred wheel at a standstill means
    /// nothing at all. Without that split, signalling at the lights would be impossible: the
    /// wheel returns to centre the moment you release it, and a cancel that only asked "is the
    /// wheel straight" would put it out again a second later.
    ///
    /// No keys, no prompts, no notifications. It is a car behaving like a car.
    /// </summary>
    internal sealed class Blinkers
    {
        private readonly Settings _cfg;

        /// <summary>-1 left, 0 off, +1 right.</summary>
        private int _side;

        /// <summary>Which way the wheel is over, and since when.</summary>
        private int _steering;
        private int _steeringSince;

        /// <summary>When the car last did something that counts as "not still turning that way".</summary>
        private int _straightSince;

        /// <summary>The car this is about, by handle, so a change of car clears it.</summary>
        private int _car;

        public Blinkers(Settings cfg)
        {
            _cfg = cfg;
        }

        public void Update(Ped me, Vehicle car, bool driving)
        {
            if (!_cfg.Blinkers) return;

            try
            {
                if (!driving || car == null || !car.Exists())
                {
                    // Left exactly as they are. A car you walked away from indicating is still
                    // indicating, which is both what a real one does and what the rest of this
                    // mod does with an engine.
                    _car = 0;
                    _steering = 0;
                    return;
                }

                if (car.Handle != _car)
                {
                    _car = car.Handle;
                    _side = Reading(car);
                    _steering = 0;
                    _steeringSince = Game.GameTime;
                    _straightSince = Game.GameTime;
                }

                var now = Game.GameTime;
                var wheel = Wheel();

                // ---- coming on -------------------------------------------------
                if (wheel != _steering)
                {
                    _steering = wheel;
                    _steeringSince = now;
                }

                var armed = (int)(_cfg.BlinkerArmSeconds * 1000f);

                if (wheel != 0 && _side != wheel && now - _steeringSince >= armed) Set(car, wheel);

                // ---- going off -------------------------------------------------
                if (_side == 0) return;

                // Steering the other way. Straightening out of a turn IS steering the other
                // way, which is exactly why a real indicator cancels there and why this needs
                // no separate rule for "the turn is finished".
                if (wheel == -_side)
                {
                    Set(car, 0);
                    return;
                }

                // Still holding it that way: nothing has gone straight.
                if (wheel == _side)
                {
                    _straightSince = now;
                    return;
                }

                // Wheel centred. MOVING is what makes that mean anything -- at a standstill a
                // centred wheel is just a wheel nobody is holding, and cancelling there would
                // make it impossible to signal before pulling away.
                float speed;
                try { speed = car.Speed; }
                catch { speed = 0f; }

                if (speed < _cfg.BlinkerMinSpeed)
                {
                    _straightSince = now;
                    return;
                }

                if (now - _straightSince >= (int)(_cfg.BlinkerCancelSeconds * 1000f)) Set(car, 0);
            }
            catch (Exception ex)
            {
                Log.Once("blinkers", "The indicators fell over: " + ex.Message);
            }
        }

        /// <summary>
        /// Which way the wheel is being held: -1 left, 0 centred, +1 right.
        ///
        /// READ FROM THE TWO ONE-SIDED CONTROLS, not from the signed axis, and that is not
        /// fussiness. The axis is one number whose sign means left or right by a convention
        /// this code would have to assume -- and getting it backwards is a mod that indicates
        /// the wrong way at every junction, which is worse than one that does nothing. The
        /// LeftOnly and RightOnly controls each report their own side, so there is no
        /// convention to be wrong about.
        ///
        /// The signed axis is still read as a fallback for setups where the one-sided controls
        /// stay at zero, and THAT is the reading BlinkerInvert exists for.
        /// </summary>
        private int Wheel()
        {
            var dead = _cfg.BlinkerDeadzone;

            try
            {
                var left = Game.GetControlValueNormalized(Control.VehicleMoveLeftOnly);
                var right = Game.GetControlValueNormalized(Control.VehicleMoveRightOnly);

                if (left > dead || right > dead) return right > left ? 1 : -1;

                var axis = Game.GetControlValueNormalized(Control.VehicleMoveLeftRight);
                if (Math.Abs(axis) <= dead) return 0;

                var side = axis > 0f ? 1 : -1;
                return _cfg.BlinkerInvert ? -side : side;
            }
            catch
            {
                return 0;
            }
        }

        private static int Reading(Vehicle car)
        {
            try
            {
                if (car.IsLeftIndicatorLightOn) return -1;
                if (car.IsRightIndicatorLightOn) return 1;
            }
            catch
            {
                // Treated as off, which the next steer will correct.
            }

            return 0;
        }

        private void Set(Vehicle car, int side)
        {
            _side = side;
            _straightSince = Game.GameTime;

            try
            {
                car.IsLeftIndicatorLightOn = side == -1;
                car.IsRightIndicatorLightOn = side == 1;
            }
            catch (Exception ex)
            {
                Log.Once("blinker-set", "Could not work the indicators: " + ex.Message);
            }
        }
    }
}
