using System;
using GTA;
using GTA.Math;
using GTA.Native;
using Fumes.Core;
using Fumes.Fuel;
using Fumes.Station;
using Fumes.UI;

namespace Fumes
{
    /// <summary>
    /// Script entry point and the only owner of the update loop.
    ///
    /// ONE Script subclass, deliberately. SHVDN instantiates every Script it finds and ticks
    /// them in an order it does not define; a single entry point means the order our own
    /// subsystems run in is ours to decide, and there is exactly one place that has to be
    /// exception-safe.
    /// </summary>
    public sealed class Main : Script
    {
        /// <summary>Consecutive tick failures before the script parks itself rather than spamming.</summary>
        private const int MaxConsecutiveFailures = 10;

        /// <summary>
        /// Core.Settings, spelt out in full every time.
        ///
        /// Script -- the SHVDN base class -- has its own inherited Settings property, and it
        /// shadows our type in expression position. Written bare, Settings.Load() does not
        /// compile and the error points at the wrong thing entirely.
        /// </summary>
        private readonly Core.Settings _cfg;

        private readonly Tanks _tanks;
        private readonly Consumption _burn;
        private readonly Starvation _starve;
        private readonly Pumps _pumps;
        private readonly Stations _stations;
        private readonly Gauge _gauge;
        private readonly Menu _menu;
        private readonly Ignition _ignition;
        private readonly Meter _meter;
        private readonly Buttons _buttons;
        private readonly Refuel _refuel;

        /// <summary>
        /// The vehicle whose fuel is being simulated.
        ///
        /// The one the player is in, or the last one they were in while it still exists. The
        /// second half matters: leaving the engine running and walking into a shop should cost
        /// fuel, and a car abandoned with the key in should be flat when you come back to it.
        /// </summary>
        private Vehicle _watched;

        private int _failures;
        private bool _parked;

        /// <summary>How long since traffic was last looked at, when traffic is being looked at.</summary>
        private float _sinceTraffic;

        /// <summary>Whether the engine under the player is currently being held off for want of fuel.</summary>
        private bool _stalled;

        /// <summary>Seconds since the last look round for pumps to correct the map with.</summary>
        private float _sinceSurvey;

        public Main()
        {
            _cfg = Core.Settings.Load();

            // Before anything can ask for a rope: picks up a probe file left behind by a
            // session that ended in a crash, and blacklists whatever it was holding.
            RopeProbe.Review(_cfg);

            _tanks = new Tanks(_cfg);
            _burn = new Consumption(_cfg);
            _starve = new Starvation(_cfg);
            _pumps = new Pumps();
            _stations = new Stations(_cfg);
            _gauge = new Gauge(_cfg);
            _menu = new Menu(_cfg, _gauge);
            _ignition = new Ignition(_cfg, _tanks);
            _meter = new Meter(_cfg, _gauge);
            _buttons = new Buttons();
            _refuel = new Refuel(_cfg, _tanks, _pumps, _stations, _gauge, _meter, _buttons);

            Interval = 0;
            Tick += OnTick;
            Aborted += OnAborted;

            Log.Info(Build.Name + " " + Build.Version + " loaded. " +
                     _stations.Count + " station(s), interact key " + _cfg.InteractKey + ".");

            if (!_cfg.Enabled)
            {
                Log.Warn("[General] Enabled is false - nothing will run until it is turned on.");
            }
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (_parked || !_cfg.Enabled) return;

            try
            {
                var me = Game.Player.Character;
                if (me == null || !me.Exists()) return;

                Greet();

                var dt = Game.LastFrameTime;

                // A paused or hitching game hands back a dt of zero or of several seconds.
                // Neither is a second of driving, and the large one would empty a tank in a
                // single frame after an alt-tab.
                if (dt <= 0f || dt > 0.5f) dt = dt > 0.5f ? 0.5f : 0f;

                // FIRST, so a key pressed to open the menu is not also read by the refuel
                // prompts on the same frame, and so Placing is known before anything draws.
                _menu.Update();

                _refuel.InputBlocked = _menu.IsOpen;

                // Not while the menu has the keyboard, or Backspace and the arrows would be
                // steering a car nobody is looking at.
                if (!_menu.IsOpen) _ignition.Update(me);

                _stations.ShowBlips();
                _tanks.Update(dt);

                Survey(dt);

                Watch(me);
                Burn(dt);

                if (_cfg.AffectTraffic) Traffic(dt);

                _refuel.Update(dt);

                // The gauge, unless the refuel screen is already showing one for the thing
                // being filled -- two gauges for two different vehicles is just confusing.
                // Not while the positioner has it: that draws its own copy, at a made-up
                // level, and two gauges in the same place is a smear rather than a preview.
                if (!_menu.Placing &&
                    _refuel.TargetTank == null && _watched != null && _watched.Exists())
                {
                    _gauge.Update(_watched, _tanks.For(_watched), false, _stalled);
                }

                // LAST, and it has to be: the bar draws whatever was queued during this
                // tick, so every Show call has to have happened already.
                _buttons.Render();

                _failures = 0;
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        /// <summary>Says hello once, after the game is actually running rather than in the constructor.</summary>
        private bool _greeted;

        private void Greet()
        {
            if (_greeted) return;
            _greeted = true;

            if (!_cfg.AnnounceOnLoad) return;

            try
            {
                GTA.UI.Notification.PostTicker(
                    "~b~" + Build.Name + "~s~ " + Build.Version + " by " + Build.By, false, false);
            }
            catch
            {
                // Not being able to say hello is not a reason to stop.
            }
        }

        /// <summary>Picks the vehicle to simulate, and remembers a tank the moment he gets in.</summary>
        private void Watch(Ped me)
        {
            Vehicle now = null;

            try
            {
                now = me.IsInVehicle() ? me.CurrentVehicle : me.LastVehicle;
                if (now != null && !now.Exists()) now = null;
            }
            catch
            {
                now = null;
            }

            if (now == _watched) return;

            _watched = now;
            if (_watched == null) return;

            var tank = _tanks.For(_watched);
            if (tank == null) return;

            // Getting in is what makes a vehicle worth remembering across sessions.
            _tanks.Touch(_watched, tank, true);

            Log.Debug("Watching " + _watched.LocalizedName + ": " +
                      tank.Litres.ToString("0.0") + "/" + tank.Capacity.ToString("0.0") + " L.");
        }

        private void Burn(float dt)
        {
            if (_watched == null || !_watched.Exists() || dt <= 0f) return;

            var tank = _tanks.For(_watched);
            if (tank == null) return;

            var before = tank.Litres;
            tank.Burn(_burn.Burn(_watched, tank, dt));

            Mirror(_watched, tank);

            // Only the vehicle the player is actually in gets cut out. A car left running in
            // a car park does not need its engine held off every frame from across the map.
            var inIt = false;
            try
            {
                var me = Game.Player.Character;
                inIt = me != null && me.Exists() && me.CurrentVehicle == _watched;
            }
            catch
            {
                // Treat as not in it.
            }

            if (inIt)
            {
                _starve.Update(_watched, tank);
                _stalled = _starve.Stalled;
            }
            else
            {
                _stalled = false;
            }

            // The save follows real change, not every frame: without the threshold this is a
            // dirty flag set sixty times a second forever.
            if (Math.Abs(before - tank.Litres) > 0.02f) _tanks.Touch(_watched, tank, false);
        }

        /// <summary>
        /// Copies our litres into the game's own fuel field.
        ///
        /// The game keeps a fuel level of its own and uses it for exactly one thing -- how a
        /// damaged tank leaks and burns. Nothing reads it for driving, so it cannot be used
        /// AS the fuel system, but leaving it at full while our tank is empty means a car that
        /// is out of fuel still spills a full tank across the road when it is shot.
        /// </summary>
        private static void Mirror(Vehicle v, Tank tank)
        {
            try
            {
                if (Math.Abs(v.FuelLevel - tank.Litres) < 0.15f) return;
                v.FuelLevel = tank.Litres;
            }
            catch
            {
                // Cosmetic. Not worth a log line per frame.
            }
        }

        /// <summary>
        /// Ambient traffic, when somebody has asked for it.
        ///
        /// Once a second over a short radius, not every frame over the whole world. Off by
        /// default; see the note on Settings.AffectTraffic for why.
        /// </summary>
        private void Traffic(float dt)
        {
            _sinceTraffic += dt;
            if (_sinceTraffic < 1f) return;

            var slice = _sinceTraffic;
            _sinceTraffic = 0f;

            try
            {
                var me = Game.Player.Character;
                if (me == null || !me.Exists()) return;

                foreach (var v in World.GetNearbyVehicles(me.Position, 120f))
                {
                    if (v == null || !v.Exists() || v == _watched) continue;
                    if (!v.IsEngineRunning) continue;

                    var tank = _tanks.For(v);
                    if (tank == null) continue;

                    tank.Burn(_burn.Burn(v, tank, slice));

                    if (!tank.Empty) continue;

                    Function.Call(Hash.SET_VEHICLE_ENGINE_ON, v.Handle, false, true, true);
                }
            }
            catch (Exception ex)
            {
                Log.Once("traffic", "Traffic fuel pass failed: " + ex.Message +
                                    " - only the player's vehicle will use fuel.");
            }
        }

        /// <summary>
        /// Looks round for a real petrol pump and moves the map onto it.
        ///
        /// The shipped station list was written down by hand and some of it is wrong -- a blip
        /// on the far side of a block from the forecourt it names. Nothing depends on those
        /// numbers, because pumps are found as OBJECTS and not by coordinate, so a bad one only
        /// ever misplaced a marker. But a misplaced marker is still the thing you navigate by,
        /// and there is no way to check twenty-six coordinates by hand that is not simply
        /// driving to all of them.
        ///
        /// So the mod checks them itself, out of the only source that cannot be wrong: the
        /// pumps. Drive within sight of one and whichever station is nearest snaps onto it.
        ///
        /// Every two seconds, and only that: a sweep is seven native calls and the answer
        /// cannot change faster than a car can move.
        /// </summary>
        private void Survey(float dt)
        {
            if (!_cfg.LearnStations) return;

            _sinceSurvey += dt;
            if (_sinceSurvey < 2f) return;

            var slice = _sinceSurvey;
            _sinceSurvey = 0f;

            try
            {
                var me = Game.Player.Character;
                if (me == null || !me.Exists()) return;

                var pump = _pumps.Sweep(me.Position, 70f);
                var found = pump != null && pump.Exists();
                var where = found ? pump.Position : Vector3.Zero;

                var changed = false;

                if (found) changed = _stations.Learn(where);

                // The other half, and it has to run whether or not a pump was found -- that is
                // the entire point of it. A station nobody can find a pump at is exactly the
                // case a "did we see a pump" early return would skip.
                changed |= _stations.Doubt(me.Position, found, where, slice);

                if (changed) _stations.Reblip();
            }
            catch (Exception ex)
            {
                Log.Once("survey", "Could not check the station map: " + ex.Message);
            }
        }

        private void Fail(Exception ex)
        {
            _failures++;
            Log.Error("Tick failed (" + _failures + "/" + MaxConsecutiveFailures + ")", ex);

            if (_failures < MaxConsecutiveFailures) return;

            _parked = true;
            Log.Error("Ten ticks in a row have failed. " + Build.Name +
                      " has stopped itself rather than keep throwing. See above for the cause.");

            try
            {
                GTA.UI.Notification.PostTicker(
                    "~r~" + Build.Name + " stopped~s~ - see Fumes.log.", false, false);
            }
            catch
            {
                // Nothing further to try.
            }

            Cleanup();
        }

        private void OnAborted(object sender, EventArgs e)
        {
            Cleanup();
        }

        /// <summary>
        /// Leaves nothing behind: no nozzle in a hand, no rope in the air, no blips, and the
        /// tanks written down.
        ///
        /// Runs on a reload as well as on shutdown, because SHVDN reloads scripts on a keypress
        /// and a mod that leaves a rope and an invisible petrol can behind every time somebody
        /// reloads is a mod that breaks a save slowly.
        /// </summary>
        private void Cleanup()
        {
            try { _refuel.Shutdown(); } catch (Exception ex) { Log.Error("Refuel shutdown", ex); }
            try { _buttons.Dispose(); } catch (Exception ex) { Log.Error("Button bar cleanup", ex); }
            try { _stations.RemoveBlips(); } catch (Exception ex) { Log.Error("Blip cleanup", ex); }
            try { _stations.SaveCorrections(); } catch (Exception ex) { Log.Error("Station corrections", ex); }
            try { _tanks.SaveToDisk(); } catch (Exception ex) { Log.Error("Final save", ex); }

            Log.Info(Build.Name + " stopped cleanly.");
        }
    }
}
