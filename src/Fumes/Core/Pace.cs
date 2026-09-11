using System.Collections.Generic;
using System.Diagnostics;
using GTA.Native;

namespace Fumes.Core
{
    /// <summary>
    /// Who held the tick.
    ///
    /// A rectangle in this game exists only on the frame it is issued, and this mod issues
    /// all of them from one tick. Anything in that tick that YIELDS -- Model.Request with a
    /// timeout, Script.Wait, anything that hands the frame back and resumes later -- ends the
    /// tick for that frame, and every frame the script is away is a frame with no phone, no
    /// toasts and no bar on it. From the pavement that is the HUD flickering, and nothing
    /// about the flicker says which of a hundred systems did it.
    ///
    /// This does. The tick calls At with a name before each system it runs, and each call
    /// closes the segment before it: how long it took on the clock, and -- the thing that
    /// matters -- whether the game drew any frames while it ran. A segment that ran inside
    /// one frame drew none. A segment that yielded drew one per frame it was away, and that
    /// count is exactly the number of frames the HUD was dark. The name goes in the log with
    /// the count, once, and then no more than once every few seconds per name with a tally,
    /// so the thing that flickers every frame does not also fill the log every frame.
    ///
    /// Clock stalls -- a segment that never yielded but sat on the CPU for longer than a
    /// frame -- are logged too, because those are the stutters, and a stutter is the other
    /// thing nobody can tell the cause of from outside.
    ///
    /// Cheap by design. A checkpoint is one Stopwatch read. The frame counter is a native
    /// and is only asked when a segment took long enough that a frame could have gone by.
    /// </summary>
    internal static class Pace
    {
        /// <summary>A segment shorter than this cannot have spanned a frame, so no native is asked.</summary>
        private const double MaybeFrameMs = 4.0;

        /// <summary>On the clock, without a yield: a stall worth naming.</summary>
        private const double StallMs = 40.0;

        /// <summary>How often the same name may be logged again, with its tally.</summary>
        private const int SayEveryMs = 8000;

        private sealed class Tally
        {
            public int Yields;
            public int Frames;
            public int Stalls;
            public double WorstMs;
            public int SaidAt;
            public bool Said;
        }

        private static readonly Dictionary<string, Tally> Tallies = new Dictionary<string, Tally>();

        private static string _at = "";
        private static long _since;
        private static int _frame;
        private static bool _armed;

        /// <summary>
        /// The top of the tick. Closes the tail of the last one -- which is allowed exactly
        /// one frame, the one between ticks -- and starts the clock.
        /// </summary>
        public static void Begin()
        {
            Close(1);
            _frame = Frame();
            Set("start");
        }

        /// <summary>Before each system: closes the segment before it, opens this one.</summary>
        public static void At(string name)
        {
            Close(0);
            Set(name);
        }

        private static void Set(string name)
        {
            _at = name;
            _since = Stopwatch.GetTimestamp();
            _armed = true;
        }

        private static void Close(int allowedFrames)
        {
            if (!_armed) return;

            var ms = (Stopwatch.GetTimestamp() - _since) * 1000.0 / Stopwatch.Frequency;

            // The tail of a tick spans one frame by nature, so its clock says nothing; only
            // the frame count can, and it is only worth asking once the clock allows a frame.
            if (ms < MaybeFrameMs) return;

            var frames = 0;
            if (ms >= MaybeFrameMs)
            {
                var now = Frame();
                frames = now - _frame - allowedFrames;
                if (frames < 0) frames = 0;
                _frame = now;
            }

            var stalled = allowedFrames == 0 && frames == 0 && ms >= StallMs;
            if (frames == 0 && !stalled) return;

            Tally t;
            if (!Tallies.TryGetValue(_at, out t))
            {
                t = new Tally();
                Tallies[_at] = t;
            }

            if (frames > 0) { t.Yields++; t.Frames += frames; }
            if (stalled) t.Stalls++;
            if (ms > t.WorstMs) t.WorstMs = ms;

            var wall = System.Environment.TickCount;
            if (t.Said && wall - t.SaidAt < SayEveryMs) return;

            var line = "Tick: " + _at + (frames > 0
                ? " yielded " + frames + " frame(s) in " + ms.ToString("0") + " ms -- the HUD is dark for each one."
                : " held the tick for " + ms.ToString("0") + " ms without yielding.");

            if (t.Said)
            {
                line += " Since last said: " + t.Yields + " yield(s) over " + t.Frames + " frame(s), " +
                        t.Stalls + " stall(s), worst " + t.WorstMs.ToString("0") + " ms.";
                t.Yields = 0;
                t.Frames = 0;
                t.Stalls = 0;
                t.WorstMs = 0;
            }

            Log.Warn(line);
            t.Said = true;
            t.SaidAt = wall;
        }

        private static int Frame()
        {
            try { return Function.Call<int>(Hash.GET_FRAME_COUNT); }
            catch { return _frame; }
        }
    }
}
