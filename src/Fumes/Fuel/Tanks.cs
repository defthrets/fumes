using System;
using System.Collections.Generic;
using System.Globalization;
using GTA;
using Fumes.Core;

namespace Fumes.Fuel
{
    /// <summary>
    /// Every tank the mod knows about: the live ones keyed by entity handle, and the
    /// remembered ones keyed by something that survives a restart.
    ///
    /// The two are deliberately separate. A handle is a slot in the game's entity pool and
    /// the game RE-USES slots, so the car you parked and the taxi that spawned where it
    /// despawned can be the same number. Anything keyed on a handle alone eventually hands
    /// one vehicle another vehicle's fuel.
    /// </summary>
    internal sealed class Tanks
    {
        /// <summary>
        /// How many remembered vehicles are kept.
        ///
        /// The save is written every time it changes and read at every load, so it cannot be
        /// allowed to grow without limit -- and it would: ambient traffic is endless. Oldest
        /// out first when the cap is hit.
        /// </summary>
        private const int MaxRemembered = 3000;

        private sealed class Live
        {
            public string Key;
            public Tank Tank;
        }

        private sealed class Remembered
        {
            public float Litres;
            public float Capacity;
            public long Seen;      // Unix seconds, so the file is readable and sortable
            public int Grade;      // 0 regular, 1 plus, 2 premium
        }

        private readonly Dictionary<int, Live> _live = new Dictionary<int, Live>();

        private readonly Dictionary<string, Remembered> _saved =
            new Dictionary<string, Remembered>(StringComparer.Ordinal);

        private readonly Settings _cfg;
        private readonly Random _rng = new Random();

        private bool _dirty;
        private float _sinceSave;
        private float _sincePurge;

        public Tanks(Settings cfg)
        {
            _cfg = cfg;
            LoadFromDisk();
        }

        // ------------------------------------------------------------------
        // Identity
        // ------------------------------------------------------------------

        /// <summary>
        /// What names a vehicle across sessions.
        ///
        /// Model plus plate. Neither alone is enough: every Sultan shares a model, and plates
        /// are only unique-ish, so a plate on its own would let a stolen Sultan inherit the
        /// fuel of a Sultan you drove last week. Together they are wrong about as often as two
        /// identical cars with identical plates, which is never in practice.
        ///
        /// Returns null for anything with no plate -- boats, aircraft, some trailers. Those
        /// still get a tank, it just lives and dies with the session, because there is nothing
        /// stable to write it under and a made-up key would be a different vehicle next time.
        /// </summary>
        public static string KeyFor(Vehicle v)
        {
            try
            {
                var plate = v.Mods == null ? null : v.Mods.LicensePlate;
                if (string.IsNullOrEmpty(plate)) return null;

                plate = plate.Trim().ToUpperInvariant();
                if (plate.Length == 0) return null;

                return v.Model.Hash.ToString("X8", CultureInfo.InvariantCulture) + ":" + plate;
            }
            catch
            {
                return null;
            }
        }

        // ------------------------------------------------------------------
        // Lookup
        // ------------------------------------------------------------------

        /// <summary>Whether this vehicle is one Fumes bothers with at all.</summary>
        public bool Covers(Vehicle v)
        {
            if (v == null || !v.Exists()) return false;

            try
            {
                switch (v.ClassType)
                {
                    case VehicleClass.Cycles:
                    case VehicleClass.Trains:
                        return false;

                    case VehicleClass.Boats:
                        return _cfg.AffectBoats;

                    case VehicleClass.Helicopters:
                    case VehicleClass.Planes:
                        return _cfg.AffectAircraft;

                    default:
                        return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// This vehicle's tank, made on the spot if it has not been seen before.
        ///
        /// Null when the vehicle is one we do not cover, so every caller has to deal with
        /// null anyway and there is no second "is this covered" check to forget.
        /// </summary>
        public Tank For(Vehicle v)
        {
            if (!Covers(v)) return null;

            int handle;
            try { handle = v.Handle; }
            catch { return null; }

            var key = KeyFor(v);

            if (_live.TryGetValue(handle, out var live))
            {
                // Same slot, same vehicle? If the key moved, the pool recycled the handle and
                // this tank belongs to a car that no longer exists.
                if (string.Equals(live.Key, key, StringComparison.Ordinal)) return live.Tank;

                Log.Debug("Handle " + handle + " changed identity (" + (live.Key ?? "-") +
                          " -> " + (key ?? "-") + "); rebuilding its tank.");
                _live.Remove(handle);
            }

            var tank = Create(v, key);
            _live[handle] = new Live { Key = key, Tank = tank };
            return tank;
        }

        /// <summary>
        /// Named Create and not Build, which is what it was.
        ///
        /// Core.Build is the class holding the version string, and a METHOD called Build
        /// in this type shadows it in expression position -- so Build.Version stopped
        /// compiling with an error that talks about method groups and never mentions the
        /// class it is really about.
        /// </summary>
        private Tank Create(Vehicle v, string key)
        {
            var capacity = Tank.CapacityOf(v);

            var tank = new Tank
            {
                Key = key,
                Capacity = capacity,
                Electric = Tank.IsElectric(v)
            };

            if (key != null && _saved.TryGetValue(key, out var remembered))
            {
                // The tank size is taken fresh from the model every time, not from the save.
                // A handling edit, or an update to this mod's own fallback table, should move
                // the capacity -- but the LITRES in it are the player's and must not be scaled
                // out from under them, so they are clamped rather than rescaled.
                tank.Litres = Tank.Clamp(remembered.Litres, 0f, capacity);
                tank.Known = true;
                tank.Grade = (FuelGrade)remembered.Grade;

                if (Math.Abs(remembered.Capacity - capacity) > 0.5f)
                {
                    Log.Debug("Tank size for " + key + " moved " + remembered.Capacity.ToString("0.#") +
                              " -> " + capacity.ToString("0.#") + " L.");
                }

                return tank;
            }

            tank.Litres = capacity * FoundFraction(v);
            return tank;
        }

        /// <summary>
        /// How full a vehicle is the first time it is ever seen.
        ///
        /// Anything the game has marked persistent is the player's -- a personal vehicle, a
        /// mission car, something spawned by another mod on purpose -- and starting one of
        /// those on a quarter tank turns a mod about fuel into a mod about walking. Traffic
        /// gets a spread, so hijacking is a gamble.
        /// </summary>
        private float FoundFraction(Vehicle v)
        {
            // A FULL TANK FOR MISSION VEHICLES, AND ONLY THOSE.
            //
            // This was "if (v.IsPersistent) return 1f", which meant every DLC car arrived at
            // 100% -- IsPersistent is true of anything script-owned, and that covers everything
            // a trainer spawns, which is how anyone gets at the online cars at all.
            //
            // Then it was off entirely, and that swung too far the other way. A story mission
            // that hands you a car for a scripted drive now hands you one at a random 18 to 85
            // per cent, and a mission written on the assumption of a full tank can fail through
            // nothing the player did. Trading a cosmetic annoyance for a soft-lock is a bad
            // trade even when the annoyance is the one being complained about.
            //
            // GET_MISSION_FLAG separates them, and it is the only thing that does: both kinds
            // of vehicle are persistent, but only one of them appears while a mission is
            // actually running. A trainer spawn in free roam is a found car; a car the game
            // gives you mid-mission is not.
            if (_cfg.MissionTanksFull)
            {
                try
                {
                    if (v.IsPersistent && Game.IsMissionActive) return 1f;
                }
                catch
                {
                    // Unknown; treat as traffic.
                }
            }

            var lo = _cfg.FoundFuelMin;
            var hi = _cfg.FoundFuelMax;
            return lo + (float)_rng.NextDouble() * (hi - lo);
        }

        // ------------------------------------------------------------------
        // Persistence
        // ------------------------------------------------------------------

        /// <summary>
        /// Marks a tank worth remembering, and the file worth rewriting.
        ///
        /// Called when the player gets in, and whenever fuel changes for a vehicle that is
        /// already Known. Traffic the player never touched is deliberately never written --
        /// otherwise every car that drives past is a line in a file forever.
        /// </summary>
        public void Touch(Vehicle v, Tank tank, bool playerWasIn)
        {
            if (tank == null) return;
            if (playerWasIn) tank.Known = true;
            if (!tank.Known || tank.Key == null) return;

            if (!_saved.TryGetValue(tank.Key, out var r))
            {
                r = new Remembered();
                _saved[tank.Key] = r;
            }

            r.Litres = tank.Litres;
            r.Capacity = tank.Capacity;
            r.Seen = Now();
            _dirty = true;
        }

        private static long Now()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        /// <summary>Housekeeping. Call once a tick; it decides for itself how often to act.</summary>
        public void Update(float dt)
        {
            _sinceSave += dt;
            _sincePurge += dt;

            if (_sincePurge >= 20f)
            {
                _sincePurge = 0f;
                DropDeadHandles();
            }

            // Not on every change: a save per litre would be a file write per frame while
            // somebody stands at a pump.
            if (_dirty && _sinceSave >= 10f)
            {
                _sinceSave = 0f;
                SaveToDisk();
            }
        }

        /// <summary>Forget live entries whose vehicle has gone, so the map does not grow all session.</summary>
        private void DropDeadHandles()
        {
            List<int> gone = null;

            foreach (var pair in _live)
            {
                bool alive;
                try
                {
                    var e = Entity.FromHandle(pair.Key);
                    alive = e != null && e.Exists();
                }
                catch { alive = false; }

                if (alive) continue;

                (gone ?? (gone = new List<int>())).Add(pair.Key);
            }

            if (gone == null) return;

            foreach (var h in gone) _live.Remove(h);
            Log.Debug("Dropped " + gone.Count + " despawned vehicle(s) from the live table.");
        }

        public void LoadFromDisk()
        {
            var root = JsonFile.Read(Paths.TanksFile, out var read);

            if (read == ReadResult.Missing)
            {
                Log.Info("No saved tanks yet - every vehicle will be found as it is.");
                return;
            }

            if (read != ReadResult.Ok || root == null)
            {
                // Deliberately NOT starting empty. An unreadable file is a file that is there,
                // and overwriting it on the next save would throw away every tank in it. Leave
                // it alone; the session runs on fresh tanks and the file stays for a human.
                Log.Error("tanks.json is there but could not be read - not touching it. " +
                          "Fuel will not persist this session.");
                _readFailed = true;
                return;
            }

            try
            {
                var list = root["tanks"];
                foreach (var key in list.Keys)
                {
                    var node = list[key];
                    _saved[key] = new Remembered
                    {
                        Litres = node["l"].AsFloat(0f),
                        Capacity = node["c"].AsFloat(65f),
                        Seen = node["t"].AsLong(Now()),

                        // Absent in files written before grades existed, which is every file
                        // out there. Regular is both the default and the right answer for fuel
                        // bought when there was only one kind.
                        Grade = node["g"].AsInt(0)
                    };
                }

                Log.Info("Remembered " + _saved.Count + " tank(s).");
            }
            catch (Exception ex)
            {
                Log.Error("tanks.json parsed but did not hold what was expected.", ex);
                _readFailed = true;
            }
        }

        /// <summary>Set when the file on disk could not be understood. Blocks every write after.</summary>
        private bool _readFailed;

        public void SaveToDisk()
        {
            if (_readFailed)
            {
                Log.Once("save-blocked", "Not writing tanks.json - it could not be read at start-up " +
                                         "and overwriting it would destroy whatever is in there.");
                return;
            }

            try
            {
                Prune();

                var tanks = Json.Object();
                foreach (var pair in _saved)
                {
                    tanks.Set(pair.Key, Json.Object()
                        .Set("l", Math.Round(pair.Value.Litres, 2))
                        .Set("c", Math.Round(pair.Value.Capacity, 1))
                        .Set("t", pair.Value.Seen)
                        .Set("g", pair.Value.Grade));
                }

                var root = Json.Object()
                    .Set("version", Build.Version)
                    .Set("tanks", tanks);

                if (JsonFile.Write(Paths.TanksFile, root)) _dirty = false;
            }
            catch (Exception ex)
            {
                Log.Error("Could not save tanks.json", ex);
            }
        }

        /// <summary>Oldest out first, once there are more than the cap.</summary>
        private void Prune()
        {
            if (_saved.Count <= MaxRemembered) return;

            var keys = new List<KeyValuePair<string, Remembered>>(_saved);
            keys.Sort((a, b) => a.Value.Seen.CompareTo(b.Value.Seen));

            var drop = _saved.Count - MaxRemembered;
            for (var i = 0; i < drop; i++) _saved.Remove(keys[i].Key);

            Log.Info("Forgot " + drop + " long-unseen vehicle(s) to keep tanks.json bounded.");
        }

        /// <summary>Number of live tanks, for the log line and nothing else.</summary>
        public int LiveCount => _live.Count;
    }
}
