using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;
using Fumes.Core;
using Fumes.Fuel;

namespace Fumes.Station
{
    /// <summary>
    /// Traffic that pulls into a forecourt when it is low, and drives off again.
    ///
    /// THE OBVIOUS VERSION OF THIS DOES NOT WORK, and it is worth writing down why, because it
    /// is the first thing anyone asks for: a low car should find the nearest station and drive
    /// to it. Stations sit a median six hundred metres apart. The mod only simulates a hundred
    /// and twenty metres around the player, and GTA despawns traffic well before that -- so a
    /// car tasked to drive half a kilometre for fuel evaporates on the way, every time. Keeping
    /// it alive means marking it a mission entity, and mission entities are never reclaimed:
    /// every low NPC would sit in the vehicle pool for the rest of the session. A mod that
    /// leaks cars gets worse the longer you play.
    ///
    /// SO IT IS INVERTED. Not "low car seeks distant station" but "car ALREADY NEAR a station
    /// pulls in". Everything then happens inside the radius that was going to be the problem:
    /// nothing needs persisting, nothing leaks, and the behaviour lands exactly where the
    /// player spends their time in this mod -- standing at a pump, watching traffic pull in
    /// beside them. It is ambience rather than simulation, and ambience is what it was ever
    /// going to be at the rate anybody would witness it.
    ///
    /// NOTHING IS EVER HELD ONTO. Every entity is looked up fresh from its handle each pass and
    /// dropped the moment it stops existing. A car that despawns mid-manoeuvre leaves one dead
    /// dictionary entry, which the next sweep removes.
    /// </summary>
    internal sealed class TrafficRefuel
    {
        private readonly Settings _cfg;
        private readonly Tanks _tanks;
        private readonly Stations _stations;

        private enum Phase
        {
            /// <summary>On its way in.</summary>
            Driving,

            /// <summary>Sitting at the pumps.</summary>
            Filling
        }

        private sealed class Visit
        {
            public int Ped;
            public Phase Phase;
            public Vector3 Pumps;

            /// <summary>Game time this phase gives up at, so nothing waits forever.</summary>
            public int Until;
        }

        private readonly Dictionary<int, Visit> _visiting = new Dictionary<int, Visit>();
        private readonly List<int> _finished = new List<int>();

        private float _since;

        public TrafficRefuel(Settings cfg, Tanks tanks, Stations stations)
        {
            _cfg = cfg;
            _tanks = tanks;
            _stations = stations;
        }

        public void Update(Ped me, float dt)
        {
            if (!_cfg.TrafficRefuels) return;

            try
            {
                Progress();

                // A couple of seconds between sweeps. Nothing here is urgent -- a car that gets
                // low now can pull in two seconds later and nobody could tell.
                _since += dt;
                if (_since < 2f) return;

                _since = 0f;

                Look(me);
            }
            catch (Exception ex)
            {
                Log.Once("traffic-refuel", "The forecourt traffic fell over: " + ex.Message);
            }
        }

        /// <summary>Finds a low car that happens to be near a station, and sends it in.</summary>
        private void Look(Ped me)
        {
            if (_visiting.Count >= _cfg.TrafficRefuelMax) return;

            foreach (var v in World.GetNearbyVehicles(me.Position, 120f))
            {
                if (_visiting.Count >= _cfg.TrafficRefuelMax) return;

                if (v == null || !v.Exists() || v.IsDead) continue;
                if (_visiting.ContainsKey(v.Handle)) continue;

                // Never the player's, and never one he is sitting in. Taking the wheel of a car
                // with the player as a passenger would be a mod driving him somewhere he did
                // not ask to go.
                if (me.CurrentVehicle != null && me.CurrentVehicle.Handle == v.Handle) continue;
                if (v.IsEngineRunning == false) continue;

                var driver = v.Driver;
                if (driver == null || !driver.Exists() || driver.IsDead) continue;
                if (driver.Handle == me.Handle) continue;

                var tank = _tanks.For(v);
                if (tank == null || tank.Fraction > _cfg.TrafficRefuelBelow) continue;

                var pumps = _stations.Nearest(v.Position, _cfg.TrafficRefuelRadius);
                if (pumps == null) continue;

                Send(v, driver, pumps.Position);
            }
        }

        private void Send(Vehicle v, Ped driver, Vector3 pumps)
        {
            try
            {
                Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD,
                              driver.Handle, v.Handle,
                              pumps.X, pumps.Y, pumps.Z,
                              12f,                       // no hurry; it is going a hundred metres
                              0,
                              v.Model.Hash,
                              _cfg.TrafficDrivingStyle,
                              8f,                        // stop this far out: the forecourt, not the sign
                              -1f);

                _visiting[v.Handle] = new Visit
                {
                    Ped = driver.Handle,
                    Phase = Phase.Driving,
                    Pumps = pumps,

                    // A hard deadline, because a car can be boxed in, wrecked, or simply
                    // unable to reach the point. Without one it would hold a slot forever and
                    // two stuck cars would stop this ever happening again.
                    Until = Game.GameTime + (int)(_cfg.TrafficRefuelGiveUpSeconds * 1000f)
                };

                Log.Debug(v.LocalizedName + " is low and pulling into a station.");
            }
            catch (Exception ex)
            {
                Log.Once("traffic-send", "Could not send a car to a station: " + ex.Message);
            }
        }

        /// <summary>Moves everything already on its way along, and clears out the dead.</summary>
        private void Progress()
        {
            if (_visiting.Count == 0) return;

            _finished.Clear();

            foreach (var pair in _visiting)
            {
                var v = Entity.FromHandle(pair.Key) as Vehicle;

                if (v == null || !v.Exists() || v.IsDead) { _finished.Add(pair.Key); continue; }

                var visit = pair.Value;

                if (Game.GameTime > visit.Until) { Release(v, visit); _finished.Add(pair.Key); continue; }

                if (visit.Phase == Phase.Driving)
                {
                    if (v.Position.DistanceTo(visit.Pumps) > 14f) continue;

                    visit.Phase = Phase.Filling;
                    visit.Until = Game.GameTime + (int)(_cfg.TrafficRefuelSeconds * 1000f);

                    try { Function.Call(Hash.CLEAR_PED_TASKS, visit.Ped); }
                    catch { /* it will drift; the release still fires */ }

                    continue;
                }

                // Filling. Nothing to do but wait -- the deadline above ends it.
            }

            foreach (var handle in _finished) _visiting.Remove(handle);
        }

        /// <summary>Tops the tank up and puts the driver back into traffic.</summary>
        private void Release(Vehicle v, Visit visit)
        {
            try
            {
                var tank = _tanks.For(v);

                // Not to the brim. A tank at exactly full would re-trigger the moment it dipped,
                // and a forecourt with the same car pulling in every minute is worse than one
                // where nothing happens at all.
                if (tank != null && visit.Phase == Phase.Filling)
                {
                    tank.Add(tank.Capacity * 0.85f - tank.Litres);
                }

                var driver = Entity.FromHandle(visit.Ped) as Ped;

                if (driver != null && driver.Exists() && !driver.IsDead)
                {
                    Function.Call(Hash.CLEAR_PED_TASKS, driver.Handle);

                    // BACK INTO TRAFFIC, not just released. A ped left with no task after a
                    // scripted one sits at the wheel doing nothing, and a car parked across a
                    // forecourt entrance is the sort of thing that gets a mod uninstalled.
                    Function.Call(Hash.TASK_VEHICLE_DRIVE_WANDER,
                                  driver.Handle, v.Handle, 15f, _cfg.TrafficDrivingStyle);
                }
            }
            catch (Exception ex)
            {
                Log.Once("traffic-release", "Could not send a car back into traffic: " + ex.Message);
            }
        }
    }
}
