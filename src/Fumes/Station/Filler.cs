using System;
using GTA;
using GTA.Math;
using Fumes.Core;

namespace Fumes.Station
{
    /// <summary>
    /// Where a vehicle takes fuel.
    ///
    /// The whole reason this file exists is that the player has to WALK TO THE RIGHT SIDE OF
    /// THE CAR. A refuel that triggers from anywhere near the vehicle would not need any of
    /// this -- a distance check on the vehicle's centre would do. Walking round to the filler
    /// is the interaction, so the filler has to be a real place on a real model, and it has to
    /// be right on a Blista, a Phantom and a bike.
    /// </summary>
    internal static class Filler
    {
        /// <summary>
        /// Bone names to try, best first.
        ///
        /// petrolcap is the one Rockstar puts on cars and it is exactly the filler flap.
        /// petroltank and its left/right pair are what bikes and lorries carry instead, and
        /// they are the tank rather than the neck -- close enough to stand next to.
        /// </summary>
        private static readonly string[] Bones =
        {
            "petrolcap",
            "petroltank",
            "petroltank_l",
            "petroltank_r"
        };

        /// <summary>
        /// The filler position in the world, and whether it came from a real bone.
        ///
        /// The bool matters to the caller: a guessed position gets a more forgiving reach,
        /// because "somewhere on the back left" deserves more slack than "this exact flap".
        /// </summary>
        public static Vector3 On(Vehicle v, out bool exact)
        {
            exact = false;
            if (v == null || !v.Exists()) return Vector3.Zero;

            try
            {
                var bones = v.Bones;

                foreach (var name in Bones)
                {
                    if (!bones.Contains(name)) continue;

                    var bone = bones[name];
                    if (!bone.IsValid) continue;

                    exact = true;
                    return bone.Position;
                }
            }
            catch (Exception ex)
            {
                Log.Once("filler-bone", "Could not read a filler bone: " + ex.Message +
                                        " - falling back to the back left corner.");
            }

            return Guess(v);
        }

        /// <summary>
        /// No usable bone: the back left quarter, worked out from the model's own bounding box.
        ///
        /// Not a fixed offset, because a fixed offset that sits on the wing of a Blista sits
        /// inside the load bed of a Phantom. Taking it from the model's dimensions means the
        /// guess is at least always ON the vehicle.
        /// </summary>
        private static Vector3 Guess(Vehicle v)
        {
            try
            {
                v.Model.GetDimensions(out var min, out var max);

                var length = max.Y - min.Y;

                // In vehicle space +X is right and +Y is forward, so the near-side rear
                // quarter is minimum X, a quarter of the way up from minimum Y.
                var local = new Vector3(min.X * 0.92f, min.Y + length * 0.26f, 0.45f);

                return v.GetOffsetPosition(local);
            }
            catch (Exception ex)
            {
                Log.Once("filler-guess", "Could not size the vehicle: " + ex.Message);
                return v.RearPosition;
            }
        }

        /// <summary>
        /// Whether the player is standing at the filler.
        ///
        /// Distance in FULL 3D on purpose. A flat distance would let somebody on the roof, or
        /// on the floor above in a multi-storey, fill a car they are nowhere near.
        /// </summary>
        public static bool WithinReach(Vector3 filler, Vector3 player, float reach, bool exact)
        {
            var slack = exact ? reach : reach * 1.45f;
            return filler.DistanceTo(player) <= slack;
        }
    }
}
