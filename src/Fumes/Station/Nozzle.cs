using System;
using System.Globalization;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using Fumes.Core;

namespace Fumes.Station
{
    /// <summary>
    /// The nozzle itself: a prop in the player's hand, and the pose that makes him look like
    /// he is holding one.
    ///
    /// Two separate problems, and the second is the hard one. A prop bolted to a hand is easy;
    /// making the ARM look right while he walks, turns, backs up and stands at a car is a whole
    /// animation set, and hand-authoring that is out of the question.
    ///
    /// So it is not authored. He is given an INVISIBLE WEAPON -- a fire extinguisher by default
    /// -- and the game's own carry, walk, run and idle animations for that weapon do all of it,
    /// at every angle, in first and third person, for free. The weapon's model is hidden; our
    /// nozzle sits where it would have been.
    ///
    /// The extinguisher is preferred over the petrol can for a reason that has nothing to do
    /// with how it looks: it is a weapon that SPRAYS, so it carries an aiming camera, a reticle
    /// and a trigger. Nothing uses them yet, but they are the only route to fuel actually
    /// coming out of the nozzle under player control, and that is the seam Overspray paints
    /// through. Same trick, same reason.
    /// </summary>
    internal sealed class Nozzle
    {
        /// <summary>
        /// The nozzle model, in the order worth trying.
        ///
        /// prop_cs_fuel_nozle first -- and yes, that is how Rockstar spelt it. It is the prop
        /// from the game's own petrol-station scene, so it is the right shape and it streams.
        /// The jerry can at the end is a last resort that is guaranteed to exist: wrong object,
        /// right idea, and far better than an empty hand with a hose coming out of it.
        /// </summary>
        private static readonly string[] Models =
        {
            "prop_cs_fuel_nozle",
            "prop_fuel_nozle",
            "prop_cs_fuel_nozzle",
            "w_am_jerrycan"
        };

        /// <summary>
        /// Whether a model is one this class stands in the world.
        ///
        /// For Refuel.SafeDelete: the game reuses entity handles, so before deleting a
        /// nozzle we dropped minutes ago it is worth asking whether the thing at that handle
        /// is still a nozzle.
        /// </summary>
        public static bool IsNozzleModel(Model model)
        {
            foreach (var name in Models)
            {
                if (model.Hash == Game.GenerateHash(name)) return true;
            }

            return false;
        }

        /// <summary>
        /// PH_R_Hand and PH_L_Hand.
        ///
        /// NOT SKEL_R_Hand (57005), which is the wrist joint the arm deforms around -- a prop
        /// hung off that sits beside the fist and moves again with every clip that changes the
        /// grip. These two are non-deforming helpers the animators put there specifically to
        /// hang props on.
        ///
        /// Props AUTHORED for those bones want no offset and no rotation. This one is not: it
        /// is a scene prop whose origin is not its grip, so it needs the placement in Settings.
        /// </summary>
        private const int RightHand = 28422;
        private const int LeftHand = 60309;

        /// <summary>Whichever hand the settings put it in. See Settings.LeftHand.</summary>
        private int HandBone => _cfg.LeftHand ? LeftHand : RightHand;

        private readonly Settings _cfg;

        private Prop _prop;
        private int _nextTry;
        private bool _moaned;

        /// <summary>What he was holding before, so it can be given back.</summary>
        private WeaponHash _previousWeapon = WeaponHash.Unarmed;
        private bool _posed;

        /// <summary>Which weapon the pose borrowed, so the right one is handed back.</summary>
        private WeaponHash _poseWeapon = WeaponHash.Unarmed;

        /// <summary>
        /// Whether he was already carrying one of these, and how full it was.
        ///
        /// THIS IS NOT BOOKKEEPING, IT IS A BUG THAT WOULD OTHERWISE HAPPEN EVERY TIME. The
        /// pose works by giving him a weapon and taking it away afterwards -- and a player who
        /// owns a fire extinguisher, or who bought a jerry can at this very pump ninety seconds
        /// ago, is carrying the same weapon. Taking "ours" back would take theirs, and the only
        /// sign would be a thing quietly missing from the weapon wheel.
        /// </summary>
        private bool _hadOwn;
        private int _ownAmmo;

        public Nozzle(Settings cfg)
        {
            _cfg = cfg;
        }

        /// <summary>Whether the nozzle is currently in his hand.</summary>
        public bool Out => _prop != null && _prop.Exists();

        /// <summary>The nozzle in his hand, for anything that needs to hang an effect on it.</summary>
        public Prop Prop => _prop;

        /// <summary>
        /// The hand, which is where the hose really ends.
        ///
        /// No longer static: which hand it is comes from the settings now, and a hose that
        /// still ran to the right hand while the nozzle sat in the left would cross his body.
        /// </summary>
        public Vector3 HandPosition()
        {
            try
            {
                var me = Game.Player.Character;
                if (me != null && me.Exists())
                {
                    return me.Bones[_cfg.LeftHand ? Bone.PHLeftHand : Bone.PHRightHand].Position;
                }
            }
            catch
            {
                // Fall through.
            }

            try { return Game.Player.Character.Position; }
            catch { return Vector3.Zero; }
        }

        /// <summary>
        /// Where the hose joins the nozzle.
        ///
        /// ON THE PROP, not on the hand. A hand bone is a hand's width from where a hose really
        /// meets a nozzle, so ending it there ran the hose into his fist and out the far side.
        /// Taken as an offset in the NOZZLE'S own space it also rotates with the nozzle, so
        /// tilting the thing swings the hose with it the way a real one would -- which a bone
        /// offset could never do, because the bone does not know the prop is turned.
        ///
        /// Falls back to the hand while the model is still streaming, so the hose has somewhere
        /// to be in the second before the nozzle appears.
        /// </summary>
        public Vector3 HoseEnd()
        {
            try
            {
                if (Out)
                {
                    var local = new Vector3(_cfg.HoseEndX, _cfg.HoseEndY, _cfg.HoseEndZ);
                    if (_cfg.HoseEndAuto) local += BackOfNozzle();

                    // The lift and the side offset go on AFTER the frame change, which is
                    // the whole point of them -- see HoseEndLift. Added to the local offset
                    // they would be up and sideways in the NOZZLE'S opinion, and the nozzle is
                    // attached turned through a right angle twice.
                    var world = _prop.GetOffsetPosition(local);

                    world += new Vector3(0f, 0f, _cfg.HoseEndLift);

                    if (Math.Abs(_cfg.HoseEndSide) > 0.0005f)
                    {
                        // His right, not the world's: the world's stops being sideways the
                        // moment he turns round, and the camera is behind him whenever anyone
                        // is looking at this.
                        var me = Game.Player.Character;
                        if (me != null && me.Exists()) world += me.RightVector * _cfg.HoseEndSide;
                    }

                    return world;
                }
            }
            catch
            {
                // Fall through to the hand.
            }

            return HandPosition();
        }

        /// <summary>The end of the nozzle, worked out once from the model's bounding box.</summary>
        private Vector3 _back;
        private bool _backKnown;

        /// <summary>
        /// Where the back of the nozzle is, in its own space, from the model's own dimensions.
        ///
        /// A fuel nozzle is a long thin thing, so the LONGEST AXIS OF ITS BOUNDING BOX is the
        /// nozzle -- spout at one end, hose at the other. Taking the attachment point off that
        /// means it is right for whichever of the four candidate models actually loaded, at
        /// whatever size, without a number being typed for any of them.
        ///
        /// The one thing the box cannot say is which end is the spout, because both ends of a
        /// box look the same to a box. That is HoseEndSign -- one bit, flipped in the tuner in
        /// a second, instead of three continuous numbers nobody can reason about.
        /// </summary>
        /// <summary>
        /// The spout: the far end from where the hose joins, in world space.
        ///
        /// THE OPPOSITE OF HoseEnd, and worked out from it rather than measured again: the
        /// nozzle is a stick with a hose on one end, so the other end is the same offset
        /// mirrored through the middle. Taken in the nozzle's own space, so it swings with
        /// the thing when he tilts it, and a fraction past the end so the fuel leaves the
        /// model rather than starting inside it.
        ///
        /// Falls back to the hand while the model streams in.
        /// </summary>
        public Vector3 Spout()
        {
            try
            {
                if (Out) return _prop.GetOffsetPosition(SpoutOffset());
            }
            catch (Exception ex)
            {
                Log.Once("nozzle-spout", "Could not find the nozzle spout: " + ex.Message);
            }

            return HoseEnd();
        }

        /// <summary>
        /// The spout in the NOZZLE'S OWN SPACE, for anything hung on the prop rather than
        /// placed in the world -- a looped particle takes an offset, not a position.
        /// </summary>
        public Vector3 SpoutOffset()
        {
            try
            {
                if (!Out) return Vector3.Zero;

                // THE GEOMETRY ONLY, NOT THE HOSE'S NUDGE. This used to mirror the whole hose
                // joint, HoseEndX/Y/Z included -- so moving where the hose meets the handle
                // dragged the fuel stream with it, and a spray that had been placed by eye
                // was somewhere else the moment the joint was placed by eye. They are two
                // ends of the same model and neither should move the other. Mirrored from
                // the measured end alone; SprayX/Y/Z is the only thing that places the
                // stream, and it is the editor that sets them.
                //
                // A fraction past the end, so what comes out leaves the model rather than
                // starting inside it.
                return -BackOfNozzle() * 1.15f;
            }
            catch
            {
                return Vector3.Zero;
            }
        }

        /// <summary>Which way the spout points, for a stream that leaves it.</summary>
        public Vector3 SpoutDirection()
        {
            try
            {
                if (Out)
                {
                    var away = Spout() - HoseEnd();
                    if (away.Length() > 0.02f) return away.Normalized;

                    return _prop.ForwardVector;
                }
            }
            catch
            {
                // Fall through.
            }

            try { return Game.Player.Character.ForwardVector; }
            catch { return Vector3.Zero; }
        }

        private Vector3 BackOfNozzle()
        {
            if (_backKnown) return _back;

            try
            {
                // FULLY QUALIFIED, because this class already has a Models -- the list of
                // nozzle props it tries. Two things called Models in one file is one too many
                // and the compiler picks the nearer one.
                Vector3 min, max;
                if (!Fumes.Core.Models.Box(_prop.Model, out min, out max)) return Vector3.Zero;

                var size = max - min;
                var centre = (min + max) * 0.5f;
                var reach = _cfg.HoseEndReach * _cfg.HoseEndSign;

                if (size.Y >= size.X && size.Y >= size.Z)
                {
                    _back = new Vector3(centre.X, centre.Y + size.Y * 0.5f * reach, centre.Z);
                }
                else if (size.X >= size.Z)
                {
                    _back = new Vector3(centre.X + size.X * 0.5f * reach, centre.Y, centre.Z);
                }
                else
                {
                    _back = new Vector3(centre.X, centre.Y, centre.Z + size.Z * 0.5f * reach);
                }

                _backKnown = true;

                Log.Info("Nozzle is " + size.X.ToString("0.00") + " x " + size.Y.ToString("0.00") +
                         " x " + size.Z.ToString("0.00") + "m; hose joins at " +
                         _back.X.ToString("0.000") + ", " + _back.Y.ToString("0.000") + ", " +
                         _back.Z.ToString("0.000") + " in its own space.");
            }
            catch (Exception ex)
            {
                Log.Once("nozzle-bounds", "Could not measure the nozzle: " + ex.Message);
                _backKnown = true;
                _back = Vector3.Zero;
            }

            return _back;
        }

        /// <summary>
        /// Puts the nozzle in his hand. Returns false only if the player is unreachable.
        ///
        /// Called repeatedly while the nozzle is meant to be out, not once: a model request
        /// under streaming pressure gets dropped, and a single attempt that fails leaves the
        /// player holding a hose attached to nothing with no way to find out why.
        /// </summary>
        public bool Take()
        {
            if (Out) { Pose(); return true; }

            if (Game.GameTime < _nextTry) return false;
            _nextTry = Game.GameTime + 800;

            try
            {
                var me = Game.Player.Character;
                if (me == null || !me.Exists()) return false;

                foreach (var name in Models)
                {
                    var model = new Model(name);
                    if (!model.IsValid || !model.IsInCdImage) continue;
                    if (!model.Request(600)) continue;

                    _prop = World.CreateProp(model, me.Position, false, false);
                    model.MarkAsNoLongerNeeded();

                    if (_prop == null || !_prop.Exists()) continue;

                    // No collision: a nozzle that can shove the player, the car or the pump
                    // turns a refuel into a physics accident.
                    _prop.IsCollisionEnabled = false;

                    _backKnown = false;
                    Seat();

                    Log.Info("Nozzle in hand: " + name + ".");
                    _moaned = false;
                    Pose();
                    return true;
                }

                if (!_moaned)
                {
                    _moaned = true;
                    Log.Warn("No nozzle model would load -- tried " + string.Join(", ", Models) +
                             ". Refuelling still works; there is just nothing in his hand.");
                }

                // Deliberately true. The pose and the hose are what the interaction actually
                // needs; a missing prop is ugly, not broken, and refusing here would mean
                // nobody on a build without that model could ever put fuel in a car.
                Pose();
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Could not put the nozzle in his hand.", ex);
                return false;
            }
        }

        /// <summary>
        /// (Re-)bolts the prop to the hand at the configured offset and rotation.
        ///
        /// Separate from Take because the tuner calls it every time a number moves. Re-attaching
        /// an already-attached entity is how the placement is changed: there is no native to
        /// nudge an existing attachment.
        /// </summary>
        private void Seat()
        {
            try
            {
                var me = Game.Player.Character;
                if (me == null || !me.Exists() || !Out) return;

                var bone = Function.Call<int>(Hash.GET_PED_BONE_INDEX, me.Handle, HandBone);

                Function.Call(Hash.ATTACH_ENTITY_TO_ENTITY, _prop.Handle, me.Handle, bone,
                              _cfg.NozzleOffsetX, _cfg.NozzleOffsetY, _cfg.NozzleOffsetZ,
                              _cfg.NozzleRotX, _cfg.NozzleRotY, _cfg.NozzleRotZ,
                              false, false, false, false, 2, true);
            }
            catch (Exception ex)
            {
                Log.Once("nozzle-seat", "Could not seat the nozzle: " + ex.Message);
            }
        }

        // ==================================================================
        // The pose
        // ==================================================================

        /// <summary>Which weapon this pose borrows.</summary>
        private WeaponHash PoseWeapon()
        {
            switch (_cfg.Pose)
            {
                case NozzlePose.FireExtinguisher: return WeaponHash.FireExtinguisher;
                case NozzlePose.PetrolCan: return WeaponHash.PetrolCan;
                default: return WeaponHash.Unarmed;
            }
        }

        /// <summary>Whether what he was carrying has been written down yet.</summary>
        private bool _recorded;

        /// <summary>
        /// Gives him the invisible weapon, and KEEPS CHECKING IT TOOK.
        ///
        /// This used to run once and latch, which was the bug: walk up to a pump holding a
        /// pistol and the selection would not take, so he ended up standing in a gunman's
        /// stance with a fuel nozzle where the pistol had been -- two-handed grip, weapon
        /// walk, the lot. The prop was right and everything around it was a gun.
        ///
        /// Now the selected weapon is read back every frame. If it is not ours, it is put
        /// right, so nothing that steals the weapon back -- another mod, a cutscene, picking
        /// something up -- can leave the pose wrong for more than a frame.
        ///
        /// What he was carrying is recorded ONCE, though, or the second pass would faithfully
        /// record the extinguisher as the thing to give him back afterwards.
        /// </summary>
        private void Pose()
        {
            var want = PoseWeapon();
            if (want == WeaponHash.Unarmed) return;

            try
            {
                var me = Game.Player.Character;
                if (me == null || !me.Exists()) return;

                if (!_recorded)
                {
                    _recorded = true;
                    _previousWeapon = me.Weapons.Current == null ? WeaponHash.Unarmed : me.Weapons.Current.Hash;
                    _poseWeapon = want;

                    _hadOwn = Function.Call<bool>(Hash.HAS_PED_GOT_WEAPON, me.Handle, (uint)want, false);
                    _ownAmmo = _hadOwn
                        ? Function.Call<int>(Hash.GET_AMMO_IN_PED_WEAPON, me.Handle, (uint)want)
                        : 0;
                }

                var held = Function.Call<uint>(Hash.GET_SELECTED_PED_WEAPON, me.Handle);

                if (held == (uint)want)
                {
                    // Right weapon already. Only the hiding is worth repeating -- anything that
                    // reselects a weapon makes its model visible again.
                    Function.Call(Hash.SET_PED_CURRENT_WEAPON_VISIBLE, me.Handle, false, false, true, false);
                    _posed = true;
                    return;
                }

                // THE WEAPON'S ASSET BEFORE THE WEAPON, and this is the flash. Selecting a
                // weapon whose asset is not yet streamed creates its object a frame or two
                // LATER, and SET_PED_CURRENT_WEAPON_VISIBLE hides the object that exists NOW --
                // so the hide below landed on nothing, the extinguisher or the can drew for a
                // frame, and only then did the per-frame re-hide above catch it. That frame is
                // what people saw pop into his hand.
                //
                // With the asset resident the object is created inside the select call itself,
                // and the hide has something to take hold of. Until then, wait: Take() asks
                // again next frame for as long as the nozzle is out, so nothing is lost by
                // not posing yet except the frame that used to show a fire extinguisher.
                if (!Function.Call<bool>(Hash.HAS_WEAPON_ASSET_LOADED, (uint)want))
                {
                    Function.Call(Hash.REQUEST_WEAPON_ASSET, (uint)want, 31, 0);
                    return;
                }

                // UNARMED FIRST, and this is the part that was missing. Going straight from a
                // pistol to the extinguisher can leave the pistol's movement and strafe clipsets
                // in place, which is what the gunman's stance actually was -- the weapon had
                // changed and the way he stood had not. Passing through unarmed and clearing
                // the clipsets makes the change complete.
                Function.Call(Hash.SET_CURRENT_PED_WEAPON, me.Handle, (uint)WeaponHash.Unarmed, true);
                Function.Call(Hash.RESET_PED_WEAPON_MOVEMENT_CLIPSET, me.Handle);
                Function.Call(Hash.RESET_PED_STRAFE_CLIPSET, me.Handle);

                // A notional unit of ammunition, because a weapon with none is not selectable.
                // Refuel disables the attack control for as long as the nozzle is out, which is
                // what actually stops him hosing the forecourt down.
                if (!_hadOwn)
                {
                    Function.Call(Hash.GIVE_WEAPON_TO_PED, me.Handle, (uint)want, 1, false, true);
                }

                Function.Call(Hash.SET_CURRENT_PED_WEAPON, me.Handle, (uint)want, true);
                Function.Call(Hash.SET_PED_CURRENT_WEAPON_VISIBLE, me.Handle, false, false, true, false);
                Function.Call(Hash.SET_PED_CAN_SWITCH_WEAPON, me.Handle, false);

                _posed = true;
            }
            catch (Exception ex)
            {
                Log.Once("pose-fail", "Could not give the carrying pose: " + ex.Message +
                                      " - the nozzle will still work, his arm will just hang.");
            }
        }

        /// <summary>
        /// Re-reads what is in the can he owns, so putting the nozzle back cannot undo a fill.
        ///
        /// THE CARRYING POSE IS THE PETROL CAN WEAPON. That is how the nozzle sits in his fist
        /// at all -- the can is equipped and hidden and the nozzle prop hangs off the hand --
        /// which means the ammo this class carefully saves and restores IS the fuel in his
        /// jerry can.
        ///
        /// Saved once, when the nozzle is taken. Fine for filling a car; wrong the moment you
        /// fill the CAN with the nozzle out, because the level then changes underneath a number
        /// that was read before it started and is written back afterwards. Hanging up would
        /// have handed back the empty can you arrived with and kept your money.
        ///
        /// So whoever changes the can says so, and the saved figure follows it.
        /// </summary>
        public void NoteOwnAmmo(Ped me)
        {
            if (!_posed || !_hadOwn) return;
            if (me == null || !me.Exists()) return;

            try
            {
                _ownAmmo = Function.Call<int>(Hash.GET_AMMO_IN_PED_WEAPON, me.Handle, (uint)_poseWeapon);
            }
            catch
            {
                // Keep the old figure. Worse than the new one, better than none.
            }
        }

        /// <summary>
        /// Hands everything back. Safe to call when nothing is out.
        ///
        /// The weapon HAS to be taken away rather than merely hidden: an invisible extinguisher
        /// left in the player's inventory is a weapon he can select, fire and not see.
        /// </summary>
        public void PutBack()
        {
            try
            {
                // THE SAME GUARD THE LOOSE PROPS GET, and for the same reason: the game
                // recycles entity handles, so a nozzle that streamed out can have its number
                // handed to the next thing spawned -- and then this deletes that instead.
                // See Refuel.SafeDelete.
                if (_prop != null && _prop.Exists() &&
                    Function.Call<bool>(Hash.IS_ENTITY_AN_OBJECT, _prop.Handle) &&
                    IsNozzleModel(_prop.Model))
                {
                    _prop.Delete();
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Nozzle prop would not delete: " + ex.Message);
            }
            _prop = null;

            if (!_posed) return;
            _posed = false;

            try
            {
                var me = Game.Player.Character;
                if (me == null || !me.Exists()) return;

                Function.Call(Hash.SET_PED_CAN_SWITCH_WEAPON, me.Handle, true);

                if (_hadOwn)
                {
                    // His own, put back exactly as full as it was. SET_PED_AMMO rather than a
                    // fresh GIVE_WEAPON_TO_PED, which would refill it for nothing.
                    Function.Call(Hash.SET_PED_AMMO, me.Handle, (uint)_poseWeapon, _ownAmmo);
                }
                else
                {
                    Function.Call(Hash.REMOVE_WEAPON_FROM_PED, me.Handle, (uint)_poseWeapon);
                }

                Function.Call(Hash.SET_PED_CURRENT_WEAPON_VISIBLE, me.Handle, true, false, true, false);

                // The stance goes back with the weapon, for the same reason it had to be
                // cleared going in.
                Function.Call(Hash.RESET_PED_WEAPON_MOVEMENT_CLIPSET, me.Handle);
                Function.Call(Hash.RESET_PED_STRAFE_CLIPSET, me.Handle);

                // EMPTY-HANDED, not back to whatever he had before.
                //
                // Restoring the previous weapon was the tidy-looking choice and the wrong one:
                // you walk away from a pump with a pistol suddenly in your hand because you
                // happened to be holding one when you arrived. Putting a nozzle down should
                // leave you holding nothing, the way it would. Nothing is taken out of his
                // inventory -- the weapon is still there, it is simply not drawn.
                Function.Call(Hash.SET_CURRENT_PED_WEAPON, me.Handle, (uint)WeaponHash.Unarmed, true);
            }
            catch (Exception ex)
            {
                Log.Error("Could not hand the player their weapon back.", ex);
            }

            _previousWeapon = WeaponHash.Unarmed;
            _poseWeapon = WeaponHash.Unarmed;
            _hadOwn = false;
            _ownAmmo = 0;
            _recorded = false;
        }

        /// <summary>
        /// Drops it on the floor where it is, rather than deleting it.
        ///
        /// This is what over-stretching the hose does, and it wants to look like the nozzle was
        /// PULLED out of his hand -- so the prop is detached, given its collision back and left
        /// to fall.
        /// </summary>
        public Prop Drop()
        {
            var dropped = _prop;
            _prop = null;

            try
            {
                if (dropped != null && dropped.Exists())
                {
                    Function.Call(Hash.DETACH_ENTITY, dropped.Handle, true, true);
                    dropped.IsCollisionEnabled = true;
                    Function.Call(Hash.ACTIVATE_PHYSICS, dropped.Handle);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Nozzle would not drop cleanly: " + ex.Message);
            }

            PutBack();      // clears the pose; the prop is already detached and not deleted
            return dropped;
        }
    }
}
