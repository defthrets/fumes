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

        /// <summary>Gives him the invisible weapon, remembering what he had.</summary>
        private void Pose()
        {
            if (_posed) return;

            var want = PoseWeapon();
            if (want == WeaponHash.Unarmed) return;

            try
            {
                var me = Game.Player.Character;
                if (me == null || !me.Exists()) return;

                _previousWeapon = me.Weapons.Current == null ? WeaponHash.Unarmed : me.Weapons.Current.Hash;
                _poseWeapon = want;

                _hadOwn = Function.Call<bool>(Hash.HAS_PED_GOT_WEAPON, me.Handle, (uint)want, false);
                _ownAmmo = _hadOwn
                    ? Function.Call<int>(Hash.GET_AMMO_IN_PED_WEAPON, me.Handle, (uint)want)
                    : 0;

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
        /// Hands everything back. Safe to call when nothing is out.
        ///
        /// The weapon HAS to be taken away rather than merely hidden: an invisible extinguisher
        /// left in the player's inventory is a weapon he can select, fire and not see.
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
            _poseWeapon = WeaponHash.Unarmed;
            _hadOwn = false;
            _ownAmmo = 0;
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

        // ==================================================================
        // The tuner
        // ==================================================================

        private static readonly string[] AxisNames =
        {
            "OffsetX", "OffsetY", "OffsetZ", "RotX", "RotY", "RotZ"
        };

        private int _axis;
        private bool _cycleDown, _lessDown, _moreDown, _dumpDown;

        /// <summary>
        /// Live placement tuning, when TuneNozzle is on.
        ///
        /// This exists because there is no way to reason out where a scene prop's origin is.
        /// The numbers in Settings are a starting guess; the only way to find the right ones is
        /// to hold the thing and look at it. NumPad5 cycles the axis, NumPad4 and NumPad6 move
        /// it, NumPad0 writes the whole set to the log already formatted for the ini.
        ///
        /// It writes into the live Settings object rather than to disk on purpose -- a tuner
        /// that silently rewrote somebody's ini would be a tuner that lost their edits.
        /// </summary>
        public void Tune()
        {
            if (!_cfg.TuneNozzle || !Out) return;

            try
            {
                if (Edge(Keys.NumPad5, ref _cycleDown)) _axis = (_axis + 1) % AxisNames.Length;

                var step = 0f;
                if (Edge(Keys.NumPad4, ref _lessDown)) step = -1f;
                if (Edge(Keys.NumPad6, ref _moreDown)) step = 1f;

                if (step != 0f)
                {
                    Nudge(step * (_axis < 3 ? 0.005f : 5f));
                    Seat();
                }

                if (Edge(Keys.NumPad0, ref _dumpDown)) Log.Info("Nozzle placement:" + Environment.NewLine + IniBlock());

                UI.Draw.Text("NOZZLE TUNER   [NumPad 5] axis   [4/6] adjust   [0] log",
                             0.5f, 0.08f, 0.32f, System.Drawing.Color.FromArgb(230, 245, 200, 90), 4, true);
                UI.Draw.Text(Readout(), 0.5f, 0.115f, 0.36f,
                             System.Drawing.Color.FromArgb(240, 255, 255, 255), 4, true);
            }
            catch (Exception ex)
            {
                Log.Once("tune", "The nozzle tuner fell over: " + ex.Message);
            }
        }

        private void Nudge(float by)
        {
            switch (_axis)
            {
                case 0: _cfg.NozzleOffsetX += by; break;
                case 1: _cfg.NozzleOffsetY += by; break;
                case 2: _cfg.NozzleOffsetZ += by; break;
                case 3: _cfg.NozzleRotX += by; break;
                case 4: _cfg.NozzleRotY += by; break;
                default: _cfg.NozzleRotZ += by; break;
            }
        }

        private string Readout()
        {
            var v = new[]
            {
                _cfg.NozzleOffsetX, _cfg.NozzleOffsetY, _cfg.NozzleOffsetZ,
                _cfg.NozzleRotX, _cfg.NozzleRotY, _cfg.NozzleRotZ
            };

            var s = "";
            for (var i = 0; i < AxisNames.Length; i++)
            {
                var value = v[i].ToString(i < 3 ? "0.000" : "0.#", CultureInfo.InvariantCulture);
                s += (i == _axis ? " >" : "  ") + AxisNames[i] + " " + value;
            }

            return s;
        }

        /// <summary>The current placement, formatted so it can be pasted straight into the ini.</summary>
        private string IniBlock()
        {
            var nl = Environment.NewLine;
            return "Nozzle" + AxisNames[0] + " = " + _cfg.NozzleOffsetX.ToString("0.000", CultureInfo.InvariantCulture) + nl +
                   "Nozzle" + AxisNames[1] + " = " + _cfg.NozzleOffsetY.ToString("0.000", CultureInfo.InvariantCulture) + nl +
                   "Nozzle" + AxisNames[2] + " = " + _cfg.NozzleOffsetZ.ToString("0.000", CultureInfo.InvariantCulture) + nl +
                   "Nozzle" + AxisNames[3] + " = " + _cfg.NozzleRotX.ToString("0.#", CultureInfo.InvariantCulture) + nl +
                   "Nozzle" + AxisNames[4] + " = " + _cfg.NozzleRotY.ToString("0.#", CultureInfo.InvariantCulture) + nl +
                   "Nozzle" + AxisNames[5] + " = " + _cfg.NozzleRotZ.ToString("0.#", CultureInfo.InvariantCulture);
        }

        /// <summary>Rising edge for a key, since the input API only reports held.</summary>
        private static bool Edge(Keys key, ref bool wasDown)
        {
            bool down;
            try { down = Game.IsKeyPressed(key); }
            catch { down = false; }

            var edge = down && !wasDown;
            wasDown = down;
            return edge;
        }
    }
}
