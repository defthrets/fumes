using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Fumes.Core
{
    /// <summary>
    /// Remembers which rope types kill the game, by surviving the crash.
    ///
    /// ADD_ROPE's type is an INDEX INTO THE GAME'S ROPE TABLE, not a value it validates. Hand
    /// it a number past the end and the engine reads off the end of the array and the process
    /// dies -- no managed exception, nothing in the SHVDN log, no chance to catch it, because
    /// by the time control would come back there is no process to come back to. Type 8 did
    /// exactly that here: the log ends mid-line on "Hose rope type is now 8."
    ///
    /// That was my fault twice over. The cycler ran 0-8 because I had written "nine rope
    /// textures" in a comment and then believed it, and the range was never checked against
    /// anything. So the fix is not only a smaller number:
    ///
    /// THE INTENT IS WRITTEN DOWN BEFORE THE RISKY CALL. A file on disk names the type about
    /// to be tried; it is deleted once the rope has existed for a second and a half without
    /// taking the game with it. If it is still there at startup, the last thing this mod did
    /// before the game vanished was try that type -- so it goes on a list and is never offered
    /// again, on this machine, whatever the range says.
    ///
    /// It costs one tiny file write per rope, and it means the picker cannot cost the same
    /// crash twice even for the types nobody has tried yet.
    /// </summary>
    internal static class RopeProbe
    {
        /// <summary>
        /// Highest rope type that will ever be offered.
        ///
        /// Seven, because 3-7 were each created and drawn in front of us without incident and 8
        /// was fatal on the very first attempt. 0-2 are below the proven band rather than
        /// proven, which is exactly what the probe file is for.
        /// </summary>
        public const int MaxType = 7;

        private static string File_ => Path.Combine(Paths.Writable, "rope-probe.txt");

        private static readonly HashSet<int> Bad = new HashSet<int>();
        private static bool _armed;

        /// <summary>True when this type has already taken the game down once.</summary>
        public static bool IsBad(int type)
        {
            return type < 0 || type > MaxType || Bad.Contains(type);
        }

        /// <summary>The next type worth trying after this one, wrapping, skipping the fatal ones.</summary>
        public static int Next(int type)
        {
            for (var step = 1; step <= MaxType + 1; step++)
            {
                var candidate = (type + step) % (MaxType + 1);
                if (!IsBad(candidate)) return candidate;
            }

            return type;
        }

        /// <summary>
        /// Called once at startup, before any rope is made.
        ///
        /// Loads the types already known to be fatal, and -- the point of the whole class --
        /// notices a probe file left behind by a session that did not get to delete it.
        /// </summary>
        public static void Review(Settings cfg)
        {
            Bad.Clear();
            foreach (var part in (cfg.BadRopeTypes ?? string.Empty).Split(',', ' ', ';'))
            {
                int value;
                if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    Bad.Add(value);
            }

            var pending = Read();
            if (pending.HasValue)
            {
                // The game died with this one in flight. Nothing else writes this file, and it
                // is removed a second and a half after a rope survives, so there is no honest
                // way for it to still be here.
                Bad.Add(pending.Value);
                Log.Warn("Rope type " + pending.Value + " took the game down last time - it will " +
                         "not be offered again. Remove it from [Nozzle] BadRopeTypes to try it.");
                Save(cfg);
                Clear();
            }

            if (IsBad(cfg.HoseRopeType))
            {
                var safe = Next(cfg.HoseRopeType);
                Log.Warn("Rope type " + cfg.HoseRopeType + " is on the bad list - using " + safe + " instead.");
                cfg.HoseRopeType = safe;
            }

            if (Bad.Count > 0) Log.Info("Rope types that crash this install: " + Listed() + ".");
        }

        /// <summary>Names the type that is about to be handed to ADD_ROPE. Call BEFORE the call.</summary>
        public static void Arm(int type)
        {
            if (_armed) return;

            try
            {
                System.IO.File.WriteAllText(File_, type.ToString(CultureInfo.InvariantCulture));
                _armed = true;
            }
            catch
            {
                // No probe is worse than a probe, but not worth refusing to draw a hose over.
            }
        }

        /// <summary>The rope has been alive long enough to be innocent.</summary>
        public static void Disarm()
        {
            if (!_armed) return;
            _armed = false;
            Clear();
        }

        private static int? Read()
        {
            try
            {
                if (!System.IO.File.Exists(File_)) return null;

                int value;
                if (int.TryParse(System.IO.File.ReadAllText(File_).Trim(),
                                 NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    return value;

                Clear();
                return null;
            }
            catch
            {
                return null;
            }
        }

        private static void Clear()
        {
            try { if (System.IO.File.Exists(File_)) System.IO.File.Delete(File_); }
            catch { /* it will be reviewed again next time and say the same thing */ }
        }

        private static void Save(Settings cfg)
        {
            cfg.BadRopeTypes = Listed();
            IniFile.SetValue(Paths.Ini, "Nozzle", "BadRopeTypes", cfg.BadRopeTypes);
        }

        private static string Listed()
        {
            var sorted = new List<int>(Bad);
            sorted.Sort();
            return string.Join(",", sorted.ConvertAll(v => v.ToString(CultureInfo.InvariantCulture)).ToArray());
        }
    }
}
