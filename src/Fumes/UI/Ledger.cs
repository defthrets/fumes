using System;
using System.Collections.Generic;
using System.Text;
using GTA;
using Fumes.Core;

namespace Fumes.UI
{
    /// <summary>
    /// The one list of rectangles the machine keeps, and everybody's share of it.
    ///
    /// THERE IS NO SUCH THING AS A MOD'S OWN LIST. DRAW_RECT puts a rectangle on ONE list the
    /// game empties once a frame, every script on the machine draws into it, and past about
    /// three hundred and fifty on this install the game silently drops whatever is handed over
    /// after that -- so the script that pays for a busy frame is whichever draws LAST, whatever
    /// it drew. That is how a phone comes up in a car with no background: the bars beside the
    /// minimap had spent the list before the phone was asked for.
    ///
    /// Each mod counted its own rectangles and warned past the ceiling, which named nobody:
    /// four logs each saying "I drew forty" while the frame drew four hundred. So the count is
    /// SHARED. SHVDN loads every script into one AppDomain and ticks them one at a time on one
    /// thread, and AppDomain data is visible to all of them -- the same channel the splash uses
    /// to stack its rows. The tally lives there, keyed by mod, and every mod's log can say what
    /// the whole frame cost and who spent it. (Mods that are not ours -- iFruitAddon2, NativeUI
    /// -- draw into the same list and are not counted, so the frame's real total is the tally
    /// plus whatever they added.)
    ///
    /// AND THE SHARING IS A RULE, NOT A REPORT. Decoration -- gradient bands, specks, stacked
    /// corners -- asks Room first, and Room is no once the machine's frame is over the soft
    /// line AND this mod is over its share of it, which is the line divided by however many
    /// mods drew last frame. Whoever is under their share keeps their sparkle; whoever is over
    /// it loses theirs, until what it costs fits again. The instruments never go: a bar with no
    /// sheen is a bar and half a bar is a bug.
    ///
    /// ONE ANSWER FOR THE WHOLE FRAME, decided from the frame before. A live comparison would
    /// trim whichever decoration happened to ask late, differently every frame, at sixty a
    /// second -- which is a flicker, and the thing this exists to stop. And it comes back only
    /// when the room is measured to be there: this mod's count with the decoration on against
    /// its count with it off is what the decoration costs, and it returns when the frame has
    /// that much spare. A latch that came back the moment it was allowed to would blink.
    ///
    /// This file is the same in every mod of the set, byte for byte after the namespace and
    /// the name. Edit the copy in Hoodrich and run tools/sync-ledger.py.
    /// </summary>
    internal static class Ledger
    {
        /// <summary>This mod, as every log will name it.</summary>
        public const string Me = "Fumes";

        /// <summary>
        /// Where the game starts dropping rectangles. The figure is the game's and is not
        /// published; this is where things started going missing on the busiest install seen.
        /// </summary>
        public const int Ceiling = 350;

        /// <summary>Where decoration stops, machine-wide. Well under the ceiling so the instruments always fit.</summary>
        public const int Soft = 260;

        /// <summary>Frames a trim decision stands before it is looked at again, either way.</summary>
        private const int HoldFrames = 30;

        private const int SayTrimEveryMs = 10000;
        private const int MinuteMs = 60000;

        private const string TallyKey = "spitmux.hud.tally";
        private const string ModsKey = "spitmux.hud.mods";

        // THE SHARED TALLY IS PLAIN ARRAYS. A type of our own would be a different type in
        // every assembly and the cast across them would fail; an int[] is the same int[]
        // everywhere. The slots:
        //
        //   Frame     the frame the tally is counting
        //   Total     rectangles so far this frame, everybody
        //   Peak      the most in any frame this session
        //   Prev      last frame's total
        //   Active    how many mods drew at least one last frame
        //   Worst     the busiest frame since the minute began
        //   MinuteAt  when it began, game time
        //   LastWorst the busiest frame of the minute just closed
        //   Gen       how many minutes have closed, so every mod says each one once
        private const int KFrame = 0, KTotal = 1, KPeak = 2, KPrev = 3, KActive = 4,
                          KWorst = 5, KMinuteAt = 6, KLastWorst = 7, KGen = 8, Size = 16;

        // Per mod: this frame, last frame, its part of the peak frame, of the busiest frame of
        // the minute so far, and of the minute just closed.
        private const int MNow = 0, MLast = 1, MAtPeak = 2, MAtWorst = 3, MAtLastWorst = 4, ModSize = 8;

        private static int[] _t;
        private static Dictionary<string, int[]> _mods;
        private static int[] _mine;
        private static bool _broken;

        private static int _myFrame;
        private static bool _trim;
        private static int _hold;
        private static int _withOn;
        private static int _share;

        private static int _saidPeak;
        private static bool _saidTrim;
        private static int _saidTrimAt;
        private static int _saidGen;

        /// <summary>A rectangle went into the list. Called by the mod's Rect, and nowhere else.</summary>
        public static void Count()
        {
            if (!Bind()) return;

            Sync();

            _t[KTotal]++;
            _mine[MNow]++;
        }

        /// <summary>
        /// Whether there is room this frame for something that is only decoration. Asked before
        /// the work, not before each rectangle -- a speck that draws three should not get one
        /// and stop -- and the answer is the same for the whole frame. See the class note.
        /// </summary>
        public static bool Room
        {
            get
            {
                if (!Bind()) return true;

                Sync();

                return !_trim;
            }
        }

        /// <summary>How many rectangles the frame can still take under the soft line. For anything that would rather draw fewer than none.</summary>
        public static int Spare
        {
            get
            {
                if (!Bind()) return int.MaxValue;

                Sync();

                if (_trim) return 0;

                var left = Soft - _t[KTotal];
                return left < 0 ? 0 : left;
            }
        }

        /// <summary>Everybody's rectangles so far this frame.</summary>
        public static int Total
        {
            get { return Bind() ? _t[KTotal] : 0; }
        }

        /// <summary>This mod's rectangles so far this frame.</summary>
        public static int Mine
        {
            get { return Bind() ? _mine[MNow] : 0; }
        }

        private static bool Bind()
        {
            if (_mine != null) return true;
            if (_broken) return false;

            try
            {
                var domain = AppDomain.CurrentDomain;

                var t = domain.GetData(TallyKey) as int[];
                if (t == null)
                {
                    t = new int[Size];
                    domain.SetData(TallyKey, t);
                }

                // A tally shorter than this copy expects was made by an older copy, and
                // replacing it would leave that copy counting into an array nobody reads.
                // Better to sit this session out than to lie.
                if (t.Length < Size) { _broken = true; return false; }

                var mods = domain.GetData(ModsKey) as Dictionary<string, int[]>;
                if (mods == null)
                {
                    mods = new Dictionary<string, int[]>();
                    domain.SetData(ModsKey, mods);
                }

                int[] mine;
                if (!mods.TryGetValue(Me, out mine) || mine.Length < ModSize)
                {
                    mine = new int[ModSize];
                    mods[Me] = mine;
                }

                _t = t;
                _mods = mods;
                _mine = mine;
                return true;
            }
            catch
            {
                _broken = true;
                return false;
            }
        }

        /// <summary>
        /// Rolls the frame over when the game has. Whoever notices first rolls the shared
        /// tally; every mod then makes its own trim decision from the frame that just closed.
        /// </summary>
        private static void Sync()
        {
            int frame;

            try { frame = Game.FrameCount; }
            catch { return; }

            if (frame == _myFrame) return;

            if (frame != _t[KFrame]) Roll(frame);

            _myFrame = frame;

            Latch();
            Minute();
            Say();
        }

        private static void Roll(int frame)
        {
            var prev = _t[KTotal];
            var active = 0;

            foreach (var m in _mods.Values)
            {
                m[MLast] = m[MNow];
                if (m[MNow] > 0) active++;
            }

            _t[KPrev] = prev;
            _t[KActive] = active;

            if (prev > _t[KPeak])
            {
                _t[KPeak] = prev;
                foreach (var m in _mods.Values) m[MAtPeak] = m[MLast];
            }

            if (prev > _t[KWorst])
            {
                _t[KWorst] = prev;
                foreach (var m in _mods.Values) m[MAtWorst] = m[MLast];
            }

            foreach (var m in _mods.Values) m[MNow] = 0;

            _t[KTotal] = 0;
            _t[KFrame] = frame;
        }

        /// <summary>
        /// Whether this mod draws its decoration this frame, from the frame before.
        ///
        /// Off when the machine is over the soft line and this mod is over its share of it.
        /// Back when what the decoration costs -- measured, the count with it on against the
        /// count with it off -- would fit under the line with a tenth to spare. Each decision
        /// stands for thirty frames, so a busy screen that comes and goes does not toggle it.
        /// </summary>
        private static void Latch()
        {
            var total = _t[KPrev];
            var mine = _mine[MLast];

            _share = Soft / Math.Max(1, _t[KActive]);

            if (!_trim) _withOn = mine;

            if (_hold > 0)
            {
                _hold--;
                return;
            }

            if (!_trim)
            {
                if (total > Soft && mine > _share)
                {
                    _trim = true;
                    _hold = HoldFrames;
                }

                return;
            }

            var cost = _withOn - mine;
            if (cost < 0) cost = 0;

            if (total + cost < Soft * 9 / 10)
            {
                _trim = false;
                _hold = HoldFrames;
            }
        }

        /// <summary>Closes the minute for everybody, once it is up. Every mod then says it once -- see Say.</summary>
        private static void Minute()
        {
            int now;

            try { now = Game.GameTime; }
            catch { return; }

            if (_t[KMinuteAt] == 0 || now < _t[KMinuteAt])
            {
                _t[KMinuteAt] = now;
                return;
            }

            if (now - _t[KMinuteAt] < MinuteMs) return;

            _t[KLastWorst] = _t[KWorst];

            foreach (var m in _mods.Values)
            {
                m[MAtLastWorst] = m[MAtWorst];
                m[MAtWorst] = 0;
            }

            _t[KWorst] = 0;
            _t[KMinuteAt] = now;
            _t[KGen]++;
        }

        private static void Say()
        {
            if (_t[KPeak] > _saidPeak)
            {
                _saidPeak = _t[KPeak];

                var line = "Draw list: " + _t[KPeak] + " rectangles in one frame across the machine -- " +
                           Breakdown(MAtPeak) + ".";

                if (_t[KPeak] >= Ceiling)
                {
                    Log.Warn(line + " Past about " + Ceiling +
                             " the game drops the rest of the frame's, whoever drew them.");
                }
                else
                {
                    Log.Info(line);
                }
            }

            if (_t[KGen] != _saidGen)
            {
                _saidGen = _t[KGen];

                if (_t[KLastWorst] > 0)
                {
                    Log.Info("Draw list: the busiest frame in the last minute was " + _t[KLastWorst] +
                             " of about " + Ceiling + " -- " + Breakdown(MAtLastWorst) + ".");
                }
            }

            int now;

            try { now = Game.GameTime; }
            catch { return; }

            if (_trim)
            {
                if (!_saidTrim && now - _saidTrimAt >= SayTrimEveryMs)
                {
                    Log.Info("Trim: " + Me + "'s decoration is off. The machine drew " + _t[KPrev] +
                             " rectangles last frame, over the " + Soft + " line, and this mod's " +
                             _mine[MLast] + " is over its " + _share + " share.");

                    _saidTrim = true;
                    _saidTrimAt = now;
                }
            }
            else if (_saidTrim)
            {
                _saidTrim = false;
                Log.Info("Trim: " + Me + "'s decoration is back. The frame has room for it again.");
            }
        }

        /// <summary>Who drew what, most first, for one of the per-mod slots.</summary>
        private static string Breakdown(int slot)
        {
            var names = new List<string>(_mods.Keys);
            names.Sort((a, b) => _mods[b][slot].CompareTo(_mods[a][slot]));

            var sb = new StringBuilder();

            foreach (var n in names)
            {
                var v = _mods[n][slot];
                if (v <= 0) continue;

                if (sb.Length > 0) sb.Append(", ");
                sb.Append(n).Append(' ').Append(v);
            }

            return sb.Length == 0 ? "nobody" : sb.ToString();
        }
    }
}
