using System;
using System.Collections.Generic;
using System.Drawing;
using Fumes.Core;

namespace Fumes.UI
{
    /// <summary>
    /// Text turned on its side, for anywhere text has to run up a narrow upright thing.
    ///
    /// GTA CANNOT ROTATE TEXT. DRAW_TEXT takes a position and a scale and no angle, and there
    /// is no argument anywhere in the text commands that gives it one. But CustomSprite has a
    /// rotation and a sprite is only a PNG -- so the letters are drawn ahead of time by
    /// tools/make_icons.py, already turned a quarter turn, and painted here as pictures.
    ///
    /// One file per character rather than one strip, because CustomSprite has no
    /// texture-coordinate rectangle: it draws a whole file or nothing, so there is no way to
    /// index into a sheet.
    ///
    /// Everything is a white silhouette tinted at draw time, so the digits take the colour of
    /// whatever they are sitting on without a second set of files.
    /// </summary>
    internal sealed class Glyphs
    {
        private readonly Dictionary<char, Icon> _chars = new Dictionary<char, Icon>();
        private readonly Icon _label = new Icon("label_fuel.png");

        /// <summary>The word FUEL, running up the bar. Height is the whole word.</summary>
        public void Label(float centreX, float centreY, float width, float height, Color tint)
        {
            _label.DrawSized(centreX, centreY, width, height, tint);
        }

        public bool LabelMissing => _label.Missing;

        private Icon For(char c)
        {
            if (_chars.TryGetValue(c, out var found)) return found;

            string file;
            if (c >= '0' && c <= '9') file = "g" + c + ".png";
            else if (c == '%') file = "gpct.png";
            else return null;

            var icon = new Icon(file);
            _chars[c] = icon;
            return icon;
        }
    }
}
