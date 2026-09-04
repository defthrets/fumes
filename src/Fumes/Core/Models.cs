using System;
using GTA;
using GTA.Math;
using GTA.Native;

namespace Fumes.Core
{
    /// <summary>
    /// Things the game knows about a model, asked for in a way every ScriptHookVDotNet has.
    /// </summary>
    internal static class Models
    {
        /// <summary>
        /// A model's bounding box, through the native rather than the wrapper.
        ///
        /// THE ONE THING THIS MOD USED THAT OLDER SCRIPTHOOKVDOTNET DOES NOT HAVE.
        /// Model.GetDimensions arrived after 3.6.0, so on the stable build every one of the
        /// five call sites threw MissingMethodException -- and a missing method does not fail
        /// at load, it fails the first time that line runs. Which turns "does not work on
        /// stable" into five unrelated mystery bugs at five unrelated moments: the nozzle at
        /// one pump, the filler on one car, the can when you put it down.
        ///
        /// GET_MODEL_DIMENSIONS is the native underneath it, and natives do not have versions.
        /// OutputArgument has been in GTA.Native since long before any of this.
        ///
        /// Checked against three builds rather than assumed -- 3.6.0 stable, 3.7.0 nightly and
        /// the 3.9.0 Enhanced fork -- and the native and OutputArgument.GetResult are in all
        /// three. See tools/check_api.py, which is what found this in the first place.
        /// </summary>
        public static bool Box(Model model, out Vector3 low, out Vector3 high)
        {
            low = Vector3.Zero;
            high = Vector3.Zero;

            try
            {
                using (var min = new OutputArgument())
                using (var max = new OutputArgument())
                {
                    Function.Call(Hash.GET_MODEL_DIMENSIONS, model.Hash, min, max);

                    low = min.GetResult<Vector3>();
                    high = max.GetResult<Vector3>();
                }

                return true;
            }
            catch (Exception ex)
            {
                Log.Once("model-box", "Could not measure a model: " + ex.Message);
                return false;
            }
        }
    }
}
