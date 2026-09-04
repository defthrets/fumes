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
        Filling,

        /// <summary>Emptying a jerry can into a tank, nowhere near a pump.</summary>
        Pouring,

        /// <summary>Filling the can from the pump, with the nozzle in hand.</summary>
        FillingCan,

        /// <summary>Drawing fuel out of somebody's tank into the can.</summary>
        Siphoning
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
        private readonly FillSound _sound;
        private readonly Hazard _hazard;
        private readonly Buttons _buttons;

        private Stage _stage = Stage.Idle;

        /// <summary>The pump the hose is attached to, and the anchor in ITS OWN space.</summary>
        private Prop _pump;
        private Vector3 _anchorLocal;

        /// <summary>Set at pickup so the price cannot change while you are standing there.</summary>
        private float _price = 1f;

        /// <summary>
        /// The station's own price, before the grade multiplier.
        ///
        /// Kept apart because the two are settled at different MOMENTS. The station's price is
        /// known when you pick the nozzle up; the grade is not known until you point it at
        /// something, since it is the vehicle that decides whether this is diesel. Multiplying
        /// in place at the pump would leave nothing to recompute from when a truck turns up.
        /// </summary>
        private float _basePrice = 1f;

        /// <summary>What is going in right now. Resolved per vehicle, not per station.</summary>
        private FuelGrade _grade = FuelGrade.Regular;

        /// <summary>
        /// The brand and the place, kept APART rather than as one joined title.
        ///
        /// The display sets them in two different fonts -- the brand in signwriter's
        /// script, the location in the block font -- so a pre-joined string would have to
        /// be split again on a separator, and any station whose name contained that
        /// separator would split in the wrong place.
        /// </summary>
        private string _stationBrand = "";
        private string _stationPlace = "";
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

        /// <summary>Prompt text for when the button bar is unavailable. See Prompt().</summary>
        private string _fallback;

        public Refuel(Settings cfg, Tanks tanks, Pumps pumps, Stations stations, Gauge gauge,
                      Meter meter, Buttons buttons)
        {
            _cfg = cfg;
            _tanks = tanks;
            _pumps = pumps;
            _stations = stations;
            _gauge = gauge;
            _meter = meter;
            _buttons = buttons;
            _nozzle = new Nozzle(cfg);
            _siphonLine = new Hose(cfg, true);
            _hose = new Hose(cfg);
            _hazard = new Hazard(cfg);
            _sound = new FillSound(cfg);
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
                // DROPPED, NOT DELETED. Getting into a car with the nozzle still in your hand
                // used to make it vanish, which quietly made "put it back" optional -- you
                // could always just drive off and the mod would tidy up after you. Now it ends
                // up on the tarmac like it would if you really did that, and the only two ways
                // to finish cleanly are to hang it up or to put it down.
                if (Busy) DropIt("left on the forecourt");
                return;
            }

            _fallback = null;

            // The prompt's memory only means anything while the nozzle is out. Left set, walking
            // away and coming back would resume mid-argument with whatever it last decided.
            if (_stage != Stage.Carrying) _prompt = 0;

            switch (_stage)
            {
                case Stage.Idle: AtRest(me); break;
                case Stage.Carrying: Carrying(me); break;
                case Stage.Filling: Filling(me, dt); break;
                case Stage.Pouring: Pouring(me, dt); break;
                case Stage.FillingCan: FillingCan(me, dt); break;
                case Stage.Siphoning: Siphoning(me, dt); break;
            }

            // AFTER the switch and driven by the stage rather than by calls inside it.
            // Filling ends in six different places and a Stop missing from any one of them is
            // a sound that plays until the game is closed.
            _sound.Update(_stage == Stage.Filling, me);

            // One help box for however many buttons were asked for, and only when the bar
            // itself could not be drawn. Calling Draw.Help per prompt would have each one
            // overwrite the last and the player would see only whichever came last.
            if (!string.IsNullOrEmpty(_fallback)) Draw.Help(_fallback);
        }

        /// <summary>
        /// Cycles the rope the hose is made of, while it is in your hand.
        ///
        /// EIGHT ROPES IS THE WHOLE OF IT. ADD_ROPE's type is a zero-based index into
        /// ropedata.xml and that file holds eight entries, 0 to 7 -- confirmed against the
        /// native's own documentation, not only against the crash that taught us the hard way.
        /// There is no ninth to look for and no second rope system to look in: ADD_ROPE is the
        /// only general rope creator the game exposes. The other rope natives all belong to
        /// something specific -- the cargobob's pickup rope, the tow truck's arm -- and cannot
        /// be pointed at a fuel hose.
        ///
        /// So this is a choice between eight, not a search, and it is one key because there is
        /// nothing else to try.
        ///
        /// Unconditional while the nozzle is out, as before. Gating a picker behind a setting
        /// that defaults to false is what made it look broken the first time: the answer to
        /// "which rope?" was locked inside a switch nobody had been told to turn on. Set
        /// [Nozzle] RopePicker = false to bind the key back once you have settled on one.
        /// </summary>
        private void RopePicker()
        {
            if (!_cfg.RopePicker || InputBlocked) return;

            if (Edge(System.Windows.Forms.Keys.Multiply, ref _ropeKeyDown))
            {
                _cfg.HoseRopeType = RopeProbe.Next(_cfg.HoseRopeType);

                // The type is baked in at ADD_ROPE, so the rope has to be thrown away and made
                // again. It respawns on the next frame from Carrying.
                _hose.Retract();

                Log.Info("Hose rope type is now " + _cfg.HoseRopeType + ".");
                Notify("Hose rope ~b~" + _cfg.HoseRopeType + "~s~ of 0-" + RopeProbe.MaxType +
                       ".   NumPad 0 to keep it.");
            }

            if (Edge(System.Windows.Forms.Keys.NumPad0, ref _ropeSaveKey))
            {
                var ok = IniFile.SetValue(Paths.Ini, "Nozzle", "HoseRopeType",
                                          _cfg.HoseRopeType.ToString(CultureInfo.InvariantCulture));

                Notify(ok ? "~g~Rope " + _cfg.HoseRopeType + " saved~s~ to Fumes.ini."
                          : "~y~Could not write Fumes.ini~s~ - it is in Fumes.log.");

                Log.Info("Rope type " + _cfg.HoseRopeType + " written to the ini.");
            }
        }

        private bool _ropeKeyDown, _ropeSaveKey;

        /// <summary>Rising edge for a key, since the input API only reports held.</summary>
        private static bool Edge(System.Windows.Forms.Keys key, ref bool wasDown)
        {
            bool down;
            try { down = Game.IsKeyPressed(key); }
            catch { down = false; }

            var edge = down && !wasDown;
            wasDown = down;
            return edge;
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
            // THE CAN IS OFFERED FIRST, and before the pump lookup rather than after it. A jerry
            // can is for the roadside -- the whole point of carrying one is that there is no
            // pump -- so anything gated behind "is there a pump nearby" would only ever work in
            // the one place it is not needed.
            if (OfferCan(me)) return;

            var pump = _pumps.Nearest(me.Position, _cfg.PumpReach);
            if (pump == null) return;

            Prompt(Control.Context, "Take the nozzle");

            if (Pressed()) Take(me, pump);
        }

        /// <summary>
        /// The prompt to pour a can into a tank. True when it took the prompt this frame.
        /// </summary>
        private bool OfferCan(Ped me)
        {
            if (!_cfg.JerryCan) return false;

            var litres = CanLitres(me);
            if (litres <= 0.01f) return false;

            var vehicle = NearestFillable(me, out var filler, out var inReach);
            if (vehicle == null || !inReach) return false;

            var tank = _tanks.For(vehicle);
            if (tank == null || tank.Litres >= tank.Capacity - 0.05f) return false;

            Prompt(Control.Context, "Pour the can into the " + vehicle.LocalizedName +
                                    "   " + litres.ToString("0.0", CultureInfo.InvariantCulture) + " L");

            OfferSiphon(me, vehicle, tank, litres);

            if (!Pressed()) return true;

            _target = vehicle;
            _targetTank = tank;
            _canLitres = litres;
            _stage = Stage.Pouring;

            Log.Info("Pouring " + litres.ToString("0.0", CultureInfo.InvariantCulture) +
                     " L from the can into " + vehicle.LocalizedName + " (" +
                     tank.Litres.ToString("0.0", CultureInfo.InvariantCulture) + "/" +
                     tank.Capacity.ToString("0.0", CultureInfo.InvariantCulture) + " L).");

            return true;
        }

        /// <summary>
        /// The petrol can in his inventory, held or not, or null if he has none.
        ///
        /// INDEXED RATHER THAN TAKEN FROM Current, and that is what makes filling one at a pump
        /// possible at all: with the nozzle in his hand the current weapon is the invisible one
        /// holding the pose, so anything that asked "is he holding a can" would answer no at
        /// exactly the moment he is standing at a pump wanting to fill it.
        /// </summary>
        private static Weapon Can(Ped me)
        {
            try
            {
                var w = me.Weapons[WeaponHash.PetrolCan];
                return w != null && w.IsPresent ? w : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Litres in the can he owns, held or not.</summary>
        private float CanFuel(Ped me)
        {
            var can = Can(me);
            if (can == null) return 0f;

            try
            {
                var max = can.MaxAmmo;
                return max <= 0 ? 0f : _cfg.JerryCanLitres * can.Ammo / max;
            }
            catch
            {
                return 0f;
            }
        }

        /// <summary>Writes litres back to the can he owns, held or not.</summary>
        private void SetCanFuel(Ped me, float litres)
        {
            var can = Can(me);
            if (can == null) return;

            try
            {
                var max = can.MaxAmmo;
                if (max <= 0) return;

                var ammo = (int)(litres / _cfg.JerryCanLitres * max);

                if (ammo < 0) ammo = 0;
                if (ammo > max) ammo = max;

                can.Ammo = ammo;
            }
            catch (Exception ex)
            {
                Log.Once("can-ammo", "Could not change what is in the can: " + ex.Message);
            }
        }

        /// <summary>
        /// Taking fuel OUT of a tank and into the can, on the secondary button.
        ///
        /// On its own button rather than sharing one, because at a car with a part-full can
        /// both directions are sensible at once -- top the car up, or help yourself -- and
        /// there is no reading of "nearer" or "longer press" that guesses which you meant.
        /// Two intentions, two buttons.
        /// </summary>
        private void OfferSiphon(Ped me, Vehicle vehicle, Tank tank, float inCan)
        {
            if (!_cfg.Siphon) return;
            if (inCan >= _cfg.JerryCanLitres - 0.05f) return;   // can already full
            if (tank.Litres <= 0.05f) return;                   // nothing to take

            Prompt(Control.ContextSecondary, "Siphon it out");

            if (!SecondaryPressed()) return;

            _target = vehicle;
            _targetTank = tank;
            _canLitres = inCan;
            _stage = Stage.Siphoning;

            PutCanDown(me);

            Log.Info("Siphoning from " + vehicle.LocalizedName + " (" +
                     tank.Litres.ToString("0.0", CultureInfo.InvariantCulture) + " L) into a can " +
                     "holding " + inCan.ToString("0.0", CultureInfo.InvariantCulture) + " L.");
        }

        // ==================================================================
        // Filling the can at the pump, and siphoning it out of a tank
        // ==================================================================

        /// <summary>
        /// Buying fuel into the can, with the nozzle in hand and the pump running.
        ///
        /// The same hose, the same price, the same meter -- only the thing on the other end of
        /// it is a can rather than a car. Without this the can is a consumable you can empty
        /// and never refill, which makes it a thing you use once and then forget you own.
        /// </summary>
        private void FillingCan(Ped me, float dt)
        {
            if (_pump == null || !_pump.Exists()) { Abandon("the pump went away"); return; }

            var anchor = Anchor();
            _hose.Update(anchor, _nozzle.HoseEnd());

            if (_hazard.Update(_pump.Position, true)) { Abandon("the pump went up"); return; }

            LockHands();
            HoldStill(me);
            FillPose(me);

            if (me.Position.DistanceTo(_pump.Position) > _cfg.PumpReach + 1.2f)
            {
                StopCan(me, "you walked away from the pump");
                return;
            }

            var room = _cfg.JerryCanLitres - _canLitres;
            if (room <= 0.02f) { StopCan(me, "the can is full"); return; }

            var wanted = _cfg.LitresPerSecond * dt;
            if (wanted > room) wanted = room;

            if (_cfg.ChargeMoney && _price > 0f)
            {
                var unpaid = _owed - _paid;
                var affordable = (Money() - unpaid) / _price;

                if (affordable <= 0.0005f) { StopCan(me, "you are out of money"); return; }
                if (wanted > affordable) wanted = affordable;
            }

            if (wanted > 0f)
            {
                _canLitres += wanted;
                _dispensed += wanted;
                _owed += wanted * _price;

                SetCanFuel(me, _canLitres);
                Settle();
            }

            // THE GLASS SHOWS THE CAN. Passing null draws an empty one, which Meter tolerates
            // but which reads as a car sitting at zero while the numbers beside it climb -- the
            // one part of that display whose whole job is to show the level, showing the wrong
            // thing's level. A stand-in tank costs nothing and makes the glass mean what it
            // looks like it means.
            _canGlass.Capacity = _cfg.JerryCanLitres;
            _canGlass.Litres = _canLitres;

            _meter.Draw(_stationBrand, _stationPlace, _dispensed, _price, _owed,
                        !_cfg.ChargeMoney, _canGlass, _grade);

            Prompt(Control.Context, "Stop   can " +
                                    _canLitres.ToString("0.0", CultureInfo.InvariantCulture) + " / " +
                                    _cfg.JerryCanLitres.ToString("0.#", CultureInfo.InvariantCulture) + " L");

            if (Pressed()) StopCan(me, null);
        }

        private void StopCan(Ped me, string why)
        {
            StopFillPose();

            Log.Info("Stopped filling the can" + (why == null ? "" : " - " + why) + ". " +
                     _canLitres.ToString("0.0", CultureInfo.InvariantCulture) + " L in it, $" +
                     _owed.ToString("0.00", CultureInfo.InvariantCulture) + " owed.");

            // Back to carrying, not to idle: the nozzle is still in his hand and the hose is
            // still run. Ending at Idle would leave both hanging in mid-air.
            _stage = Stage.Carrying;
        }

        /// <summary>
        /// Drawing fuel out of a tank into the can. Slow, and free, because it is not bought.
        /// </summary>
        private void Siphoning(Ped me, float dt)
        {
            if (_target == null || !_target.Exists() || _targetTank == null)
            {
                StopSiphon(me, "the vehicle went away");
                return;
            }

            if (Can(me) == null) { StopSiphon(me, "you have no can"); return; }

            var filler = Filler.On(_target, out var exact);

            if (!Filler.WithinReach(filler, me.Position, _cfg.CapReach + 0.6f, exact))
            {
                StopSiphon(me, "you moved away");
                return;
            }

            HoldStill(me);

            var swinging = Swinging();

            if (swinging)
            {
                Swing(me);
            }
            else
            {
                Swung(me);
                SiphonPose(me, filler);
                FaceThe(me, filler);

                // The line runs from the FREE hand, raised at the filler, down to the spout of
                // the can in the other one. Same rope and the same dark paint as the pump hose,
                // because it is the same kind of object and two different-looking hoses is one
                // too many.
                //
                // Both ends move with him, and neither is a fixed offset: the hand end is a
                // bone, and the spout end is measured off the can he is actually holding.
                _siphonLine.Update(me.Bones[FreeHand].Position, CanSpout(me));
            }

            var room = _cfg.JerryCanLitres - _canLitres;
            if (room <= 0.02f) { StopSiphon(me, "the can is full"); return; }

            if (_targetTank.Litres <= 0.02f) { StopSiphon(me, "the tank is dry"); return; }

            // Nothing moves while he is swinging. He is not siphoning, he is fighting.
            var wanted = swinging ? 0f : _cfg.SiphonLitresPerSecond * dt;
            if (wanted > room) wanted = room;
            if (wanted > _targetTank.Litres) wanted = _targetTank.Litres;

            if (wanted > 0f)
            {
                _targetTank.Burn(wanted);
                _canLitres += wanted;

                SetCanFuel(me, _canLitres);
                _tanks.Touch(_target, _targetTank, false);
            }

            _gauge.Update(_target, _targetTank, true);

            Prompt(Control.Context, "Stop   can " +
                                    _canLitres.ToString("0.0", CultureInfo.InvariantCulture) + " / " +
                                    _cfg.JerryCanLitres.ToString("0.#", CultureInfo.InvariantCulture) + " L");

            if (Pressed()) StopSiphon(me, null);
        }

        /// <summary>
        /// Stands the can on the floor beside him for the duration.
        ///
        /// A REAL PROP, not the weapon. He is holding the can as a weapon, and a weapon cannot
        /// be put down without taking it off him -- so this is a second, separate object placed
        /// on the ground, and the one in his hands is simply hidden for as long as it is there.
        /// Hiding rather than removing, because removing a weapon and giving it back is how you
        /// lose a player's ammo.
        /// </summary>
        private void PutCanDown(Ped me)
        {
            // HE JUST KEEPS HOLDING IT. The can is a weapon, so leaving it selected gets the
            // game's own carrying animation for free -- the right hand is already solved, by
            // the people who made the model, and no prop, no bone offset and no rotation has to
            // be guessed at to put a can in a fist.
            if (_cfg.SiphonCanInHand) return;

            try
            {
                var at = me.Position + me.RightVector * 0.55f - me.ForwardVector * 0.15f;

                foreach (var name in CanProps)
                {
                    var model = new Model(name);
                    if (!model.IsValid) continue;

                    model.Request(1200);
                    if (!model.IsLoaded) { model.MarkAsNoLongerNeeded(); continue; }

                    _canOnGround = World.CreateProp(model, at, false, false);
                    model.MarkAsNoLongerNeeded();

                    if (_canOnGround == null || !_canOnGround.Exists()) continue;

                    _canOnGround.IsPositionFrozen = true;
                    Function.Call(Hash.PLACE_OBJECT_ON_GROUND_PROPERLY, _canOnGround.Handle);

                    // The top of the bounding box, which on all three cans is the neck.
                    try
                    {
                        Vector3 low, high;
                        _canOnGround.Model.GetDimensions(out low, out high);
                        if (high.Z > 0.02f) _canTop = high.Z;
                    }
                    catch
                    {
                        // The default is close enough for prop_jerrycan_01a.
                    }

                    // Turned to face him, so the handle is not pointing into the car.
                    _canOnGround.Heading = me.Heading + 90f;

                    Log.Once("can-prop", "Siphon can prop: " + name + ".");
                    break;
                }

                // The one in his hands goes away while the one on the floor is out, or he is
                // holding a can AND standing over one.
                me.Weapons.Select(WeaponHash.Unarmed, true);
            }
            catch (Exception ex)
            {
                Log.Once("can-down", "Could not put the can down: " + ex.Message);
            }
        }

        private void PickCanUp(Ped me)
        {
            _siphonLine.Retract();

            // Nothing was ever put down.
            if (_cfg.SiphonCanInHand) return;

            try
            {
                if (_canOnGround != null && _canOnGround.Exists())
                {
                    // Detached first. Deleting an attached entity works, but leaving the
                    // detach to the delete is the kind of thing that is fine until the delete
                    // is the call that fails.
                    if (_canOnGround.IsAttached()) _canOnGround.Detach();
                    _canOnGround.Delete();
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not clear the can prop: " + ex.Message);
            }

            _canOnGround = null;

            // Back in his hands. The ammo was never touched, so it comes back as it was.
            try { if (me != null && me.Exists()) me.Weapons.Select(WeaponHash.PetrolCan, true); }
            catch { /* he can pick it himself */ }
        }

        /// <summary>
        /// Him siphoning: the can in the hand that carries it, the other hand up at the filler.
        ///
        /// NO POSE ANIMATION FOR THE CAN AT ALL, which is the point. The can is a weapon, so
        /// leaving it selected gets the game's own carrying animation -- the one it plays any
        /// time you walk around with a jerry can -- and that hand is then solved by the people
        /// who built the model. The version this replaces hid the can, spawned a prop, and
        /// attached it to a bone with six numbers that had to be guessed at.
        ///
        /// The free arm is IK rather than an animation because no clip exists of a man holding a
        /// can in one hand and reaching up with the other, and IK composes with whatever the
        /// carrying animation is doing instead of fighting it. Re-issued every frame: an IK
        /// target is a request for THIS frame, not a state that sticks.
        ///
        /// LockHands with it, and that one is not cosmetic -- he is holding a petrol can with
        /// live ammunition in it, and the attack button on a petrol can lays a trail of fuel
        /// across the forecourt he is standing on.
        /// </summary>
        private void SiphonPose(Ped me, Vector3 filler)
        {
            LockHands();

            try
            {
                if (_cfg.SiphonCanInHand) me.Weapons.Select(WeaponHash.PetrolCan, true);

                // Up to the cap, a little proud of it, so the arm reaches to the hose end
                // rather than into the bodywork.
                var reach = filler + new Vector3(0f, 0f, 0.10f);

                Function.Call(Hash.SET_IK_TARGET, me.Handle, _cfg.SiphonIkPart, 0, 0,
                              reach.X, reach.Y, reach.Z, 0, 400, 400);
            }
            catch (Exception ex)
            {
                Log.Once("siphon-pose", "Could not pose the siphon: " + ex.Message);
            }
        }

        /// <summary>
        /// The top of the can he is holding, or of the one on the floor.
        ///
        /// Measured off the entity rather than assumed, because the can he is holding is the
        /// WEAPON model and the one on the floor is a scene prop, and they are not the same
        /// size. CurrentWeaponObject is the held one as a real entity, which is what makes the
        /// spout reachable at all.
        /// </summary>
        private Vector3 CanSpout(Ped me)
        {
            try
            {
                if (!_cfg.SiphonCanInHand)
                {
                    return _canOnGround != null && _canOnGround.Exists()
                        ? _canOnGround.GetOffsetPosition(new Vector3(0f, 0f, _canTop))
                        : me.Position;
                }

                var held = me.Weapons.CurrentWeaponObject;

                if (held != null && held.Exists())
                {
                    Vector3 low, high;
                    held.Model.GetDimensions(out low, out high);

                    return held.GetOffsetPosition(new Vector3(0f, 0f, high.Z > 0.02f ? high.Z : _canTop));
                }
            }
            catch (Exception ex)
            {
                Log.Once("can-spout", "Could not find the can spout: " + ex.Message);
            }

            // The carrying hand, which is where the can is even when it cannot be measured.
            try { return me.Bones[_cfg.LeftHand ? Bone.PHLeftHand : Bone.PHRightHand].Position; }
            catch { return me.Position; }
        }

        // ==================================================================
        // Swinging while holding the can
        // ==================================================================

        private int _kickUntil;
        private bool _swinging;

        /// <summary>
        /// Whether he is mid-swing, and starts one if the button has just gone down.
        ///
        /// THE ATTACK BUTTON STAYS DISABLED THROUGHOUT, and that is what makes this safe rather
        /// than clever. LockHands disables it because the thing in his hand is a petrol can with
        /// live ammunition, and the trigger on a petrol can lays a trail of fuel across the
        /// forecourt. IsControlJustPressed reads a control even while it is disabled -- so the
        /// press is seen, the can never fires, and the press is free to mean something else.
        ///
        /// Short-circuiting the four checks is fine here, unlike the edge-detector this looks
        /// like: the game tracks just-pressed itself, so an unevaluated check has no state left
        /// stale by not running.
        /// </summary>
        private bool Swinging()
        {
            if (!_cfg.KickWhileFilling) return false;
            if (Game.GameTime < _kickUntil) return true;

            var hit = Game.IsControlJustPressed(Control.Attack)
                      || Game.IsControlJustPressed(Control.MeleeAttackLight)
                      || Game.IsControlJustPressed(Control.MeleeAttackHeavy)
                      || Game.IsControlJustPressed(Control.MeleeAttackAlternate);

            if (!hit) return false;

            _kickUntil = Game.GameTime + (int)(_cfg.KickSeconds * 1000f);
            return true;
        }

        /// <summary>
        /// Gets the can out of his hands for the swing, and takes the hose off screen with it.
        ///
        /// Unarmed because you cannot kick while holding a jerry can -- the game gives you the
        /// can's own attack instead, which is the fuel trail this exists to avoid. Selecting
        /// rather than removing, so the fuel in the can survives the punch.
        /// </summary>
        private void Swing(Ped me)
        {
            if (_swinging) return;
            _swinging = true;

            try { me.Weapons.Select(WeaponHash.Unarmed, true); }
            catch { /* he swings with it, or he does not swing */ }

            // The line would otherwise stretch between his two empty hands.
            _siphonLine.Retract();
        }

        /// <summary>Puts the can back the moment the window closes.</summary>
        private void Swung(Ped me)
        {
            if (!_swinging) return;
            _swinging = false;

            try { if (_cfg.SiphonCanInHand) me.Weapons.Select(WeaponHash.PetrolCan, true); }
            catch { /* the pose reselects it next frame anyway */ }
        }

        private void StopSiphon(Ped me, string why)
        {
            Swung(me);
            PickCanUp(me);
            StopFillPose();

            Log.Info("Stopped siphoning" + (why == null ? "" : " - " + why) + ". " +
                     _canLitres.ToString("0.0", CultureInfo.InvariantCulture) + " L in the can.");

            _target = null;
            _targetTank = null;
            _canLitres = 0f;
            _stage = Stage.Idle;
        }

        /// <summary>
        /// What is left in the can he is holding, in litres, or 0 if he is not holding one.
        ///
        /// THE CAN'S AMMO IS ITS FUEL. GTA has no notion of a jerry can's contents beyond the
        /// ammo count on the weapon -- which is exactly what it is, and what drains when you
        /// pour petrol on the floor. Reading it means a can half emptied making a trail is half
        /// empty here too, with no bookkeeping of our own to drift out of step with the game's.
        ///
        /// Scaled to litres by the weapon's own maximum rather than a hardcoded 4500, because
        /// the number is the game's to change and a DLC can may not share it.
        /// </summary>
        private float CanLitres(Ped me)
        {
            try
            {
                var weapon = me.Weapons.Current;
                if (weapon == null || weapon.Hash != WeaponHash.PetrolCan) return 0f;

                var max = weapon.MaxAmmo;
                if (max <= 0) return 0f;

                return _cfg.JerryCanLitres * weapon.Ammo / max;
            }
            catch
            {
                return 0f;
            }
        }

        /// <summary>Writes litres back to the can as ammo, so the game and the mod agree.</summary>
        private void SetCanLitres(Ped me, float litres)
        {
            try
            {
                var weapon = me.Weapons.Current;
                if (weapon == null || weapon.Hash != WeaponHash.PetrolCan) return;

                var max = weapon.MaxAmmo;
                if (max <= 0) return;

                var ammo = (int)(litres / _cfg.JerryCanLitres * max);

                if (ammo < 0) ammo = 0;
                if (ammo > max) ammo = max;

                weapon.Ammo = ammo;
            }
            catch (Exception ex)
            {
                Log.Once("can-ammo", "Could not empty the can: " + ex.Message);
            }
        }

        // ==================================================================
        // Pouring: a jerry can into a tank
        // ==================================================================

        /// <summary>
        /// No pump, no hose, no money, no station. Just a can and a filler.
        ///
        /// Deliberately slower than a pump. A forecourt sells fuel through a hose at a couple of
        /// litres a second; a man tipping a can into a wing does not, and the difference is most
        /// of what makes the can a last resort rather than a way to skip the drive.
        /// </summary>
        private void Pouring(Ped me, float dt)
        {
            if (_target == null || !_target.Exists() || _targetTank == null)
            {
                StopPouring(me, "the vehicle went away");
                return;
            }

            // Putting the can away is how you stop, and it is the obvious gesture. Checked
            // before anything else so swapping weapons cannot leave fuel pouring out of a
            // pistol.
            if (CanLitres(me) <= 0f && _canLitres > 0.01f)
            {
                StopPouring(me, "you put the can away");
                return;
            }

            var filler = Filler.On(_target, out var exact);

            if (!Filler.WithinReach(filler, me.Position, _cfg.CapReach + 0.6f, exact))
            {
                StopPouring(me, "you moved away from the filler");
                return;
            }

            HoldStill(me);

            var swinging = Swinging();

            if (swinging)
            {
                StopPourPose();
                Swing(me);
            }
            else
            {
                Swung(me);
                PourPose(me);
                FaceThe(me, filler);
            }

            var room = _targetTank.Capacity - _targetTank.Litres;
            if (room <= 0.02f) { StopPouring(me, null); return; }

            // As with the siphon: nothing pours while he is swinging.
            var wanted = swinging ? 0f : _cfg.JerryCanLitresPerSecond * dt;
            if (wanted > room) wanted = room;
            if (wanted > _canLitres) wanted = _canLitres;

            if (wanted > 0f)
            {
                _targetTank.Add(wanted);
                _canLitres -= wanted;

                SetCanLitres(me, _canLitres);
                _tanks.Touch(_target, _targetTank, false);
            }

            _gauge.Update(_target, _targetTank, true);

            Prompt(Control.Context, "Stop pouring   " +
                                    _canLitres.ToString("0.0", CultureInfo.InvariantCulture) +
                                    " L left");

            if (Pressed()) { StopPouring(me, null); return; }

            if (_canLitres <= 0.01f) StopPouring(me, "the can is empty");
        }

        private void StopPouring(Ped me, string why)
        {
            StopPourPose();

            Log.Info("Stopped pouring" + (why == null ? "" : " - " + why) + ". " +
                     _canLitres.ToString("0.0", CultureInfo.InvariantCulture) + " L left in the can.");

            _target = null;
            _targetTank = null;
            _canLitres = 0f;
            _stage = Stage.Idle;
        }

        /// <summary>Litres left in the can being poured. See CanLitres.</summary>
        private float _canLitres;

        /// <summary>The can on the ground, and the line from it to the filler, while siphoning.</summary>
        private Prop _canOnGround;

        /// <summary>
        /// How far up the can its neck is, in the can's own space.
        ///
        /// Measured from the model rather than guessed, because the three can props are not the
        /// same size and a fixed offset that sits on the neck of one floats above another. Read
        /// once when the can is put down: the bounding box of a model cannot change, and asking
        /// the streamer for it every frame of a siphon would be sixty pointless calls a second.
        /// </summary>
        private float _canTop = 0.28f;
        private readonly Hose _siphonLine;

        /// <summary>
        /// Jerry can props, in the order they are tried -- and it is a DIFFERENT order for a can
        /// that is held than for one standing on the floor, because they are different jobs.
        ///
        /// w_am_jerrycan is the game's own carried can. Its origin is the grip, so it hangs off
        /// a hand bone correctly at no offset and no rotation, which is the whole reason it is
        /// first when he is holding one -- picking the model built for the job beats tuning
        /// offsets onto a model that is not.
        ///
        /// prop_jerrycan_01a has its origin in the middle and is built to stand on a floor, so
        /// it leads when the can is being put down.
        /// </summary>
        /// <summary>
        /// Jerry can props for the version that stands one on the floor. Only that version needs
        /// a prop at all: when he keeps hold of it, the can is his WEAPON and the game supplies
        /// both the model and the animation for carrying it.
        /// </summary>
        private static readonly string[] CanProps =
        {
            "prop_jerrycan_01a",
            "prop_ld_jerrycan_01",
            "w_am_jerrycan"
        };

        /// <summary>
        /// The hand that is NOT holding the can. See Nozzle, which owns the other one -- these
        /// are the PH_ prop-holding bones, not the skeleton hands, so the hose ends where a
        /// held object would sit rather than at the wrist.
        /// </summary>
        private const int PhRightHand = 28422;
        private const int PhLeftHand = 60309;

        private Bone FreeHand => _cfg.LeftHand ? Bone.PHRightHand : Bone.PHLeftHand;

        /// <summary>A stand-in tank so the pump display can show the CAN filling.</summary>
        private readonly Tank _canGlass = new Tank { Capacity = 20f, Litres = 0f };

        /// <summary>
        /// The secondary prompt at the pump: fill the can you are carrying.
        ///
        /// Offered where hanging up is offered, because that is where the pump is -- and on the
        /// secondary button, because the primary one there already means "put it back" and
        /// stacking a third meaning on it would bring back exactly the strobing this took two
        /// attempts to get rid of.
        /// </summary>
        private void OfferCanFill(Ped me)
        {
            if (!_cfg.JerryCan) return;

            var can = Can(me);
            if (can == null) return;

            var litres = CanFuel(me);
            if (litres >= _cfg.JerryCanLitres - 0.05f) return;

            Prompt(Control.ContextSecondary, "Fill the can   " +
                                             litres.ToString("0.0", CultureInfo.InvariantCulture) + " / " +
                                             _cfg.JerryCanLitres.ToString("0.#", CultureInfo.InvariantCulture) + " L");

            if (!SecondaryPressed()) return;

            _canLitres = litres;
            _stage = Stage.FillingCan;

            // Back to the bought grade. A truck hovered a moment ago would otherwise leave the
            // diesel price on the can.
            _grade = _cfg.Grade;
            _price = _basePrice * _cfg.PriceFor(_grade);

            Log.Info("Filling the can at " + _stationName + " ($" +
                     _price.ToString("0.00", CultureInfo.InvariantCulture) + "/L), " +
                     litres.ToString("0.0", CultureInfo.InvariantCulture) + " L in it.");
        }

        private void Take(Ped me, Prop pump)
        {
            _pump = pump;

            // The anchor side is chosen ONCE, from where he is standing at the moment he takes
            // it. Recomputing it every frame would flip the hose to the far side of the pump
            // the instant he walked round it, and the hose would appear to pass through the
            // machine.
            _anchorLocal = LocalAnchor(pump, me.Position);

            _basePrice = _stations.PriceAt(pump.Position, out var forecourt);

            // The selected grade until a vehicle says otherwise -- which is also the right
            // answer for a jerry can, since a can holds whatever you bought.
            _grade = _cfg.Grade;
            _price = _basePrice * _cfg.PriceFor(_grade);

            _stationBrand = forecourt == null ? "PUMP" : forecourt.Brand;
            _stationPlace = forecourt == null ? "" : forecourt.Name;
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

                return new Vector3(_cfg.HoseAnchorX * side, _cfg.HoseAnchorY, AnchorHeight(pump));
            }
            catch
            {
                return new Vector3(0f, 0f, _cfg.HoseAnchorZ);
            }
        }

        /// <summary>
        /// How far up the pump the hose leaves, from the pump's own height.
        ///
        /// A typed height is a guess at the size of six different pump models, and it was
        /// raised three times before this: 1.05, then 1.52, then 1.85, then 2.10, and it was
        /// still too low on the machine in front of us. The model's bounding box knows how tall
        /// it is. Ninety-two per cent of the way up puts the hose at the holster on the lid on
        /// any of them, which is where a real one hangs.
        /// </summary>
        private float AnchorHeight(Prop pump)
        {
            if (!_cfg.HoseAnchorAuto) return _cfg.HoseAnchorZ;

            try
            {
                pump.Model.GetDimensions(out var min, out var max);

                var height = max.Z - min.Z;
                if (height < 0.3f) return _cfg.HoseAnchorZ;   // not a pump-shaped thing

                var z = min.Z + height * _cfg.HoseAnchorHeight;

                Log.Once("pump-height-" + pump.Model.Hash,
                         "Pump model is " + height.ToString("0.00") + "m tall; hose leaves it at " +
                         z.ToString("0.00") + "m.");

                return z;
            }
            catch (Exception ex)
            {
                Log.Once("pump-bounds", "Could not measure the pump: " + ex.Message);
                return _cfg.HoseAnchorZ;
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

            return _pump != null && _pump.Exists() ? _pump.Position : _nozzle.HandPosition();
        }

        // ==================================================================
        // Carrying: nozzle in hand, hose out
        // ==================================================================

        private void Carrying(Ped me)
        {
            if (_pump == null || !_pump.Exists()) { Abandon("the pump went away"); return; }

            _nozzle.Take();     // keeps asking until the model streams; no-op once it is out
            RopePicker();
            LockHands();

            var anchor = Anchor();
            _hose.Update(anchor, _nozzle.HoseEnd());

            if (_hazard.Update(_pump.Position, false)) { Abandon("the pump went up"); return; }

            if (Leash(me, anchor)) return;

            // HangUpReach, not PumpReach, and they are different numbers for a reason that only
            // turns up in play: you park right next to the pump, so the filler is nearly always
            // inside PumpReach as well.
            var toPump = me.Position.DistanceTo(_pump.Position);
            var atPump = toPump <= _cfg.HangUpReach;

            var vehicle = NearestFillable(me, out var filler, out var inReach);
            var tank = vehicle == null ? null : _tanks.For(vehicle);
            var hasRoom = tank != null && tank.Litres < tank.Capacity - 0.05f;

            if (_cfg.ShowFillerMarker && vehicle != null)
            {
                World.DrawMarker(MarkerType.Cylinder,
                                 filler - new Vector3(0f, 0f, 0.45f),
                                 Vector3.Zero, Vector3.Zero,
                                 new Vector3(0.22f, 0.22f, 0.22f),
                                 inReach ? Color.FromArgb(170, 120, 235, 130)
                                         : Color.FromArgb(120, 245, 175, 55),
                                 false, false, false, null, null, false);
            }

            // WHICHEVER YOU ARE ACTUALLY STANDING AT WINS. Not a fixed order, because a fixed
            // order in either direction makes one of the two prompts unreachable.
            //
            // Both want the same button, and at a pump you are inside both radii at once: the
            // car is parked at the pump, so the filler is within CapReach at the same moment the
            // pump is within HangUpReach. Hanging up was tested first once, and the fill prompt
            // could not be got at all. Filling was put first to fix that, and now hanging up
            // cannot be got. Ordering was never the answer -- it just chooses which half of the
            // interaction to break.
            //
            // Distance decides it, and it decides it correctly without any new numbers: standing
            // at the pump the pump is under a metre away and the filler is three, and standing
            // at the filler it is the other way round. That is exactly the intent that the
            // radii were being asked, and failing, to express.
            var toFiller = vehicle == null ? float.MaxValue : me.Position.DistanceTo(filler);

            RangeOnce(toPump, toFiller);

            var canFill = vehicle != null && inReach && hasRoom;

            // AND IT STICKS ONCE IT HAS DECIDED. Nearer-wins was right and not enough.
            //
            // Reported from the wild: "it'll bounce between filling up the car and hanging up
            // the nozzle, and it can be difficult to find the exact right place". The log says
            // why -- standing at a pump the two distances come out 1.41m and 1.06m, or 2.46 and
            // 2.24. Twenty to thirty centimetres apart. A bare "which is nearer" flips on every
            // step, every idle sway of the camera, and the prompt strobes between two things
            // that both want the same button.
            //
            // So a choice already made has to be BEATEN, not merely matched: the other one must
            // be clearly nearer, by more than the distance a standing player drifts. Nothing
            // changes when you are plainly at one or the other; it only stops the coin-flip in
            // the middle, which is the only place it was ever wrong.
            var margin = _cfg.PromptStickiness;

            int choice;

            if (!canFill && !atPump) choice = 0;
            else if (canFill && !atPump) choice = 1;
            else if (!canFill) choice = 2;
            else if (_prompt == 1) choice = toFiller < toPump + margin ? 1 : 2;
            else if (_prompt == 2) choice = toFiller < toPump - margin ? 1 : 2;
            else choice = toFiller < toPump ? 1 : 2;

            _prompt = choice;

            if (choice == 1)
            {
                // RESOLVED BEFORE THE PROMPT, so the price you are quoted is the price you
                // pay. Doing it after the button would quote unleaded and charge diesel.
                _grade = Diesel.GradeFor(_cfg, vehicle, _cfg.Grade);
                _price = _basePrice * _cfg.PriceFor(_grade);

                // Named only when it is worth naming. REGULAR on every prompt is a word that
                // never changes and so stops being read; DIESEL and PREMIUM are the ones that
                // cost differently and are the reason to look.
                var grade = _grade == FuelGrade.Regular ? "" : "   " + Diesel.Name(_grade);

                Prompt(Control.Context, "Fill the " + vehicle.LocalizedName + grade +
                                        "   $" + _price.ToString("0.00", CultureInfo.InvariantCulture) + "/L");

                if (!Pressed()) return;

                _target = vehicle;
                _targetTank = tank;
                _stage = Stage.Filling;

                // TURNED ON THE SPOT, once, as filling begins.
                //
                // FaceThe has been called every frame of the fill all along and he stayed put,
                // because SET_PED_DESIRED_HEADING is a nudge for the LOCOMOTION system to
                // resolve -- and he is standing still with the movement controls disabled and an
                // animation on him, so there is no locomotion left to resolve it. A desired
                // heading with nothing to walk it round is just a number nobody reads.
                //
                // Setting the heading itself does not go through any of that. It is abrupt by
                // nature, which is the trade: he is already roughly facing the car by the time
                // he can reach the filler, so the correction is small, and a small snap on a
                // button press reads as him squaring up to the job.
                Turn(me, filler);

                Log.Info("Filling " + vehicle.LocalizedName + " (" +
                         tank.Litres.ToString("0.0", CultureInfo.InvariantCulture) + "/" +
                         tank.Capacity.ToString("0.0", CultureInfo.InvariantCulture) + " L).");
                return;
            }

            if (choice == 2)
            {
                Prompt(Control.Context, "Hang the nozzle up");

                OfferCanFill(me);

                if (Pressed()) HangUp();
                return;
            }

            if (vehicle != null && inReach && !hasRoom)
            {
                Draw.Help(vehicle.LocalizedName + " is full. Hang the nozzle back on the pump.");
                return;
            }

            // NO "DROP IT" ANY MORE. Putting the nozzle back is the end of the interaction, and
            // an escape hatch that skipped it made the walk back optional -- which is most of
            // what the mod is. It can still be pulled out of your hands by walking too far, and
            // it still ends up on the tarmac if you get into a car with it, but neither of
            // those is a button you press.
            Draw.Help(vehicle == null
                          ? "Walk the nozzle to the filler on the side of your vehicle."
                          : "Take the nozzle to the filler on the " + vehicle.LocalizedName + ".");
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
            _hose.Update(anchor, _nozzle.HoseEnd());

            if (_hazard.Update(_pump.Position, true)) { Abandon("the pump went up"); return; }

            LockHands();
            HoldStill(me);
            FillPose(me);

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
                _targetTank.Grade = _grade;

                _dispensed += wanted;
                _owed += wanted * _price;
                Settle();
            }

            _tanks.Touch(_target, _targetTank, true);

            _meter.Draw(_stationBrand, _stationPlace, _dispensed, _price, _owed,
                        !_cfg.ChargeMoney, _targetTank, _grade);
            _gauge.Update(_target, _targetTank, true);

            Prompt(Control.Context, "Stop");
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
            if (due > _paid) Charge(due - _paid);
        }

        /// <summary>
        /// Takes money, and CHECKS THAT IT WENT.
        ///
        /// The old version marked the bill paid whether or not the write landed, which is the
        /// worst of both worlds: no money leaves the player's wallet and the mod believes it
        /// has been paid, so nothing anywhere reports a thing. There are real reasons the write
        /// can do nothing --
        ///
        ///   * SHVDN's Player.Money reads and writes SP0/SP1/SP2_TOTAL_CASH chosen by the
        ///     player's MODEL. On any ped that is not Michael, Franklin or Trevor -- an online
        ///     model, a ped another mod put you in -- the getter returns 0 and the setter is a
        ///     no-op, silently.
        ///   * Another script writing the same stat every frame simply overwrites it.
        ///
        /// So the balance is read back. If it did not move, the charge is reported once and
        /// then abandoned for the session rather than pretending, because a pump that says it
        /// is charging you and is not is worse than one that says it cannot.
        /// </summary>
        private void Charge(int amount)
        {
            if (amount <= 0 || _cannotCharge) return;

            try
            {
                var before = Game.Player.Money;
                Game.Player.Money = before - amount;
                var after = Game.Player.Money;

                if (after < before)
                {
                    _paid += before - after;
                    return;
                }

                _cannotCharge = true;

                Log.Warn("Tried to take $" + amount + " and the balance did not move (" +
                         before + " before, " + after + " after). Player.Money works off the " +
                         "SP0/SP1/SP2_TOTAL_CASH stat picked by the player's MODEL, so it does " +
                         "nothing on a ped that is not one of the three protagonists -- and " +
                         "another mod writing the same stat will overwrite it. Fuel is free for " +
                         "the rest of this session and the pump will say so.");

                Notify("~y~The pump could not take payment~s~ - see Fumes.log.");
            }
            catch (Exception ex)
            {
                _cannotCharge = true;
                Log.Error("Could not take payment; fuel is free this session.", ex);
            }
        }

        /// <summary>Set once the wallet has proved it will not move. Stops the mod lying about it.</summary>
        private bool _cannotCharge;

        private void Stop(Ped me, string because)
        {
            StopFillPose();

            var litres = _dispensed;
            var owed = _owed;

            _stage = Stage.Carrying;
            _target = null;
            _targetTank = null;
            _dispensed = 0f;
            _owed = 0f;

            if (litres < 0.05f)
            {
                if (because != null) Notify("~y~Stopped:~s~ " + because + ".");
                return;
            }

            // The last part-dollar. Settle only ever takes WHOLE dollars as it goes, so
            // without this the final few cents of every fill were quietly forgiven -- small,
            // but it made the receipt a number that had not actually been charged.
            if (_cfg.ChargeMoney)
            {
                var total = (int)Math.Ceiling(owed - 0.001f);
                if (total > _paid) Charge(total - _paid);
            }

            var receipt = _gauge.Volume(litres);
            if (_cfg.ChargeMoney)
            {
                receipt += _cannotCharge
                    ? " ~s~- ~y~not charged~s~"
                    : " ~s~for ~g~$" + owed.ToString("0.00", CultureInfo.InvariantCulture) + "~s~";
            }

            _paid = 0;

            Notify(_stationName + ": " + receipt + (because == null ? "." : " - " + because + "."));

            // You are still holding it. Said once, on the transition, rather than left to the
            // prompt -- the prompt only appears once you are back within reach of something,
            // and the moment you need telling is the moment the pump stops.
            Notify("Hang the nozzle back on the pump.");
        }

        // ==================================================================
        // Putting it back
        // ==================================================================

        /// <summary>
        /// Says, once, how far the pump and the filler actually are when both are in range.
        ///
        /// The two prompts are chosen between by distance now, and the whole question is which
        /// of two numbers nobody can see is smaller. One line saying what they were the first
        /// time both were live turns "it still picks the wrong one" into something checkable.
        /// </summary>
        private void RangeOnce(float toPump, float toFiller)
        {
            if (_rangeLogged || toFiller > 500f) return;
            _rangeLogged = true;

            Log.Info("Standing " + toPump.ToString("0.00", CultureInfo.InvariantCulture) +
                     "m from the pump and " + toFiller.ToString("0.00", CultureInfo.InvariantCulture) +
                     "m from the filler (hang up within " +
                     _cfg.HangUpReach.ToString("0.0", CultureInfo.InvariantCulture) + "m, fill within " +
                     _cfg.CapReach.ToString("0.0", CultureInfo.InvariantCulture) + "m).");
        }

        private bool _rangeLogged;

        /// <summary>Which prompt is showing: 0 none, 1 fill, 2 hang up. See Carrying.</summary>
        private int _prompt;

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

            StopFillPose();

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
            _siphonLine.Release();
            _sound.Silence();
            TidyDropped(true);
            Clear();
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
        /// <summary>
        /// Puts him square to something now, rather than asking him to get there.
        ///
        /// The blunt instrument behind FaceThe: same arithmetic, but it writes the heading
        /// instead of desiring it. Used once when filling starts, because a desired heading
        /// needs a locomotion system to act on it and he has none while held still.
        /// </summary>
        private static void Turn(Ped me, Vector3 point)
        {
            try
            {
                var to = point - me.Position;
                if (to.Length() < 0.05f) return;

                var heading = (float)(Math.Atan2(-to.X, to.Y) * 180d / Math.PI);

                me.Heading = heading;
                Function.Call(Hash.SET_PED_DESIRED_HEADING, me.Handle, heading);

                Log.Debug("Turned him to " + heading.ToString("0") + " degrees to face the filler.");
            }
            catch (Exception ex)
            {
                Log.Once("turn", "Could not turn him to the car: " + ex.Message);
            }
        }

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

        /// <summary>
        /// One action, on the game's own instructional button bar.
        ///
        /// Going through a CONTROL rather than drawing a letter is what makes this work on a
        /// pad: the same call renders E on a keyboard and the right D-pad glyph on a
        /// controller, and it follows a rebind made in the game's own settings.
        ///
        /// A custom InteractKey is the one case the glyph cannot show, because the glyph is
        /// for the control and the custom key is ours -- so it is named in the label instead,
        /// and only when it is not already the key the control is on.
        /// </summary>
        private void Prompt(Control control, string label)
        {
            if (control == Control.Context && !IsDefaultKey()) label += "   [" + KeyName() + "]";

            if (_cfg.Prompts == PromptStyle.ButtonBar)
            {
                _buttons.Show(control, label);
                if (!_buttons.Failed) return;
            }

            // Help text, top left, with a REAL BUTTON GLYPH in it. ~INPUT_...~ resolves to
            // whatever the player has that control bound to, on keyboard or pad -- and help
            // text is the only place in the game where those tags resolve at all. Anywhere
            // else they draw nothing, not even as literal text, which is indistinguishable
            // from a broken icon.
            var line = Tag(control) + " " + label;
            _fallback = string.IsNullOrEmpty(_fallback) ? line : _fallback + "~n~" + line;
        }

        /// <summary>The help-text token that draws a control as its button.</summary>
        private static string Tag(Control control)
        {
            return control == Control.ContextSecondary
                ? "~INPUT_CONTEXT_SECONDARY~"
                : "~INPUT_CONTEXT~";
        }

        /// <summary>Whether InteractKey is still the key the context control itself is on.</summary>
        /// <summary>
        /// The filling pose: one arm out to the car, over whatever his legs are doing.
        ///
        /// FLAG 51 is what makes it usable. The native's own table calls 48-63 "upper body,
        /// controllable" -- it blends over the lower body and leaves the player in charge, so
        /// he can still turn and be shoved about. A full-body clip would plant him rigid at the
        /// car, which is a cutscene, not a pose.
        ///
        /// Asked for every frame but only STARTED when it is not already running: TASK_PLAY_ANIM
        /// restarts from the first frame every time it is called, so calling it unconditionally
        /// is an arm that twitches back to the start sixty times a second.
        /// </summary>
        private void FillPose(Ped me)
        {
            if (string.IsNullOrEmpty(_cfg.FillAnimDict) || string.IsNullOrEmpty(_cfg.FillAnimClip)) return;

            if (_poseImpossible) return;

            try
            {
                // Checked rather than assumed: a dict name that is not in the game makes
                // REQUEST_ANIM_DICT wait forever, so without this a typo in the ini is a pose
                // that never appears and never explains itself.
                if (!Function.Call<bool>(Hash.DOES_ANIM_DICT_EXIST, _cfg.FillAnimDict))
                {
                    _poseImpossible = true;
                    Log.Warn("[Nozzle] FillAnimDict '" + _cfg.FillAnimDict + "' is not an " +
                             "animation dictionary this game has. He will hold the nozzle " +
                             "still instead.");
                    return;
                }

                if (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, _cfg.FillAnimDict))
                {
                    // Requested every frame until it arrives. A single request that gets dropped
                    // under streaming pressure is never made again.
                    Function.Call(Hash.REQUEST_ANIM_DICT, _cfg.FillAnimDict);
                    return;
                }

                if (!Function.Call<bool>(Hash.IS_ENTITY_PLAYING_ANIM, me.Handle,
                                         _cfg.FillAnimDict, _cfg.FillAnimClip, 3))
                {
                    Function.Call(Hash.TASK_PLAY_ANIM, me.Handle, _cfg.FillAnimDict, _cfg.FillAnimClip,
                                  4f, -4f, -1, _cfg.FillAnimFlag, 0f, false, false, false);
                }

                _posing = true;

                if (_cfg.FillAnimPhase < 0f) return;

                // HELD, NOT PLAYED, and held EVERY FRAME rather than once.
                //
                // A handshake is a movement -- reach, grip, shake, withdraw -- so letting it run
                // gives an arm that pumps and then drops back to his side. Stopping it at the
                // reach turns the movement into a pose. Speed zero freezes it and the time is
                // re-set each frame because the task keeps its own clock: set once, it creeps.
                Function.Call(Hash.SET_ENTITY_ANIM_SPEED, me.Handle,
                              _cfg.FillAnimDict, _cfg.FillAnimClip, 0f);

                Function.Call(Hash.SET_ENTITY_ANIM_CURRENT_TIME, me.Handle,
                              _cfg.FillAnimDict, _cfg.FillAnimClip, _cfg.FillAnimPhase);
            }
            catch (Exception ex)
            {
                Log.Once("fillpose", "Could not play the filling animation: " + ex.Message +
                                     " - he will hold the nozzle still instead.");
            }
        }

        /// <summary>Set when the configured clip cannot work at all, so nothing keeps retrying.</summary>
        private bool _poseImpossible;

        // ==================================================================
        // The pouring animation
        // ==================================================================

        /// <summary>
        /// Clips to try for tipping the can, best first.
        ///
        /// THE GAME'S OWN POURING ANIMATION, PLAYED DIRECTLY. The alternative is to give him the
        /// petrol can and make him fire it, which is how the animation normally happens -- and
        /// which also lays a petrol trail down the forecourt, drains the ammo on the game's
        /// schedule rather than ours, and leaves a lit fuse next to a pump. Playing the clip
        /// gets the motion and none of the consequences.
        ///
        /// CANDIDATES, because clip names inside a dictionary cannot be listed from a script:
        /// DOES_ANIM_DICT_EXIST answers for the dictionary and nothing answers for what is in
        /// it. Each is started and then checked with IS_ENTITY_PLAYING_ANIM -- an animation that
        /// is not playing a moment after being asked for does not exist -- and the one that
        /// takes is written to the log so it can become the setting.
        /// </summary>
        private static readonly string[][] PourClips =
        {
            // THE RIGHT ONE, and it is now known rather than guessed. The trailing @ is not a
            // typo: weapons@misc@jerrycan@ holds discard, fire, fire_intro, fire_outro and
            // unholster -- the ACTIONS -- while weapons@misc@jerrycan@mp_male holds idle, run,
            // sprint and walk, which are how you CARRY one. Two dictionaries a character apart,
            // and only one of them pours.
            //
            // The probe settled on mp_male/idle and was not wrong to: idle exists and plays,
            // so "is this animation running" answered yes. It cannot answer "is this the
            // animation I meant", which is the same wall the fill sound hit. The fix was not a
            // better probe, it was looking the clips up -- they are listed in DurtyFree's
            // gta-v-data-dumps animDictsCompact.json, all twenty thousand dictionaries of them.
            new[] { "weapons@misc@jerrycan@", "fire" },
            new[] { "weapon@w_sp_jerrycan", "fire" },
            new[] { "weapons@misc@jerrycan@", "fire_intro" },
        };

        private int _pourClip = -1;
        private bool _pouring;
        private int _pourStartedAt;
        private bool _pourImpossible;

        /// <summary>Plays the pour, looping, and moves on from a clip that will not run.</summary>
        private void PourPose(Ped me)
        {
            if (_pourImpossible) return;

            var dict = Dict();
            var clip = Clip();

            if (string.IsNullOrEmpty(dict) || string.IsNullOrEmpty(clip)) return;

            try
            {
                if (!Function.Call<bool>(Hash.DOES_ANIM_DICT_EXIST, dict)) { NextPour(dict, clip, "no such dictionary"); return; }

                if (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dict))
                {
                    // Asked every frame until it arrives; one request can be dropped under
                    // streaming load and never made again.
                    Function.Call(Hash.REQUEST_ANIM_DICT, dict);
                    _pourStartedAt = Game.GameTime;
                    return;
                }

                var playing = Function.Call<bool>(Hash.IS_ENTITY_PLAYING_ANIM, me.Handle, dict, clip, 3);

                if (!playing)
                {
                    if (_pouring && Game.GameTime - _pourStartedAt > 700)
                    {
                        // Asked for, loaded, and still not playing: the dictionary is real and
                        // this clip is not in it.
                        NextPour(dict, clip, "the clip is not in it");
                        return;
                    }

                    // LOOPING, not held. Pouring is a repeated motion -- the flag band 48-63 is
                    // the game's own "upper body, still in control", and 1 on top of it loops.
                    Function.Call(Hash.TASK_PLAY_ANIM, me.Handle, dict, clip,
                                  4f, -4f, -1, 49, 0f, false, false, false);

                    if (!_pouring) _pourStartedAt = Game.GameTime;
                    _pouring = true;
                    return;
                }

                if (_pourClip >= 0 && !_pourLogged)
                {
                    _pourLogged = true;
                    Log.Info("Pour animation: \"" + clip + "\" from \"" + dict + "\". Put those in " +
                             "[Nozzle] PourAnimDict and PourAnimClip to skip the search.");
                }
            }
            catch (Exception ex)
            {
                Log.Once("pourpose", "Could not play the pouring animation: " + ex.Message);
                _pourImpossible = true;
            }
        }

        private bool _pourLogged;

        private string Dict()
        {
            if (!string.IsNullOrEmpty(_cfg.PourAnimDict)) return _cfg.PourAnimDict;
            return _pourClip < 0 || _pourClip >= PourClips.Length ? PourClips[0][0] : PourClips[_pourClip][0];
        }

        private string Clip()
        {
            if (!string.IsNullOrEmpty(_cfg.PourAnimClip)) return _cfg.PourAnimClip;
            return _pourClip < 0 || _pourClip >= PourClips.Length ? PourClips[0][1] : PourClips[_pourClip][1];
        }

        private void NextPour(string dict, string clip, string why)
        {
            Log.Info("Pour animation: \"" + clip + "\" from \"" + dict + "\" - " + why + ".");

            _pouring = false;

            if (!string.IsNullOrEmpty(_cfg.PourAnimDict))
            {
                // Written in by hand and wrong. Not falling through to the list: a name was
                // asked for by name, and quietly using a different one hides the mistake.
                _pourImpossible = true;
                Log.Warn("Pour animation: the configured clip will not play. Clear [Nozzle] " +
                         "PourAnimDict to go back to the built-in list.");
                return;
            }

            _pourClip = _pourClip < 0 ? 1 : _pourClip + 1;

            if (_pourClip >= PourClips.Length)
            {
                _pourImpossible = true;
                Log.Warn("Pour animation: none of the " + PourClips.Length + " candidates will " +
                         "play. He will pour without an animation.");
            }
        }

        private void StopPourPose()
        {
            if (!_pouring) return;
            _pouring = false;
            _pourLogged = false;

            try
            {
                var me = Player();
                if (me == null) return;

                Function.Call(Hash.STOP_ANIM_TASK, me.Handle, Dict(), Clip(), -4f);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not stop the pouring animation: " + ex.Message);
            }
        }

        private bool _posing;

        /// <summary>Ends the pose. Safe whenever; does the work once.</summary>
        private void StopFillPose()
        {
            if (!_posing) return;
            _posing = false;

            try
            {
                var me = Player();
                if (me == null) return;

                Function.Call(Hash.STOP_ANIM_TASK, me.Handle, _cfg.FillAnimDict, _cfg.FillAnimClip, -4f);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not stop the filling animation: " + ex.Message);
            }
        }

        private bool IsDefaultKey()
        {
            return string.Equals(KeyName(), "E", StringComparison.Ordinal);
        }

        private string NameOf(Control control)
        {
            return control == Control.ContextSecondary ? SecondaryName() : KeyName();
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
        /// <summary>
        /// Set while the settings menu has the keyboard.
        ///
        /// Only the INPUT stops. The hose still hangs, the animation still plays and the tank
        /// still fills -- pausing the interaction because a menu is open would strand a nozzle
        /// in mid-air. It is Enter and the arrow keys that have to mean one thing at a time.
        /// </summary>
        public bool InputBlocked;

        private bool Pressed()
        {
            if (InputBlocked) return false;

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
