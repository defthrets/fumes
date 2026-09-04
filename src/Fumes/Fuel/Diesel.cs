using System;
using System.Collections.Generic;
using GTA;
using Fumes.Core;

namespace Fumes.Fuel
{
    /// <summary>
    /// Which vehicles burn diesel.
    ///
    /// THE GAME DOES NOT KNOW. There is no diesel flag on a vehicle, nothing in handling.meta a
    /// script can reach, and no native that answers it -- the closest thing the game has to an
    /// opinion is the class it files a vehicle under, and that is a showroom category rather
    /// than a statement about the engine. So this is a judgement, made from the class and then
    /// corrected by name where the class is wrong.
    ///
    /// THE CLASSES ARE WRONG IN BOTH DIRECTIONS, which is why both override lists exist:
    ///
    ///   Service holds every bus in the game AND the Taxi. Buses are diesel; the Taxi is a
    ///   petrol saloon that happens to be filed with them because of what it is FOR.
    ///
    ///   Utility holds tractors and tow trucks alongside the Caddy and the Airtug, which are
    ///   battery carts, and the Lawn Mower, which is a petrol engine you can sit on.
    ///
    ///   Emergency holds the Fire Truck and Ambulance -- both diesel -- next to a fleet of
    ///   police cars that are not. That one is left OUT of the class list and the two diesels
    ///   named instead, because naming two is smaller and clearer than naming the eight
    ///   exceptions the other way round would need.
    ///
    /// Names are matched by HASH, not by string. A script cannot read a vehicle's model name
    /// back out of the game -- Model carries a hash and DisplayName gives you the showroom
    /// name, which is localised and is not what anyone writes in an ini. Game.GenerateHash is
    /// the same joaat the game itself uses, so hashing the configured names once at load turns
    /// the whole thing into an integer lookup.
    /// </summary>
    internal static class Diesel
    {
        private static HashSet<int> _diesel;
        private static HashSet<int> _petrol;
        private static HashSet<string> _classes;
        private static string _builtFrom;

        /// <summary>
        /// Whether this vehicle takes diesel.
        ///
        /// The name lists are checked BEFORE the class, and petrol before diesel, so an
        /// exception always beats the rule it is an exception to.
        /// </summary>
        public static bool Is(Settings cfg, Vehicle v)
        {
            if (cfg == null || !cfg.DieselVehicles) return false;
            if (v == null || !v.Exists()) return false;

            try
            {
                Build(cfg);

                var hash = v.Model.Hash;

                if (_petrol.Contains(hash)) return false;
                if (_diesel.Contains(hash)) return true;

                return _classes.Contains(v.ClassType.ToString());
            }
            catch (Exception ex)
            {
                Log.Once("diesel", "Could not work out the fuel type: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// What is actually going into this vehicle.
        ///
        /// A diesel vehicle can only ever hold diesel, so the answer comes from the VEHICLE and
        /// the stored grade is only ever consulted for petrol cars. That matters for a truck
        /// you have never filled: its tank was written before diesel existed, or generated
        /// part-full and never touched, so its stored grade is Regular -- and reading that
        /// literally would run a Phantom on petrol economy until the first visit to a pump.
        /// </summary>
        public static FuelGrade GradeFor(Settings cfg, Vehicle v, FuelGrade stored)
        {
            return Is(cfg, v) ? FuelGrade.Diesel : stored;
        }

        /// <summary>Hashes the configured names, and only when they have actually changed.</summary>
        private static void Build(Settings cfg)
        {
            var from = cfg.DieselClasses + "|" + cfg.DieselModels + "|" + cfg.PetrolModels;
            if (_builtFrom == from && _diesel != null) return;

            _diesel = Hashes(cfg.DieselModels);
            _petrol = Hashes(cfg.PetrolModels);
            _classes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in Split(cfg.DieselClasses)) _classes.Add(name);

            _builtFrom = from;

            Log.Debug("Diesel: " + _classes.Count + " classes, " + _diesel.Count +
                      " named diesel, " + _petrol.Count + " named petrol.");
        }

        private static HashSet<int> Hashes(string list)
        {
            var set = new HashSet<int>();

            // GenerateHash is marked obsolete in current SHVDN in favour of StringHash, and is
            // used anyway ON PURPOSE. It is the one that has been in every 3.x build; StringHash
            // is not, and this mod already fields more "it will not load" reports than anything
            // else. A deprecation warning at build time is a smaller cost than a MissingMethod
            // on somebody's older install.
#pragma warning disable 618
            foreach (var name in Split(list)) set.Add(Game.GenerateHash(name.ToLowerInvariant()));
#pragma warning restore 618

            return set;
        }

        private static IEnumerable<string> Split(string list)
        {
            if (string.IsNullOrEmpty(list)) yield break;

            foreach (var part in list.Split(','))
            {
                var name = part.Trim();
                if (name.Length > 0) yield return name;
            }
        }

        /// <summary>The word on the pump. Kept here so nothing has to switch on the enum.</summary>
        public static string Name(FuelGrade grade)
        {
            if (grade == FuelGrade.Diesel) return "DIESEL";
            if (grade == FuelGrade.Plus) return "PLUS";
            if (grade == FuelGrade.Premium) return "PREMIUM";
            return "REGULAR";
        }
    }
}
