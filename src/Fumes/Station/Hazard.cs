using System;
using GTA;
using GTA.Math;
using GTA.Native;
using Fumes.Core;

namespace Fumes.Station
{
    /// <summary>
    /// What happens when there is fire on a forecourt.
    ///
    /// Kept small and kept FAIR. The game already blows a pump up when you shoot it, so this
    /// is not about adding an explosion -- it is about the pump being live while you are
    /// standing at it with the hose running. Three things can set it off and all three are
    /// visible before they happen: you are on fire, something near the pump is already
    /// burning, or somebody is shooting on the forecourt.
    ///
    /// Nothing here fires from a hidden roll on an ordinary refuel. A player who fills up at a
    /// quiet station a hundred times should never once be surprised by this, or it stops being
    /// a hazard and becomes a tax.
    /// </summary>
    internal sealed class Hazard
    {
        private readonly Settings _cfg;
        private readonly Random _rng = new Random();

        /// <summary>Gunfire is rolled once a second, not once a frame.</summary>
        private int _nextRoll;

        /// <summary>Odds per second that a shootout at the pumps sets the vapour off.</summary>
        private const float GunfireChancePerSecond = 0.28f;

        public Hazard(Settings cfg)
        {
            _cfg = cfg;
        }

        /// <summary>
        /// Returns true if it went up, in which case the caller must drop everything.
        /// Only called while the nozzle is out.
        /// </summary>
        public bool Update(Vector3 pump, bool flowing)
        {
            if (!_cfg.ForecourtHazard) return false;

            try
            {
                var me = Game.Player.Character;
                if (me == null || !me.Exists()) return false;

                if (Function.Call<bool>(Hash.IS_ENTITY_ON_FIRE, me.Handle))
                {
                    return Ignite(pump, "you were on fire");
                }

                // Anything already burning right at the pump. The radius is deliberately tight:
                // a car fire across the forecourt is atmosphere, a fire under the pump is not.
                if (Function.Call<int>(Hash.GET_NUMBER_OF_FIRES_IN_RANGE, pump.X, pump.Y, pump.Z, 4.5f) > 0)
                {
                    return Ignite(pump, "there was a fire at the pump");
                }

                // Gunfire only counts while fuel is actually moving -- that is when there is
                // vapour in the air, and it is also the only moment the player has committed
                // to standing still.
                if (!flowing) return false;

                var now = Game.GameTime;
                if (now < _nextRoll) return false;
                _nextRoll = now + 1000;

                if (!ShootingNear(pump)) return false;
                if (_rng.NextDouble() > GunfireChancePerSecond) return false;

                return Ignite(pump, "somebody opened fire on the forecourt");
            }
            catch (Exception ex)
            {
                Log.Once("hazard", "Forecourt hazard check failed: " + ex.Message +
                                   " - pumps will not catch this session.");
                return false;
            }
        }

        private static bool ShootingNear(Vector3 pump)
        {
            try
            {
                foreach (var ped in World.GetNearbyPeds(pump, 12f))
                {
                    if (ped == null || !ped.Exists()) continue;
                    if (Function.Call<bool>(Hash.IS_PED_SHOOTING, ped.Handle)) return true;
                }
            }
            catch
            {
                // No answer is the safe answer here.
            }

            return false;
        }

        private static bool Ignite(Vector3 pump, string why)
        {
            Log.Info("Forecourt went up: " + why + ".");

            try
            {
                // The game's own petrol pump explosion, so it looks and sounds like every
                // other pump that has ever gone up rather than like a grenade appearing.
                World.AddExplosion(pump, ExplosionType.PetrolPump, 2.0f, 1.0f, null, true, false);
            }
            catch (Exception ex)
            {
                Log.Error("Could not set the pump off.", ex);
            }

            try
            {
                GTA.UI.Notification.Show("~r~The pump went up~s~ - " + why + ".", false);
            }
            catch
            {
                // The fireball is the notification.
            }

            return true;
        }
    }
}
