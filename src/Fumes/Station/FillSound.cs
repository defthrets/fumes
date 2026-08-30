using System;
using GTA;
using GTA.Native;
using Fumes.Core;

namespace Fumes.Station
{
    /// <summary>
    /// The sound of fuel going in, for as long as it is going in.
    ///
    /// THE HARD PART IS NOT PLAYING A SOUND, IT IS KNOWING WHETHER ONE PLAYED.
    ///
    /// PLAY_SOUND_FROM_ENTITY takes a sound name and a sound set as STRINGS, looked up in the
    /// game's audio metadata at runtime. A name that does not exist is not an error: the call
    /// returns nothing, no exception is thrown, and the result is silence that looks exactly
    /// like a volume problem, an occlusion problem or a mod conflict. There is no native that
    /// lists the valid names and no file on this machine that holds them -- I looked, including
    /// through every other script installed here, for a corpus of real ones. There was none.
    ///
    /// So the names below are CANDIDATES, not knowledge, and they are treated that way. The
    /// probe is HAS_SOUND_FINISHED: a looping sound that is really playing does not finish, so
    /// one that reports finished half a second after being started never started at all. That
    /// candidate is struck off and the next is tried, and the one that survives is named in the
    /// log so it can become the default.
    ///
    /// This is the same shape as the animation clip list and the rope probe, and for the same
    /// reason: where the game will not tell you in advance, ask it afterwards. Guessing once
    /// and shipping it is what put a crash in the rope cycler.
    /// </summary>
    internal sealed class FillSound
    {
        private readonly Settings _cfg;

        /// <summary>The live sound's id, or -1. Not a handle to an entity -- an audio slot.</summary>
        private int _id = -1;

        /// <summary>Which candidate is being tried, and when it started.</summary>
        private int _candidate = -1;
        private int _startedAt;

        /// <summary>Set once a candidate has survived the probe, so it stops being probed.</summary>
        private bool _proven;

        /// <summary>Set when every candidate has failed, so nothing keeps retrying forever.</summary>
        private bool _hopeless;

        private bool _wanted;

        public FillSound(Settings cfg)
        {
            _cfg = cfg;
        }

        /// <summary>
        /// Candidate (sound, set, audio bank) triples, best guess first.
        ///
        /// The bank is requested before playing where one is named, because a sound whose bank
        /// is not loaded behaves exactly like a sound that does not exist -- which would make
        /// the probe strike off a name that was fine.
        /// </summary>
        private static readonly string[][] Candidates =
        {
            // A pump filling a tank. If any of these is right it is most likely one of the
            // first two; the rest are liquid-adjacent loops that would at least read as flow.
            new[] { "Fuel_Pump_Loop", "PETROL_PUMP_SOUNDS", null },
            new[] { "Pouring_Loop", "WEAPONS_PLAYER_JERRYCAN", "WEAPONS_PLAYER_JERRYCAN" },
            new[] { "Water_Loop", "CAR_WASH_SOUNDS", null },
            new[] { "Liquid_Pour", "OIL_RIG_SOUNDS", null },
            new[] { "Fire_Extinguisher_Loop", "WEAPONS_PLAYER_FIRE_EXTINGUISHER", null },
        };

        /// <summary>
        /// Called every frame with whether fuel is currently flowing.
        ///
        /// Driven by a boolean rather than by Start and Stop calls on purpose. Filling ends in
        /// six different places -- tank full, hose stretched, pump destroyed, player walks off,
        /// vehicle removed, script reloaded -- and a Stop call missing from any one of them is
        /// a sound that plays until the game is closed. A flag checked every tick cannot have
        /// that bug.
        /// </summary>
        public void Update(bool flowing, Ped at)
        {
            if (!_cfg.FillSound) { Silence(); return; }

            if (flowing != _wanted)
            {
                _wanted = flowing;

                if (flowing) Begin(at);
                else Silence();

                return;
            }

            if (!flowing || _proven || _hopeless || _id < 0) return;

            Probe(at);
        }

        /// <summary>
        /// Half a second after starting, ask whether it is still going.
        ///
        /// A loop that has already finished did not start. Half a second because the sound is
        /// started on the same frame the pump display appears, and audio does not necessarily
        /// report as playing on the very next tick.
        /// </summary>
        private void Probe(Ped at)
        {
            if (Game.GameTime - _startedAt < 500) return;

            bool finished;
            try { finished = Function.Call<bool>(Hash.HAS_SOUND_FINISHED, _id); }
            catch { finished = true; }

            if (!finished)
            {
                _proven = true;

                Log.Info("Fill sound: \"" + Candidates[_candidate][0] + "\" from \"" +
                         Candidates[_candidate][1] + "\" is playing. Put those in [Nozzle] " +
                         "FillSoundName and FillSoundSet to skip the search next time.");
                return;
            }

            Log.Info("Fill sound: \"" + Candidates[_candidate][0] + "\" from \"" +
                     Candidates[_candidate][1] + "\" finished immediately, so it is not a " +
                     "sound this game has. Trying the next.");

            Release();
            Play(at, _candidate + 1);
        }

        private void Begin(Ped at)
        {
            _proven = false;

            // A name set by hand in the ini is taken on trust and never probed -- if somebody
            // has written one in, they know something this code does not.
            if (!string.IsNullOrEmpty(_cfg.FillSoundName) && !string.IsNullOrEmpty(_cfg.FillSoundSet))
            {
                _proven = true;
                Play(at, -1);
                return;
            }

            if (_hopeless) return;

            Play(at, 0);
        }

        /// <summary>Starts candidate n, or the configured pair when n is negative.</summary>
        private void Play(Ped at, int n)
        {
            string name, set, bank;

            if (n < 0)
            {
                name = _cfg.FillSoundName;
                set = _cfg.FillSoundSet;
                bank = null;
            }
            else
            {
                if (n >= Candidates.Length)
                {
                    _hopeless = true;
                    Log.Warn("Fill sound: none of the " + Candidates.Length + " candidates is a " +
                             "sound this game has, so refuelling stays quiet. Set [Nozzle] " +
                             "FillSoundName and FillSoundSet if you know one, or FillSound = " +
                             "false to stop it looking.");
                    return;
                }

                name = Candidates[n][0];
                set = Candidates[n][1];
                bank = Candidates[n][2];
            }

            _candidate = n;

            try
            {
                if (!string.IsNullOrEmpty(bank))
                {
                    Function.Call(Hash.REQUEST_SCRIPT_AUDIO_BANK, bank, false, -1);
                }

                _id = Function.Call<int>(Hash.GET_SOUND_ID);

                if (_id < 0)
                {
                    Log.Once("fillsound-id", "The game would not give out a sound id, so " +
                                             "refuelling stays quiet.");
                    return;
                }

                // From the PED rather than from the pump or the car: he is the one thing that is
                // certainly there, certainly near the filler, and certainly still alive at the
                // moment the sound has to stop.
                Function.Call(Hash.PLAY_SOUND_FROM_ENTITY, _id, name,
                              at == null ? 0 : at.Handle, set, false, 0);

                _startedAt = Game.GameTime;
            }
            catch (Exception ex)
            {
                Log.Once("fillsound-play", "Could not start the fill sound: " + ex.Message);
                Release();
            }
        }

        /// <summary>Stops whatever is playing. Safe to call when nothing is.</summary>
        public void Silence()
        {
            if (_id < 0) return;

            try { Function.Call(Hash.STOP_SOUND, _id); }
            catch { /* it is stopping either way */ }

            Release();
            _wanted = false;
        }

        /// <summary>
        /// Hands the audio slot back.
        ///
        /// SEPARATE FROM STOPPING, and it matters: sound ids are a small pool, and one that is
        /// stopped but never released is leaked. Refuelling happens dozens of times a session
        /// and a leak per fill would eventually take the game's own audio down with it.
        /// </summary>
        private void Release()
        {
            if (_id < 0) return;

            try { Function.Call(Hash.RELEASE_SOUND_ID, _id); }
            catch { /* nothing else to try */ }

            _id = -1;
        }
    }
}
