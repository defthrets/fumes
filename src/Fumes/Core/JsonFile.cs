using System;
using System.IO;
using System.Text;

namespace Fumes.Core
{
    /// <summary>
    /// How a read turned out.
    ///
    /// THE WHOLE POINT OF THIS ENUM is that "there is no file" and "there is a file and I
    /// could not read it" are opposite situations that both used to come back as null. The
    /// first means a new game. The second means a save is sitting right there and something
    /// went wrong reaching it -- a half-written file, a lock held by a backup tool or an
    /// antivirus scanner, a permissions change -- and treating that as a new game is how a
    /// playthrough gets overwritten by a blank one on the next autosave.
    /// </summary>
    internal enum ReadResult
    {
        Ok,
        Missing,
        Unreadable
    }

    /// <summary>Load/save helpers for Json documents on disk.</summary>
    internal static class JsonFile
    {
        /// <summary>Reads a Json document. A missing or malformed file yields null, never an exception.</summary>
        public static Json Read(string path)
        {
            ReadResult ignored;
            return Read(path, out ignored);
        }

        /// <summary>
        /// The same read, but it says WHY it came back empty.
        ///
        /// Callers that cannot do anything useful with the difference keep using the short
        /// overload. The save is not one of those callers.
        /// </summary>
        public static Json Read(string path, out ReadResult how)
        {
            try
            {
                if (!File.Exists(path))
                {
                    how = ReadResult.Missing;
                    return null;
                }

                var text = File.ReadAllText(path, Encoding.UTF8);

                if (!Json.TryParse(text, out var doc))
                {
                    Log.Error("Malformed JSON in " + Path.GetFileName(path) + " - ignoring it.");
                    how = ReadResult.Unreadable;
                    return null;
                }

                how = ReadResult.Ok;
                return doc;
            }
            catch (Exception ex)
            {
                Log.Error("Could not read " + path, ex);
                how = ReadResult.Unreadable;
                return null;
            }
        }

        /// <summary>The rolling copy Write leaves behind, for anybody who needs to go back one.</summary>
        public static string BackupOf(string path)
        {
            return path + ".bak";
        }

        /// <summary>
        /// Writes atomically: full write to a .tmp sibling, then replace. A crash mid-save
        /// (which for a game mod means an alt-F4 during an autosave) leaves the previous
        /// save intact instead of a truncated one.
        /// </summary>
        public static bool Write(string path, Json doc)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var tmp = path + ".tmp";
                File.WriteAllText(tmp, doc.ToJsonString(true), new UTF8Encoding(false));

                if (File.Exists(path))
                {
                    var bak = path + ".bak";
                    if (File.Exists(bak)) File.Delete(bak);
                    File.Replace(tmp, path, bak);
                }
                else
                {
                    File.Move(tmp, path);
                }

                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Could not write " + path, ex);
                return false;
            }
        }
    }
}
