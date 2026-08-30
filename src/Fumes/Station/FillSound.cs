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
    /// lists the valid names and nothing on this machine holds them either -- not the game
    /// folders, not the strings of any other script installed in them.
    ///
    /// The names in Candidates ARE real: they come from a dump of the game's own audio
    /// metadata. But real is not the same as working -- a DLC sound needs its bank loaded, and
    /// a one-shot will not loop for the length of a fill -- so every one is still probed.
    ///
    /// The probe is HAS_SOUND_FINISHED: a looping sound that is really playing does not finish,
    /// so one that reports finished half a second after being started never started at all.
    /// That candidate is struck off and the next is tried, and the one that survives is named
    /// in the log so it can become the default.
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
        /// Candidate (sound, set, audio bank) triples, best first.
        ///
        /// THESE ARE REAL NAMES NOW, not invented ones. The first five were guesses and all
        /// five failed the probe in two and a half seconds -- which is what the probe is for,
        /// but a guess with a 0-for-5 record is not worth another round. These come out of
        /// DurtyFree's gta-v-data-dumps soundNames.json, 2204 sounds across 501 sets pulled
        /// from the game's own audio metadata, filtered for anything that flows.
        ///
        /// The game has no fuel-pump sound, because vanilla GTA has no refuelling. So the list
        /// is things that ARE a liquid or gas under pressure and happen to be needed elsewhere:
        /// a car wash spray, a cutting torch, a meter filling. Ordered by how much each one
        /// sounds like a hose running rather than by how close its name is.
        ///
        /// Every one is still probed. A name being real is not the same as its bank being
        /// loaded, and being loaded is not the same as it looping.
        /// </summary>
        private static readonly string[][] Candidates =
        {
            // A meter filling. Tried first because collect_water, which played perfectly
            // well, sounded like water being collected -- which is the trouble with borrowing
            // sounds: the probe can tell you a sound is REAL and playing, and cannot tell you
            // it suits. That part only you can hear.
            new[] { "Meter_Fill_Loop", "DLC_IE_Tail_Vehicle_Sounds", "DLC_IE_Tail_Vehicle_Sounds" },

            // A car wash spraying a car: pressurised liquid, loops for as long as the wash
            // runs, and it is a base-game set with no DLC bank behind it.
            new[] { "SPRAY", "CARWASH_SOUNDS", null },
            new[] { "SPRAY_CAR", "CARWASH_SOUNDS", null },

            // A cutting torch: a continuous gas hiss, which is most of what a pump running
            // actually sounds like from arm's length.
            new[] { "Blowtorch_Loop", "DLC_H4_Underwater_Blowtorch_Sounds",
                    "DLC_H4_Underwater_Blowtorch_Sounds" },

            // Real and it works; it just sounds like water.
            new[] { "collect_water", "dlc_sum20_yacht_missions_ah_sounds",
                    "dlc_sum20_yacht_missions_ah_sounds" },

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

            var name = _candidate < 0 ? _cfg.FillSoundName : Candidates[_candidate][0];
            var set = _candidate < 0 ? _cfg.FillSoundSet : Candidates[_candidate][1];

            if (!finished)
            {
                _proven = true;

                Log.Info("Fill sound: \"" + name + "\" from \"" + set + "\" is playing." +
                         (_candidate < 0
                              ? ""
                              : "  Put those in [Nozzle] FillSoundName and FillSoundSet to "
                                + "skip the search next time."));
                return;
            }

            Release();

            if (_candidate < 0)
            {
                // Asked for by name and it did not play. NOT falling through to the
                // candidate list: quietly substituting a different sound would look like
                // the chosen one working badly rather than not working at all.
                _hopeless = true;

                Log.Warn("Fill sound: \"" + name + "\" from \"" + set + "\" finished the "
                         + "instant it started, so it is not playing. Check the spelling, and "
                         + "whether it needs FillSoundBank set. Clear FillSoundName to go "
                         + "back to the built-in list.");
                return;
            }

            Log.Info("Fill sound: \"" + name + "\" from \"" + set + "\" finished "
                     + "immediately, so it is not a sound this game has. Trying the next.");

            Play(at, _candidate + 1);
        }

        private void Begin(Ped at)
        {
            _proven = false;

            // A name set by hand in the ini is used exactly as given, and PROBED ALL THE SAME.
            //
            // It used to be taken on trust, on the grounds that anyone typing one in knows
            // something this code does not. That was wrong in the one way that matters here:
            // the failure mode is silence, and silence is also what a working sound looks like
            // with the volume down. Trusting it meant a typo produced no sound and no
            // explanation either. The name is not overridden -- it is checked and reported.
            if (!string.IsNullOrEmpty(_cfg.FillSoundName) && !string.IsNullOrEmpty(_cfg.FillSoundSet))
            {
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
                bank = _cfg.FillSoundBank;
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
