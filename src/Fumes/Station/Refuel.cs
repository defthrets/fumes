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
        private readonly Buttons _buttons;

        private Stage _stage = Stage.Idle;

        /// <summary>The pump the hose is attached to, and the anchor in ITS OWN space.</summary>
        private Prop _pump;
        private Vector3 _anchorLocal;

        /// <summary>Set at pickup so the price cannot change while you are standing there.</summary>
        private float _price = 1f;

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
                // DROPPED, NOT DELETED. Getting into a car with the nozzle still in your hand
                // used to make it vanish, which quietly made "put it back" optional -- you
                // could always just drive off and the mod would tidy up after you. Now it ends
                // up on the tarmac like it would if you really did that, and the only two ways
                // to finish cleanly are to hang it up or to put it down.
                if (Busy) DropIt("left on the forecourt");
                return;
            }

            _fallback = null;

            switch (_stage)
            {
                case Stage.Idle: AtRest(me); break;
                case Stage.Carrying: Carrying(me); break;
                case Stage.Filling: Filling(me, dt); break;
            }

            // One help box for however many buttons were asked for, and only when the bar
            // itself could not be drawn. Calling Draw.Help per prompt would have each one
            // overwrite the last and the player would see only whichever came last.
            if (!string.IsNullOrEmpty(_fallback)) Draw.Help(_fallback);
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

            Prompt(Control.Context, "Take the nozzle");

            if (Pressed()) Take(me, pump);
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

            return _pump != null && _pump.Exists() ? _pump.Position : _nozzle.HandPosition();
        }

        // ==================================================================
        // Carrying: nozzle in hand, hose out
        // ==================================================================

        private void Carrying(Ped me)
        {
            if (_pump == null || !_pump.Exists()) { Abandon("the pump went away"); return; }

            _nozzle.Take();     // keeps asking until the model streams; no-op once it is out
            _nozzle.Tune();     // no-op unless [Nozzle] TuneNozzle is on
            TuneHose();         // likewise
            LockHands();

            var anchor = Anchor();
            _hose.Update(anchor, _nozzle.HoseEnd());

            if (_hazard.Update(_pump.Position, false)) { Abandon("the pump went up"); return; }

            if (Leash(me, anchor)) return;

            // HangUpReach, not PumpReach, and they are different numbers for a reason that only
            // turns up in play: you park right next to the pump, so the filler is nearly always
            // inside PumpReach as well.
            var atPump = me.Position.DistanceTo(_pump.Position) <= _cfg.HangUpReach;

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

            // FILLING WINS OVER HANGING UP, and this order is the whole fix for a station being
            // unusable. Both prompts want the same button, and at a real pump you are standing
            // inside both radii at once -- so whichever is tested first is the only one you can
            // ever get. Hanging up used to be first, which meant the fill prompt was unreachable
            // at exactly the moment it was the thing you wanted.
            if (vehicle != null && inReach && hasRoom)
            {
                Prompt(Control.Context, "Fill the " + vehicle.LocalizedName +
                                        "   $" + _price.ToString("0.00", CultureInfo.InvariantCulture) + "/L");

                if (!Pressed()) return;

                _target = vehicle;
                _targetTank = tank;
                _stage = Stage.Filling;

                Log.Info("Filling " + vehicle.LocalizedName + " (" +
                         tank.Litres.ToString("0.0", CultureInfo.InvariantCulture) + "/" +
                         tank.Capacity.ToString("0.0", CultureInfo.InvariantCulture) + " L).");
                return;
            }

            if (atPump)
            {
                Prompt(Control.Context, "Hang the nozzle up");
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
            TunePose();

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

            _meter.Draw(_stationBrand, _stationPlace, _dispensed, _price, _owed,
                        !_cfg.ChargeMoney, _targetTank);
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

        private bool _phaseDownKey, _phaseUpKey, _clipNextKey, _clipPrevKey, _saveKey;

        /// <summary>
        /// Clips worth trying for the filling pose, in the order they are cycled.
        ///
        /// A LIST RATHER THAN A CHOICE, because the thing that disqualifies a clip cannot be
        /// checked from outside the game. TASK_PLAY_ANIM has no way to animate ONE ARM: the
        /// upper-body flag takes the whole upper body, so a clip that raises the free arm
        /// raises it whatever you do. Whether a given clip does that is not written down
        /// anywhere -- you have to look at it.
        ///
        /// So the list is candidates, not answers. Cycle them at a pump, keep the one where
        /// only the nozzle arm moves, and press save. Anything this game does not have is
        /// skipped with a word in the log rather than silently doing nothing.
        /// </summary>
        private static readonly string[][] Clips =
        {
            // Ordered by how little of him each one is likely to move, smallest first.
            //
            // What is wanted is ONE ARM, LIFTED A LITTLE, and the clips most likely to give
            // that are the ones authored for somebody operating a small object at waist
            // height: a key fob, a parking meter, a hand-over. A greeting or a hold-up moves
            // the whole torso because that is what those gestures are.
            new[] { "anim@mp_player_intmenu@key_fob@", "fob_click" },
            new[] { "amb@prop_human_parking_meter@male@idle_a", "idle_a" },
            new[] { "mp_common", "givetake1_a" },
            new[] { "mp_common", "givetake2_a" },
            new[] { "weapons@misc@jerrycan@mp_male", "idle" },
            new[] { "anim@heists@humane_labs@finale@keycards", "ped_a_enter_loop" },
            new[] { "mp_ped_interaction", "handshake_guy_a" },
            new[] { "anim@am_hold_up@male", "shoplift_high" }
        };

        private int _clip = -1;

        /// <summary>
        /// Nudges the frozen point of the pose while you are looking at it, when TuneNozzle is on.
        ///
        /// The right phase of a clip is not something that can be reasoned to -- it is wherever
        /// that particular animation happens to have the arm in the right place, which you can
        /// only see. NumPad 7 and 9, because the gauge tuner that also uses them is switched off
        /// while your hands are full.
        /// </summary>
        private void TunePose()
        {
            if (!_cfg.TuneNozzle) return;

            try
            {
                if (Edge(System.Windows.Forms.Keys.NumPad7, ref _phaseDownKey)) _cfg.FillAnimPhase -= 0.02f;
                if (Edge(System.Windows.Forms.Keys.NumPad9, ref _phaseUpKey)) _cfg.FillAnimPhase += 0.02f;

                if (_cfg.FillAnimPhase < 0f) _cfg.FillAnimPhase = 0f;
                if (_cfg.FillAnimPhase > 1f) _cfg.FillAnimPhase = 1f;

                if (Edge(System.Windows.Forms.Keys.NumPad8, ref _clipNextKey)) CycleClip(1);
                if (Edge(System.Windows.Forms.Keys.NumPad2, ref _clipPrevKey)) CycleClip(-1);
                if (Edge(System.Windows.Forms.Keys.NumPad0, ref _saveKey)) KeepPose();

                Draw.Text("POSE  [8/2] clip   [7/9] phase   [0] save",
                          0.5f, 0.135f, 0.30f,
                          System.Drawing.Color.FromArgb(230, 245, 200, 90), 4, true);

                Draw.Text(_cfg.FillAnimDict + " / " + _cfg.FillAnimClip + "   @ " +
                          _cfg.FillAnimPhase.ToString("0.00", CultureInfo.InvariantCulture),
                          0.5f, 0.163f, 0.32f,
                          System.Drawing.Color.FromArgb(240, 255, 255, 255), 4, true);
            }
            catch (Exception ex)
            {
                Log.Once("posetune", "The pose tuner fell over: " + ex.Message);
            }
        }

        /// <summary>
        /// Moves to the next clip in the list, skipping any this game does not have.
        ///
        /// The skip is the useful part: a dict that is not in the build makes
        /// REQUEST_ANIM_DICT wait for it forever, so a candidate list without this check would
        /// stall on its first bad entry and look like the cycler was broken.
        /// </summary>
        private void CycleClip(int step)
        {
            for (var tried = 0; tried < Clips.Length; tried++)
            {
                _clip = ((_clip + step) % Clips.Length + Clips.Length) % Clips.Length;

                var dict = Clips[_clip][0];

                bool have;
                try { have = Function.Call<bool>(Hash.DOES_ANIM_DICT_EXIST, dict); }
                catch { have = false; }

                if (!have)
                {
                    Log.Info("Skipping " + dict + " -- this game does not have it.");
                    continue;
                }

                StopFillPose();

                _cfg.FillAnimDict = dict;
                _cfg.FillAnimClip = Clips[_clip][1];
                _poseImpossible = false;

                Log.Info("Filling pose: " + _cfg.FillAnimDict + " / " + _cfg.FillAnimClip + ".");
                return;
            }

            Log.Warn("None of the candidate filling clips exist in this game.");
        }

        /// <summary>Writes the pose straight into Fumes.ini, like the other tuners.</summary>
        private void KeepPose()
        {
            var ok = IniFile.SetValue(Paths.Ini, "Nozzle", "FillAnimDict", _cfg.FillAnimDict)
                   & IniFile.SetValue(Paths.Ini, "Nozzle", "FillAnimClip", _cfg.FillAnimClip)
                   & IniFile.SetValue(Paths.Ini, "Nozzle", "FillAnimPhase",
                                      _cfg.FillAnimPhase.ToString("0.00", CultureInfo.InvariantCulture));

            Log.Info("Filling pose saved: " + _cfg.FillAnimDict + " / " + _cfg.FillAnimClip +
                     " @ " + _cfg.FillAnimPhase.ToString("0.00", CultureInfo.InvariantCulture));

            Notify(ok ? "~g~Filling pose saved~s~ to Fumes.ini."
                      : "~y~Could not write Fumes.ini~s~ - it is in Fumes.log.");
        }

        private static bool Edge(System.Windows.Forms.Keys key, ref bool wasDown)
        {
            bool down;
            try { down = Game.IsKeyPressed(key); }
            catch { down = false; }

            var edge = down && !wasDown;
            wasDown = down;
            return edge;
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

        private bool _ropeKeyDown;

        /// <summary>
        /// Cycles the rope texture while you look at it, when TuneNozzle is on.
        ///
        /// This exists because there is no way to choose between GTA's nine rope types from
        /// outside the game. They are authored textures with no names, no previews and no
        /// documentation -- the only description of type 5 anywhere is that SHVDN calls it
        /// "thin metal wire" -- so picking one is a matter of looking at all nine and saying
        /// which is a fuel hose. That takes about fifteen seconds in front of a pump and is
        /// otherwise impossible.
        ///
        /// NumPad * because it is one of the very few keys nothing else on this machine claims;
        /// NumPad + belongs to Enhanced Native Trainer.
        /// </summary>
        private void TuneHose()
        {
            if (!_cfg.TuneNozzle) return;

            bool down;
            try { down = Game.IsKeyPressed(System.Windows.Forms.Keys.Multiply); }
            catch { down = false; }

            var edge = down && !_ropeKeyDown;
            _ropeKeyDown = down;

            if (edge)
            {
                _cfg.HoseRopeType = (_cfg.HoseRopeType + 1) % 9;

                // The type is baked in at ADD_ROPE, so the rope has to be thrown away and made
                // again. It respawns on the next frame from Carrying.
                _hose.Retract();

                Log.Info("Hose rope type is now " + _cfg.HoseRopeType + ".");
            }

            Draw.Text("HOSE  [NumPad *]  rope type " + _cfg.HoseRopeType + " of 0-8",
                      0.5f, 0.145f, 0.32f,
                      System.Drawing.Color.FromArgb(235, 245, 200, 90), 4, true);

            MarkHoseEnd();
        }

        /// <summary>
        /// Puts a dot exactly where the hose is joining the nozzle, while tuning.
        ///
        /// The three HoseEnd numbers are an offset in the NOZZLE'S own space, and the nozzle is
        /// rotated three ways -- so which direction "back a bit" turns out to be is not
        /// something anybody can work out in their head, including me. Being able to see the
        /// point turns the tuning from a guess into a nudge.
        ///
        /// Only while TuneNozzle is on, obviously. It is a workbench light, not a feature.
        /// </summary>
        private void MarkHoseEnd()
        {
            try
            {
                World.DrawMarker(MarkerType.Sphere, _nozzle.HoseEnd(),
                                 Vector3.Zero, Vector3.Zero,
                                 new Vector3(0.02f, 0.02f, 0.02f),
                                 System.Drawing.Color.FromArgb(200, 120, 235, 255),
                                 false, false, false, null, null, false);
            }
            catch (Exception ex)
            {
                Log.Once("hosemark", "Could not mark the hose end: " + ex.Message);
            }
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
