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
    /// when the room is measured to be there: THE DECORATION COUNTS ITSELF -- see Decorating --
    /// so what it costs is a number this class has actually watched go into the list, and it
    /// returns when the frame has that much spare. A latch that came back the moment it was
    /// allowed to would blink.
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

        /// <summary>
        /// Consecutive frames over the line before a trim is made at all. One is a menu
        /// opening; three is a scene that is busy. See Latch.
        /// </summary>
        private const int TripFrames = 3;
        private static int _overRun;

        /// <summary>
        /// Frames a trim stands before the decoration is put back ON to be measured again,
        /// whatever the sums say. A SAFETY NET RATHER THAN A RULE: everything below is an
        /// estimate of what the decoration costs, and an estimate that comes out too high
        /// once is an estimate that comes out too high for ever -- the latch is then shut
        /// with no evidence that will ever open it. This is what stopped that being possible:
        /// worst case the decoration returns for half a second every fifteen, is measured,
        /// and goes again. Fifteen seconds at sixty a frame.
        /// </summary>
        private const int RetestFrames = 900;
        private static int _trimFor;

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
        private static int _share;

        // What the decoration costs, counted rather than inferred. See Decorating.
        private static bool _decorating;
        private static int _decorNow;
        private static int _decorLast;
        private static int _decorCost;

        // The frame the trim was actually decided on, so the log can quote it rather than
        // whatever the machine happened to be drawing ten seconds later when it got to speak.
        private static int _tripTotal;
        private static int _tripMine;
        private static int _tripShare;

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

            if (_decorating) _decorNow++;
        }

        /// <summary>
        /// Opens and closes the decoration, so it can be counted apart from the instrument.
        ///
        /// WHY THIS EXISTS. What the decoration costs used to be guessed at: this mod's whole
        /// count on a frame with it on, against its whole count on a frame with it off. Those
        /// are two different frames, and everything else the mod happened to be drawing went
        /// into the difference. Trim while the pocket is open and the sum says the specks cost
        /// a hundred rectangles -- it was the pocket -- and the release test then asks for a
        /// frame under a hundred and thirty that is never coming. The log has it: trimmed at
        /// 16:55 with the machine at 165 of 260, and still trimmed twenty-four minutes later.
        ///
        /// So the decoration is counted, not inferred. Everything that goes into the list
        /// between Decorating(true) and Decorating(false) is decoration; the tally the release
        /// test reads is what those draws actually cost on the last frame that drew them.
        ///
        /// It is reset every frame, so a caller that returns between the two -- an early exit,
        /// an exception -- costs one frame's measurement and nothing else. A mod that never
        /// calls it prices its decoration at nothing and comes back as soon as the machine is
        /// under the line, which is the behaviour this had before any of it was measured.
        /// </summary>
        public static void Decorating(bool on)
        {
            _decorating = on;
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

            // THIS MOD'S OWN ROLL, not the shared one. Roll above happens once for the whole
            // machine and whichever mod noticed first does it; these three are private to this
            // copy of the class, so they turn over here, where every mod passes exactly once a
            // frame. Decorating is cleared with them: an unclosed block is one bad measurement
            // rather than a flag stuck on for the rest of the session.
            _decorLast = _decorNow;
            _decorNow = 0;
            _decorating = false;

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

            // WHAT THE DECORATION COSTS, AS A RUNNING AVERAGE OF WHAT IT DREW.
            //
            // Not a difference between two whole frames -- that is what Decorating exists to
            // stop being necessary, and the note there has why. This is the decoration's own
            // rectangles off the last frame that drew any, smoothed an eighth at a time so a
            // frame where a bar happened to be empty does not halve the figure.
            if (!_trim && _decorLast > 0)
            {
                _decorCost = _decorCost == 0 ? _decorLast : (_decorCost * 7 + _decorLast) / 8;
            }

            if (_hold > 0)
            {
                _hold--;
                return;
            }

            // THE SAFETY NET, BEFORE ANY SUM. A trim that has stood for RetestFrames is let go
            // whatever the numbers say, so the decoration is drawn, counted, and judged again
            // on what it really costs now. If the frame is still busy the three frames below
            // put it straight back. See RetestFrames.
            if (_trim && ++_trimFor >= RetestFrames)
            {
                _trimFor = 0;
                _trim = false;
                _hold = HoldFrames;
                return;
            }

            if (!_trim)
            {
                // AND NOT ON ONE FRAME. A frame over the line is a menu opening or a toast
                // arriving; three in a row is a scene that is genuinely busy. The hold below
                // already stops the decision flapping once made -- this stops it being made
                // on evidence a single frame wide.
                _overRun = total > Soft && mine > _share ? _overRun + 1 : 0;

                if (_overRun >= TripFrames)
                {
                    _overRun = 0;
                    _trim = true;
                    _trimFor = 0;
                    _hold = HoldFrames;

                    // The frame this was decided on, for the log. It speaks at most once every
                    // ten seconds and by then the machine is drawing something else entirely,
                    // which is how it came to report a trim "over the 260 line" at 165.
                    _tripTotal = total;
                    _tripMine = mine;
                    _tripShare = _share;
                }

                return;
            }

            if (total + _decorCost < Soft * 9 / 10)
            {
                _trim = false;
                _trimFor = 0;
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
                    Log.Info("Trim: " + Me + "'s decoration is off. The machine drew " + _tripTotal +
                             " rectangles in the frame that decided it, over the " + Soft +
                             " line, and this mod's " + _tripMine + " was over its " + _tripShare +
                             " share. The decoration costs " + _decorCost +
                             "; it is back when the frame has that spare.");

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
