using System;
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
    /// making the ARM look right while he walks, turns, backs up and stands at a car is a
    /// whole animation set, and hand-authoring that is out of the question.
    ///
    /// So it is not authored. He is given an INVISIBLE PETROL CAN as a weapon. The game
    /// already owns a complete set of hold, walk, run and idle animations for a man carrying
    /// a petrol can in one hand at his side -- which is exactly the shape of a man carrying a
    /// fuel nozzle -- and all of it comes free, at every angle, in first and third person.
    /// The can is hidden; our nozzle sits where the can would be.
    ///
    /// Same trick Overspray uses to get an aiming camera out of a fire extinguisher.
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
        /// PH_R_Hand.
        ///
        /// NOT SKEL_R_Hand (57005), which is the wrist joint the arm deforms around -- a prop
        /// hung off that sits beside the fist and moves again with every clip that changes the
        /// grip. 28422 is a non-deforming helper the animators put there specifically to hang
        /// props on, which is why props attached to it want no offset and no rotation at all.
        /// </summary>
        private const int RightHand = 28422;

        private readonly Settings _cfg;

        private Prop _prop;
        private int _nextTry;
        private bool _moaned;

        /// <summary>What he was holding before, so it can be given back.</summary>
        private WeaponHash _previousWeapon = WeaponHash.Unarmed;
        private bool _posed;

        /// <summary>
        /// Whether he was already carrying a real jerry can, and how full it was.
        ///
        /// THIS IS NOT BOOKKEEPING, IT IS A BUG THAT WOULD OTHERWISE HAPPEN EVERY TIME. The
        /// pose works by giving him a petrol can and taking it away afterwards -- and a player
        /// who bought a jerry can at this very pump ninety seconds ago is carrying the same
        /// weapon. Taking "ours" back would take theirs, and the only sign would be a jerry
        /// can that quietly is not in the weapon wheel any more.
        /// </summary>
        private bool _hadOwnCan;
        private int _ownCanAmmo;

        public Nozzle(Settings cfg)
        {
            _cfg = cfg;
        }

        /// <summary>Whether the nozzle is currently in his hand.</summary>
        public bool Out => _prop != null && _prop.Exists();

        /// <summary>Where the business end is, for the hose to attach to and the fuel to come from.</summary>
        public Vector3 Tip
        {
            get
            {
                try
                {
                    if (Out) return _prop.GetOffsetPosition(new Vector3(0f, 0.12f, 0f));
                }
                catch
                {
                    // Fall through to the hand.
                }

                return HandPosition();
            }
        }

        /// <summary>The hand itself, which is where the hose really ends.</summary>
        public static Vector3 HandPosition()
        {
            try
            {
                var me = Game.Player.Character;
                if (me != null && me.Exists()) return me.Bones[Bone.PHRightHand].Position;
            }
            catch
            {
                // Fall through.
            }

            try { return Game.Player.Character.Position; }
            catch { return Vector3.Zero; }
        }

        /// <summary>
        /// Puts the nozzle in his hand. Returns false if the model would not come.
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

                    var bone = Function.Call<int>(Hash.GET_PED_BONE_INDEX, me.Handle, RightHand);

                    Function.Call(Hash.ATTACH_ENTITY_TO_ENTITY, _prop.Handle, me.Handle, bone,
                                  0f, 0f, 0f,
                                  0f, 0f, 0f,
                                  false, false, false, false, 2, true);

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

        /// <summary>Gives him the invisible petrol can, remembering what he had.</summary>
        private void Pose()
        {
            if (_posed || _cfg.Pose != NozzlePose.PetrolCan) return;

            try
            {
                var me = Game.Player.Character;
                if (me == null || !me.Exists()) return;

                _previousWeapon = me.Weapons.Current == null ? WeaponHash.Unarmed : me.Weapons.Current.Hash;

                _hadOwnCan = Function.Call<bool>(Hash.HAS_PED_GOT_WEAPON, me.Handle,
                                                 (uint)WeaponHash.PetrolCan, false);
                _ownCanAmmo = _hadOwnCan
                    ? Function.Call<int>(Hash.GET_AMMO_IN_PED_WEAPON, me.Handle, (uint)WeaponHash.PetrolCan)
                    : 0;

                // ammo 0 would leave him with nothing selectable, so he gets one notional
                // unit; Refuel disables the attack control for as long as this is out, which
                // is what actually stops him hosing the forecourt down with petrol.
                if (!_hadOwnCan)
                {
                    Function.Call(Hash.GIVE_WEAPON_TO_PED, me.Handle, (uint)WeaponHash.PetrolCan, 1, false, true);
                }
                Function.Call(Hash.SET_CURRENT_PED_WEAPON, me.Handle, (uint)WeaponHash.PetrolCan, true);
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
        /// Hands everything back. Safe to call when nothing is out.
        ///
        /// The weapon HAS to be taken away rather than merely hidden: an invisible petrol can
        /// left in the player's inventory is a weapon he can select, fire and not see, and the
        /// first thing he would do with it is set himself on fire.
        /// </summary>
        public void PutBack()
        {
            try
            {
                if (_prop != null && _prop.Exists()) _prop.Delete();
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

                if (_hadOwnCan)
                {
                    // His own can, put back exactly as full as it was. SET_PED_AMMO rather
                    // than a fresh GIVE_WEAPON_TO_PED, which would refill it for free.
                    Function.Call(Hash.SET_PED_AMMO, me.Handle, (uint)WeaponHash.PetrolCan, _ownCanAmmo);
                }
                else
                {
                    Function.Call(Hash.REMOVE_WEAPON_FROM_PED, me.Handle, (uint)WeaponHash.PetrolCan);
                }

                Function.Call(Hash.SET_PED_CURRENT_WEAPON_VISIBLE, me.Handle, true, false, true, false);

                if (_previousWeapon != WeaponHash.Unarmed)
                {
                    Function.Call(Hash.SET_CURRENT_PED_WEAPON, me.Handle, (uint)_previousWeapon, true);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not hand the player their weapon back.", ex);
            }

            _previousWeapon = WeaponHash.Unarmed;
            _hadOwnCan = false;
            _ownCanAmmo = 0;
        }

        /// <summary>
        /// Drops it on the floor where it is, rather than deleting it.
        ///
        /// This is what over-stretching the hose does, and it wants to look like the nozzle
        /// was PULLED out of his hand -- so the prop is detached, given its collision back and
        /// left to fall. Hose retracts it a moment later.
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
