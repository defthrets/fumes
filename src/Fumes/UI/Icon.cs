using System;
using System.Drawing;
using System.IO;
using GTA.UI;
using Fumes.Core;

namespace Fumes.UI
{
    /// <summary>
    /// A PNG from disk, drawn on the HUD, optionally bobbing and swaying.
    ///
    /// CustomSprite is the whole trick here and it is worth saying why it matters: it loads an
    /// ordinary PNG off disk at runtime through ScriptHookVDotNet's own texture loader. The
    /// alternative -- the only alternative -- is packing a .ytd into one of the game's RPF
    /// archives, which turns a mod anybody can drop into scripts\ into an asset mod needing
    /// OpenIV, a limit adjuster and a different install per game edition. For one icon.
    ///
    /// The PNGs are white silhouettes; the colour comes from Tint at draw time, so one file
    /// serves the amber on the pump, a red warning and whatever a later screen wants.
    /// </summary>
    internal sealed class Icon
    {
        /// <summary>
        /// CustomSprite.Draw positions and sizes in a fixed 1280x720 canvas, NOT in real
        /// pixels -- which is what makes a position written here look the same on every
        /// monitor. Named rather than left as two magic numbers in the arithmetic below.
        /// </summary>
        private const float CanvasW = 1280f;
        private const float CanvasH = 720f;

        private readonly string _path;
        private CustomSprite _sprite;
        private bool _missing;

        /// <summary>Height as a fraction of the screen. Width follows, since the PNGs are square.</summary>
        public float Scale = 0.055f;

        public Color Tint = Color.FromArgb(235, 245, 175, 55);

        /// <summary>How far it rises and falls, as a fraction of the screen, and how fast.</summary>
        public float BobHeight = 0.006f;
        public float BobSeconds = 1.9f;

        /// <summary>How far it rocks either side of upright, in degrees, and how fast.</summary>
        public float SwayDegrees = 7f;
        public float SwaySeconds = 2.7f;

        public Icon(string fileName)
        {
            _path = Path.Combine(Paths.Icons, fileName);
        }

        /// <summary>True once we know the file is not there, so nothing keeps retrying it.</summary>
        public bool Missing => _missing;

        /// <summary>
        /// Draws it centred on a point given as a fraction of the screen.
        ///
        /// animate = false freezes it upright and still, for anywhere a moving icon would be
        /// a distraction rather than a bit of life.
        /// </summary>
        public void Draw(float fx, float fy, bool animate = true)
        {
            if (_missing) return;

            try
            {
                if (!Ready()) return;

                // Wall clock, not a frame counter: the bob has to keep the same rhythm whatever
                // the framerate is doing, and a counter makes it race on a fast machine.
                var t = Environment.TickCount / 1000f;

                var lift = animate ? (float)Math.Sin(t * (Math.PI * 2f) / BobSeconds) * BobHeight : 0f;
                var tilt = animate ? (float)Math.Sin(t * (Math.PI * 2f) / SwaySeconds) * SwayDegrees : 0f;

                var side = Scale * CanvasH;

                _sprite.Size = new SizeF(side, side);
                _sprite.Position = new PointF(fx * CanvasW, (fy + lift) * CanvasH);
                _sprite.Rotation = tilt;
                _sprite.Color = Tint;
                _sprite.Draw();
            }
            catch (Exception ex)
            {
                _missing = true;
                Log.Once("icon-" + _path, "Could not draw " + _path + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Draws it with everything stated outright: no bob, no sway, no stored tint.
        ///
        /// For anything animating the icon ITSELF rather than just placing it -- a droplet
        /// falling and fading has its own position, size and alpha every frame, and none of
        /// them are the ones on this object.
        /// </summary>
        public void DrawRaw(float fx, float fy, float scale, Color tint, float rotation = 0f)
        {
            if (_missing) return;

            try
            {
                if (!Ready()) return;

                var side = scale * CanvasH;

                _sprite.Size = new SizeF(side, side);
                _sprite.Position = new PointF(fx * CanvasW, fy * CanvasH);
                _sprite.Rotation = rotation;
                _sprite.Color = tint;
                _sprite.Draw();
            }
            catch (Exception ex)
            {
                _missing = true;
                Log.Once("icon-" + _path, "Could not draw " + _path + ": " + ex.Message);
            }
        }

        private bool Ready()
        {
            if (_sprite != null) return true;

            if (!File.Exists(_path))
            {
                _missing = true;
                Log.Once("icon-missing-" + _path,
                         "Icon " + _path + " is not there. Everything still works; there is just " +
                         "no picture. Run tools/make_icons.py and deploy with -FreshData.");
                return false;
            }

            // Centred, because everything that positions an icon thinks in terms of where its
            // middle goes -- and because a rotation is about the sprite's own origin, so an
            // uncentred sprite swings around its top-left corner like a hinged sign.
            _sprite = new CustomSprite(_path, new SizeF(64f, 64f), new PointF(0f, 0f),
                                       Tint, 0f, true);
            return true;
        }
    }
}
