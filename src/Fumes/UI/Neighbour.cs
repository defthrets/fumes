using System;
using System.Reflection;
using GTA;

using Fumes.Core;

namespace Fumes.UI
{
    /// <summary>
    /// Bare Minimum's row of bars, if it is running: where it stands and how big its bars are.
    ///
    /// THE FUEL GAUGE IS THE SIXTH BAR IN A ROW OF FIVE. It is drawn by this mod and the other
    /// five are drawn by that one, and the only thing that makes them read as one instrument is
    /// that every number matches. Those numbers used to be copied across by hand -- Width,
    /// Height and Y typed into this mod's ini to match that one's -- and it held until that
    /// mod's minimap frame started deciding where the row ends. Then every change to the frame
    /// moved the five and left the sixth standing where it was, three times in one day. So it
    /// is asked for now, once a frame, and there is one mod that owns the answer.
    ///
    /// LATE-BOUND, and for the reason the food bridge going the other way spells out: a GTA
    /// scripts\ folder is one assembly resolution namespace, so two mods that both reference a
    /// third assembly must agree about its version forever, and the day they stop agreeing the
    /// failure is a TypeLoadException with no log -- because the thing that writes the log is
    /// the thing that did not load. Nothing here references BareMinimum.dll, nothing here fails
    /// to compile or run without it, and the answer to "not installed" is Ready == false and
    /// this mod's own settings, which is exactly how it behaved before this file existed.
    ///
    /// IT RETRIES RATHER THAN RESOLVING ONCE. SHVDN builds scripts in whatever order it finds
    /// them, so on about half of all launches this mod is constructed before the other exists
    /// in the AppDomain at all. A bridge that looked once at startup would be permanently
    /// absent on those launches only -- intermittent, and not reproducible by whoever wrote it.
    /// </summary>
    internal static class Neighbour
    {
        private const string Assembly = "BareMinimum";
        private const string TypeName = "BareMinimum.Api.Rack";

        /// <summary>The contract this code was written against.</summary>
        private const int WantApi = 1;

        private const int GiveUpAfterMs = 30000;
        private const int RetryEveryMs = 2000;

        private static Type _type;
        private static bool _gaveUp;
        private static int _nextTry;
        private static int _firstTry;
        private static bool _said;

        private static PropertyInfo _ready, _bottom, _barWidth, _barLength, _opacity;

        /// <summary>Whether the other mod is here, has laid its row out, and is drawing bars.</summary>
        public static bool Ready
        {
            get
            {
                if (Resolve() == null) return false;

                try { return _ready != null && (bool)_ready.GetValue(null, null); }
                catch { return false; }
            }
        }

        /// <summary>The line the row's plates end on. A fraction of screen height.</summary>
        public static float Bottom { get { return Read(_bottom); } }

        /// <summary>How wide one of its bars is, as a fraction of screen width.</summary>
        public static float BarWidth { get { return Read(_barWidth); } }

        /// <summary>How tall the whole instrument is -- bar, breath and plate -- as a fraction of screen height.</summary>
        public static float BarLength { get { return Read(_barLength); } }

        /// <summary>Its opacity, 0 to 1.</summary>
        public static float Opacity { get { return Read(_opacity); } }

        private static float Read(PropertyInfo p)
        {
            try
            {
                if (p == null || Resolve() == null) return 0f;
                return (float)p.GetValue(null, null);
            }
            catch { return 0f; }
        }

        private static Type Resolve()
        {
            if (_type != null) return _type;
            if (_gaveUp) return null;

            int now;
            try { now = Game.GameTime; }
            catch { return null; }

            if (_firstTry == 0) _firstTry = now;
            if (now < _nextTry) return null;

            _nextTry = now + RetryEveryMs;

            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != Assembly) continue;

                    var type = asm.GetType(TypeName);
                    if (type == null) continue;

                    // READ BEFORE ANYTHING ELSE, which is what makes the other side free to
                    // change later: an old Fumes against a new Bare Minimum sees a number it
                    // does not recognise and quietly keeps its own settings, rather than
                    // half-calling an API that has moved underneath it.
                    var api = type.GetProperty("ApiVersion", BindingFlags.Public | BindingFlags.Static);
                    var have = api == null ? 0 : (int)api.GetValue(null, null);

                    if (have != WantApi)
                    {
                        Log.Info("Bare Minimum speaks row API v" + have + " and this wants v" + WantApi +
                                 ". The gauge will use its own size and position.");
                        _gaveUp = true;
                        return null;
                    }

                    _ready = type.GetProperty("Ready", BindingFlags.Public | BindingFlags.Static);
                    _bottom = type.GetProperty("Bottom", BindingFlags.Public | BindingFlags.Static);
                    _barWidth = type.GetProperty("BarWidth", BindingFlags.Public | BindingFlags.Static);
                    _barLength = type.GetProperty("BarLength", BindingFlags.Public | BindingFlags.Static);
                    _opacity = type.GetProperty("Opacity", BindingFlags.Public | BindingFlags.Static);

                    _type = type;

                    var version = type.GetProperty("Version", BindingFlags.Public | BindingFlags.Static);
                    Log.Info("Bare Minimum " +
                             (version == null ? "?" : version.GetValue(null, null) as string) +
                             " found. The gauge will stand in its row.");

                    return _type;
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not look for Bare Minimum's row: " + ex.Message);
            }

            if (now - _firstTry > GiveUpAfterMs)
            {
                _gaveUp = true;
                Log.Info("Bare Minimum is not installed. The gauge will use its own size and position.");
            }

            return null;
        }

        /// <summary>
        /// This frame's size and position for the gauge, taken from the row when there is one.
        ///
        /// THE X IS NEVER TAKEN. The row stands on the far side of the minimap and this gauge
        /// stands on the near side of it -- that is the arrangement, and it is the one thing
        /// here that is genuinely this mod's own decision. Everything else is a matter of the
        /// two matching, and matching means copying.
        /// </summary>
        public static bool Match(Settings cfg, ref float y, ref float w, ref float h)
        {
            if (!cfg.GaugeMatchBars || !Ready) return false;

            var width = BarWidth;
            var length = BarLength;
            var bottom = Bottom;

            // A row with nothing in it, or numbers that have not been filled in yet, is not
            // something to lay a gauge out from.
            if (width < 0.0005f || length < 0.004f || bottom <= 0.004f) return false;

            w = width;
            h = length;
            y = bottom - length;

            if (!_said)
            {
                _said = true;
                Log.Info("The gauge is standing in Bare Minimum's row: width " +
                         w.ToString("0.0000") + ", height " + h.ToString("0.0000") +
                         ", top " + y.ToString("0.0000") + ".");
            }

            return true;
        }
    }
}
