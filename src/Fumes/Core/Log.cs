using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Fumes.Core
{
    internal enum LogLevel
    {
        Error = 0,
        Warn = 1,
        Info = 2,
        Debug = 3
    }

    /// <summary>
    /// File logger for Fumes.log.
    ///
    /// Every method swallows its own exceptions. A logger that can throw takes the whole
    /// script down from inside a Tick handler, which is precisely the moment the log is
    /// the only thing that could have told you why.
    /// </summary>
    internal static class Log
    {
        private const long MaxBytes = 2 * 1024 * 1024;

        private static readonly object Gate = new object();
        private static bool _started;

        public static LogLevel Level = LogLevel.Info;

        public static void Error(string message, Exception ex = null) => Write(LogLevel.Error, message, ex);
        public static void Warn(string message) => Write(LogLevel.Warn, message, null);
        public static void Info(string message) => Write(LogLevel.Info, message, null);
        public static void Debug(string message) => Write(LogLevel.Debug, message, null);

        /// <summary>
        /// Says a thing once and then shuts up about it.
        ///
        /// Most of what goes wrong in here goes wrong every frame -- a model that will not
        /// stream, a bone that is not on this vehicle -- and a tick-rate log is a log nobody
        /// can read. The key is the caller's, so two different failures still both get said.
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> Said =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        public static void Once(string key, string message)
        {
            lock (Gate)
            {
                if (!Said.Add(key)) return;
            }
            Write(LogLevel.Warn, message, null);
        }

        /// <summary>Lets a once-only message be said again, after the thing it was about changed.</summary>
        public static void Forget(string key)
        {
            lock (Gate) { Said.Remove(key); }
        }

        private static void Write(LogLevel level, string message, Exception ex)
        {
            if (level > Level) return;

            try
            {
                lock (Gate)
                {
                    var path = Paths.LogFile;
                    if (!_started)
                    {
                        RollIfLarge(path);
                        _started = true;
                        AppendLine(path, "");
                        AppendLine(path, "=== " + Build.Name + " " + Build.Version + " by " + Build.By + " started " +
                                         DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " ===");
                    }

                    var sb = new StringBuilder();
                    sb.Append('[').Append(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append("] ");
                    sb.Append(level.ToString().ToUpperInvariant().PadRight(5)).Append(' ');
                    sb.Append(message);

                    if (ex != null)
                    {
                        sb.AppendLine();
                        sb.Append("    ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
                        if (!string.IsNullOrEmpty(ex.StackTrace))
                        {
                            sb.AppendLine();
                            sb.Append(ex.StackTrace);
                        }
                        if (ex.InnerException != null)
                        {
                            sb.AppendLine();
                            sb.Append("    inner: ").Append(ex.InnerException.GetType().Name)
                              .Append(": ").Append(ex.InnerException.Message);
                        }
                    }

                    AppendLine(path, sb.ToString());
                }
            }
            catch
            {
                // Logging must never be the reason a script dies.
            }
        }

        private static void AppendLine(string path, string line)
        {
            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }

        private static void RollIfLarge(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < MaxBytes) return;

                var old = path + ".1";
                if (File.Exists(old)) File.Delete(old);
                File.Move(path, old);
            }
            catch
            {
                // A locked or unrollable log is not worth failing over.
            }
        }
    }

    /// <summary>
    /// What this thing is called, in the one place anything is allowed to ask.
    ///
    /// The display name and the file names are deliberately different things. Fumes.dll,
    /// Fumes.ini, Fumes.log and the folder beside them are PATHS -- renaming those breaks
    /// every install that exists. Name is the word people read.
    /// </summary>
    internal static class Build
    {
        public const string Version = "0.1.0";
        public const string Name = "Fumes";
        public const string By = "spitmux";
    }
}
