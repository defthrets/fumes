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
        /// <summary>
        /// A filled rectangle, positioned by its CENTRE.
        ///
        /// That is DRAW_RECT's own convention and it is worth stating, because every other
        /// coordinate in a HUD is a corner and getting it wrong shifts everything by half its
        /// own size -- which looks like a rounding error rather than a mistake.
        /// </summary>
        public static void Rect(float centreX, float centreY, float width, float height, Color colour)
        {
            try
            {
                Function.Call(Hash.DRAW_RECT, centreX, centreY, width, height,
                              colour.R, colour.G, colour.B, colour.A, false);
            }
            catch (Exception ex)
            {
                Log.Once("draw-rect", "DRAW_RECT failed: " + ex.Message);
            }
        }

        /// <summary>A rectangle drawn from its top-left, which is how a bar is actually thought about.</summary>
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
                                int font = 4, bool centre = false, bool rightAlign = false)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (text.Length > 99) text = text.Substring(0, 99);

            try
            {
                Function.Call(Hash.SET_TEXT_FONT, font);
                Function.Call(Hash.SET_TEXT_SCALE, 0f, scale);
                Function.Call(Hash.SET_TEXT_COLOUR, colour.R, colour.G, colour.B, colour.A);
                Function.Call(Hash.SET_TEXT_DROP_SHADOW);
                Function.Call(Hash.SET_TEXT_OUTLINE);
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
        /// The game's own help box, top left.
        ///
        /// Used rather than drawn text for anything that is an INSTRUCTION, because this is
        /// where the player already looks for one, and because it is the only place where
        /// ~INPUT_...~ resolves to the button they have actually got bound.
        /// </summary>
        public static void Help(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (text.Length > 99) text = text.Substring(0, 99);

            try
            {
                Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_HELP, "STRING");
                Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, text);
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

        /// <summary>Clears a help box early, so a prompt does not linger after you walk away.</summary>
        public static void ClearHelp()
        {
            try { Function.Call(Hash.CLEAR_ALL_HELP_MESSAGES); }
            catch { /* nothing to do about it */ }
        }
    }
}
