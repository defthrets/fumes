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
        Siphoning,

        /// <summary>
        /// The grade card is up and nothing is flowing yet.
        ///
        /// Between taking the nozzle and fuel moving, because that is where the decision
        /// belongs: after you have chosen the car, before you have bought anything.
        /// </summary>
        Choosing
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
            _grades = new Grades(cfg);

            LoadCan();
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
            TidyDroppedCan();

            // THE CAN'S LEVEL ON THE TANKS' TEN-SECOND CADENCE. It was written at shutdown
            // only, and shutdown is the one moment a crash never reaches -- so the level that
            // survived a crash was whatever the game's ammo said, which is the number this
            // whole file exists to distrust.
            _sinceCanSave += dt;
            if (_canDirty && _sinceCanSave >= 10f)
            {
                _sinceCanSave = 0f;
                SaveCan();
            }

            var me = Player();
            if (me == null)
            {
                if (Busy) Abandon("the player is not available");
                return;
            }

            // Getting into a car, dying, being arrested, a cutscene starting -- all of them
            // end the same way, and none of them should leave a hose hanging in the air.
            // THE NATIVE, NOT Ped.IsEnteringVehicle: that property arrived after 3.6.0 and this
            // has to build against 3.6.0 -- see build.ps1 for why the reference is the oldest
            // build and not the newest. IS_PED_GETTING_INTO_A_VEHICLE is in every Hash enum
            // this runs on, checked rather than assumed.
            if (!me.IsAlive || me.IsInVehicle() ||
                Function.Call<bool>(Hash.IS_PED_GETTING_INTO_A_VEHICLE, me.Handle))
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

            // BEFORE the stage runs, so a prompt is never offered against litres the game
            // invented a frame ago.
            GuardCan(me);

            // The prompt's memory only means anything while the nozzle is out. Left set, walking
            // away and coming back would resume mid-argument with whatever it last decided.
            if (_stage != Stage.Carrying) _prompt = 0;

            switch (_stage)
            {
                case Stage.Idle: AtRest(me); break;
                case Stage.Carrying: Carrying(me, dt); break;
                case Stage.Filling: Filling(me, dt); break;
                case Stage.Pouring: Pouring(me, dt); break;
                case Stage.FillingCan: FillingCan(me, dt); break;
                case Stage.Siphoning: Siphoning(me, dt); break;
                case Stage.Choosing: Choosing(me); break;
            }

            // AFTER the switch and driven by the stage rather than by calls inside it.
            // Filling ends in six different places and a Stop missing from any one of them is
            // a sound that plays until the game is closed.
            _sound.Update(_stage == Stage.Filling, me);

            // AND THE POSES, for exactly the same reason and in exactly the same place.
            //
            // The sound got this treatment because filling ends in six different places and a
            // Stop missing from any one of them is a sound that plays until the game is closed.
            // The animations end in MORE places than the sound does -- the can runs out, the
            // tank fills, you walk off the leash, the car despawns, you swing at somebody, you
            // press stop -- and every one of them was expected to remember to take the clip off
            // him. Miss one and he siphons for the rest of the session.
            //
            // Driven by the stage, nothing has to remember. If the stage that wants a pose is
            // not running, the pose comes off, whichever way it stopped running.
            Poses(me);

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
            var pump = _pumps.Nearest(me.Position, _cfg.PumpReach);

            // THE CAN IS OFFERED FIRST. A jerry can is for the roadside -- the whole point of
            // carrying one is that there is no pump -- so nothing about it is gated behind
            // "is there a pump nearby".
            //
            // BUT THE PUMP STILL GETS ITS SAY. The can's prompts sit on the interact key and
            // filling it at a pump sits on the secondary, so the two can stand together -- and
            // they have to, because this branch used to return before the pump was looked at.
            // An EMPTY can was offered "put it down" and nothing else, which made the one can
            // that most needed a pump the one can a pump could never fill; a can with a tenth
            // of a litre in it fell through and filled fine. Reported by AliG_15, twice.
            if (OfferCan(me, pump != null))
            {
                if (pump != null) OfferCanAtPump(me, pump);
                return;
            }

            // AFTER the can he is holding, before the pump. A can on the floor is a smaller
            // thing than a forecourt and you are standing on top of it, so it wins over a pump
            // several metres away -- but it must not talk over the can already in his hands.
            if (OfferPickUpCan(me)) return;

            if (pump == null) return;

            Prompt(Control.Context, "Take the nozzle");

            OfferCanAtPump(me, pump);

            if (Pressed()) Take(me, pump);
        }

        /// <summary>
        /// The prompt to pour a can into a tank. True when it took the prompt this frame.
        /// </summary>
        private bool OfferCan(Ped me, bool atPump)
        {
            if (!_cfg.JerryCan) return false;

            var litres = CanLitres(me);
            if (litres <= 0.01f) return OfferEmptyCan(me, atPump);

            var vehicle = NearestFillable(me, out var filler, out var inReach);
            if (vehicle == null || !inReach) return false;

            var tank = _tanks.For(vehicle);
            if (tank == null) return false;

            // A FULL CAR USED TO END THIS WHOLE METHOD, which took the siphon prompt down with
            // the pour prompt -- so the one vehicle you would most want to help yourself from
            // was the one vehicle you could not. Only the pouring half depends on there being
            // room in it.
            var hasRoom = tank.Litres < tank.Capacity - 0.05f;

            if (hasRoom)
            {
                // THE LITRES BEFORE THE NAME, because the help box drops whatever does not fit
                // from the END, and of the two the name is the part already standing in front
                // of you. In the other order a long vehicle name pushed the quantity off the
                // edge, and a half-eaten "11.1" with its unit missing is worse than a
                // shortened name.
                Prompt(Control.Context, "Pour " + litres.ToString("0.0", CultureInfo.InvariantCulture) +
                                        " L into the " + vehicle.LocalizedName);
            }

            // Not at a pump: there the secondary key is the pump's. See OfferEmptyCan.
            if (!atPump) OfferSiphon(me, vehicle, tank, litres);

            if (!hasRoom) return true;

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
        /// <summary>
        /// Whether the can is IN HIS HANDS, as opposed to merely in his inventory.
        ///
        /// A SEPARATE QUESTION FROM Can(), and the one that was asked wrongly. Can() looks up
        /// the weapon he OWNS, which is right for reading and writing what is in it -- that has
        /// to work with the can on his back. It is the wrong test for showing a prompt: it is
        /// true from the moment he first picks a can up until he loses it, so "Put the empty
        /// can down" followed him around the map empty-handed.
        /// </summary>
        private static bool HoldingCan(Ped me)
        {
            try
            {
                var w = me.Weapons.Current;
                return w != null && w.Hash == WeaponHash.PetrolCan;
            }
            catch
            {
                return false;
            }
        }

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
        /// <summary>
        /// Litres from an ammo count, and ammo back from litres, with ONE UNIT HELD BACK.
        ///
        /// THE GAME TAKES AN EMPTY PETROL CAN OFF YOU. That is vanilla behaviour and nothing to
        /// do with this mod -- run the ammo to zero and he drops it, which is fine when the can
        /// is only ever a weapon and ruinous here, where an empty can is still the thing you
        /// siphon INTO. Losing it at the exact moment it becomes useful is the wrong way round.
        ///
        /// So one unit of ammo is reserved and never spent. Ammo 1 means "he has a can and it
        /// is empty"; the useful range is 1..max mapped onto 0..capacity, so the reserved unit
        /// is not readable as fuel and cannot be poured. The can stays in his hands and the
        /// mod's own litres stay honest.
        ///
        /// Both directions live here because the mapping is written in four places -- held and
        /// owned, read and write -- and a reserve applied to three of them is a can that gains
        /// or loses a litre depending on which one asked.
        /// </summary>
        private float LitresFrom(int ammo, int max)
        {
            var floor = _cfg.KeepEmptyCan ? 1 : 0;

            if (max <= floor) return 0f;

            var have = ammo - floor;
            if (have <= 0) return 0f;

            return _cfg.JerryCanLitres * have / (max - floor);
        }

        private int AmmoFrom(float litres, int max)
        {
            var floor = _cfg.KeepEmptyCan ? 1 : 0;

            if (max <= floor) return 0;

            var ammo = floor + (int)(litres / _cfg.JerryCanLitres * (max - floor));

            if (ammo < floor) ammo = floor;
            if (ammo > max) ammo = max;

            return ammo;
        }

        private float CanFuel(Ped me)
        {
            var can = Can(me);
            if (can == null) return 0f;

            try
            {
                return LitresFrom(can.Ammo, can.MaxAmmo);
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
                if (can.MaxAmmo <= 0) return;

                // Written first, remembered second. Remembering a level that then failed to
                // land is a memory of a can that does not exist.
                can.Ammo = AmmoFrom(litres, can.MaxAmmo);
                Remember(litres);
            }
            catch (Exception ex)
            {
                Log.Once("can-ammo", "Could not change what is in the can: " + ex.Message);
            }
        }

        /// <summary>
        /// What an empty can can still do: be put down, or be filled back up.
        ///
        /// IT COULD DO NEITHER BEFORE. OfferCan gives up above zero litres and OfferSiphon was
        /// called from inside it, so a can drained to nothing offered no prompt at all -- and
        /// being refilled is the entire reason KeepEmptyCan holds on to it in the first place.
        /// Reaching for it did nothing whatever, which reads as the mod having stopped working.
        ///
        /// Both directions on their own buttons, same as a part-full can: the primary puts it
        /// down, the secondary siphons into it.
        ///
        /// GATED ON THE CAN BEING OUT. This first shipped gated on Can(), which asks whether he
        /// OWNS one -- true from the first can he ever picks up -- so the prompt followed him
        /// round the map with empty hands. CanLitres reporting zero for a can that is not out
        /// is what routed a full can down here in the first place.
        /// </summary>
        private bool OfferEmptyCan(Ped me, bool atPump)
        {
            if (!_cfg.DropEmptyCan) return false;

            // IN HIS HANDS, not in his pockets. Can() answers "does he own one", which is true
            // from the first can he ever picks up onwards -- so this prompt followed him round
            // the map with nothing in his hands at all. It also stopped a full can he was not
            // holding from being read as an empty one, since CanLitres reports zero for a
            // weapon that is not out.
            if (!HoldingCan(me)) return false;

            // Still worth offering the siphon, and this is the case that needed it most --
            // except at a pump, where the secondary key is the pump's: filling the can there
            // is the thing you walked up for, and two prompts on one key is a coin toss.
            var vehicle = NearestFillable(me, out var filler, out var inReach);

            if (vehicle != null && inReach && !atPump)
            {
                var tank = _tanks.For(vehicle);
                if (tank != null) OfferSiphon(me, vehicle, tank, 0f);
            }

            Prompt(Control.Context, "Put the empty can down");

            if (Pressed()) DropCan(me);

            return true;
        }

        /// <summary>
        /// Puts the can on the floor as a real object and takes it off him.
        ///
        /// A PROP RATHER THAN A DELETED WEAPON, because "it vanished from my hands" is what a
        /// bug looks like. The same three models the siphon stands on the ground, so whatever
        /// this build has is what gets used.
        ///
        /// One at a time. Without that a determined player can carpet a forecourt in jerry
        /// cans, which is funny once and a physics bill forever.
        /// </summary>
        private void DropCan(Ped me)
        {
            try
            {
                TidyDroppedCan(true);

                var at = me.Position + me.ForwardVector * 0.6f;

                foreach (var name in CanProps)
                {
                    var model = new Model(name);
                    if (!model.IsValid) continue;

                    model.Request(1200);
                    if (!model.IsLoaded) { model.MarkAsNoLongerNeeded(); continue; }

                    _droppedCan = World.CreateProp(model, at, false, false);
                    model.MarkAsNoLongerNeeded();

                    if (_droppedCan == null || !_droppedCan.Exists()) continue;

                    Function.Call(Hash.PLACE_OBJECT_ON_GROUND_PROPERLY, _droppedCan.Handle);
                    _droppedCan.Heading = me.Heading + 90f;
                    break;
                }

                // Off him only once something is on the floor, so a build with none of the
                // three models keeps the can rather than losing it to a prop that never came.
                if (_droppedCan != null && _droppedCan.Exists())
                {
                    Function.Call(Hash.REMOVE_WEAPON_FROM_PED, me.Handle, (uint)WeaponHash.PetrolCan);
                    Remember(0f);

                    _tidyCanAt = Game.GameTime + 120000;

                    Notify("~y~Empty can put down.~s~");
                    Log.Info("The empty can was put down.");
                }
                else
                {
                    Log.Once("drop-can", "No jerry can model in this build to put down; " +
                                         "the can stays in his hands.");
                }
            }
            catch (Exception ex)
            {
                Log.Once("drop-can-fail", "Could not put the can down: " + ex.Message);
            }
        }

        private Prop _droppedCan;
        private int _tidyCanAt;

        /// <summary>
        /// Picking a put-down can back up.
        ///
        /// THE OTHER HALF OF PUTTING IT DOWN, and without it "put down" is a polite word for
        /// throwing it away. The can is the thing you siphon into; a player who sets one down
        /// to free his hands and then cannot retrieve it has been quietly robbed of the only
        /// item in the mod.
        ///
        /// Standing near it also holds the sweeper off, so it cannot vanish from under someone
        /// who is looking straight at it and deciding.
        /// </summary>
        private bool OfferPickUpCan(Ped me)
        {
            if (_droppedCan == null) return false;

            try
            {
                if (!_droppedCan.Exists()) { _droppedCan = null; return false; }

                if (me.Position.DistanceTo(_droppedCan.Position) > _cfg.CanPickUpReach) return false;

                // Near it, so it is not litter yet.
                _tidyCanAt = Game.GameTime + 120000;

                Prompt(Control.Context, "Pick the can up");

                if (Pressed()) PickUpCan(me);

                return true;
            }
            catch (Exception ex)
            {
                Log.Once("pickup-can", "Could not offer the can: " + ex.Message);
                return false;
            }
        }

        /// <summary>Back into his hands, with exactly what it had in it.</summary>
        private void PickUpCan(Ped me)
        {
            try
            {
                var had = _canOwn < 0f ? 0f : _canOwn;

                // A notional unit, then the real level written over it. GIVE_WEAPON_TO_PED is
                // what refills a can to full on its own, so nothing is trusted to it but the
                // fact that he now has one.
                Function.Call(Hash.GIVE_WEAPON_TO_PED, me.Handle,
                              (uint)WeaponHash.PetrolCan, 1, false, false);

                // Written through the owned-can setter rather than the held one, because
                // selecting a weapon does not take effect in the frame you ask for it and the
                // held setter reads whatever is in his hands right now.
                SetCanFuel(me, had);
                Remember(had);

                try { me.Weapons.Select(WeaponHash.PetrolCan, true); }
                catch { /* he has it; which hand it is in can wait a frame */ }

                TidyDroppedCan(true);

                Notify("~y~Can picked up.~s~");
                Log.Info("The can was picked back up with " +
                         had.ToString("0.0", CultureInfo.InvariantCulture) + " L in it.");
            }
            catch (Exception ex)
            {
                Log.Once("pickup-can-fail", "Could not pick the can up: " + ex.Message);
            }
        }

        /// <summary>Takes a put-down can away again, so the world does not fill up with them.</summary>
        private void TidyDroppedCan(bool now = false)
        {
            if (_droppedCan == null) return;
            if (!now && Game.GameTime < _tidyCanAt) return;

            SafeDelete(_droppedCan, "put-down can");
            _droppedCan = null;
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

            // SQUARE HIM UP FIRST, once, before any of the pose goes on.
            //
            // The pose is a fixed clip: it puts his arm wherever the animator put it, relative
            // to HIM, and it has no idea where the car is. So the only way the hand lands on
            // the filler is for him to be standing at the right angle to it, and that is an
            // offset from facing the cap rather than facing it -- the clip reaches across his
            // body, not straight out in front.
            //
            // Once, here, rather than every frame: held every frame it would fight him the
            // moment he tried to move, and he is allowed to move.
            Turn(me, Filler.On(vehicle, out var _), _cfg.SiphonTurnRight);

            // Read ONCE, here, before anything changes it. Reading it later would read back
            // the crouch this mod put on him and then "restore" it.
            try { _wasStealthy = me.IsInStealthMode; }
            catch { _wasStealthy = false; }

            _crouched = false;
            _burstFrom = Game.GameTime;

            _spoutWasForward = _cfg.SiphonSpoutForward;
            _spoutWasSide = _cfg.SiphonSpoutSide;
            _spoutWasUp = _cfg.SiphonSpoutUp;

            _spilled = 0f;
            _poolWidth = 0f;
            _poolDecals = 0;
            _poolAt = Vector3.Zero;

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

            if (_hazard.Update(_pump.Position, true)) { Abandon("the pump went up"); return; }

            // THE NOZZLE AND THE HOSE IN BOTH MODES. He came to the pump for fuel either way,
            // and a can filling itself from a pump he is not holding is what looked wrong.
            var anchor = Anchor();
            _hose.Update(anchor, _nozzle.HoseEnd());

            LockHands();

            if (_canAtPump)
            {
                // The can on the floor and him crouched over it, nozzle still in his hand and
                // lifted by the same pose that fills a car. The crouch is a full-body clip
                // underneath and the pose is upper body on top -- one owns the legs, the other
                // owns the arms -- so he bends over the can and the arm still reaches.
                //
                // No HoldStill: the crouch clip owns his legs already, and planting him twice
                // is what makes a pose look like a freeze.
                PutCanDown(me, true);
                Crouch(me);
            }
            else
            {
                HoldStill(me);
            }

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

                // EVERY TICK, NOT AT THE END. The nozzle restores the can's ammo whenever it
                // goes back, and it goes back on every way out of here -- hanging up, walking
                // off, dying, getting into a car. Telling it only when the fill finishes
                // cleanly would still lose the fuel on all the other endings.
                _nozzle.NoteOwnAmmo(me);

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

            if (_canAtPump)
            {
                _canAtPump = false;
                StandUp(me);
                PickCanUp(me);
            }

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

            // THE LEASH, not the reach test the other stages use. Standing still, "can he
            // touch the cap" and "is he still doing this" are the same question. Walking, they
            // come apart, and it is the second one that ends a siphon.
            if (me.Position.DistanceTo(filler) > _cfg.SiphonLeash)
            {
                StopSiphon(me, "you moved away");
                return;
            }

            if (_cfg.SiphonWalk)
            {
                // Everything except a sprint and a jump. Walking with a hose in the tank is
                // fine; sprinting with one means he has left it behind.
                Game.DisableControlThisFrame(Control.Sprint);
                Game.DisableControlThisFrame(Control.Jump);

                // Duck too, while the spout editor is on: it raises the spout, and left enabled
                // it would ALSO toggle his stance and fight the crouch clip. Disabling it does
                // not hide the press -- IsControlJustPressed reads a disabled control, which is
                // the whole reason the editor can borrow keys that already mean something.
                if (_cfg.SiphonSpoutEdit) Game.DisableControlThisFrame(Control.Duck);
            }
            else
            {
                HoldStill(me);
            }

            var swinging = Swinging();

            if (swinging)
            {
                Swing(me);
            }
            else
            {
                Swung(me);
                SiphonPose(me, filler);

                // Not turned to face anything while he is free to walk -- a forced heading
                // fights the direction he is actually going, and the argument is visible.
                if (!_cfg.SiphonWalk) FaceThe(me, filler);

                // The line runs from the FREE hand, raised at the filler, down to the spout of
                // the can in the other one. Same rope and the same dark paint as the pump hose,
                // because it is the same kind of object and two different-looking hoses is one
                // too many.
                //
                // Both ends move with him, and neither is a fixed offset: the hand end is a
                // bone, and the spout end is measured off the can he is actually holding.
                var spout = CanSpout(me);

                // THROUGH BOTH HANDS. One hand holds the end at the car and the other feeds it
                // down to the can, which is how a person actually holds a length of hose -- and
                // it gives the run a bend, so it stops reading as a straight line from an
                // armpit to the floor.
                if (_cfg.SiphonHoseBothHands)
                {
                    _siphonLine.Update(me.Bones[FreeHand].Position,
                                       me.Bones[OtherHand].Position, spout);
                }
                else
                {
                    _siphonLine.Update(me.Bones[FreeHand].Position, spout);
                }

                SpoutEditor(me, spout);
            }

            var room = _cfg.JerryCanLitres - _canLitres;
            var full = room <= 0.02f;

            // FULL IS NOT A REASON TO STOP any more -- the hose does not know the can is full.
            // It keeps coming, and what does not fit goes on the floor.
            if (full && !_cfg.SiphonOverflow) { StopSiphon(me, "the can is full"); return; }

            if (_targetTank.Litres <= 0.02f) { StopSiphon(me, "the tank is dry"); return; }

            // Nothing moves while he is swinging. He is not siphoning, he is fighting.
            var wanted = swinging ? 0f : _cfg.SiphonLitresPerSecond * dt;
            if (!full && wanted > room) wanted = room;
            if (wanted > _targetTank.Litres) wanted = _targetTank.Litres;

            if (wanted > 0f)
            {
                _targetTank.Burn(wanted);

                if (full)
                {
                    _spilled += wanted;
                    Spill(me, dt);
                }
                else
                {
                    _canLitres += wanted;
                    SetCanFuel(me, _canLitres);
                }

                _tanks.Touch(_target, _targetTank, false);
            }

            _gauge.Update(_target, _targetTank, true);

            Prompt(Control.Context, full
                ? "Stop   can FULL   " + _spilled.ToString("0.0", CultureInfo.InvariantCulture) +
                  " L on the ground"
                : "Stop   can " + _canLitres.ToString("0.0", CultureInfo.InvariantCulture) + " / " +
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
            PutCanDown(me, false);
        }

        /// <summary>
        /// Stands the can on the floor. force ignores SiphonCanInHand.
        ///
        /// FORCED FOR THE PUMP FILL, which is a different job from the siphon: you are setting
        /// a can down at a pump to fill it, and a can held in a fist while a hose runs into it
        /// is not what that looks like. The siphon keeps the setting because there the can in
        /// the hand is the whole trick -- see below.
        /// </summary>
        private void PutCanDown(Ped me, bool force)
        {
            // HE JUST KEEPS HOLDING IT. The can is a weapon, so leaving it selected gets the
            // game's own carrying animation for free -- the right hand is already solved, by
            // the people who made the model, and no prop, no bone offset and no rotation has to
            // be guessed at to put a can in a fist.
            if (_cfg.SiphonCanInHand && !force) return;

            // ONE CAN, HOWEVER MANY TIMES THIS IS ASKED. The siphon called it once, at the
            // start, so nothing here ever needed to check -- and the pump fill calls it every
            // frame, because the crouch and the pose have to be re-asserted every frame and
            // the can was put down alongside them.
            //
            // Without this that is a new can sixty times a second, each one orphaning the last:
            // _canOnGround only ever points at the newest, so picking it up collected one and
            // left every earlier one standing in the road.
            if (_canOnGround != null)
            {
                var there = false;
                try { there = _canOnGround.Exists(); }
                catch { there = false; }

                if (there) return;

                // Streamed out or deleted from under us. Forget it and stand a new one.
                _canOnGround = null;
            }

            try
            {
                // IN FRONT OF HIM, not out to the side. It was beside-and-slightly-behind
                // back when he stood square to the car and the can was just somewhere to put it
                // down. He turns 80 degrees now and works across his body, so in front is both
                // where the hose can reach and where he is looking.
                var at = me.Position
                         + me.ForwardVector * _cfg.SiphonCanForward
                         + me.RightVector * _cfg.SiphonCanSide;

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
                        if (Models.Box(_canOnGround.Model, out low, out high) && high.Z > 0.02f)
                        {
                            _canTop = high.Z;
                        }
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

            // WHETHER ONE IS ACTUALLY DOWN, rather than whether the setting says one should be.
            // The setting can be changed from the menu mid-siphon, and reading it here meant a
            // can put down under the old value was never collected -- a jerry can left standing
            // in the road forever. The prop's own existence cannot disagree with itself.
            if (_canOnGround == null) return;

            try
            {
                // Detached first, inside SafeDelete: deleting an attached entity works, but
                // leaving the detach to the delete is the kind of thing that is fine until
                // the delete is the call that fails.
                SafeDelete(_canOnGround, "siphon can");
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

                // The legs first, then the arm on top of them. Reversed, the full-body clip
                // lands on a ped that is already running an upper-body one and takes the arm
                // back with it.
                if (_cfg.SiphonCrouch) Crouch(me);

                // AN ANIMATION, NOT IK, and that is a correction rather than a preference.
                //
                // IK was the obvious way to put one arm somewhere: name the part, name the
                // point, done. It is also unverifiable from here -- the arm index is a bare
                // number the API does not name, and a carrying animation is entitled to
                // override arm IK anyway, so a call that does nothing looks exactly like a call
                // that worked.
                //
                // The pump pose has been doing this correctly all along: mp_common givetake1_a
                // frozen at 18 per cent is an arm held out at the filler. That dictionary has
                // four clips, and givetake1_a / givetake1_b are the two SIDES of an exchange --
                // one hands over, one receives -- so the pair is the same reach on either arm.
                // Taking the other one gets the left arm out with a clip that is known to work,
                // instead of a native that cannot be checked.
                Pose(me, _cfg.SiphonAnimDict, _cfg.SiphonAnimClip,
                     _cfg.SiphonAnimPhase, _cfg.SiphonAnimFlag);

                Burst(me);
            }
            catch (Exception ex)
            {
                Log.Once("siphon-pose", "Could not pose the siphon: " + ex.Message);
            }
        }

        private int _burstFrom;

        /// <summary>
        /// Runs the pose in bursts -- a few seconds of work, a few seconds of nothing.
        ///
        /// FROZEN RATHER THAN STOPPED, which is the whole difference between a pause and a
        /// restart. Stopping the task drops his arm to his side and starting it again lifts it
        /// back, so a burst would read as him giving up and having another go every few
        /// seconds. Setting the animation's SPEED to zero leaves him exactly where the motion
        /// had got to, and setting it back to one carries on from there.
        ///
        /// The mod already had this: it is the same freeze the pump pose uses to hold a
        /// handshake at 18 per cent, only switched on and off on a clock instead of held.
        ///
        /// Meaningless when the pose is a still to begin with, so a configured phase wins.
        /// </summary>
        private void Burst(Ped me)
        {
            if (!_cfg.SiphonBurst) return;
            if (_cfg.SiphonAnimPhase >= 0f) return;

            var on = _cfg.SiphonBurstOn;
            var off = _cfg.SiphonBurstOff;

            if (on <= 0.01f || off <= 0.01f) return;

            try
            {
                var cycle = on + off;
                var t = ((Game.GameTime - _burstFrom) / 1000f) % cycle;

                Function.Call(Hash.SET_ENTITY_ANIM_SPEED, me.Handle,
                              _cfg.SiphonAnimDict, _cfg.SiphonAnimClip,
                              t < on ? 1f : 0f);
            }
            catch (Exception ex)
            {
                Log.Once("burst", "Could not pace the siphon animation: " + ex.Message);
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
                        ? _canOnGround.GetOffsetPosition(new Vector3(_cfg.SiphonSpoutSide,
                                                                     _cfg.SiphonSpoutForward,
                                                                     _canTop + _cfg.SiphonSpoutUp))
                        : me.Position;
                }

                var held = me.Weapons.CurrentWeaponObject;

                if (held != null && held.Exists())
                {
                    Vector3 low, high;
                    Models.Box(held.Model, out low, out high);

                    var top = high.Z > 0.02f ? high.Z : _canTop;

                    return held.GetOffsetPosition(new Vector3(_cfg.SiphonSpoutSide,
                                                              _cfg.SiphonSpoutForward,
                                                              top + _cfg.SiphonSpoutUp));
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

        // ==================================================================
        // Moving the spout while you look at it
        // ==================================================================

        private bool _spoutSaved;

        /// <summary>What the offsets were when this siphon began, to tell a nudge from nothing.</summary>
        private float _spoutWasForward, _spoutWasSide, _spoutWasUp;

        /// <summary>
        /// Nudges where the hose meets the can, live, with the arrow keys.
        ///
        /// SIX ROUNDS OF "A BIT FURTHER FORWARD" IS WHY THIS EXISTS. The offset is three numbers
        /// against a model whose origin nobody can see, and every attempt to reason it out of a
        /// bounding box has landed somewhere slightly wrong -- because a bounding box knows
        /// where the can ENDS and not where its neck is. Moving it and looking settles in
        /// seconds what arithmetic has not settled in six goes.
        ///
        /// Written back on Enter rather than continuously: the ini is a file, and rewriting it
        /// on every arrow press is a lot of disk for a number that is still being chosen.
        /// </summary>
        private void SpoutEditor(Ped me, Vector3 at)
        {
            if (!_cfg.SiphonSpoutEdit) return;

            try
            {
                var step = _cfg.SiphonSpoutStep;

                // Disabled controls read anyway -- the same trick the swing uses -- so the arrows
                // move the spout without also driving whatever they normally drive.
                if (Game.IsControlJustPressed(Control.PhoneUp)) _cfg.SiphonSpoutForward += step;
                if (Game.IsControlJustPressed(Control.PhoneDown)) _cfg.SiphonSpoutForward -= step;
                if (Game.IsControlJustPressed(Control.PhoneLeft)) _cfg.SiphonSpoutSide -= step;
                if (Game.IsControlJustPressed(Control.PhoneRight)) _cfg.SiphonSpoutSide += step;
                if (Game.IsControlJustPressed(Control.Jump)) _cfg.SiphonSpoutUp += step;
                if (Game.IsControlJustPressed(Control.Duck)) _cfg.SiphonSpoutUp -= step;

                // A marker on the point itself, so you are aiming at something rather than
                // guessing from where the hose ends.
                World.DrawMarker(MarkerType.DebugSphere, at, Vector3.Zero, Vector3.Zero,
                                 new Vector3(0.03f, 0.03f, 0.03f),
                                 Color.FromArgb(220, 255, 190, 60));

                var line = "SPOUT   fwd " + _cfg.SiphonSpoutForward.ToString("0.000", CultureInfo.InvariantCulture) +
                           "   side " + _cfg.SiphonSpoutSide.ToString("0.000", CultureInfo.InvariantCulture) +
                           "   up " + _cfg.SiphonSpoutUp.ToString("0.000", CultureInfo.InvariantCulture);

                Draw.Rect(0.5f, 0.115f, 0.34f, 0.055f, Color.FromArgb(190, 8, 8, 10));

                Draw.Text(line, 0.5f, 0.098f, 0.36f,
                          Color.FromArgb(240, 250, 200, 110), 4, true);

                Draw.Text(_spoutSaved
                              ? "saved and locked"
                              : "arrows move it, space/ctrl raise it, ENTER saves and locks",
                          0.5f, 0.128f, 0.26f,
                          Color.FromArgb(200, 200, 200, 205), 4, true);

                // SAVED AND LOCKED, in one press. "Put it there and leave it" is one
                // intention, so it is one button -- and an editor that stays on afterwards is
                // a readout and a marker on screen at every siphon.
                if (Game.IsControlJustPressed(Control.FrontendAccept)) SaveSpout(true);
            }
            catch (Exception ex)
            {
                Log.Once("spout-edit", "The spout editor fell over: " + ex.Message);
            }
        }

        /// <summary>
        /// Writes the three offsets to the ini, and optionally turns the editor off.
        ///
        /// CALLED WHEN THE SIPHON ENDS AS WELL AS ON ENTER, which is the point. Every reload
        /// re-reads the ini, so a nudge that lived only in memory was destroyed by the exact
        /// key you press to go and look at it -- adjust, reload to see it, adjust again, and
        /// nothing ever accumulates. Twice that happened before it was noticed.
        ///
        /// So stopping the siphon keeps whatever you moved, and ENTER additionally locks it.
        /// The write only happens if something actually changed: a file rewritten every time
        /// anyone siphons, to store the numbers it already had, is wear for nothing.
        /// </summary>
        private void SaveSpout(bool andLock)
        {
            if (!_cfg.SiphonSpoutEdit) return;

            var moved = Math.Abs(_cfg.SiphonSpoutForward - _spoutWasForward) > 0.0005f
                        || Math.Abs(_cfg.SiphonSpoutSide - _spoutWasSide) > 0.0005f
                        || Math.Abs(_cfg.SiphonSpoutUp - _spoutWasUp) > 0.0005f;

            if (!moved && !andLock) return;

            try
            {
                var ok = IniFile.SetValue(Paths.Ini, "Station", "SiphonSpoutForward",
                                          _cfg.SiphonSpoutForward.ToString("0.000", CultureInfo.InvariantCulture))
                         && IniFile.SetValue(Paths.Ini, "Station", "SiphonSpoutSide",
                                          _cfg.SiphonSpoutSide.ToString("0.000", CultureInfo.InvariantCulture))
                         && IniFile.SetValue(Paths.Ini, "Station", "SiphonSpoutUp",
                                          _cfg.SiphonSpoutUp.ToString("0.000", CultureInfo.InvariantCulture));

                if (ok && andLock)
                {
                    IniFile.SetValue(Paths.Ini, "Station", "SiphonSpoutEdit", "false");
                    _cfg.SiphonSpoutEdit = false;
                }

                _spoutSaved = ok;

                _spoutWasForward = _cfg.SiphonSpoutForward;
                _spoutWasSide = _cfg.SiphonSpoutSide;
                _spoutWasUp = _cfg.SiphonSpoutUp;

                Log.Info(ok
                    ? "Spout saved" + (andLock ? " and locked" : "") + ": forward " +
                      _cfg.SiphonSpoutForward.ToString("0.000", CultureInfo.InvariantCulture) +
                      ", side " + _cfg.SiphonSpoutSide.ToString("0.000", CultureInfo.InvariantCulture) +
                      ", up " + _cfg.SiphonSpoutUp.ToString("0.000", CultureInfo.InvariantCulture) + "."
                    : "Could not write the spout offsets to Fumes.ini.");
            }
            catch (Exception ex)
            {
                Log.Once("spout-save", "Could not save the spout offsets: " + ex.Message);
            }
        }

        /// <summary>
        /// Starts the fill proper: squares him up and says so in the log.
        ///
        /// Pulled out of Carrying because there are TWO ways in now -- straight through when
        /// there is no grade to choose, and out the far side of the card when there is. Left
        /// inline it would have had to be copied, and a copy is a thing that gets fixed once.
        /// </summary>
        private void Begin(Ped me)
        {
            _stage = Stage.Filling;

            // TURNED ON THE SPOT, once, as filling begins.
            //
            // FaceThe has been called every frame of the fill all along and he stayed put,
            // because SET_PED_DESIRED_HEADING is a nudge for the LOCOMOTION system to resolve
            // -- and he is standing still with the movement controls disabled and an animation
            // on him, so there is no locomotion left to resolve it. A desired heading with
            // nothing to walk it round is just a number nobody reads.
            //
            // Setting the heading itself does not go through any of that. It is abrupt by
            // nature, which is the trade: he is already roughly facing the car by the time he
            // can reach the filler, so the correction is small, and a small snap on a button
            // press reads as him squaring up to the job.
            try
            {
                bool exact;
                Turn(me, Filler.On(_target, out exact));
            }
            catch
            {
                // He fills from wherever he is standing.
            }

            Log.Info("Filling " + _target.LocalizedName + " (" +
                     _targetTank.Litres.ToString("0.0", CultureInfo.InvariantCulture) + "/" +
                     _targetTank.Capacity.ToString("0.0", CultureInfo.InvariantCulture) + " L) with " +
                     Fumes.Fuel.Diesel.Name(_grade) + " at $" +
                     _price.ToString("0.00", CultureInfo.InvariantCulture) + "/L.");
        }

        // ==================================================================
        // Choosing a grade
        // ==================================================================

        private readonly Grades _grades;

        /// <summary>
        /// Holds the nozzle still while the card is up, and acts on what it returns.
        ///
        /// The hose is still drawn and the nozzle still held, so this reads as a pause in the
        /// middle of refuelling rather than a menu that happens to be about fuel. Backing out
        /// returns to Carrying with the nozzle in hand, not to standing about empty-handed --
        /// cancelling a grade is not cancelling the whole errand.
        /// </summary>
        private void Choosing(Ped me)
        {
            if (_target == null || !_target.Exists() || _targetTank == null)
            {
                _grades.Close();
                _stage = Stage.Carrying;
                return;
            }

            LockHands();
            HoldStill(me);

            var anchor = _pump != null && _pump.Exists()
                ? _pump.GetOffsetPosition(_anchorLocal)
                : me.Position;

            _hose.Update(anchor, _nozzle.HoseEnd());

            switch (_grades.Update())
            {
                case Grades.Result.Chosen:
                    _grade = _grades.Picked;
                    _price = _basePrice * _cfg.PriceFor(_grade);

                    // REMEMBERED, so the next fill starts on it. A grade you pick at every
                    // pump is a habit, and a habit the mod forgets is a chore.
                    _cfg.Grade = _grade;

                    Begin(me);
                    break;

                case Grades.Result.Dropped:
                    _stage = Stage.Carrying;
                    break;
            }
        }

        // ==================================================================
        // What does not fit in the can
        // ==================================================================

        private float _spilled;
        private float _poolWidth;
        private int _poolDecals;
        private int _poolNextAt;
        private Vector3 _poolAt;

        /// <summary>
        /// Grows a pool of petrol under the can once it stops being able to hold any more.
        ///
        /// A DECAL CANNOT BE RESIZED once it is down, so a pool that grows is successive decals
        /// at the same spot, each a little wider than the last. That is also why this runs on a
        /// timer rather than every frame: sixty decals a second is the whole decal budget spent
        /// inside a second, and that budget is shared with every scuff, skid mark and bullet
        /// hole already on the street.
        ///
        /// If he walks away from the puddle he starts another one, which is not a special case
        /// -- it is what a can pouring onto the ground does while somebody carries it.
        ///
        /// These are the game's OWN petrol decals, the same ones a jerry can leaves. They
        /// ignite. That is less a feature this adds than one it declines to take away.
        /// </summary>
        private void Spill(Ped me, float dt)
        {
            if (!_cfg.SiphonPool) return;
            if (_poolDecals >= _cfg.SiphonPoolMaxDecals) return;
            if (Game.GameTime < _poolNextAt) return;

            _poolNextAt = Game.GameTime + (int)(_cfg.SiphonPoolEverySeconds * 1000f);

            try
            {
                // Under the CAN rather than under him: it is the can that is overflowing, and
                // the difference is an arm's length.
                var over = CanSpout(me);
                var at = new Vector3(over.X, over.Y, me.Position.Z - 0.9f);

                // WIDTH COMES FROM HOW MUCH HAS GONE ON THE FLOOR, not from a counter that adds
                // a fixed step each time it fires. The step version reached its ceiling in about
                // four seconds and then did nothing for the rest of the spill -- so nine litres
                // looked exactly like two, which is the opposite of the point. Tied to litres it
                // keeps spreading for as long as fuel keeps coming.
                var want = _cfg.SiphonPoolWidth + _spilled * _cfg.SiphonPoolPerLitre;
                if (want > _cfg.SiphonPoolMaxWidth) want = _cfg.SiphonPoolMaxWidth;

                // Far enough from the last one to be a new puddle rather than the same one.
                var moved = _poolAt == Vector3.Zero || at.DistanceTo(_poolAt) > _cfg.SiphonPoolStep;

                if (moved)
                {
                    _poolAt = at;
                }
                else if (want - _poolWidth < _cfg.SiphonPoolGrowth)
                {
                    // Not visibly bigger than the one already down. A decal that lands inside
                    // its predecessor costs budget and changes nothing anybody can see, and at
                    // the cap that is every single one of them from then on.
                    return;
                }

                _poolWidth = want;

                Function.Call(Hash.ADD_PETROL_DECAL, _poolAt.X, _poolAt.Y, _poolAt.Z,
                              0.1f, _poolWidth, 1f);

                _poolDecals++;
            }
            catch (Exception ex)
            {
                Log.Once("spill", "Could not put petrol on the ground: " + ex.Message);
            }
        }

        /// <summary>
        /// Takes each pose off him the moment its own stage stops running.
        ///
        /// A SWEEP RATHER THAN A STOP AT EVERY EXIT. Three clips, each owned by a different set
        /// of stages: the held pose belongs to Filling, FillingCan and Siphoning, the pour to
        /// Pouring, the crouch to Siphoning alone. Asking every exit path to know which of the
        /// three it owes a Stop to is how one gets missed, and a missed one is not subtle --
        /// he keeps the animation until something else happens to clear his tasks.
        ///
        /// Cheap to call every frame: each Stop is guarded by its own "am I actually on" flag,
        /// so the common case is three boolean reads.
        /// </summary>
        private void Poses(Ped me)
        {
            var siphoning = _stage == Stage.Siphoning;

            // The pump fill crouches too, so it counts as a stage that is allowed to be bent
            // over. Without this the sweep stood him up every frame while he was filling.
            var crouching = siphoning || (_stage == Stage.FillingCan && _canAtPump);

            if (!siphoning && _stage != Stage.Filling && _stage != Stage.FillingCan) StopFillPose();
            if (_stage != Stage.Pouring) StopPourPose();
            if (!crouching) StandUp(me);

            // And the can on the floor belongs to whichever of the two put it there.
            if (!crouching && _canOnGround != null) PickCanUp(me);

            // THE CARRYING POSE, KEPT UP FOR AS LONG AS THE NOZZLE IS OUT.
            //
            // Take() re-hides the pose weapon every time it is called, and it was only being
            // called from Carrying -- so the grade card and the fill itself, both of which have
            // the nozzle in his hand, ran without it. Anything that reselects a weapon in that
            // window leaves the pose weapon SHOWING, and the pose weapon is a jerry can or a
            // fire extinguisher: exactly the two things people reported appearing in the hand
            // already holding the nozzle, and gone again the moment it was hung up.
            //
            // It is idempotent -- Out means pose and return -- so asking every frame costs a
            // weapon comparison, and here rather than in three separate stages because this is
            // where the file already keeps poses honest by stage rather than by memory.
            if (_stage == Stage.Carrying || _stage == Stage.Choosing ||
                _stage == Stage.Filling || _stage == Stage.FillingCan)
            {
                _nozzle.Take();
            }
        }

        /// <summary>
        /// Crouches him with an ANIMATION, because the stance system will not.
        ///
        /// THAT IS SETTLED RATHER THAN ASSUMED, which is what the last attempt was for. It
        /// pressed the duck control six times, checked IsInStealthMode after each, and wrote
        /// the result down: he stayed upright every time. A man holding a jerry can is not
        /// allowed the stealth stance, and no amount of asking changes that.
        ///
        /// So the crouch is a full-body clip played underneath, and the arm clip is upper-body
        /// on top of it. That is what the upper-body flag is FOR -- it is how the game lays a
        /// gesture over an idle -- and it is why the two do not fight: one owns the legs, the
        /// other owns the arms.
        ///
        /// The cost is walking. A full-body clip owns the legs, so he cannot crouch and walk at
        /// once; SiphonWalk still governs the leash, but with a crouch on he will stay put.
        /// </summary>
        private void Crouch(Ped me)
        {
            if (string.IsNullOrEmpty(_cfg.SiphonCrouchDict) ||
                string.IsNullOrEmpty(_cfg.SiphonCrouchClip)) return;

            if (_crouchImpossible) return;

            try
            {
                if (!Function.Call<bool>(Hash.DOES_ANIM_DICT_EXIST, _cfg.SiphonCrouchDict))
                {
                    _crouchImpossible = true;
                    Log.Warn("SiphonCrouchDict '" + _cfg.SiphonCrouchDict + "' is not an animation " +
                             "dictionary this game has. He will siphon standing up.");
                    return;
                }

                if (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, _cfg.SiphonCrouchDict))
                {
                    Function.Call(Hash.REQUEST_ANIM_DICT, _cfg.SiphonCrouchDict);
                    return;
                }

                if (!Function.Call<bool>(Hash.IS_ENTITY_PLAYING_ANIM, me.Handle,
                                         _cfg.SiphonCrouchDict, _cfg.SiphonCrouchClip, 3))
                {
                    Function.Call(Hash.TASK_PLAY_ANIM, me.Handle,
                                  _cfg.SiphonCrouchDict, _cfg.SiphonCrouchClip,
                                  4f, -4f, -1, _cfg.SiphonCrouchFlag, 0f, false, false, false);
                }

                _crouched = true;
            }
            catch (Exception ex)
            {
                Log.Once("crouch", "Could not crouch him: " + ex.Message);
                _crouchImpossible = true;
            }
        }

        /// <summary>Puts his stance back to whatever it was before the siphon.</summary>
        private void StandUp(Ped me)
        {
            if (!_crouched) return;
            _crouched = false;

            try
            {
                if (me != null && me.Exists())
                {
                    Function.Call(Hash.STOP_ANIM_TASK, me.Handle,
                                  _cfg.SiphonCrouchDict, _cfg.SiphonCrouchClip, -4f);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not stand him back up: " + ex.Message);
            }
        }

        private void StopSiphon(Ped me, string why)
        {
            // Keeps a nudge that has not been locked yet, so walking away does not lose it.
            SaveSpout(false);

            if (_spilled > 0.05f)
            {
                Log.Info("Spilled " + _spilled.ToString("0.0", CultureInfo.InvariantCulture) +
                         " L on the ground over " + _poolDecals + " decals.");
            }

            StandUp(me);
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

                return LitresFrom(weapon.Ammo, weapon.MaxAmmo);
            }
            catch
            {
                return 0f;
            }
        }

        /// <summary>
        /// What we last knew to be in the can, in litres. Negative until one has been seen.
        ///
        /// THE MOD'S NUMBER, NOT THE GAME'S. The can's contents live in the weapon's ammo,
        /// which belongs to the game -- and the game puts it back to full on its own, after a
        /// respawn, an arrest, a mission, a reload. So an emptied can came back brimmed, which
        /// is free fuel, which removes the reason to visit a station at all.
        ///
        /// Only ever raised by something the mod did: a station fill or a siphon. Anything
        /// else that raises it is the game handing out petrol, and is put back.
        /// </summary>
        private float _canOwn = -1f;

        private bool _canDirty;
        private float _sinceCanSave;

        /// <summary>
        /// Puts back what the game topped up.
        ///
        /// Runs every tick and does nothing at all in the stages that are allowed to change the
        /// level, because those go through SetCanLitres and tell this what they did. A rise
        /// nobody claimed is the game's doing.
        ///
        /// A DROP IS ALWAYS ACCEPTED. Petrol poured on the floor as a weapon is the player
        /// spending it, and second-guessing that would fight the one bit of vanilla behaviour
        /// worth keeping.
        /// </summary>
        private void GuardCan(Ped me)
        {
            if (!_cfg.JerryCan || !_cfg.RememberCan) return;
            if (_stage == Stage.FillingCan || _stage == Stage.Siphoning || _stage == Stage.Pouring) return;

            try
            {
                if (Can(me) == null) return;

                // THE CAN HE OWNS, NOT THE ONE IN HIS HANDS, and reading the wrong one is what
                // emptied everybody's can in 0.1.5. CanLitres asks what he is HOLDING, which is
                // zero whenever the can is on his back -- so the moment you holstered a full
                // can this read 0, took it for you having spent it, and remembered nothing in
                // it. Re-equipping then showed twenty litres of ammo against a remembered
                // nought, which looks exactly like the game refilling it, so the guard
                // "put it back" to empty and saved that. Stuck at zero, across restarts.
                var now = CanFuel(me);

                // First sight this session with nothing remembered: whatever it holds is the truth.
                if (_canOwn < 0f) { _canOwn = now; return; }

                if (now > _canOwn + 0.25f)
                {
                    SetCanFuel(me, _canOwn);

                    Log.Once("can-topup", "The game refilled the petrol can to " +
                                          now.ToString("0.0", CultureInfo.InvariantCulture) +
                                          " L; put back to the " +
                                          _canOwn.ToString("0.0", CultureInfo.InvariantCulture) +
                                          " L it had. Set RememberCan = false to allow it.");
                    return;
                }

                if (now < _canOwn - 0.01f) Remember(now);
            }
            catch (Exception ex)
            {
                Log.Once("can-guard", "Could not check the can: " + ex.Message);
            }
        }

        /// <summary>Records a level and marks it for saving.</summary>
        private void Remember(float litres)
        {
            if (litres < 0f) litres = 0f;

            _canOwn = litres;
            _canDirty = true;
        }

        /// <summary>Reads the remembered level back. A missing file just means no can yet.</summary>
        private void LoadCan()
        {
            try
            {
                var doc = JsonFile.Read(Paths.CanFile);
                if (doc == null) return;

                // ANYTHING OLDER THAN 2 IS THROWN AWAY, because 0.1.5 wrote zeroes into this
                // file that were never true -- it read the can he was holding rather than the
                // can he owned, so holstering a full can recorded it as empty. Trusting those
                // files would carry the bug through the fix: the guard would see real fuel
                // against a remembered nought and empty the can again on sight.
                //
                // Discarded rather than migrated, since there is nothing in a wrong number to
                // migrate. No file means no memory, and no memory means the first can he picks
                // up is taken at face value, which is the right way to start again.
                if (doc["v"].AsInt(1) < 2)
                {
                    Log.Info("Ignoring a can.json from before 0.1.6; it may hold a level that " +
                             "version recorded wrongly. The can starts from whatever is in it.");
                    return;
                }

                var litres = doc["litres"].AsFloat(-1f);
                if (litres >= 0f) _canOwn = litres;
            }
            catch (Exception ex)
            {
                Log.Once("can-load", "Could not read the can level: " + ex.Message);
            }
        }

        /// <summary>Writes it out, if it moved.</summary>
        private void SaveCan()
        {
            if (!_canDirty || _canOwn < 0f) return;

            try
            {
                if (JsonFile.Write(Paths.CanFile,
                                   Json.Object()
                                       .Set("v", 2)
                                       .Set("litres", Math.Round(_canOwn, 2))))
                {
                    _canDirty = false;
                }
            }
            catch (Exception ex)
            {
                Log.Once("can-save", "Could not save the can level: " + ex.Message);
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

            // THE ONE STAGE THAT DID NOT. Every other stage with a can or a nozzle in his hand
            // locks the attack control, and this one is the stage where the thing in his hand
            // is a jerry can with its own idea of what the fire button does -- so holding it
            // poured a second stream onto the road, from the same can, on the game's own
            // accounting rather than ours.
            LockHands();

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

                SetCanFuel(me, _canLitres);
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
        /// <summary>
        /// Jerry can props for the version that stands one on the floor, best first.
        ///
        /// w_am_jerrycan IS THE CAN HE CARRIES. It is the weapon model, so standing that one on
        /// the ground matches what is in his hands by construction rather than by two models
        /// happening to agree -- and they did not agree: prop_jerrycan_01a, which used to lead
        /// this list, is the scuffed old scenery can, a different red and visibly beaten up next
        /// to the clean one on his back.
        ///
        /// It was leading because it is the one BUILT to stand on a floor, which is a real
        /// point and the smaller one. PLACE_OBJECT_ON_GROUND_PROPERLY stands anything up; only
        /// one of these is the right can.
        /// </summary>
        private static readonly string[] CanProps =
        {
            "w_am_jerrycan",
            "prop_jerrycan_01a",
            "prop_ld_jerrycan_01"
        };

        /// <summary>
        /// The hand that is NOT holding the can. See Nozzle, which owns the other one -- these
        /// are the PH_ prop-holding bones, not the skeleton hands, so the hose ends where a
        /// held object would sit rather than at the wrist.
        /// </summary>
        private const int PhRightHand = 28422;
        private const int PhLeftHand = 60309;

        /// <summary>
        /// The hand the hose ends in.
        ///
        /// With the can on the floor both hands are free, so this is the setting rather than
        /// "whichever one is not carrying anything". With the can held, the carrying hand is
        /// still ruled out -- a hose and a jerry can in one fist is one thing too many.
        /// </summary>
        private Bone FreeHand
        {
            get
            {
                if (_cfg.SiphonCanInHand) return _cfg.LeftHand ? Bone.PHRightHand : Bone.PHLeftHand;
                return _cfg.SiphonHoseRightHand ? Bone.PHRightHand : Bone.PHLeftHand;
            }
        }

        /// <summary>The hand the hose does NOT start in -- it passes through this one.</summary>
        private Bone OtherHand => FreeHand == Bone.PHRightHand ? Bone.PHLeftHand : Bone.PHRightHand;

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
        /// <summary>
        /// Filling the can by walking up to a pump holding it.
        ///
        /// THE OBVIOUS WAY ROUND, and it was not the way it worked. Filling a can needed the
        /// NOZZLE out first -- the prompt lived next to "Hang the nozzle up" -- so the one
        /// errand where you are plainly carrying the can was the one that made you pick up
        /// something else before it would talk to you.
        ///
        /// On the secondary button because the primary is already taking the nozzle, which is
        /// still the thing most people at a pump want. Two intentions, two buttons, same as
        /// everywhere else in here.
        /// </summary>
        private void OfferCanAtPump(Ped me, Prop pump)
        {
            if (!_cfg.JerryCan) return;
            if (!HoldingCan(me)) return;

            var litres = CanFuel(me);
            if (litres >= _cfg.JerryCanLitres - 0.05f) return;

            Prompt(Control.ContextSecondary, "Fill the can   " +
                                             litres.ToString("0.0", CultureInfo.InvariantCulture) + " / " +
                                             _cfg.JerryCanLitres.ToString("0.#", CultureInfo.InvariantCulture) + " L");

            if (!SecondaryPressed()) return;

            // THROUGH Take, WHICH IS THE WHOLE OF STARTING AT A PUMP: the anchor the hose hangs
            // from, the forecourt's price, its name for the meter, the counters reset, and the
            // nozzle into his hand. Reproducing that list here is how the two drift apart, and
            // the copy that lived here had already missed the anchor -- so the hose had nothing
            // to hang from.
            //
            // It leaves the stage at Carrying, which is exactly where StopCan puts him back to
            // afterwards: he finishes with the nozzle in his hand and hangs it up like any
            // other fill.
            Take(me, pump);

            _canLitres = litres;
            _canAtPump = true;
            _stage = Stage.FillingCan;

            Log.Info("Filling the can at the pump, " +
                     litres.ToString("0.0", CultureInfo.InvariantCulture) + " L in it.");
        }

        /// <summary>True while the can is being filled at a pump rather than off the nozzle.</summary>
        private bool _canAtPump;

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

            // SAID EXPLICITLY, because the other way into FillingCan sets it and an abandoned
            // pump fill -- a death, a car -- left it set. This path would then have stood the
            // can on the floor and crouched him for a fill that has the nozzle in his hand.
            _canAtPump = false;
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
                Vector3 min, max;
                if (!Models.Box(pump.Model, out min, out max)) return _cfg.HoseAnchorZ;

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

        private void Carrying(Ped me, float dt)
        {
            if (_pump == null || !_pump.Exists()) { Abandon("the pump went away"); return; }

            _nozzle.Take();     // keeps asking until the model streams; no-op once it is out
            RopePicker();
            LockHands();

            // The trigger, before anything that might return: hold it and fuel comes out of
            // the nozzle onto the ground. See Spray.
            var spraying = Spray(me, dt);

            var anchor = Anchor();
            _hose.Update(anchor, _nozzle.HoseEnd());

            if (_hazard.Update(_pump.Position, spraying)) { Abandon("the pump went up"); return; }

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
                // VerticalCylinder is the 3.6.0 name for marker 1; newer builds carry both
                // names for the same value, so this is the one that exists everywhere.
                World.DrawMarker(MarkerType.VerticalCylinder,
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

            // THE PUMP WINS AS SOON AS IT IS THE NEARER THING, with nothing to beat.
            //
            // The margin cannot defend this direction, and this is the bug people could not
            // hang the nozzle up through. The branch above forces "fill" for the entire walk
            // back from the car, so you arrive at the pump already latched to 1 -- and from 1
            // the old rule wanted the filler to be further than the pump BY THE WHOLE MARGIN
            // before it would offer hanging up. At a pump the two distances sit twenty to
            // thirty centimetres apart. Half a metre is wider than the whole spread it was
            // arbitrating, so on a snug park hang-up was unreachable at any stance while the
            // tank still had room. The margin had been sized off the gap between the two
            // distances, mistaken for jitter; the drift of a standing ped is centimetres.
            //
            // Hanging up is the end of the errand. It does not have to out-argue a prompt you
            // have already walked away from.
            else if (toPump < toFiller) choice = 2;

            // COMING BACK THE OTHER WAY STILL HAS TO BE EARNED, and that is what kills the
            // strobe the margin was put in for: once hang-up is showing, the filler has to be
            // clearly nearer to take the button back, so a step or a sway cannot flip it. From
            // 1 this flips to 2 once and then wants a real half-metre to flip back -- a
            // per-frame coin toss never produces that.
            else if (_prompt == 2) choice = toFiller < toPump - margin ? 1 : 2;
            else choice = 1;

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

                // THE CARD, unless there is nothing to decide. A diesel vehicle takes diesel;
                // asking which of three petrols it wants is a button press to be told no.
                if (_cfg.GradeMenu && _grade != FuelGrade.Diesel)
                {
                    _grades.Show(_basePrice, _stationBrand);
                    _stage = Stage.Choosing;
                    return;
                }

                Begin(me);
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
            _canAtPump = false;
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

            SafeDelete(_dropped, "dropped nozzle");
            _dropped = null;
        }

        // ==================================================================
        // Fuel out of the nozzle
        // ==================================================================

        /// <summary>How much has gone on the tarmac this time out, and the pool it has made.</summary>
        private float _sprayed;
        private float _sprayWidth;
        private int _sprayDecals;
        private int _sprayNextAt;
        private Vector3 _sprayAt;

        /// <summary>The looped stream at the spout, and the rung of SprayLadder it came from.</summary>
        private int _stream;
        private static int _streamRung = -1;

        private const string PtfxAsset = "core";

        /// <summary>
        /// Names tried in order for the stream out of the spout, the first the game accepts
        /// kept. There is no list of particle names on disk to check against and the game
        /// refuses a wrong one in silence, so the log says which rung it took.
        /// </summary>
        private static readonly string[] SprayLadder =
        {
            "ent_ray_meth_leaky_pipe", "ent_sht_petrol", "ent_sht_water", "weap_extinguisher"
        };

        /// <summary>
        /// Hold the fire button with the nozzle in hand and fuel comes out of it.
        ///
        /// THE SEAM THE POSE WAS CHOSEN FOR. The carrying pose is an invisible fire
        /// extinguisher rather than a jerry can precisely because an extinguisher is a weapon
        /// that SPRAYS -- it carries a trigger -- and that was always the route to fuel
        /// leaving the nozzle under the player's control. Everything up to here has disabled
        /// the trigger and nothing has read it.
        ///
        /// READ THROUGH THE DISABLED CONTROL. LockHands turns Attack off every frame, which is
        /// what stops the extinguisher itself spraying white foam; IS_DISABLED_CONTROL_PRESSED
        /// is how a control that has been turned off can still be asked whether it is held.
        ///
        /// It is the station's fuel, so it is charged for at the pump's price like any other
        /// litre, it feeds the forecourt hazard the same way a fill does, and it leaves the
        /// game's own petrol decals -- the same ones a jerry can leaves. Returns true while
        /// fuel is actually coming out.
        /// </summary>
        private bool Spray(Ped me, float dt)
        {
            var held = false;

            if (_cfg.NozzleSpray && dt > 0f)
            {
                try
                {
                    held = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)Control.Attack)
                           || Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)Control.Attack2);
                }
                catch
                {
                    held = false;
                }
            }

            if (!held)
            {
                if (_sprayed > 0.05f)
                {
                    Log.Info("Sprayed " + _sprayed.ToString("0.0", CultureInfo.InvariantCulture) +
                             " L out of the nozzle over " + _sprayDecals + " decal(s).");
                }

                StopStream();
                _sprayed = 0f;
                _sprayWidth = 0f;
                _sprayDecals = 0;
                _sprayAt = Vector3.Zero;
                return false;
            }

            var wanted = _cfg.NozzleSprayLitresPerSecond * dt;

            _sprayed += wanted;
            _dispensed += wanted;

            if (_cfg.ChargeMoney && _price > 0f)
            {
                _owed += wanted * _price;
                Settle();
            }

            Stream(me);
            Puddle(me, _nozzle.Spout());

            Prompt(Control.Context, "Fuel on the ground   " +
                                    _sprayed.ToString("0.0", CultureInfo.InvariantCulture) + " L");

            return true;
        }

        /// <summary>The stream itself, looped at the spout and moved with it every frame.</summary>
        private void Stream(Ped me)
        {
            try
            {
                if (_stream != 0 && Function.Call<bool>(Hash.DOES_PARTICLE_FX_LOOPED_EXIST, _stream)) return;

                _stream = 0;

                if (!Function.Call<bool>(Hash.HAS_NAMED_PTFX_ASSET_LOADED, PtfxAsset))
                {
                    Function.Call(Hash.REQUEST_NAMED_PTFX_ASSET, PtfxAsset);
                    return;
                }

                var prop = _nozzle.Prop;
                if (prop == null || !prop.Exists()) return;

                var first = _streamRung >= 0 ? _streamRung : 0;
                var last = _streamRung >= 0 ? _streamRung : SprayLadder.Length - 1;

                for (var i = first; i <= last; i++)
                {
                    Function.Call(Hash.USE_PARTICLE_FX_ASSET, PtfxAsset);

                    // ON THE NOZZLE, in the nozzle's own space, so it follows the thing in his
                    // hand rather than being restarted at a world position every frame.
                    var handle = Function.Call<int>(Hash.START_PARTICLE_FX_LOOPED_ON_ENTITY,
                                                    SprayLadder[i], prop.Handle,
                                                    0f, 0f, 0f, 0f, 0f, 0f,
                                                    _cfg.NozzleSprayScale, false, false, false);

                    if (handle == 0)
                    {
                        if (_streamRung < 0)
                        {
                            Log.Info("Nozzle spray: " + PtfxAsset + "/" + SprayLadder[i] +
                                     " refused; trying the next.");
                        }
                        continue;
                    }

                    _stream = handle;

                    if (_streamRung < 0)
                    {
                        _streamRung = i;
                        Log.Info("Nozzle spray: " + PtfxAsset + "/" + SprayLadder[i] + " out of the spout.");
                    }

                    return;
                }

                Log.Once("spray-none", "Nozzle spray: none of " + SprayLadder.Length +
                                       " effects was accepted; the petrol on the ground is the whole of it.");
            }
            catch (Exception ex)
            {
                Log.Once("spray-fail", "Could not start the nozzle spray: " + ex.Message);
            }
        }

        private void StopStream()
        {
            if (_stream == 0) return;

            try
            {
                if (Function.Call<bool>(Hash.DOES_PARTICLE_FX_LOOPED_EXIST, _stream))
                {
                    Function.Call(Hash.STOP_PARTICLE_FX_LOOPED, _stream, false);
                }
            }
            catch
            {
                // It is gone either way.
            }

            _stream = 0;
        }

        /// <summary>
        /// Petrol on the tarmac under a point, spreading as more of it goes down.
        ///
        /// The same rules as the siphon's overflow pool and for the same reasons -- a decal
        /// cannot be resized once it is down, so a pool that grows is successive decals, and
        /// one that lands inside its predecessor costs budget and changes nothing.
        /// </summary>
        private void Puddle(Ped me, Vector3 over)
        {
            if (!_cfg.SiphonPool) return;
            if (_sprayDecals >= _cfg.SiphonPoolMaxDecals) return;
            if (Game.GameTime < _sprayNextAt) return;

            _sprayNextAt = Game.GameTime + (int)(_cfg.SiphonPoolEverySeconds * 1000f);

            try
            {
                var at = new Vector3(over.X, over.Y, me.Position.Z - 0.9f);

                var want = _cfg.SiphonPoolWidth + _sprayed * _cfg.SiphonPoolPerLitre;
                if (want > _cfg.SiphonPoolMaxWidth) want = _cfg.SiphonPoolMaxWidth;

                var moved = _sprayAt == Vector3.Zero || at.DistanceTo(_sprayAt) > _cfg.SiphonPoolStep;

                if (moved) _sprayAt = at;
                else if (want - _sprayWidth < _cfg.SiphonPoolGrowth) return;

                _sprayWidth = want;

                Function.Call(Hash.ADD_PETROL_DECAL, _sprayAt.X, _sprayAt.Y, _sprayAt.Z,
                              0.1f, _sprayWidth, 1f);

                _sprayDecals++;
            }
            catch (Exception ex)
            {
                Log.Once("spray-decal", "Could not put petrol on the ground: " + ex.Message);
            }
        }

        /// <summary>
        /// Deletes one of OUR props, and nothing else that might be wearing its number.
        ///
        /// THE GAME RECYCLES ENTITY HANDLES. A prop this mod stood on the ground and then
        /// forgot about for a while -- the dropped nozzle goes six seconds later, a put-down
        /// can two minutes later -- can be streamed out by the game in the meantime, and its
        /// handle handed to the next thing spawned. Exists() is then TRUE, because something
        /// exists at that handle, and Delete() deletes it: which was, more than once, the car
        /// somebody had just walked up to with a jerry can. So a delete first asks whether the
        /// thing at the handle is still an object at all, and still one of our models; if it
        /// is not, it is somebody else's now and it stays.
        /// </summary>
        private static void SafeDelete(Prop prop, string what)
        {
            try
            {
                if (prop == null || !prop.Exists()) return;

                if (!Function.Call<bool>(Hash.IS_ENTITY_AN_OBJECT, prop.Handle))
                {
                    Log.Warn("Not deleting the " + what + ": handle " + prop.Handle +
                             " is no longer an object -- the game has reused it.");
                    return;
                }

                if (!OurModel(prop.Model))
                {
                    Log.Warn("Not deleting the " + what + ": handle " + prop.Handle +
                             " is a different object now (" + prop.Model.Hash + ").");
                    return;
                }

                if (prop.IsAttached()) prop.Detach();
                prop.Delete();
            }
            catch (Exception ex)
            {
                Log.Debug("Could not delete the " + what + ": " + ex.Message);
            }
        }

        /// <summary>The models this file ever stands on the ground: the cans, and the nozzle.</summary>
        private static bool OurModel(Model model)
        {
            foreach (var name in CanProps)
            {
                if (model.Hash == Game.GenerateHash(name)) return true;
            }

            return Nozzle.IsNozzleModel(model);
        }

        /// <summary>Called on shutdown. Leaves nothing of ours in the world.</summary>
        public void Shutdown()
        {
            // THE POSES FIRST, and this is the one that actually bit: reloading the script mid
            // siphon left the crouch and the arm clip running on him with nothing left alive to
            // take them off. Every other kind of stop goes through a stage change; this one does
            // not happen at all, because the object stops existing. He kept siphoning at nothing
            // until the game was closed.
            try { Poses(Player()); }
            catch { /* he is beyond helping */ }

            SaveCan();
            TidyDroppedCan(true);

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
        private static void Turn(Ped me, Vector3 point, float rightwards = 0f)
        {
            try
            {
                var to = point - me.Position;
                if (to.Length() < 0.05f) return;

                var heading = (float)(Math.Atan2(-to.X, to.Y) * 180d / Math.PI);

                // SUBTRACTED, because a GTA heading counts anticlockwise: north is 0 and west
                // is 90, so turning to the right is going down through the numbers rather than
                // up. Adding here would turn him the other way and look like the setting was
                // simply the wrong size.
                heading -= rightwards;

                while (heading < 0f) heading += 360f;
                while (heading >= 360f) heading -= 360f;

                me.Heading = heading;
                Function.Call(Hash.SET_PED_DESIRED_HEADING, me.Handle, heading);

                Log.Debug("Turned him to " + heading.ToString("0") + " degrees" +
                          (Math.Abs(rightwards) > 0.5f
                              ? " (" + rightwards.ToString("0") + " right of the filler)."
                              : " to face the filler."));
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
            Pose(me, _cfg.FillAnimDict, _cfg.FillAnimClip, _cfg.FillAnimPhase, _cfg.FillAnimFlag);
        }

        /// <summary>Which clip is currently held, so the right one gets stopped.</summary>
        private string _posedDict, _posedClip;

        /// <summary>Whether he was already crouched when the siphon began, so it can be put back.</summary>
        private bool _wasStealthy;
        private bool _crouched;
        private bool _crouchImpossible;

        /// <summary>
        /// Holds a ped at one frame of a clip, for as long as it is called.
        ///
        /// Was FillPose, and is now shared, because the siphon wants the same trick with a
        /// different clip: an arm out at the filler. Same machinery rather than a second copy,
        /// since the awkward parts -- request the dict every frame, freeze the speed, re-set the
        /// time every frame -- are awkward for the same reasons either way.
        /// </summary>
        private void Pose(Ped me, string dict, string clip, float phase, int flag)
        {
            if (string.IsNullOrEmpty(dict) || string.IsNullOrEmpty(clip)) return;

            if (_poseImpossible) return;

            try
            {
                // Checked rather than assumed: a dict name that is not in the game makes
                // REQUEST_ANIM_DICT wait forever, so without this a typo in the ini is a pose
                // that never appears and never explains itself.
                if (!Function.Call<bool>(Hash.DOES_ANIM_DICT_EXIST, dict))
                {
                    _poseImpossible = true;
                    Log.Warn("'" + dict + "' is not an animation dictionary this game has. " +
                             "He will hold still instead.");
                    return;
                }

                if (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dict))
                {
                    // Requested every frame until it arrives. A single request that gets dropped
                    // under streaming pressure is never made again.
                    Function.Call(Hash.REQUEST_ANIM_DICT, dict);
                    return;
                }

                if (!Function.Call<bool>(Hash.IS_ENTITY_PLAYING_ANIM, me.Handle, dict, clip, 3))
                {
                    // A DIFFERENT clip may be held from a moment ago -- he can go from the pump
                    // pose straight into the siphon pose. Stop that one first, or two upper-body
                    // tasks fight and the newer one loses.
                    if (_posing && _posedClip != clip) StopFillPose();

                    Function.Call(Hash.TASK_PLAY_ANIM, me.Handle, dict, clip,
                                  4f, -4f, -1, flag, 0f, false, false, false);
                }

                _posing = true;
                _posedDict = dict;
                _posedClip = clip;

                if (phase < 0f) return;

                // HELD, NOT PLAYED, and held EVERY FRAME rather than once.
                //
                // A handshake is a movement -- reach, grip, shake, withdraw -- so letting it run
                // gives an arm that pumps and then drops back to his side. Stopping it at the
                // reach turns the movement into a pose. Speed zero freezes it and the time is
                // re-set each frame because the task keeps its own clock: set once, it creeps.
                Function.Call(Hash.SET_ENTITY_ANIM_SPEED, me.Handle, dict, clip, 0f);

                Function.Call(Hash.SET_ENTITY_ANIM_CURRENT_TIME, me.Handle, dict, clip, phase);
            }
            catch (Exception ex)
            {
                Log.Once("fillpose", "Could not play '" + clip + "': " + ex.Message +
                                     " - he will hold still instead.");
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

                if (string.IsNullOrEmpty(_posedClip)) return;

                // SPEED BACK TO ONE BEFORE IT STOPS. The burst pacing parks this clip at speed
                // zero for seconds at a time, and a stop that does not take -- the task already
                // replaced, the clip renamed under it -- leaves a man frozen mid-motion rather
                // than merely still posing. Costs one native to make the failure survivable.
                Function.Call(Hash.SET_ENTITY_ANIM_SPEED, me.Handle, _posedDict, _posedClip, 1f);

                Function.Call(Hash.STOP_ANIM_TASK, me.Handle, _posedDict, _posedClip, -4f);
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
            try { GTA.UI.Notification.Show(Lang.T(text), false); }
            catch { /* not worth a crash */ }
        }
    }
}
