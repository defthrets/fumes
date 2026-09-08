using System;
using System.Collections.Generic;
using GTA;
using Fumes.Core;

namespace Fumes.Fuel
{
    /// <summary>
    /// One vehicle's tank.
    ///
    /// Litres, not a 0-1 fraction, because a pump sells litres and a price is per litre. A
    /// fraction would mean converting at every reading and would quietly make a bike and a
    /// tanker cost the same to fill.
    /// </summary>
    internal sealed class Tank
    {
        /// <summary>model hash and plate, joined. See Tanks.KeyFor for why both.</summary>
        public string Key;

        public float Capacity;
        public float Litres;

        /// <summary>Whether this is a battery rather than a tank. Only changes the words.</summary>
        public bool Electric;

        /// <summary>Whether the player has ever actually been in it. Only these are worth saving.</summary>
        public bool Known;

        /// <summary>
        /// What is actually IN it, not what you would buy today.
        ///
        /// Kept per tank because that is the only way it can mean anything: the economy has to
        /// follow the fuel that is in the car, and a car you filled with premium last week is
        /// still running on premium whatever the setting says now. Reading the setting at burn
        /// time would let somebody flip a menu row and change the mileage of every car they own
        /// retroactively, which is a cheat, not a feature.
        ///
        /// Last fill wins. Blending grades by volume would be more accurate and would need a
        /// paragraph to explain what a tank of 0.4 Premium means; this needs none.
        /// </summary>
        public FuelGrade Grade;

        public float Fraction => Capacity > 0.01f ? Litres / Capacity : 0f;
        public bool Empty => Litres <= 0.001f;

        public void Add(float litres)
        {
            Litres = Clamp(Litres + litres, 0f, Capacity);
        }

        public void Burn(float litres)
        {
            Litres = Clamp(Litres - litres, 0f, Capacity);
        }

        public static float Clamp(float v, float lo, float hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        // ------------------------------------------------------------------
        // Capacity
        // ------------------------------------------------------------------

        /// <summary>
        /// Fallback tank sizes in litres, by class.
        ///
        /// Only reached when the model has no usable fPetrolTankVolume of its own -- which
        /// happens more than you would think, because plenty of add-on and online models ship
        /// a zero there and the game does not care, having nothing that reads it.
        /// </summary>
        private static readonly Dictionary<VehicleClass, float> ByClass = new Dictionary<VehicleClass, float>
        {
            { VehicleClass.Compacts,        45f },
            { VehicleClass.Sedans,          65f },
            { VehicleClass.SUVs,            80f },
            { VehicleClass.Coupes,          60f },
            { VehicleClass.Muscle,          75f },
            { VehicleClass.SportsClassics,  65f },
            { VehicleClass.Sports,          65f },
            { VehicleClass.Super,           70f },
            { VehicleClass.Motorcycles,     16f },
            { VehicleClass.OffRoad,         80f },
            { VehicleClass.Industrial,     200f },
            { VehicleClass.Utility,        150f },
            { VehicleClass.Vans,            80f },
            { VehicleClass.Cycles,           0f },
            { VehicleClass.Boats,          200f },
            { VehicleClass.Helicopters,    400f },
            { VehicleClass.Planes,         500f },
            { VehicleClass.Service,        120f },
            { VehicleClass.Emergency,       90f },
            { VehicleClass.Military,       250f },
            { VehicleClass.Commercial,     300f },
            { VehicleClass.Trains,           0f },
            { VehicleClass.OpenWheel,      100f }
        };

        /// <summary>
        /// How big this vehicle's tank is.
        ///
        /// PetrolTankVolume is the handling file's own fPetrolTankVolume, which is the right
        /// answer whenever it is present -- it is per MODEL, so a Sultan and a Phantom differ
        /// the way they should without a table here listing every car in the game.
        ///
        /// It is not always present. Values of 0, and absurd ones, come back from models whose
        /// handling was written by somebody who knew nothing reads that field. So it is only
        /// believed inside a plausible range, and the class table catches the rest.
        /// </summary>
        public static float CapacityOf(Vehicle v)
        {
            var byClass = 65f;

            try
            {
                if (ByClass.TryGetValue(v.ClassType, out var c) && c > 0f) byClass = c;
            }
            catch
            {
                // Class unavailable; the 65 stands.
            }

            try
            {
                var handling = v.PetrolTankVolume;
                if (handling >= 5f && handling <= 800f) return handling;
            }
            catch
            {
                // No handling data reachable. Fall through.
            }

            return byClass;
        }

        /// <summary>
        /// The battery cars, BY NAME. The safety net under IsElectric, not the whole of it.
        ///
        /// This used to say there was no flag on the model. There is: vehicles.meta carries
        /// FLAG_IS_ELECTRIC, and ScriptHookVDotNet exposes it as Model.IsElectricVehicle in
        /// every build this mod runs on -- checked by reflection against 3.6.0, the 3.7.0.189
        /// nightly and 3.9.0, not assumed. That flag is the game's own answer, and it knows
        /// about every DLC electric and every add-on car this list has never heard of. The
        /// list was nineteen names and already short an I-Wagen and two Inductors.
        ///
        /// Kept underneath the flag rather than deleted, because a model whose meta is wrong
        /// is not unheard of and a name here costs nothing. Nothing about the simulation
        /// changes for these; the gauge says CHARGE instead of FUEL and the forecourt sells
        /// them electricity.
        ///
        /// NAMES, NOT HASHES, and the difference matters. A table of 0x8CD0264C literals is a
        /// table of numbers nobody can check, and a wrong one fails silently -- the car simply
        /// never says CHARGE and there is no way to tell that from "I typed it wrong". The game
        /// hashes these itself at start-up, so a name that is right is right.
        /// </summary>
        private static readonly string[] ElectricNames =
        {
            "voltic", "voltic2", "khamelion", "dilettante", "dilettante2", "surge",
            "caddy", "caddy2", "caddy3", "airtug", "tezeract", "neon", "raiden",
            "cyclone", "cyclone2", "imorgon", "omnisegt", "powersurge", "virtue"
        };

        private static HashSet<int> _electric;

        /// <summary>
        /// Built once, on first ask, from the names above.
        ///
        /// Not in a static initialiser: Model construction is a game call and running one at
        /// class-load time means running it during SHVDN's own script construction, which is
        /// not a moment the game is guaranteed to answer in.
        /// </summary>
        private static HashSet<int> Electrics()
        {
            if (_electric != null) return _electric;

            var set = new HashSet<int>();
            foreach (var name in ElectricNames)
            {
                try { set.Add(new Model(name).Hash); }
                catch { /* a name this build does not have is simply not electric */ }
            }

            _electric = set;
            return _electric;
        }

        public static bool IsElectric(Vehicle v)
        {
            try
            {
                var model = v.Model;

                // THE MODEL'S OWN FLAG FIRST. It is the reason a DLC electric or an add-on car
                // gets left alone without anybody here having typed its name.
                if (model.IsElectricVehicle) return true;

                return Electrics().Contains(model.Hash);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The word for what is in it. Only ever a label.</summary>
        public string Noun => Electric ? "CHARGE" : "FUEL";
    }
}
