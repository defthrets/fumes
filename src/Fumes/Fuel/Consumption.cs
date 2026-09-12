using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

        /// <summary>Litres per 100 km by model name, from data\models.json. Wins over everything.</summary>
        private readonly Dictionary<string, float> _overrides =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Models whose figure has been written to the log, once each.</summary>
        private readonly HashSet<string> _said = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public Consumption(Settings cfg)
        {
            _cfg = cfg;
            LoadOverrides();
        }

        private void LoadOverrides()
        {
            try
            {
                var doc = JsonFile.Read(Path.Combine(Paths.Data, "models.json"));
                if (doc == null) return;

                var models = doc["models"];

                foreach (var key in models.Keys)
                {
                    var rate = models[key].AsFloat(-1f);
                    if (rate >= 0f && rate < 500f) _overrides[key.Trim()] = rate;
                }

                if (_overrides.Count > 0)
                {
                    Log.Info(_overrides.Count + " per-model consumption figure(s) from models.json.");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Could not read models.json: " + ex.Message + " - the worked-out figures apply.");
            }
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

                // A BIKE'S THIRST IS WORKED OUT FROM ITS TANK, not looked up, so that a quarter
                // of the fuel still covers the same ground. Sixteen litres at the class figure
                // of 4.5 is three hundred and fifty kilometres against a saloon's seven
                // hundred, which is the honest consequence of a small tank and not what anybody
                // wants from a motorbike.
                //
                // Derived rather than another number in the table, because the two have to
                // agree: change the tank and the range holds by construction. Two independent
                // settings would drift the first time one of them moved.
                if (_cfg.BikeRangeKm > 0f && Tanks.IsBike(v) && tank.Capacity > 0f)
                {
                    thirst = tank.Capacity / _cfg.BikeRangeKm * 100f;
                }
                else if (_cfg.PerModel > 0f)
                {
                    // Everything that is not a bike: its own figure, from what it is.
                    thirst = ModelThirst(v, thirst);
                }

                if (thirst <= 0f) return 0f;

                // Distance actually covered, in kilometres. Speed is metres per second and is
                // the entity's real speed, so a car sliding sideways or being pushed reads the
                // same as one driving -- which is right: the engine is running either way.
                var km = Math.Abs(v.Speed) * dt / 1000f;

                var driving = thirst * km / 100f;
                var idling = _cfg.IdleLitresPerHour * IdleScale(v) * dt / 3600f;

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
        /// <summary>
        /// This model's own litres per 100 km, blended into the class figure by [Fuel] PerModel.
        ///
        /// NO TABLE OF MODELS. The game already knows what every vehicle weighs and how hard
        /// its engine pushes -- fMass and fInitialDriveForce in handling.meta, which SHVDN
        /// hands over as HandlingData -- and real consumption follows those two closely enough
        /// to be worked out rather than looked up: a couple of litres to keep an engine
        /// turning, about 1.8 more for every tonne it has to move, and about 0.05 for every
        /// kilowatt that makes it quick. Calibrated so a Blista lands near the Compacts figure
        /// and an Adder near the Super one -- and every DLC and add-on vehicle gets a figure of
        /// its own without anybody typing it. models.json is for the exceptions, and wins.
        ///
        /// Said once per model in the log, with the numbers it came from, so "this car drinks
        /// too much" can be answered with the figures rather than a feeling.
        /// </summary>
        private float ModelThirst(Vehicle v, float classRate)
        {
            var name = ModelName(v);
            float own;
            string from;

            if (name.Length > 0 && _overrides.TryGetValue(name, out own))
            {
                from = "models.json";
            }
            else
            {
                // Aircraft, boats and rail carry mass and drive force too, and neither means
                // what it means on a road. They keep the class figure unless models.json says.
                var c = v.ClassType;
                if (c == VehicleClass.Helicopters || c == VehicleClass.Planes || c == VehicleClass.Boats ||
                    c == VehicleClass.Trains || c == VehicleClass.Cycles)
                {
                    return classRate;
                }

                if (!Derive(v, out own, out from)) return classRate;
            }

            var w = _cfg.PerModel;
            if (w < 0f) w = 0f;
            if (w > 1f) w = 1f;

            var rate = classRate + (own - classRate) * w;

            if (name.Length > 0 && _said.Add(name))
            {
                Log.Info(v.LocalizedName + " (" + name.ToLowerInvariant() + "): " + from + " -> " +
                         own.ToString("0.0", CultureInfo.InvariantCulture) + " L/100km; class " +
                         classRate.ToString("0.0", CultureInfo.InvariantCulture) + ", PerModel " +
                         w.ToString("0.0", CultureInfo.InvariantCulture) + " -> " +
                         rate.ToString("0.0", CultureInfo.InvariantCulture) + " L/100km.");
            }

            return rate;
        }

        private static string ModelName(Vehicle v)
        {
            try { return (v.DisplayName ?? "").Trim(); }
            catch { return ""; }
        }

        /// <summary>A figure from the handling numbers, or false when the game did not give usable ones.</summary>
        private static bool Derive(Vehicle v, out float rate, out string how)
        {
            rate = 0f;
            how = "";

            float mass, force;

            try
            {
                var h = v.HandlingData;
                if (h == null) return false;

                mass = h.Mass;
                force = h.InitialDriveForce;
            }
            catch
            {
                return false;
            }

            if (float.IsNaN(mass) || float.IsNaN(force)) return false;
            if (mass < 50f || mass > 60000f || force <= 0.01f || force > 3f) return false;

            var kw = Power(mass, force);

            rate = 2.0f + 1.8f * (mass / 1000f) + 0.05f * kw;
            how = mass.ToString("0", CultureInfo.InvariantCulture) + " kg, " +
                  force.ToString("0.00", CultureInfo.InvariantCulture) + " g, ~" +
                  kw.ToString("0", CultureInfo.InvariantCulture) + " kW";

            return true;
        }

        /// <summary>
        /// The engine's push at a hundred an hour, in kilowatts. fInitialDriveForce is an
        /// acceleration in g, so the force is m·g·drive and the power is that times the speed.
        /// </summary>
        private static float Power(float mass, float force)
        {
            return mass * 9.81f * force * 27.8f / 1000f;
        }

        /// <summary>
        /// Idle scaled by engine size, on the same numbers: a truck ticking over drinks more
        /// than a Blista does. Eighty kilowatts is the one that idles at the ini's figure.
        /// </summary>
        private float IdleScale(Vehicle v)
        {
            if (_cfg.PerModel <= 0f) return 1f;

            try
            {
                var h = v.HandlingData;
                if (h == null) return 1f;

                var kw = Power(h.Mass, h.InitialDriveForce);
                if (float.IsNaN(kw) || kw <= 1f) return 1f;

                var s = kw / 80f;
                if (s < 0.5f) s = 0.5f;
                if (s > 3f) s = 3f;

                var w = _cfg.PerModel > 1f ? 1f : _cfg.PerModel;
                return 1f + (s - 1f) * w;
            }
            catch
            {
                return 1f;
            }
        }

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
