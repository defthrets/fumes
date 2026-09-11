using System;
using System.Drawing;
using GTA;
using GTA.Native;
using Fumes.Core;

namespace Fumes.UI
{
    /// <summary>
    /// The two drawing primitives this mod needs, and nothing else.
    ///
    /// No LemonUI, no NativeUI, no menu framework. A GTA scripts\ folder is one shared
    /// assembly-resolution namespace and every UI library in it is a version fight waiting to
    /// happen with somebody else's mod -- and all Fumes ever draws is a bar, a border and a
    /// couple of lines of text.
    /// </summary>
    internal static class Draw
    {
        // ======================================================================
        // The budget
        // ======================================================================

        /// <summary>
        /// How many rectangles this frame, and the worst frame so far.
        ///
        /// THERE IS A CEILING AND IT IS NOT OURS. The game keeps ONE list of these for the
        /// whole machine and drops whatever is handed to it once that list is full -- so the
        /// script that pays for a busy frame is whichever draws LAST, whatever it drew. A HUD
        /// spending three hundred rectangles on gradients beside the minimap is not a slow
        /// HUD; it is a HUD that takes somebody else's panel off the screen, and that is
        /// exactly what happened: Hoodrich's phone came up in a car with no background at all, because
        /// its screen is a rectangle and the HUDs beside the minimap had already spent
        /// the list.
        ///
        /// So it is counted, and the peak is logged when it gets worse -- a handful of lines
        /// in a session rather than sixty a second. The frame is told apart by the game's own
        /// frame number, so nothing has to remember to call a Begin.
        /// </summary>
        public static int RectsThisFrame { get; private set; }

        public static int PeakRects { get; private set; }

        /// <summary>Where this install starts dropping them. The real figure is the game's and is not published.</summary>
        private const int Ceiling = 350;

        private static int _frame;

        private static void Counted()
        {
            int frame;

            try { frame = Game.FrameCount; }
            catch { frame = 0; }

            if (frame != _frame)
            {
                _frame = frame;

                if (RectsThisFrame > PeakRects)
                {
                    PeakRects = RectsThisFrame;

                    if (PeakRects >= Ceiling)
                    {
                        Log.Warn("Draw budget: " + PeakRects + " rectangles in a frame. Past about " +
                                 Ceiling + " the game drops the rest of the frame's -- for every " +
                                 "script on the machine, not only this one.");
                    }
                    else
                    {
                        Log.Info("Draw budget: " + PeakRects + " rectangles in a frame (new peak).");
                    }
                }

                RectsThisFrame = 0;
            }

            RectsThisFrame++;
        }

        /// <summary>The screen's height in pixels, for the sub-pixel test. Read once.</summary>
        private static int Tall
        {
            get
            {
                if (_tall > 0) return _tall;

                try { _tall = GTA.UI.Screen.Resolution.Height; }
                catch { _tall = 1080; }

                return _tall;
            }
        }

        private static int _tall;

        /// <summary>
        /// A filled rectangle, positioned by its CENTRE.
        ///
        /// That is DRAW_RECT's own convention and it is worth stating, because every other
        /// coordinate in a HUD is a corner and getting it wrong shifts everything by half its
        /// own size -- which looks like a rounding error rather than a mistake.
        /// </summary>
        public static void Rect(float centreX, float centreY, float width, float height, Color colour)
        {
            // NOTHING UNDER HALF A PIXEL. A band a fifth of a pixel tall is not a faint line,
            // it is nothing at all -- and it costs exactly as much of the frame's one list of
            // rectangles as a band you can see. See Counted for who pays.
            if (height * Tall < 0.5f) return;

            try
            {
                Counted();

                Function.Call(Hash.DRAW_RECT, centreX, centreY, width, height,
                              colour.R, colour.G, colour.B, colour.A, false);
            }
            catch (Exception ex)
            {
                Log.Once("draw-rect", "DRAW_RECT failed: " + ex.Message);
            }
        }

        /// <summary>A rectangle drawn from its top-left, which is how a bar is actually thought about.</summary>
        /// <summary>The screen's shape, for turning a height into a width that matches it.</summary>
        public static float Aspect()
        {
            try
            {
                var a = GTA.UI.Screen.AspectRatio;
                if (a > 0.5f && a < 6f) return a;
            }
            catch
            {
                // Fall through to the safe default.
            }

            return 1.7778f;
        }

        public static Color Blend(Color a, Color b, float t)
        {
            if (t <= 0f) return a;
            if (t >= 1f) return b;

            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t),
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        /// <summary>How lit a point on the ring is, given where the chase has got to.</summary>
        private static float Ring(float u, float chase, float halo)
        {
            var d = Math.Abs(u - chase);
            if (d > 0.5f) d = 1f - d;

            var g = 1f - d / halo;
            return g < 0f ? 0f : g;
        }

        /// <summary>
        /// A frame with a light running round it, the way a lit forecourt sign does.
        ///
        /// LIVED IN Meter UNTIL THE GRADE CARD WANTED THE SAME THING. It is the pump display's
        /// signature and the card sits directly in front of it, so the two matching is the
        /// point rather than a nicety -- and two copies of a chase are two places for it to
        /// drift out of step.
        ///
        /// Built as a ring of short segments rather than four sliding rectangles, which is the
        /// trick that makes it simple: a travelling highlight drawn as a moving rectangle has
        /// to be split by hand every time it crosses a corner, and gets the maths wrong at
        /// exactly the four moments anybody is looking at it. A ring of fixed segments, each
        /// brightened by how near the chase is, turns corners for free.
        ///
        /// Segment lengths are weighted by the screen's ASPECT. Fractions of width and
        /// fractions of height are not the same distance, so an unweighted ring runs the light
        /// along the top edge at nearly twice the speed it climbs the sides -- which reads as a
        /// stutter rather than a circuit.
        ///
        /// The unlit frame is four solid rectangles drawn first and in one piece. It used to be
        /// the same ring of segments, every one drawn whether lit or not, and a ring of
        /// abutting rectangles does not abut once each edge is rounded to whole pixels: the
        /// frame came out as a row of gold blocks with gaps, which reads as broken rather than
        /// dim.
        /// </summary>
        public static void ChaseFrame(float left, float top, float w, float h,
                                      Color dim, Color lit,
                                      float seconds = 2.8f, int segments = 120,
                                      float thick = 0.0022f, float halo = 0.10f)
        {
            try
            {
                Bar(left, top, w, thick, dim);
                Bar(left, top + h - thick, w, thick, dim);
                Bar(left, top, thick, h, dim);
                Bar(left + w - thick, top, thick, h, dim);

                var aspect = Aspect();
                var wide = w * aspect;          // top and bottom, in height-equivalent units
                var perimeter = 2f * (wide + h);

                if (perimeter <= 0.0001f || segments < 4) return;

                var step = perimeter / segments;
                var chase = (Environment.TickCount % (int)(seconds * 1000)) / (seconds * 1000f);

                for (var i = 0; i < segments; i++)
                {
                    var u = (float)i / segments;

                    // Two lights, opposite each other. One reads as a stray pixel; two read as
                    // a sign that is meant to be doing this.
                    var glow = Math.Max(Ring(u, chase, halo), Ring(u, (chase + 0.5f) % 1f, halo));
                    if (glow <= 0.02f) continue;

                    var colour = Blend(dim, lit, glow * glow);
                    var d = u * perimeter;

                    // Each piece is drawn a shade longer than its spacing so neighbours overlap
                    // rather than leaving a hairline of frame between them -- AND every piece is
                    // clamped to its own edge, because that overlap is what used to send the
                    // last segment before a corner spurting out past the end of the frame.
                    var run = step * 1.15f;

                    if (d < wide)
                    {
                        var x = left + (d / wide) * w;
                        var len = Math.Min(run / aspect, left + w - x);
                        if (len > 0f) Bar(x, top, len, thick, colour);
                    }
                    else if (d < wide + h)
                    {
                        var y = top + (d - wide);
                        var len = Math.Min(run, top + h - y);
                        if (len > 0f) Bar(left + w - thick, y, thick, len, colour);
                    }
                    else if (d < 2f * wide + h)
                    {
                        var x = left + w - ((d - wide - h) / wide) * w;
                        var from = Math.Max(left, x - run / aspect);
                        if (x > from) Bar(from, top + h - thick, x - from, thick, colour);
                    }
                    else
                    {
                        var y = top + h - (d - 2f * wide - h);
                        var from = Math.Max(top, y - run);
                        if (y > from) Bar(left, from, thick, y - from, colour);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Once("chase", "Could not draw the frame: " + ex.Message);
            }
        }

        public static void Bar(float left, float top, float width, float height, Color colour)
        {
            Rect(left + width / 2f, top + height / 2f, width, height, colour);
        }

        /// <summary>
        /// One line of text.
        ///
        /// ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME is the right component despite the name:
        /// it is the one that takes a literal string rather than a label from the game's text
        /// table. It also has a hard limit of 99 characters, so anything longer is cut here
        /// rather than silently drawing nothing at all.
        /// </summary>
        public static void Text(string text, float x, float y, float scale, Color colour,
                                int font = 4, bool centre = false, bool rightAlign = false,
                                bool outline = true)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (text.Length > 99) text = text.Substring(0, 99);

            try
            {
                Function.Call(Hash.SET_TEXT_FONT, font);
                Function.Call(Hash.SET_TEXT_SCALE, 0f, scale);
                Function.Call(Hash.SET_TEXT_COLOUR, colour.R, colour.G, colour.B, colour.A);
                // BOTH OF THESE DRAW IN BLACK, which is fine on light text over a dark HUD
                // and actively harmful on dark text over a light one: a black outline round
                // black digits nine pixels wide fills in the holes in 8, 9 and 0 until all
                // three of them are the same blob.
                if (outline)
                {
                    Function.Call(Hash.SET_TEXT_DROP_SHADOW);
                    Function.Call(Hash.SET_TEXT_OUTLINE);
                }
                Function.Call(Hash.SET_TEXT_CENTRE, centre);

                if (rightAlign)
                {
                    Function.Call(Hash.SET_TEXT_RIGHT_JUSTIFY, true);

                    // A right-justified string is laid out against the RIGHT edge of a wrap
                    // window, and with no window set that edge is zero -- so the text is drawn
                    // off the left of the screen and looks like it never drew.
                    Function.Call(Hash.SET_TEXT_WRAP, 0f, x);
                }

                Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
                Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, text);
                Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, x, y, 0);
            }
            catch (Exception ex)
            {
                Log.Once("draw-text", "Text drawing failed: " + ex.Message);
            }
        }

        /// <summary>
        /// How TALL a line of text is at a given scale, as a fraction of the screen.
        ///
        /// Asked for rather than worked out. Placing text by its bottom edge means subtracting
        /// its height from where the foot should sit, and END_TEXT_COMMAND_DISPLAY_TEXT takes
        /// the TOP -- so a wrong height is a number drawn half out of the bar, which reads as a
        /// positioning bug rather than as a bad constant. This gauge has already lost two
        /// settings to numbers that were assumed instead of measured; the game knows this one,
        /// so it gets asked.
        ///
        /// The fallback is only for the case where the native is missing entirely, and it says
        /// so in the log rather than quietly standing in.
        /// </summary>
        public static float Height(float scale, int font = 4)
        {
            try
            {
                return Function.Call<float>(Hash.GET_RENDERED_CHARACTER_HEIGHT, scale, font);
            }
            catch (Exception ex)
            {
                Log.Once("draw-height", "Could not measure text height: " + ex.Message +
                                        " - estimating it instead.");
                return scale * 0.035f;
            }
        }

        /// <summary>
        /// How wide a string will be, as a fraction of the screen.
        ///
        /// The font and scale have to be set BEFORE the measuring command begins, exactly as
        /// they do before drawing -- the game measures with whatever is currently selected,
        /// not with anything passed to the measure call. Getting that order wrong returns the
        /// width the string would have had in the previous font, which is a very quiet way to
        /// mis-centre a line.
        ///
        /// This exists so two different fonts can sit on one line and still be centred as a
        /// unit: measure both, then place each from the left.
        /// </summary>
        public static float Width(string text, float scale, int font = 4)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            if (text.Length > 99) text = text.Substring(0, 99);

            try
            {
                Function.Call(Hash.SET_TEXT_FONT, font);
                Function.Call(Hash.SET_TEXT_SCALE, 0f, scale);

                Function.Call(Hash.BEGIN_TEXT_COMMAND_GET_SCREEN_WIDTH_OF_DISPLAY_TEXT, "STRING");
                Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, text);

                return Function.Call<float>(Hash.END_TEXT_COMMAND_GET_SCREEN_WIDTH_OF_DISPLAY_TEXT, true);
            }
            catch (Exception ex)
            {
                Log.Once("text-width", "Could not measure text: " + ex.Message);
                return 0f;
            }
        }

        /// <summary>
        /// The game's own help box, top left.
        ///
        /// Used rather than drawn text for anything that is an INSTRUCTION, because this is
        /// where the player already looks for one, and because it is the only place where
        /// ~INPUT_...~ resolves to the button they have actually got bound.
        /// </summary>
        public static void Help(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            // In the player's language, looked up here so no caller has to know there is one.
            text = Lang.T(text);

            try
            {
                Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_HELP, "STRING");

                // FED IN CHUNKS RATHER THAN TRUNCATED. One text component takes at most 99
                // characters, and this used to just cut the string there -- which is fine for
                // a sentence and ruinous for a prompt, because a cut landing inside a
                // ~INPUT_CONTEXT~ tag leaves half a tag on screen as literal tildes and drops
                // the button glyph entirely.
                //
                // Chunks are split on SPACES, which is what makes it safe: a formatting tag
                // never contains one, so no split can ever land inside a tag.
                foreach (var chunk in Chunks(Fit(text, 98), 96))
                {
                    Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, chunk);
                }
                // playSound = FALSE. This is called every frame for as long as a prompt is
                // on screen, and with the sound on that is the help chime sixty times a
                // second for as long as you stand near a pump.
                Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_HELP, 0, false, false, -1);
            }
            catch (Exception ex)
            {
                Log.Once("draw-help", "Help text failed: " + ex.Message);
            }
        }

        /// <summary>Splits on spaces into pieces no longer than the limit. Never splits a ~tag~.</summary>
        /// <summary>
        /// Splits a string into text components without changing a character of it.
        ///
        /// THE PIECES ARE PUT BACK TOGETHER WITH NOTHING BETWEEN THEM. The game concatenates
        /// text components directly, so anything this drops at a boundary is dropped from the
        /// sentence -- and the version that split on spaces and rejoined with " " dropped
        /// exactly one space at every boundary, because the space that separated the last word
        /// of one chunk from the first word of the next belonged to neither.
        ///
        /// Harmless in prose. Ruinous here, because the boundary lands wherever the string
        /// happens to be long enough, and when it landed straight after a ~INPUT_...~ tag it
        /// welded the button glyph onto the word behind it -- which is how the siphon line came
        /// out as a button and no text at all.
        ///
        /// So the cut walks BACK to a space and keeps it, rather than splitting on spaces and
        /// rebuilding them. Backing off to a space is still what keeps a cut from landing
        /// inside a ~TAG~, since a tag never contains one.
        /// </summary>
        private static System.Collections.Generic.List<string> Chunks(string text, int limit)
        {
            var pieces = new System.Collections.Generic.List<string>();
            var at = 0;

            while (at < text.Length)
            {
                if (text.Length - at <= limit) { pieces.Add(text.Substring(at)); break; }

                // Backwards from the end of the window, over the window only.
                var space = text.LastIndexOf(' ', at + limit - 1, limit);

                // The space stays on the END of this chunk. No space to back off to means a
                // single run longer than a whole component, which can only be cut where the
                // window ends -- the one case where a tag can still be split, and there is
                // nothing to be done about it.
                var end = space > at ? space + 1 : at + limit;

                pieces.Add(text.Substring(at, end - at));
                at = end;
            }

            return pieces;
        }

        /// <summary>
        /// Trims a help string to what the box will actually draw, a line at a time.
        ///
        /// ONE HELP MESSAGE HOLDS ABOUT A HUNDRED CHARACTERS however many components it is fed
        /// in. Feeding it more does not spill into a second box and does not wrap: the tail is
        /// simply not drawn. Chunks() was made lossless to stop a cut landing inside a ~tag~,
        /// and that was necessary and not sufficient -- every character still arrives, and the
        /// box still stops rendering at its own limit.
        ///
        /// WHICH IS WHY THE SIPHON PROMPT WAS A BUTTON WITH NO WORDS. Stack two prompts with
        /// ~n~ and the part that falls off the end is the whole of the second one's text, while
        /// its ~INPUT_CONTEXT_SECONDARY~ tag sits safely inside the limit and draws its glyph.
        /// The prompt was never missing. It was beheaded.
        ///
        /// Shortened the LONGEST line first, and only ever its words, so what goes is the least
        /// load-bearing text on screen -- the name of a vehicle you are standing in front of --
        /// and never a whole line. Every prompt keeps its glyph and enough words to read.
        /// </summary>
        private static string Fit(string text, int limit)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= limit) return text;

            var lines = text.Split(new[] { Break }, StringSplitOptions.None);

            // The separators are not negotiable, so they come off the budget first.
            var budget = limit - (lines.Length - 1) * Break.Length;
            if (budget < lines.Length) return text.Substring(0, limit);

            // Longest first, one word at a time, until the whole thing fits. Trimming the
            // longest rather than the last keeps two prompts legible instead of sacrificing
            // the second one to spare the first.
            for (var guard = 0; guard < 200; guard++)
            {
                var total = 0;
                foreach (var l in lines) total += l.Length;
                if (total <= budget) break;

                var worst = 0;
                for (var i = 1; i < lines.Length; i++)
                {
                    if (lines[i].Length > lines[worst].Length) worst = i;
                }

                var line = lines[worst];

                // Back to a space, so a cut can never land inside a ~tag~ -- no tag has one.
                var cut = line.LastIndexOf(' ');
                if (cut <= 0) break;

                lines[worst] = line.Substring(0, cut).TrimEnd();
            }

            var joined = string.Join(Break, lines);

            Log.Once("help-fit", "A prompt was too long for the help box and was shortened: \"" +
                                 text + "\" -> \"" + joined + "\".");

            return joined.Length <= limit ? joined : joined.Substring(0, limit);
        }

        /// <summary>The help-text newline. Named because its length is part of the budget above.</summary>
        private const string Break = "~n~";

        /// <summary>Clears a help box early, so a prompt does not linger after you walk away.</summary>
        public static void ClearHelp()
        {
            try { Function.Call(Hash.CLEAR_ALL_HELP_MESSAGES); }
            catch { /* nothing to do about it */ }
        }
    }
}
