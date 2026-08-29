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
                var d = f.Position.DistanceTo(position);
                if (d >= bestDist) continue;

                best = f;
                bestDist = d;
            }

            return best;
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
