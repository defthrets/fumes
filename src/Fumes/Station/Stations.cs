using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using Fumes.Core;

namespace Fumes.Station
{
    /// <summary>One forecourt: a name to print, a place to blip, and what it charges.</summary>
    internal sealed class Forecourt
    {
        public string Name = "Gas Station";
        public string Brand = "RON";
        public Vector3 Position;
        public float PriceMultiplier = 1f;
        public Blip Blip;

        /// <summary>Whether this position came from a real pump rather than the shipped list.</summary>
        public bool Learned;

        /// <summary>
        /// Struck off: somebody stood on this coordinate and there was no pump anywhere near it.
        ///
        /// Kept in the list rather than deleted from it, so the shipped file can still be
        /// reloaded and so a tombstone can be written down. A removed station draws no blip and
        /// names no price.
        /// </summary>
        public bool Removed;

        /// <summary>Seconds spent standing on this coordinate with no pump in sight.</summary>
        public float Doubt;

        public string Title => Brand + " - " + Name;
    }

    /// <summary>
    /// The station list: blips on the map, and a name and a price for wherever you are.
    ///
    /// DELIBERATELY NOT LOAD-BEARING. Everything that makes refuelling work lives in Pumps,
    /// which asks the game where the pumps are. This is the cosmetic half -- the blip, the
    /// "RON - Sandy Shores" on the receipt, and the fact that fuel costs more up a mountain.
    /// A station missing from here still refuels, at the base price, with no blip.
    /// </summary>
    internal sealed class Stations
    {
        /// <summary>How near counts as being at a station, for naming and pricing.</summary>
        private const float AtStationMetres = 70f;

        private readonly List<Forecourt> _all = new List<Forecourt>();
        private readonly Settings _cfg;

        public Stations(Settings cfg)
        {
            _cfg = cfg;
            Load();
            LoadCorrections();
        }

        public int Count => _all.Count;

        private void Load()
        {
            var root = JsonFile.Read(Paths.StationsFile, out var how);

            if (how != ReadResult.Ok || root == null)
            {
                Log.Warn("No usable stations.json (" + how + "). No blips, and every pump " +
                         "charges the base price. Refuelling itself is unaffected.");
                return;
            }

            try
            {
                foreach (var node in root["stations"].Items)
                {
                    var f = new Forecourt
                    {
                        Name = node["name"].AsString("Gas Station"),
                        Brand = node["brand"].AsString("RON"),
                        Position = new Vector3(node["x"].AsFloat(0f),
                                               node["y"].AsFloat(0f),
                                               node["z"].AsFloat(0f)),
                        PriceMultiplier = node["price"].AsFloat(1f)
                    };

                    // A zero multiplier would mean free fuel forever from one typo.
                    if (f.PriceMultiplier <= 0.01f) f.PriceMultiplier = 1f;

                    _all.Add(f);
                }

                Log.Info("Loaded " + _all.Count + " station(s).");
            }
            catch (Exception ex)
            {
                Log.Error("stations.json is there but is not shaped as expected.", ex);
            }
        }

        /// <summary>When the blips are next worth checking on. See ShowBlips.</summary>
        private int _nextBlipCheck;

        /// <summary>How far a listed station may be from a real pump before it is moved onto it.</summary>
        private const float Tolerance = 12f;

        /// <summary>
        /// How far afield a pump will look for a station to move.
        ///
        /// EIGHTY METRES, AND THE NUMBER IS NOT A FEELING. The two closest stations in the
        /// shipped list -- Route 68 West and Route 68 East -- are 168m apart, and Grove Street
        /// and Davis Avenue are 181m. A claim radius has to stay under HALF the closest pair,
        /// or a pump sitting between two stations can be claimed by the wrong one, and both of
        /// them can end up snapped onto the same forecourt. Half of 168 is 84.
        ///
        /// The first draft of this used 250m, which would have done exactly that.
        ///
        /// If stations are ever added to the data file closer together than about 170m, this
        /// number has to come down with them.
        /// </summary>
        private const float Claim = 80f;

        /// <summary>
        /// Near enough that a pump is presumed to belong to a listed station we simply cannot
        /// move -- so it is reported rather than duplicated.
        ///
        /// The band between Claim and this is the honest gap: a pump 80-250m from a listed
        /// station either means that station's coordinate is badly wrong, or means there is a
        /// second forecourt there that nothing has listed. Nothing in the position alone can
        /// tell those apart, so the mod does neither and says so in the log instead.
        /// </summary>
        private const float Quiet = 250f;

        private bool _correctionsDirty;

        /// <summary>Corrections could not be read, so they must not be overwritten either.</summary>
        private bool _correctionsUnreadable;

        /// <summary>
        /// Puts the map blips down. Safe to call every tick; it does the work once.
        ///
        /// Throttled because "is it still there" is a native call PER STATION, and there are
        /// twenty-six of them -- a per-frame loop over that is a lot of nothing. They are
        /// re-checked rather than set once because another mod, or a session reload, can
        /// take a blip away underneath us.
        /// </summary>
        public void ShowBlips()
        {
            if (!_cfg.ShowBlips) return;

            if (Game.GameTime < _nextBlipCheck) return;
            _nextBlipCheck = Game.GameTime + 5000;

            foreach (var f in _all)
            {
                if (f.Removed) continue;
                if (f.Blip != null && f.Blip.Exists()) continue;

                try
                {
                    var b = World.CreateBlip(f.Position);
                    b.Sprite = BlipSprite.JerryCan;
                    b.Color = BlipColor.White;
                    b.IsShortRange = true;
                    b.Scale = 0.8f;
                    b.Name = f.Title;
                    f.Blip = b;
                }
                catch (Exception ex)
                {
                    Log.Once("blip-fail", "Could not blip " + f.Title + ": " + ex.Message);
                }
            }
        }

        public void RemoveBlips()
        {
            foreach (var f in _all)
            {
                try { if (f.Blip != null && f.Blip.Exists()) f.Blip.Delete(); }
                catch { /* the map is not worth an exception on shutdown */ }
                f.Blip = null;
            }
        }

        /// <summary>Which forecourt a position belongs to, or null out in the world.</summary>
        public Forecourt At(Vector3 position)
        {
            Forecourt best = null;
            var bestDist = AtStationMetres;

            foreach (var f in _all)
            {
                if (f.Removed) continue;

                var d = f.Position.DistanceTo(position);
                if (d >= bestDist) continue;

                best = f;
                bestDist = d;
            }

            return best;
        }

        // ==================================================================
        // Correcting itself
        // ==================================================================

        /// <summary>
        /// Moves the nearest listed station onto a pump that has actually been seen, or adds
        /// one where nothing is listed at all.
        ///
        /// Returns true when something changed, so the caller can put the blips down again.
        /// </summary>
        public bool Learn(Vector3 pump)
        {
            if (!_cfg.LearnStations) return false;

            try
            {
                var best = Nearest(pump, Claim);
                var bestDist = best == null ? float.MaxValue : best.Position.DistanceTo(pump);

                if (best == null)
                {
                    // Nothing close enough to move. Before inventing a station, check whether
                    // there is one just outside claiming range -- because that is far more
                    // likely to be a badly written-down coordinate than a second forecourt, and
                    // adding one would leave the map showing two.
                    var stray = Nearest(pump, Quiet);

                    if (stray != null)
                    {
                        Log.Once("stray-" + stray.Title,
                                 stray.Title + " is " + stray.Position.DistanceTo(pump).ToString("0") +
                                 "m from a real pump -- too far to move safely, since stations in " +
                                 "the list come as close as 168m to each other. Its coordinate in " +
                                 "stations.json is probably wrong. Nothing has been changed.");
                        return false;
                    }

                    // Genuinely nothing listed anywhere near: a station the shipped list does
                    // not know about, which is exactly what a map mod produces. The brand is
                    // unknowable, so it is not invented.
                    var place = Zone(pump);

                    _all.Add(new Forecourt
                    {
                        Name = place,
                        Brand = "Fuel",
                        Position = pump,
                        PriceMultiplier = 1f,
                        Learned = true
                    });

                    Log.Info("Found a station nothing had listed, at " + place + ".");
                    _correctionsDirty = true;
                    return true;
                }

                if (bestDist <= Tolerance) return false;

                Log.Info(best.Title + " was " + bestDist.ToString("0") +
                         "m from its pumps; moved onto them.");

                best.Position = pump;
                best.Learned = true;
                _correctionsDirty = true;
                return true;
            }
            catch (Exception ex)
            {
                Log.Once("learn", "Could not correct a station: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// The closest station to a point within a radius, or null.
        ///
        /// Public because the forecourt traffic needs the same question asked from further out:
        /// At() is fixed to the "am I standing at a pump" distance, and this one takes the range
        /// as an argument. It was already here and already private -- writing a second copy of
        /// it was the wrong instinct, and the compiler said so.
        /// </summary>
        public Forecourt Nearest(Vector3 to, float radius)
        {
            Forecourt best = null;
            var bestDist = radius;

            foreach (var f in _all)
            {
                if (f.Removed) continue;

                var d = f.Position.DistanceTo(to);
                if (d >= bestDist) continue;

                best = f;
                bestDist = d;
            }

            return best;
        }

        /// <summary>
        /// How near you have to be to a station's coordinate for your not finding a pump there
        /// to mean anything.
        /// </summary>
        private const float Judge = 45f;

        /// <summary>
        /// Seconds of standing that near with no pump before the station is struck off.
        ///
        /// Not one sighting. Objects stream, and a single sweep taken in the wrong second can
        /// come back empty on a forecourt that is really there -- which would delete a good
        /// station on the strength of one unlucky frame. Several seconds of continuous nothing
        /// is not a streaming hiccup.
        /// </summary>
        private const float Condemn = 5f;

        /// <summary>
        /// Strikes off a station you are standing on that has no pump anywhere near it.
        ///
        /// The mirror of Learn, and needed for the same reason: the shipped list was written by
        /// hand, and a hand-written list has entries that are simply not real -- one of them
        /// put a petrol station on a residential stop sign in Davis. Learn can move a station
        /// that is nearly right; nothing but this can get rid of one that was never there.
        ///
        /// Returns true when something changed.
        /// </summary>
        public bool Doubt(Vector3 standingAt, bool sawPump, Vector3 pumpAt, float dt)
        {
            if (!_cfg.LearnStations) return false;

            var changed = false;

            foreach (var f in _all)
            {
                if (f.Removed) continue;

                if (f.Position.DistanceTo(standingAt) > Judge) { f.Doubt = 0f; continue; }

                // A pump near THIS station clears it, even if the sweep found it while you were
                // nearer a different one.
                if (sawPump && pumpAt.DistanceTo(f.Position) <= Claim) { f.Doubt = 0f; continue; }

                f.Doubt += dt;
                if (f.Doubt < Condemn) continue;

                f.Removed = true;
                f.Doubt = 0f;
                _correctionsDirty = true;
                changed = true;

                Log.Info("Struck off " + f.Title + ": stood on its coordinate for " +
                         Condemn.ToString("0") + "s and there is no pump within " +
                         Claim.ToString("0") + "m. It is not a real station.");

                Notify("~y~" + f.Title + "~s~ is not a real station - taken off the map.");
            }

            return changed;
        }

        private static void Notify(string text)
        {
            try { GTA.UI.Notification.PostTicker(text, false, false); }
            catch { /* not worth a crash */ }
        }

        private static string Zone(Vector3 at)
        {
            try
            {
                var name = World.GetZoneLocalizedName(at);
                return string.IsNullOrEmpty(name) ? "Gas Station" : name;
            }
            catch
            {
                return "Gas Station";
            }
        }

        /// <summary>Takes the blips down so the next ShowBlips puts them back in the right place.</summary>
        public void Reblip()
        {
            RemoveBlips();
            _nextBlipCheck = 0;
        }

        private void LoadCorrections()
        {
            var root = JsonFile.Read(Paths.StationsLocalFile, out var how);

            if (how == ReadResult.Missing) return;

            if (how != ReadResult.Ok || root == null)
            {
                // Deliberately NOT starting empty. A file that is there but unreadable is a
                // file with somebody's corrections in it, and rewriting it would throw them
                // away. Leave it for a human and do not touch it this session.
                Log.Error("stations.local.json is there but could not be read - leaving it alone. " +
                          "Learned positions will not be saved this session.");
                _correctionsUnreadable = true;
                return;
            }

            try
            {
                var applied = 0;

                foreach (var node in root["stations"].Items)
                {
                    var brand = node["brand"].AsString("");
                    var name = node["name"].AsString("");
                    var at = new Vector3(node["x"].AsFloat(0f), node["y"].AsFloat(0f), node["z"].AsFloat(0f));

                    var existing = _all.Find(f =>
                        string.Equals(f.Brand, brand, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

                    if (existing != null)
                    {
                        existing.Position = at;
                        existing.Learned = true;
                    }
                    else
                    {
                        _all.Add(new Forecourt
                        {
                            Brand = string.IsNullOrEmpty(brand) ? "Fuel" : brand,
                            Name = string.IsNullOrEmpty(name) ? "Gas Station" : name,
                            Position = at,
                            PriceMultiplier = node["price"].AsFloat(1f),
                            Learned = true
                        });
                    }

                    applied++;
                }

                if (applied > 0) Log.Info("Applied " + applied + " corrected station position(s).");

                var struck = 0;

                foreach (var node in root["removed"].Items)
                {
                    var brand = node["brand"].AsString("");
                    var name = node["name"].AsString("");

                    var gone = _all.Find(f =>
                        string.Equals(f.Brand, brand, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

                    if (gone == null) continue;

                    gone.Removed = true;
                    struck++;
                }

                if (struck > 0) Log.Info("Kept " + struck + " station(s) off the map.");
            }
            catch (Exception ex)
            {
                Log.Error("stations.local.json is not shaped as expected.", ex);
                _correctionsUnreadable = true;
            }
        }

        /// <summary>Writes the corrections. Safe to call when there are none.</summary>
        public void SaveCorrections()
        {
            if (!_correctionsDirty || _correctionsUnreadable) return;

            try
            {
                var list = Json.Array();

                foreach (var f in _all)
                {
                    if (!f.Learned) continue;

                    list.Add(Json.Object()
                        .Set("brand", f.Brand)
                        .Set("name", f.Name)
                        .Set("x", Math.Round(f.Position.X, 2))
                        .Set("y", Math.Round(f.Position.Y, 2))
                        .Set("z", Math.Round(f.Position.Z, 2))
                        .Set("price", f.PriceMultiplier));
                }

                var gone = Json.Array();

                foreach (var f in _all)
                {
                    if (!f.Removed) continue;
                    gone.Add(Json.Object().Set("brand", f.Brand).Set("name", f.Name));
                }

                var root = Json.Object()
                    .Set("_readme", "Station positions this install worked out from real pumps, and " +
                                    "stations it went to and found were not there. Delete this file to " +
                                    "go back to the shipped list exactly as it ships.")
                    .Set("version", Build.Version)
                    .Set("stations", list)
                    .Set("removed", gone);

                if (JsonFile.Write(Paths.StationsLocalFile, root)) _correctionsDirty = false;
            }
            catch (Exception ex)
            {
                Log.Error("Could not save corrected station positions.", ex);
            }
        }

        /// <summary>
        /// What a litre costs right here.
        ///
        /// The per-station multiplier from the data file, then the ini variance on top so two
        /// runs of the same station are not identical. The variance is seeded from the station
        /// position rather than from a clock, so a price does not change while you are standing
        /// at the pump watching it -- which is the one thing that would look like a bug.
        /// </summary>
        public float PriceAt(Vector3 position, out Forecourt where)
        {
            where = At(position);

            var price = _cfg.PricePerLitre;
            if (where != null) price *= where.PriceMultiplier;

            if (_cfg.PriceVariance > 0f)
            {
                var seed = where != null
                    ? (int)(where.Position.X * 7f + where.Position.Y * 13f)
                    : (int)(position.X * 7f + position.Y * 13f);

                // A cheap deterministic hash into -1..1.
                var wobble = (float)(Math.Sin(seed * 12.9898) * 43758.5453);
                wobble -= (float)Math.Floor(wobble);
                price *= 1f + (wobble * 2f - 1f) * _cfg.PriceVariance;
            }

            return price < 0.01f ? 0.01f : price;
        }
    }
}
