using System;
using GTA;
using GTA.Native;
using Fumes.Core;

namespace Fumes.Fuel
{
    /// <summary>
    /// The chime when a tank drops into reserve.
    ///
    /// ONCE PER DIP, not once a second and not once a frame. A warning that repeats is a
    /// warning you turn off, and the gauge is already saying it continuously in a colour --
    /// the sound is only there to make you look at the gauge. It re-arms when the tank goes
    /// back up, so filling to a quarter and running down again chimes again.
    ///
    /// NO NOTIFICATION, deliberately. A ticker message for something the dashboard already
    /// shows is the kind of thing that reads as a mod announcing itself.
    ///
    /// A ONE-SHOT CANNOT BE PROBED the way the filling loop is. HAS_SOUND_FINISHED tells you a
    /// loop never started, because a loop that is playing has not finished -- but a chime that
    /// played and a chime that never existed both report finished a moment later, and there is
    /// nothing to tell them apart. So the name is a setting and the log says what was asked
    /// for; that is the honest limit of what can be checked from in here.
    /// </summary>
    internal sealed class LowFuel
    {
        private readonly Settings _cfg;

        /// <summary>The vehicle the last warning was about, and whether it has been given.</summary>
        private int _car;
        private bool _warned;

        public LowFuel(Settings cfg)
        {
            _cfg = cfg;
        }

        public void Update(Vehicle car, Tank tank, bool driving)
        {
            if (!_cfg.LowFuelChime || car == null || tank == null) return;

            try
            {
                if (!driving)
                {
                    // Not cleared. Getting out at a pump and back in should not re-chime a tank
                    // that is still in reserve -- only actually putting fuel in should.
                    return;
                }

                if (car.Handle != _car)
                {
                    _car = car.Handle;

                    // A car you get into ALREADY in reserve counts as warned. The chime is for
                    // the moment it crosses over, and a car that was low before you touched it
                    // has not crossed anything.
                    _warned = tank.Fraction <= _cfg.ReserveFraction;
                    return;
                }

                var low = tank.Fraction <= _cfg.ReserveFraction;

                // A margin on the way back up, or a tank sitting exactly on the line chimes
                // every time the needle wobbles across it.
                if (_warned)
                {
                    if (tank.Fraction > _cfg.ReserveFraction + 0.03f) _warned = false;
                    return;
                }

                if (!low) return;

                _warned = true;
                Chime();
            }
            catch (Exception ex)
            {
                Log.Once("lowfuel", "The low fuel warning fell over: " + ex.Message);
            }
        }

        private void Chime()
        {
            var name = _cfg.LowFuelSoundName;
            var set = _cfg.LowFuelSoundSet;

            if (string.IsNullOrEmpty(name)) return;

            try
            {
                if (!string.IsNullOrEmpty(_cfg.LowFuelSoundBank))
                {
                    Function.Call(Hash.REQUEST_SCRIPT_AUDIO_BANK, _cfg.LowFuelSoundBank, false, -1);
                }

                // FRONTEND, not from the car. This is a dashboard warning: it belongs to the
                // driver, not to a position in the world, and it should not get quieter because
                // the camera swung round the bonnet.
                Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, name, set, true);

                Log.Once("lowfuel-sound", "Low fuel chime: \"" + name + "\" from \"" + set +
                                          "\". If you hear nothing, that pair is the thing to " +
                                          "change in [Fuel] LowFuelSoundName and LowFuelSoundSet.");
            }
            catch (Exception ex)
            {
                Log.Once("lowfuel-chime", "Could not play the low fuel chime: " + ex.Message);
            }
        }
    }
}
