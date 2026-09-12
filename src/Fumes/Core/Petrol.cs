using System;
using System.Collections.Generic;
using GTA;
using GTA.Native;

namespace Fumes.Core
{
    /// <summary>
    /// Cars that are scenery, and do not burn fuel.
    ///
    /// A MOD'S PROP CAR IS NOT A CAR. The takeover parks a row of them across the junction
    /// with their engines and their radios held on, because a car with a dead engine has no
    /// radio and the music IS the party; the car meet's lowrider is the sound system for the
    /// same reason; the boombox is an invisible car under a speaker prop. None of them is
    /// ever driven, nobody is ever in one, and not one of them is a vehicle in any sense the
    /// player would recognise -- they are furniture with a stereo in.
    ///
    /// AND FUMES WAS TAKING THEIR PETROL. It watches every vehicle in the world, an idling
    /// engine burns, and a parked prop idles for the length of a party -- so the tank emptied
    /// and Fumes cut the engine, which is exactly what it is for. Then the takeover's own
    /// five-second pass turned it straight back on, because the music had stopped. Off, on,
    /// off, on, for as long as anybody stood there. Neither mod was wrong; they were answering
    /// different questions about the same car.
    ///
    /// So one of them has to say which. This is the channel: the mod that put the car there
    /// marks it, and Fumes leaves it alone -- no tank, no gauge, no prompt at a pump and no
    /// fuel burned, because Covers is its one gate.
    ///
    /// THE HANDLE AND THE MODEL, NOT THE HANDLE. The game reuses entity handles, so a handle
    /// on its own would eventually exempt somebody's actual car; the model is stored with it
    /// and both have to match. Entries whose vehicle has gone are dropped as they are found.
    ///
    /// Same channel as everything else the set shares: SHVDN loads every script into one
    /// AppDomain, so data parked there is visible to all of them. See UI.Ledger, which does
    /// the same for the rectangle budget.
    ///
    /// This file is the same in every mod of the set, byte for byte after the namespace.
    /// Edit the copy in Hoodrich and run tools/sync-ledger.py.
    /// </summary>
    internal static class Petrol
    {
        private const string Key = "spitmux.fuel.spared";

        private static Dictionary<int, int> _spared;
        private static bool _broken;

        /// <summary>This one is scenery. It burns nothing from now until it is released.</summary>
        public static void Spare(Vehicle car)
        {
            if (car == null || !car.Exists()) return;
            if (!Bind()) return;

            try
            {
                _spared[car.Handle] = car.Model.Hash;
            }
            catch
            {
                // Then it burns fuel, which is the old behaviour and not a crash.
            }

            Sweep();
        }

        /// <summary>It is somebody's car again -- being driven off, or handed back to the game.</summary>
        public static void Release(Vehicle car)
        {
            if (car == null) return;
            if (!Bind()) return;

            try
            {
                _spared.Remove(car.Handle);
            }
            catch
            {
                // It stays on the list until its handle is swept.
            }
        }

        /// <summary>
        /// Whether a fuel mod should leave this one alone.
        ///
        /// NOT WHILE HE IS IN IT, and that rule is what makes the rest of this safe. Marking
        /// is done in a dozen places across the set and releasing is done in fewer, so sooner
        /// or later a car is marked and never let go -- and a car that is permanently exempt
        /// is a car with free petrol for ever, which is a cheat nobody asked for and nobody
        /// would ever find. The moment the player is sitting in one it is a car again,
        /// whatever any list says, so the worst a forgotten mark can do is spare a parked
        /// prop that was burning nothing anyway.
        /// </summary>
        public static bool Spared(Vehicle car)
        {
            if (car == null || !car.Exists()) return false;
            if (!Bind() || _spared.Count == 0) return false;

            try
            {
                var me = Game.Player.Character;

                if (me != null && me.Exists() && me.IsInVehicle(car)) return false;
            }
            catch
            {
                // Then the list decides, which is the answer it would have given anyway.
            }

            try
            {
                int model;
                if (!_spared.TryGetValue(car.Handle, out model)) return false;

                // The same handle, a different car. See the class note.
                return model == car.Model.Hash;
            }
            catch
            {
                return false;
            }
        }

        private static bool Bind()
        {
            if (_spared != null) return true;
            if (_broken) return false;

            try
            {
                var domain = AppDomain.CurrentDomain;

                var had = domain.GetData(Key) as Dictionary<int, int>;

                if (had == null)
                {
                    had = new Dictionary<int, int>();
                    domain.SetData(Key, had);
                }

                _spared = had;
                return true;
            }
            catch
            {
                _broken = true;
                return false;
            }
        }

        /// <summary>
        /// Drops the ones whose car has gone.
        ///
        /// On marking rather than on a clock: the list only grows when somebody adds to it,
        /// so that is the only moment it can need tidying, and a party is a dozen entries
        /// rather than a thousand.
        /// </summary>
        private static void Sweep()
        {
            if (_spared.Count < 32) return;

            List<int> dead = null;

            foreach (var handle in _spared.Keys)
            {
                var there = false;

                try { there = Function.Call<bool>(Hash.DOES_ENTITY_EXIST, handle); }
                catch { there = true; }

                if (there) continue;

                if (dead == null) dead = new List<int>();
                dead.Add(handle);
            }

            if (dead == null) return;

            foreach (var handle in dead) _spared.Remove(handle);
        }
    }
}
