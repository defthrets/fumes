using System;
using System.Drawing;
using System.Globalization;
using GTA;
using GTA.Math;
using GTA.Native;
using Fumes.Core;
using Fumes.Fuel;
using Fumes.UI;

namespace Fumes.Station
{
    internal enum Stage
    {
        /// <summary>Nothing in hand.</summary>
        Idle,

        /// <summary>Nozzle out, hose run, walking about with it.</summary>
        Carrying,

        /// <summary>Nozzle in the filler, fuel going in.</summary>
        Filling
    }

    /// <summary>
    /// The whole forecourt interaction, start to finish.
    ///
    /// The shape of it is deliberate and it is the point of the mod: you get out, you walk to
    /// a pump, you take the nozzle OFF the pump, and the hose comes with you. You walk it round
    /// to the side of your car where the filler actually is. You put fuel in. You walk the
    /// nozzle back and hang it up. Pull too far and it comes out of your hands.
    ///
    /// Nothing here teleports, snaps or fades. Every step is a thing the player physically did.
    /// </summary>
    internal sealed class Refuel
    {
        private readonly Settings _cfg;
        private readonly Tanks _tanks;
        private readonly Pumps _pumps;
        private readonly Stations _stations;
        private readonly Gauge _gauge;
        private readonly Meter _meter;
        private readonly Nozzle _nozzle;
        private readonly Hose _hose;
        private readonly Hazard _hazard;

        private Stage _stage = Stage.Idle;

        /// <summary>The pump the hose is attached to, and the anchor in ITS OWN space.</summary>
        private Prop _pump;
        private Vector3 _anchorLocal;

        /// <summary>Set at pickup so the price cannot change while you are standing there.</summary>
        private float _price = 1f;
        private string _stationName = "";

        private Vehicle _target;
        private Tank _targetTank;

        private float _dispensed;
        private float _owed;
        private int _paid;

        /// <summary>A nozzle pulled out of his hands, lying on the floor, and when it is tidied.</summary>
        private Prop _dropped;
        private int _tidyDroppedAt;

        /// <summary>
        /// Edge detection for the configured key, which the input API only reports as HELD.
        ///
        /// Sampled ONCE PER TICK from Update, not inside Pressed(). Pressed() is only reached
        /// down some branches -- near a pump, holding the nozzle -- so sampling there leaves
        /// the "was it down last time" flag holding a reading from whenever the player last
        /// happened to be standing somewhere that asked. Press and release the key while
        /// walking between two pumps and the next real press is not an edge at all.
        /// </summary>
        private bool _keyWasDown;
        private bool _keyEdge;

        /// <summary>Stops the over-stretch warning being said sixty times a second.</summary>
        private int _stretchMoanedAt;

        public Refuel(Settings cfg, Tanks tanks, Pumps pumps, Stations stations, Gauge gauge, Meter meter)
        {
            _cfg = cfg;
            _tanks = tanks;
            _pumps = pumps;
            _stations = stations;
            _gauge = gauge;
            _meter = meter;
            _nozzle = new Nozzle(cfg);
            _hose = new Hose(cfg);
            _hazard = new Hazard(cfg);
        }

        /// <summary>Whether the player is holding the nozzle or filling something.</summary>
        public bool Busy => _stage != Stage.Idle;

        /// <summary>What is being filled, so the gauge can show it while he stands there.</summary>
        public Vehicle Target => _stage == Stage.Filling ? _target : null;
        public Tank TargetTank => _stage == Stage.Filling ? _targetTank : null;

        // ==================================================================
        // Tick
        // ==================================================================

        public void Update(float dt)
        {
            SampleKey();
            TidyDropped();

            var me = Player();
            if (me == null)
            {
                if (Busy) Abandon("the player is not available");
                return;
            }

            // Getting into a car, dying, being arrested, a cutscene starting -- all of them
            // end the same way, and none of them should leave a hose hanging in the air.
            if (!me.IsAlive || me.IsInVehicle() || me.IsEnteringVehicle)
            {
                if (Busy) Abandon("the player left on foot");
                return;
            }

            switch (_stage)
            {
                case Stage.Idle: AtRest(me); break;
                case Stage.Carrying: Carrying(me); break;
                case Stage.Filling: Filling(me, dt); break;
            }
        }

        private static Ped Player()
        {
            try
            {
                var me = Game.Player.Character;
                return me != null && me.Exists() ? me : null;
            }
            catch
            {
                return null;
            }
        }

        // ==================================================================
        // Idle: standing near a pump with empty hands
        // ==================================================================

        private void AtRest(Ped me)
        {
            var pump = _pumps.Nearest(me.Position, _cfg.PumpReach);
            if (pump == null) return;

            var canBuyCan = _cfg.JerryCanRefill && CanAfford(_cfg.JerryCanPrice);

            var prompt = "Press ~b~" + KeyName() + "~s~ to take the nozzle.";
            if (_cfg.JerryCanRefill)
            {
                prompt += "~n~Press ~b~" + SecondaryName() + "~s~ to fill a jerry can  $" +
                          _cfg.JerryCanPrice.ToString("0", CultureInfo.InvariantCulture);
            }

            Draw.Help(prompt);

            if (Pressed()) { Take(me, pump); return; }

            if (!_cfg.JerryCanRefill || !SecondaryPressed()) return;

            if (!canBuyCan)
            {
                Notify("~r~You cannot afford a can of fuel.~s~");
                return;
            }

            FillJerryCan(me);
        }

        private void Take(Ped me, Prop pump)
        {
            _pump = pump;

            // The anchor side is chosen ONCE, from where he is standing at the moment he takes
            // it. Recomputing it every frame would flip the hose to the far side of the pump
            // the instant he walked round it, and the hose would appear to pass through the
            // machine.
            _anchorLocal = LocalAnchor(pump, me.Position);

            _price = _stations.PriceAt(pump.Position, out var forecourt);
            _stationName = forecourt == null ? "PUMP" : forecourt.Title;

            _dispensed = 0f;
            _owed = 0f;
            _paid = 0;

            _nozzle.Take();
            _stage = Stage.Carrying;

            Log.Info("Nozzle taken at " + _stationName + " ($" +
                     _price.ToString("0.00", CultureInfo.InvariantCulture) + "/L).");
        }

        /// <summary>The anchor as an offset in the pump's own space, so it turns with the pump.</summary>
        private Vector3 LocalAnchor(Prop pump, Vector3 standingAt)
        {
            try
            {
                var local = pump.GetPositionOffset(standingAt);
                var side = local.X >= 0f ? 1f : -1f;
                return new Vector3(_cfg.HoseAnchorX * side, _cfg.HoseAnchorY, _cfg.HoseAnchorZ);
            }
            catch
            {
                return new Vector3(0f, 0f, _cfg.HoseAnchorZ);
            }
        }

        private Vector3 Anchor()
        {
            try
            {
                if (_pump != null && _pump.Exists()) return _pump.GetOffsetPosition(_anchorLocal);
            }
            catch
            {
                // Fall through to whatever we can still name.
            }

            return _pump != null && _pump.Exists() ? _pump.Position : Nozzle.HandPosition();
        }

        // ==================================================================
        // Carrying: nozzle in hand, hose out
        // ==================================================================

        private void Carrying(Ped me)
        {
            if (_pump == null || !_pump.Exists()) { Abandon("the pump went away"); return; }

            _nozzle.Take();     // keeps asking until the model streams; no-op once it is out
            LockHands();

            var anchor = Anchor();
            _hose.Update(anchor, Nozzle.HandPosition());

            if (_hazard.Update(_pump.Position, false)) { Abandon("the pump went up"); return; }

            if (Leash(me, anchor)) return;

            // Hanging it back up takes priority over filling: if you are standing at the pump
            // with a car also in reach, you meant the pump.
            if (me.Position.DistanceTo(_pump.Position) <= _cfg.PumpReach)
            {
                Draw.Help("Press ~b~" + KeyName() + "~s~ to hang the nozzle up.");
                if (Pressed()) HangUp();
                return;
            }

            var vehicle = NearestFillable(me, out var filler, out var inReach);

            if (vehicle == null)
            {
                Draw.Help("Walk the nozzle to the filler on the side of your vehicle." +
                          "~n~Press ~b~" + SecondaryName() + "~s~ to drop it.");
                if (SecondaryPressed()) DropIt("dropped");
                return;
            }

            // A marker on the actual filler.
            //
            // THE POINT OF IT IS TO GET YOU THERE, so it is drawn from several metres out and
            // not only once you are already standing on the spot -- which is what it used to
            // do, and which made it a confirmation of something you had already found rather
            // than the thing that told you where to walk. Where the filler is depends entirely
            // on the model, and hunting for it in circles is not the interaction.
            World.DrawMarker(MarkerType.Cylinder,
                             filler - new Vector3(0f, 0f, 0.45f),
                             Vector3.Zero, Vector3.Zero,
                             new Vector3(0.28f, 0.28f, 0.3f),
                             inReach ? Color.FromArgb(190, 120, 235, 130)
                                     : Color.FromArgb(140, 245, 175, 55),
                             false, false, false, null, null, false);

            var tank = _tanks.For(vehicle);
            if (tank == null)
            {
                Draw.Help("This one does not take fuel.");
                return;
            }

            if (tank.Litres >= tank.Capacity - 0.05f)
            {
                Draw.Help(vehicle.LocalizedName + " is already full.");
                return;
            }

            if (!inReach)
            {
                Draw.Help("Take the nozzle to the marker on the " + vehicle.LocalizedName + ".");
                return;
            }

            Draw.Help("Press ~b~" + KeyName() + "~s~ to fill the " + vehicle.LocalizedName +
                      "  ~y~$" + _price.ToString("0.00", CultureInfo.InvariantCulture) + "/L~s~");

            if (!Pressed()) return;

            _target = vehicle;
            _targetTank = tank;
            _stage = Stage.Filling;

            Log.Info("Filling " + vehicle.LocalizedName + " (" +
                     tank.Litres.ToString("0.0", CultureInfo.InvariantCulture) + "/" +
                     tank.Capacity.ToString("0.0", CultureInfo.InvariantCulture) + " L).");
        }

        /// <summary>
        /// The hose running out of length.
        ///
        /// Two behaviours, because the two are genuinely different games. HoseSnaps is the
        /// forecourt-accident one: walk off and the nozzle is torn out of your hand. With it
        /// off you simply cannot go further -- the player is pulled back to the end of the
        /// hose each frame, which at a few centimetres a frame does not read as a teleport, it
        /// reads as being held.
        ///
        /// Returns true when it took the interaction over this frame.
        /// </summary>
        private bool Leash(Ped me, Vector3 anchor)
        {
            var span = me.Position.DistanceTo(anchor);
            var max = _cfg.HoseMaxMetres;

            if (span < max * _cfg.HoseWarnFraction) return false;

            if (span <= max)
            {
                if (Game.GameTime - _stretchMoanedAt > 2500)
                {
                    _stretchMoanedAt = Game.GameTime;
                    Draw.Help("The hose is nearly at full stretch.");
                }
                return false;
            }

            if (_cfg.HoseSnaps)
            {
                DropIt("pulled out of your hands");
                return true;
            }

            try
            {
                var back = me.Position - anchor;
                if (back.Length() > 0.001f)
                {
                    back.Normalize();
                    me.Position = anchor + back * max;
                }
            }
            catch
            {
                // If he cannot be moved, the warning is all there is.
            }

            Draw.Help("The hose will not reach any further.");
            return true;
        }

        /// <summary>How far out a filler is worth pointing at, even though you cannot reach it yet.</summary>
        private const float GuideMetres = 6.5f;

        /// <summary>
        /// The nearest vehicle worth walking the nozzle to, and whether you are there yet.
        ///
        /// Deliberately NOT reach-gated. Reach decides whether you can start filling, which is
        /// what inReach is for; finding the car at all has to happen before that, or the marker
        /// only lights up once you no longer need it.
        /// </summary>
        private Vehicle NearestFillable(Ped me, out Vector3 filler, out bool inReach)
        {
            filler = Vector3.Zero;
            inReach = false;

            Vehicle best = null;
            var bestDist = GuideMetres;

            try
            {
                // A small radius on purpose: this runs every frame while the nozzle is out,
                // and the answer can only ever be a vehicle he could walk to on the hose.
                foreach (var v in World.GetNearbyVehicles(me.Position, 9f))
                {
                    if (v == null || !v.Exists()) continue;
                    if (!_tanks.Covers(v)) continue;

                    var point = Filler.On(v, out var isExact);

                    var d = point.DistanceTo(me.Position);
                    if (d >= bestDist) continue;

                    best = v;
                    bestDist = d;
                    filler = point;
                    inReach = Filler.WithinReach(point, me.Position, _cfg.CapReach, isExact);
                }
            }
            catch (Exception ex)
            {
                Log.Once("fillable", "Could not look for a vehicle to fill: " + ex.Message);
            }

            return best;
        }

        // ==================================================================
        // Filling
        // ==================================================================

        private void Filling(Ped me, float dt)
        {
            if (_target == null || !_target.Exists() || _targetTank == null)
            {
                Stop(me, "the vehicle went away");
                return;
            }

            if (_pump == null || !_pump.Exists()) { Abandon("the pump went away"); return; }

            var anchor = Anchor();
            _hose.Update(anchor, Nozzle.HandPosition());

            if (_hazard.Update(_pump.Position, true)) { Abandon("the pump went up"); return; }

            LockHands();
            HoldStill(me);

            var filler = Filler.On(_target, out var exact);
            FaceThe(me, filler);

            // Walking off mid-fill is not possible while the movement controls are held, but
            // ragdolls, explosions and other mods can all move him anyway.
            if (!Filler.WithinReach(filler, me.Position, _cfg.CapReach + 0.6f, exact))
            {
                Stop(me, "you moved away from the filler");
                return;
            }

            var room = _targetTank.Capacity - _targetTank.Litres;
            if (room <= 0.02f) { Stop(me, null); return; }

            var wanted = _cfg.LitresPerSecond * dt;
            if (wanted > room) wanted = room;

            if (_cfg.ChargeMoney && _price > 0f)
            {
                // Only the part of the bill that has NOT already been taken counts against the
                // balance. Settle() deducts as it goes, so the balance already reflects _paid;
                // subtracting the whole of _owed from it charges the same fuel twice and cuts
                // the pump off at half the fuel the player could actually buy.
                var unpaid = _owed - _paid;
                var affordable = (Money() - unpaid) / _price;

                if (affordable <= 0.0005f)
                {
                    Stop(me, "you are out of money");
                    return;
                }
                if (wanted > affordable) wanted = affordable;
            }

            if (wanted > 0f)
            {
                _targetTank.Add(wanted);
                _dispensed += wanted;
                _owed += wanted * _price;
                Settle();
            }

            _tanks.Touch(_target, _targetTank, true);

            _meter.Draw(_stationName, _dispensed, _price, _owed, !_cfg.ChargeMoney);
            _gauge.Update(_target, _targetTank, true);

            Draw.Help("Press ~b~" + KeyName() + "~s~ to stop.");
            if (Pressed()) Stop(me, null);
        }

        /// <summary>
        /// Takes the money as it goes, in whole dollars.
        ///
        /// Rather than one charge at the end, which would be free fuel to anybody who found a
        /// way to be interrupted -- and there are a lot of ways to be interrupted in this game.
        /// Whole dollars because the player's balance is an integer; the fractional part stays
        /// on the tab and is caught by the next dollar.
        /// </summary>
        private void Settle()
        {
            if (!_cfg.ChargeMoney) return;

            var due = (int)Math.Floor(_owed);
            if (due <= _paid) return;

            try
            {
                Game.Player.Money -= due - _paid;
                _paid = due;
            }
            catch (Exception ex)
            {
                Log.Once("charge", "Could not take payment: " + ex.Message + " - fuel is free this session.");
            }
        }

        private void Stop(Ped me, string because)
        {
            var litres = _dispensed;
            var owed = _owed;

            _stage = Stage.Carrying;
            _target = null;
            _targetTank = null;
            _dispensed = 0f;
            _owed = 0f;
            _paid = 0;

            if (litres < 0.05f)
            {
                if (because != null) Notify("~y~Stopped:~s~ " + because + ".");
                return;
            }

            var receipt = _gauge.Volume(litres);
            if (_cfg.ChargeMoney)
            {
                receipt += " ~s~for ~g~$" + owed.ToString("0.00", CultureInfo.InvariantCulture) + "~s~";
            }

            Notify(_stationName + ": " + receipt + (because == null ? "." : " - " + because + "."));
        }

        // ==================================================================
        // Putting it back
        // ==================================================================

        private void HangUp()
        {
            _nozzle.PutBack();
            _hose.Retract();
            Clear();
            Log.Debug("Nozzle hung up.");
        }

        /// <summary>Nozzle on the floor, hose gone. The prop is tidied away a few seconds later.</summary>
        private void DropIt(string why)
        {
            var prop = _nozzle.Drop();
            _hose.Retract();

            if (prop != null)
            {
                // Only one loose nozzle at a time; the previous one goes now rather than in
                // its own good time, or a determined player can litter a forecourt with them.
                TidyDropped(true);
                _dropped = prop;
                _tidyDroppedAt = Game.GameTime + 6000;
            }

            Clear();
            Notify("~y~The nozzle was " + why + ".~s~");
        }

        /// <summary>Everything ends here -- a death, a cutscene, getting in a car.</summary>
        private void Abandon(string why)
        {
            Log.Debug("Refuel abandoned: " + why + ".");

            _nozzle.PutBack();
            _hose.Retract();
            Clear();
        }

        private void Clear()
        {
            _stage = Stage.Idle;
            _pump = null;
            _target = null;
            _targetTank = null;
            _dispensed = 0f;
            _owed = 0f;
            _paid = 0;
            Draw.ClearHelp();
        }

        private void TidyDropped(bool now = false)
        {
            if (_dropped == null) return;

            if (!now && Game.GameTime < _tidyDroppedAt) return;

            try { if (_dropped.Exists()) _dropped.Delete(); }
            catch { /* it will go with the session */ }

            _dropped = null;
        }

        /// <summary>Called on shutdown. Leaves nothing of ours in the world.</summary>
        public void Shutdown()
        {
            _nozzle.PutBack();
            _hose.Release();
            TidyDropped(true);
            Clear();
        }

        // ==================================================================
        // Jerry can
        // ==================================================================

        private void FillJerryCan(Ped me)
        {
            try
            {
                var weapon = me.Weapons.Give(WeaponHash.PetrolCan, 4500, false, true);
                if (weapon != null) weapon.Ammo = 4500;

                if (_cfg.ChargeMoney) Game.Player.Money -= (int)Math.Round(_cfg.JerryCanPrice);

                Notify("Jerry can filled. ~r~-$" +
                       _cfg.JerryCanPrice.ToString("0", CultureInfo.InvariantCulture) + "~s~");
            }
            catch (Exception ex)
            {
                Log.Error("Could not fill the jerry can.", ex);
            }
        }

        // ==================================================================
        // Input, money and the small print
        // ==================================================================

        /// <summary>
        /// Stops him doing anything with his hands that a man holding a fuel nozzle cannot do.
        ///
        /// Attack in particular: the pose is a real petrol can with real ammunition in it, and
        /// without this the trigger pours petrol across the forecourt. Getting in a car is
        /// blocked for a duller reason -- the hose does not come with you and the game has no
        /// idea it is there.
        /// </summary>
        private static void LockHands()
        {
            try
            {
                Game.DisableControlThisFrame(Control.Attack);
                Game.DisableControlThisFrame(Control.Attack2);
                Game.DisableControlThisFrame(Control.Aim);
                Game.DisableControlThisFrame(Control.Enter);
                Game.DisableControlThisFrame(Control.SelectWeapon);
                Game.DisableControlThisFrame(Control.Reload);
                Game.DisableControlThisFrame(Control.Detonate);
            }
            catch
            {
                // Controls are a nicety; the rest still works.
            }
        }

        /// <summary>Feet planted while the fuel goes in.</summary>
        private static void HoldStill(Ped me)
        {
            try
            {
                Game.DisableControlThisFrame(Control.MoveLeftRight);
                Game.DisableControlThisFrame(Control.MoveUpDown);
                Game.DisableControlThisFrame(Control.Sprint);
                Game.DisableControlThisFrame(Control.Jump);
                Game.DisableControlThisFrame(Control.Cover);
            }
            catch
            {
                // As above.
            }
        }

        /// <summary>
        /// Turns him toward the filler.
        ///
        /// SET_PED_DESIRED_HEADING rather than a turn TASK, because a task would fight
        /// whatever else is running on him and would have to be cleared afterwards. A desired
        /// heading is a nudge the locomotion system resolves on its own.
        /// </summary>
        private static void FaceThe(Ped me, Vector3 point)
        {
            try
            {
                var to = point - me.Position;
                if (to.Length() < 0.05f) return;

                // Heading is degrees about the vertical with zero pointing north (+Y), and it
                // runs the opposite way round from atan2 -- hence the negated X.
                var heading = (float)(Math.Atan2(-to.X, to.Y) * 180d / Math.PI);
                Function.Call(Hash.SET_PED_DESIRED_HEADING, me.Handle, heading);
            }
            catch
            {
                // He fills it facing whichever way he was.
            }
        }

        private static int Money()
        {
            try { return Game.Player.Money; }
            catch { return 0; }
        }

        private static bool CanAfford(float amount)
        {
            return Money() >= amount;
        }

        /// <summary>Reads the configured key once, at the top of the tick. See _keyEdge.</summary>
        private void SampleKey()
        {
            try
            {
                var down = Game.IsKeyPressed(_cfg.InteractKey);
                _keyEdge = down && !_keyWasDown;
                _keyWasDown = down;
            }
            catch
            {
                _keyEdge = false;
                _keyWasDown = false;
            }
        }

        /// <summary>
        /// The interact key, on its rising edge.
        ///
        /// Both the configured keyboard key AND the game's own context control, so a controller
        /// works without anybody configuring anything.
        /// </summary>
        private bool Pressed()
        {
            if (_keyEdge) return true;

            try { return Game.IsControlJustPressed(Control.Context); }
            catch { return false; }
        }

        private static bool SecondaryPressed()
        {
            try { return Game.IsControlJustPressed(Control.ContextSecondary); }
            catch { return false; }
        }

        private string KeyName()
        {
            return _cfg.InteractKey.ToString().ToUpperInvariant();
        }

        private static string SecondaryName()
        {
            // ContextSecondary is Q on a keyboard out of the box. Named rather than tagged
            // because ~INPUT_~ only resolves inside help text and this string is also logged.
            return "Q";
        }

        private static void Notify(string text)
        {
            try { GTA.UI.Notification.PostTicker(text, false, false); }
            catch { /* not worth a crash */ }
        }
    }
}
