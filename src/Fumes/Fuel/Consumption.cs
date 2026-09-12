using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using GTA;
using Fumes.Core;

namespace Fumes.Fuel
{
    internal sealed class Consumption
    {
        private const float MapScale = 10f;

        private readonly Settings _cfg;

        /// <summary>
        /// Litres per 100 km by model name. The shipped models.json first, then the player's
        /// own models.local.json on top -- and only the second is ever written, so an update
        /// cannot undo a figure somebody tuned.
        /// </summary>
        private readonly Dictionary<string, float> _overrides =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The names in _overrides that belong to models.local.json.</summary>
        private readonly HashSet<string> _local = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private bool _dirty;

        /// <summary>Models whose figure has been written to the log, once each.</summary>
        private readonly HashSet<string> _said = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public Consumption(Settings cfg)
        {
            _cfg = cfg;
            LoadOverrides();
        }

        private void LoadOverrides()
        {
            Read(Path.Combine(Paths.Data, "models.json"), false);
            Read(Paths.ModelsLocalFile, true);

            if (_overrides.Count > 0)
            {
                Log.Info(_overrides.Count + " per-model consumption figure(s), " + _local.Count +
                         " of them from models.local.json.");
            }
        }

        private void Read(string path, bool local)
        {
            try
            {
                var doc = JsonFile.Read(path);
                if (doc == null) return;

                var models = doc["models"];

                foreach (var key in models.Keys)
                {
                    var rate = models[key].AsFloat(-1f);
                    if (rate < 0f || rate >= 500f) continue;

                    var name = key.Trim();
                    _overrides[name] = rate;
                    if (local) _local.Add(name);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Could not read " + Path.GetFileName(path) + ": " + ex.Message +
                         " - the worked-out figures apply.");
            }
        }

        // ==================================================================
        // What the menu needs
        // ==================================================================

        /// <summary>The model name as the game spells it -- "SULTAN" -- or empty.</summary>
        public static string ModelName(Vehicle v)
        {
            try { return (v.DisplayName ?? "").Trim(); }
            catch { return ""; }
        }

        /// <summary>This vehicle's litres per 100 km as the burn will use them, before the multiplier.</summary>
        public float RateFor(Vehicle v, Tank tank)
        {
            if (v == null) return 0f;

            try { return Thirst(v, tank, false); }
            catch { return 0f; }
        }

        public bool HasOverride(string model)
        {
            return model.Length > 0 && _overrides.ContainsKey(model);
        }

        /// <summary>Pins a model to a figure of the player's own. Kept in memory until SaveOverrides.</summary>
        public void SetOverride(string model, float rate)
        {
            if (model.Length == 0) return;

            if (rate < 0.5f) rate = 0.5f;
            if (rate > 300f) rate = 300f;

            _overrides[model] = rate;
            _local.Add(model);
            _dirty = true;
        }

        /// <summary>Back to the worked-out figure.</summary>
        public void ClearOverride(string model)
        {
            if (model.Length == 0) return;

            if (_overrides.Remove(model)) _dirty = true;
            _local.Remove(model);
            _said.Remove(model);      // so the worked-out figure is said again
        }

        /// <summary>Writes models.local.json if anything moved. True when it did and the write went.</summary>
        public bool SaveOverrides()
        {
            if (!_dirty) return false;

            var models = Json.Object();

            foreach (var name in _local)
            {
                float rate;
                if (_overrides.TryGetValue(name, out rate)) models.Set(name, Math.Round(rate, 1));
            }

            var doc = Json.Object()
                .Set("note", "Litres per 100 km by model, tuned from the menu. Wins over models.json and " +
                             "over the figure worked out from the vehicle's weight and power. Delete an " +
                             "entry, or use RESET THIS VEHICLE, to go back.")
                .Set("models", models);

            var ok = JsonFile.Write(Paths.ModelsLocalFile, doc);
            if (ok) _dirty = false;

            Log.Info(ok ? _local.Count + " per-model figure(s) written to models.local.json."
                        : "Could not write models.local.json.");
            return ok;
        }

        // ==================================================================
        // The burn
        // ==================================================================

        public float Burn(Vehicle v, Tank tank, float dt)
        {
            if (v == null || tank == null || dt <= 0f) return 0f;

            try
            {
                if (!v.IsEngineRunning) return LeakOnly(v, tank, dt);

                var thirst = Thirst(v, tank, true);

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
        /// Litres per 100 km for this vehicle: the class figure, or a bike's from its tank, or
        /// the model's own. One place, so the menu shows exactly what the burn uses.
        /// </summary>
        private float Thirst(Vehicle v, Tank tank, bool say)
        {
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
            if (_cfg.BikeRangeKm > 0f && Tanks.IsBike(v) && tank != null && tank.Capacity > 0f)
            {
                thirst = tank.Capacity / _cfg.BikeRangeKm * 100f;

                // ...unless the player pinned this bike by name, which is their call.
                float pinned;
                var bike = ModelName(v);
                if (bike.Length > 0 && _overrides.TryGetValue(bike, out pinned)) thirst = pinned;

                return thirst;
            }

            if (_cfg.PerModel > 0f) thirst = ModelThirst(v, thirst, say);

            return thirst;
        }

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
        /// its own without anybody typing it. The three numbers are settings, and the log says
        /// what they produced the first time a model is driven.
        ///
        /// A FIGURE THE PLAYER PINNED IS NOT BLENDED. models.json and the menu's THIS VEHICLE
        /// row say "this car, this number"; halving that by PerModel = 0.5 would make the row
        /// lie about what it just set.
        /// </summary>
        private float ModelThirst(Vehicle v, float classRate, bool say)
        {
            var name = ModelName(v);
            float own;

            if (name.Length > 0 && _overrides.TryGetValue(name, out own))
            {
                if (say && _said.Add(name))
                {
                    Log.Info(v.LocalizedName + " (" + name.ToLowerInvariant() + "): pinned to " +
                             own.ToString("0.0", CultureInfo.InvariantCulture) + " L/100km by " +
                             (_local.Contains(name) ? "models.local.json" : "models.json") + ".");
                }

                return own;
            }

            // Aircraft, boats and rail carry mass and drive force too, and neither means
            // what it means on a road. They keep the class figure unless pinned.
            var c = v.ClassType;
            if (c == VehicleClass.Helicopters || c == VehicleClass.Planes || c == VehicleClass.Boats ||
                c == VehicleClass.Trains || c == VehicleClass.Cycles)
            {
                return classRate;
            }

            string from;
            if (!Derive(v, out own, out from)) return classRate;

            var w = _cfg.PerModel;
            if (w < 0f) w = 0f;
            if (w > 1f) w = 1f;

            var rate = classRate + (own - classRate) * w;

            if (say && name.Length > 0 && _said.Add(name))
            {
                Log.Info(v.LocalizedName + " (" + name.ToLowerInvariant() + "): " + from + " -> " +
                         own.ToString("0.0", CultureInfo.InvariantCulture) + " L/100km; class " +
                         classRate.ToString("0.0", CultureInfo.InvariantCulture) + ", PerModel " +
                         w.ToString("0.0", CultureInfo.InvariantCulture) + " -> " +
                         rate.ToString("0.0", CultureInfo.InvariantCulture) + " L/100km.");
            }

            return rate;
        }

        /// <summary>A figure from the handling numbers, or false when the game did not give usable ones.</summary>
        private bool Derive(Vehicle v, out float rate, out string how)
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

            rate = _cfg.PerModelBase + _cfg.PerModelPerTonne * (mass / 1000f) + _cfg.PerModelPerKw * kw;
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

        private static float Wear(Vehicle v)
        {
            float health;
            try { health = v.EngineHealth; }
            catch { return 1f; }

            if (health >= 1000f) return 1f;
            if (health <= 0f) return 1.35f;

            return 1f + (1000f - health) / 1000f * 0.35f;
        }

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

        private float LeakOnly(Vehicle v, Tank tank, float dt)
        {
            var l = Leak(v, tank, dt);
            return float.IsNaN(l) || l < 0f ? 0f : l;
        }
    }
}
