using System;
using System.Collections.Generic;
using GTA;
using GTA.Native;
using Fumes.Core;

namespace Fumes.UI
{
    /// <summary>One row of the button bar: a control to draw the glyph for, and what it does.</summary>
    internal struct ButtonPrompt
    {
        public Control Control;
        public string Label;

        public ButtonPrompt(Control control, string label)
        {
            Control = control;
            Label = label;
        }
    }

    /// <summary>
    /// The game's own instructional button bar, bottom right.
    ///
    /// This is the black strip the game puts up during every mission and minigame -- a real
    /// button glyph followed by what it does. It is a scaleform movie R* ships with the game
    /// (`instructional_buttons`), so using it costs no textures, no assets and nothing to
    /// install, and it looks exactly like the rest of the HUD because it IS the rest of the HUD.
    ///
    /// The glyph comes from GET_CONTROL_INSTRUCTIONAL_BUTTONS_STRING, which returns a token for
    /// whatever the CONTROL is bound to on whatever the player is currently holding. That is the
    /// whole reason to go through a control rather than draw a letter: the same call gives "E"
    /// on a keyboard and the right D-pad glyph on a pad, and it follows a rebind in the game's
    /// own settings without anybody telling it to.
    /// </summary>
    internal sealed class Buttons : IDisposable
    {
        private const string MovieName = "instructional_buttons";

        private Scaleform _movie;
        private bool _moaned;

        /// <summary>
        /// What was on the bar last frame.
        ///
        /// The scaleform is only re-populated when the prompts actually CHANGE. Rebuilding it
        /// every frame is a dozen scaleform method calls sixty times a second to draw the same
        /// two buttons, and it makes the bar flicker as it is cleared and refilled.
        /// </summary>
        private string _showing = "";

        /// <summary>Whether anything asked for the bar this frame. Reset by Render.</summary>
        private bool _wanted;

        private readonly List<ButtonPrompt> _pending = new List<ButtonPrompt>();

        /// <summary>Queues a button for this frame. Order is the order they appear.</summary>
        public void Show(Control control, string label)
        {
            if (string.IsNullOrEmpty(label)) return;

            _pending.Add(new ButtonPrompt(control, label));
            _wanted = true;
        }

        /// <summary>
        /// Draws whatever was queued, and nothing at all if nothing was.
        ///
        /// Called once per tick from the top-level script, AFTER everything that might want to
        /// put a button up. That ordering is what lets any subsystem call Show without knowing
        /// whether another one already has.
        /// </summary>
        public void Render()
        {
            if (!_wanted)
            {
                _pending.Clear();
                _showing = "";
                return;
            }

            _wanted = false;

            try
            {
                if (!Ready()) { _pending.Clear(); return; }

                var signature = Signature();
                if (signature != _showing)
                {
                    _showing = signature;
                    Populate();
                }

                _movie.Render2D();
            }
            catch (Exception ex)
            {
                Log.Once("buttons", "The button bar could not be drawn: " + ex.Message +
                                    " - prompts fall back to plain help text.");
                Failed = true;
            }
            finally
            {
                _pending.Clear();
            }
        }

        /// <summary>
        /// Set when the bar cannot be had at all.
        ///
        /// The caller watches this and puts its plain help text up instead. A prompt that does
        /// not appear is worse than an ugly prompt: the player has no way to know the mod wanted
        /// anything from them.
        /// </summary>
        public bool Failed { get; private set; }

        /// <summary>A cheap identity for the current set, so a rebuild only happens on a change.</summary>
        private string Signature()
        {
            var s = "";
            foreach (var b in _pending) s += (int)b.Control + ":" + b.Label + "|";
            return s;
        }

        /// <summary>When the movie was first asked for, so "not yet" can be told from "never".</summary>
        private int _askedAt;

        private bool Ready()
        {
            if (_movie != null && _movie.IsLoaded)
            {
                // It arrived. If we had already given up and switched to help text, take that
                // back -- otherwise one slow load at the start of a session means plain text
                // for the rest of it.
                Failed = false;
                return true;
            }

            if (_movie == null)
            {
                // THE CONSTRUCTOR, NOT Scaleform.RequestMovie. The factory arrived after 3.6.0;
                // the constructor requests the same movie and is in every build this runs on.
                _movie = new Scaleform(MovieName);
                _askedAt = Game.GameTime;
            }

            if (_movie == null) return false;
            if (_movie.IsLoaded) { Failed = false; return true; }

            // Give up relative to WHEN WE ASKED, not to the clock.
            //
            // This used to be "GameTime > 20000", which is true of every session more than
            // twenty seconds old -- so the very first request, on a mod loaded into a game
            // already running, was declared a failure in the same frame it was made and the
            // bar was never used again.
            if (!_moaned && Game.GameTime - _askedAt > 3000)
            {
                _moaned = true;
                Failed = true;
                Log.Warn("The " + MovieName + " scaleform has not loaded after three seconds. " +
                         "Prompts fall back to help text.");
            }

            return false;
        }

        private void Populate()
        {
            var handle = _movie.Handle;

            Call(handle, "CLEAR_ALL");

            // Without a container the slots are set but nothing lays them out, and the bar
            // draws empty -- which looks exactly like the movie failing to load.
            Call(handle, "CREATE_CONTAINER");

            for (var i = 0; i < _pending.Count; i++)
            {
                var b = _pending[i];

                Function.Call(Hash.BEGIN_SCALEFORM_MOVIE_METHOD, handle, "SET_DATA_SLOT");
                Function.Call(Hash.SCALEFORM_MOVIE_METHOD_ADD_PARAM_INT, i);
                Function.Call(Hash.SCALEFORM_MOVIE_METHOD_ADD_PARAM_PLAYER_NAME_STRING, Glyph(b.Control));
                Function.Call(Hash.SCALEFORM_MOVIE_METHOD_ADD_PARAM_PLAYER_NAME_STRING, b.Label);
                Function.Call(Hash.END_SCALEFORM_MOVIE_METHOD);
            }

            Function.Call(Hash.BEGIN_SCALEFORM_MOVIE_METHOD, handle, "DRAW_INSTRUCTIONAL_BUTTONS");
            Function.Call(Hash.SCALEFORM_MOVIE_METHOD_ADD_PARAM_INT, -1);
            Function.Call(Hash.END_SCALEFORM_MOVIE_METHOD);

            // A backing that is dark but not solid, so the bar sits on the scene the way the
            // game's own does rather than punching a black box into it.
            Function.Call(Hash.BEGIN_SCALEFORM_MOVIE_METHOD, handle, "SET_BACKGROUND_COLOUR");
            Function.Call(Hash.SCALEFORM_MOVIE_METHOD_ADD_PARAM_INT, 0);
            Function.Call(Hash.SCALEFORM_MOVIE_METHOD_ADD_PARAM_INT, 0);
            Function.Call(Hash.SCALEFORM_MOVIE_METHOD_ADD_PARAM_INT, 0);
            Function.Call(Hash.SCALEFORM_MOVIE_METHOD_ADD_PARAM_INT, 80);
            Function.Call(Hash.END_SCALEFORM_MOVIE_METHOD);
        }

        private static void Call(int handle, string method)
        {
            Function.Call(Hash.BEGIN_SCALEFORM_MOVIE_METHOD, handle, method);
            Function.Call(Hash.END_SCALEFORM_MOVIE_METHOD);
        }

        /// <summary>
        /// The token the bar turns into a picture of a button.
        ///
        /// The first argument is the control GROUP; 2 is the standard gameplay group, which is
        /// where every control this mod uses lives. Ask for the wrong group and you get a token
        /// for a button in a menu nobody is in.
        /// </summary>
        private static string Glyph(Control control)
        {
            try
            {
                return Function.Call<string>(Hash.GET_CONTROL_INSTRUCTIONAL_BUTTONS_STRING,
                                             2, (int)control, true) ?? "";
            }
            catch
            {
                return "";
            }
        }

        public void Dispose()
        {
            try
            {
                if (_movie != null) _movie.Dispose();
            }
            catch
            {
                // The movie goes with the session either way.
            }

            _movie = null;
            _showing = "";
        }
    }
}
