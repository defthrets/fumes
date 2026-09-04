using System;
using GTA;
using Fumes.Core;

namespace Fumes.Fuel
{
    /// <summary>
    /// Turns a second of driving into litres.
    ///
    /// The model is deliberately physical -- distance times a litres-per-100km figure, plus an
    /// idle burn, times an engine-load factor -- rather than a flat "percent per minute". A
    /// physical model is the only kind where a Phantom towing a trailer up Chiliad costs more
    /// than a Blista on the freeway without anybody writing a rule saying so.
    /// </summary>
    internal sealed class Consumption
    {
        /// <summary>
        /// The fudge that makes realism playable, named so nobody deletes it by accident.
        ///
        /// Los Santos is roughly a tenth of real scale: a coast-to-coast drive is about 8 km,
        /// where the real thing it is drawn from is several hundred. So a genuinely realistic
        /// engine on a genuinely realistic tank has a range of forty real-world minutes of
        /// solid motorway driving, and NOTHING ELSE IN THE GAME IS THAT LONG. You would fill
        /// up once a session, by accident, and never think about fuel again.
        ///
        /// Multiplying the burn by ten puts a tank at roughly half an hour of ordinary play,
        /// which is the number this whole mod is actually about. It lives here, as one named
        /// constant, instead of being smuggled into every litres-per-100km figure in the ini --
        /// where it would make every one of those numbers a lie and make the ini impossible to
        /// tune by anybody who knows what a car drinks.
        /// </summary>
        private const float MapScale = 10f;

        private readonly Settings _cfg;

        public Consumption(Settings cfg)
        {
            _cfg = cfg;
        }

        /// <summary>
        /// Litres burnt by this vehicle over dt real seconds.
        ///
        /// Never negative, never NaN, and zero for anything with the engine off -- a parked car
        /// does not drink, and a car being towed or shipped should not either.
        /// </summary>
        public float Burn(Vehicle v, Tank tank, float dt)
        {
            if (v == null || tank == null || dt <= 0f) return 0f;

            try
            {
                if (!v.IsEngineRunning) return LeakOnly(v, tank, dt);

                var thirst = _cfg.ThirstFor(v.ClassType);
                if (thirst <= 0f) return 0f;

                // Distance actually covered, in kilometres. Speed is metres per second and is
                // the entity's real speed, so a car sliding sideways or being pushed reads the
                // same as one driving -- which is right: the engine is running either way.
                var km = Math.Abs(v.Speed) * dt / 1000f;

                var driving = thirst * km / 100f;
                var idling = _cfg.IdleLitresPerHour * dt / 3600f;

                // THE GRADE IN THE TANK, not the one selected. See Tank.Grade -- except for
                // diesel, which comes from the VEHICLE, because a diesel truck cannot be
                // holding anything else whatever its tank was last written with. Reading the
                // stored grade literally would run every truck you have not yet filled on
                // petrol economy, including the one you have owned since the start.
                var litres = (driving * Load(v) + idling) * MapScale * _cfg.ConsumptionMultiplier
                             * _cfg.EconomyFor(Diesel.GradeFor(_cfg, v, tank.Grade));

                // A wrecked engine is an inefficient one. Health runs 0..1000 and anything
                // under about half is smoking.
                litres *= Wear(v);

                if (float.IsNaN(litres) || float.IsInfinity(litres) || litres < 0f) return 0f;

                return litres + Leak(v, tank, dt);
            }
            catch (Exception ex)
            {
                Log.Once("burn-throw", "Could not work out fuel burn: " + ex.Message +
                                       " - that vehicle will not use fuel this session.");
                return 0f;
            }
        }

        /// <summary>
        /// How hard the engine is working, 0.55 at a coast to about 1.5 flat out.
        ///
        /// RPM rather than throttle, because RPM already carries the gear. Flooring it in top
        /// at 30mph and flooring it in first are the same throttle and very different fuel.
        /// </summary>
        private static float Load(Vehicle v)
        {
            float rpm;
            try { rpm = v.CurrentRPM; }
            catch { return 1f; }

            if (float.IsNaN(rpm)) return 1f;
            if (rpm < 0f) rpm = 0f;
            if (rpm > 1f) rpm = 1f;

            return 0.55f + rpm * 0.95f;
        }

        /// <summary>A damaged engine burns more. 1.0 healthy, 1.35 at death's door.</summary>
        private static float Wear(Vehicle v)
        {
            float health;
            try { health = v.EngineHealth; }
            catch { return 1f; }

            if (health >= 1000f) return 1f;
            if (health <= 0f) return 1.35f;

            return 1f + (1000f - health) / 1000f * 0.35f;
        }

        /// <summary>
        /// What pours out of a shot tank whether the engine runs or not.
        ///
        /// This is the one bit of the model that keeps working with the key out, and it is
        /// deliberate: a car left overnight with a hole in it should be empty in the morning.
        /// PetrolTankHealth runs 0..1000 and only moves when something has actually hit it.
        /// </summary>
        private float Leak(Vehicle v, Tank tank, float dt)
        {
            if (!_cfg.TankLeaks || tank.Empty) return 0f;

            float health;
            try { health = v.PetrolTankHealth; }
            catch { return 0f; }

            if (health >= 999f) return 0f;

            // Full-bore at zero health is a tank emptied in about a minute and a half; a
            // scratch is a slow weep. Scaled by capacity so a tanker does not drain like a bike.
            var severity = (1000f - Math.Max(health, 0f)) / 1000f;
            return tank.Capacity * severity * severity * dt / 90f;
        }

        /// <summary>Engine off: nothing burns, but a holed tank still empties.</summary>
        private float LeakOnly(Vehicle v, Tank tank, float dt)
        {
            var l = Leak(v, tank, dt);
            return float.IsNaN(l) || l < 0f ? 0f : l;
        }
    }
}
