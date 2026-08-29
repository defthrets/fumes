using System;
using GTA;
using GTA.Math;
using GTA.Native;
using Fumes.Core;

namespace Fumes.Station
{
    /// <summary>
    /// Finds the pump you are standing at.
    ///
    /// BY PROP, NOT BY COORDINATE, and that is the single most important decision in this
    /// file. A hardcoded list of forecourt positions is wrong the moment anybody installs a
    /// map mod, adds a station, or plays on a build whose pumps sit a metre from where the
    /// list says -- and it is wrong silently, with the player standing at a pump that the mod
    /// insists is not there. Asking the game which pump object is nearest is exact, works on
    /// every station including ones nobody has written down, and costs a handful of native
    /// calls a second.
    ///
    /// Stations.cs still keeps a coordinate list, but only for map blips and for pricing --
    /// nothing there can stop a refuel working.
    /// </summary>
    internal sealed class Pumps
    {
        /// <summary>
        /// Every petrol pump model the base game places.
        ///
        /// The _lod variants are deliberately absent: they are the low-detail stand-ins the
        /// streamer swaps in at distance, and matching one means offering a nozzle from a pump
        /// that is not really drawn yet.
        /// </summary>
        private static readonly string[] PumpModels =
        {
            "prop_gas_pump_1a",
            "prop_gas_pump_1b",
            "prop_gas_pump_1c",
            "prop_gas_pump_1d",
            "prop_gas_pump_old2",
            "prop_gas_pump_old3",
            "prop_vintage_pump"
        };

        private int[] _hashes;

        /// <summary>The scan is not free, so it runs on a clock rather than every frame.</summary>
        private int _nextScan;

        private Prop _found;

        /// <summary>Hashes are computed once, lazily, for the reason given in Tank.Electrics.</summary>
        private int[] Hashes()
        {
            if (_hashes != null) return _hashes;

            var list = new System.Collections.Generic.List<int>();
            foreach (var name in PumpModels)
            {
                try { list.Add(new Model(name).Hash); }
                catch { /* a model this build lacks simply never matches */ }
            }

            _hashes = list.ToArray();
            Log.Info("Watching for " + _hashes.Length + " pump model(s).");
            return _hashes;
        }

        /// <summary>The pump within reach, or null. Re-scanned a few times a second.</summary>
        public Prop Nearest(Vector3 from, float radius)
        {
            var now = Game.GameTime;

            // A pump already held on to stays valid between scans, so the prompt does not
            // flicker at the edge of the radius and a hose in use is not dropped mid-frame.
            //
            // BUT IT IS STILL RANGE-CHECKED. Without this the cache hands back a pump the
            // player has already walked away from for up to a quarter of a second -- long
            // enough to take a nozzle off a pump twenty metres behind you.
            if (now < _nextScan && _found != null && _found.Exists() &&
                _found.Position.DistanceTo(from) <= radius)
            {
                return _found;
            }

            _nextScan = now + 250;
            _found = Scan(from, radius);
            return _found;
        }

        private Prop Scan(Vector3 from, float radius)
        {
            Prop best = null;
            var bestDist = float.MaxValue;

            foreach (var hash in Hashes())
            {
                try
                {
                    // GET_CLOSEST_OBJECT_OF_TYPE sees MAP objects, which is the whole point:
                    // pumps are placed in the world, not spawned, so they are not in the pool
                    // that GET_ALL_OBJECTS walks and World.GetNearbyProps cannot see them.
                    var handle = Function.Call<int>(Hash.GET_CLOSEST_OBJECT_OF_TYPE,
                                                    from.X, from.Y, from.Z, radius, hash,
                                                    false, false, false);
                    if (handle == 0) continue;

                    // Entity.FromHandle rather than a constructor: SHVDN 3.9 does not
                    // expose public pool-object constructors, and FromHandle returns the
                    // right subclass or null for a handle that is not live any more.
                    var prop = Entity.FromHandle(handle) as Prop;
                    if (prop == null || !prop.Exists()) continue;

                    var d = prop.Position.DistanceTo(from);
                    if (d >= bestDist) continue;

                    best = prop;
                    bestDist = d;
                }
                catch (Exception ex)
                {
                    Log.Once("pump-scan", "Pump lookup failed: " + ex.Message);
                    return null;
                }
            }

            return best;
        }

    }
}
