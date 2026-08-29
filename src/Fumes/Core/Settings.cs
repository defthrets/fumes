using System;
using System.Collections.Generic;
using System.Windows.Forms;
using GTA;

namespace Fumes.Core
{
    /// <summary>How a volume is written on screen.</summary>
    internal enum Units
    {
        Litres,
        Gallons
    }

    /// <summary>How the hose between pump and nozzle is drawn.</summary>
    internal enum HoseMode
    {
        /// <summary>
        /// A real physics rope, but painted by us instead of wearing its own texture.
        ///
        /// The best of both, and the default. GTA has no way to TINT a rope -- the colour comes
        /// from whichever of nine authored textures the rope type picks, and the nearest thing
        /// to a fuel line among them is still a beige mooring rope. So the rope is created as
        /// the darkest, thinnest type there is, and then a thick black line is drawn ALONG ITS
        /// OWN VERTICES. The shape, the swing and the sag are the rope solver's work; only the
        /// colour is ours, and the thin dark wire underneath vanishes inside the line.
        /// </summary>
        Painted,

        /// <summary>A real rope wearing its own texture. Falls back to Line if ropes fail.</summary>
        Auto,

        /// <summary>Insist on the rope and its texture, with no fallback.</summary>
        Rope,

        /// <summary>No rope at all: a drawn catenary. Cheapest, and it cannot fail.</summary>
        Line,

        None
    }

    /// <summary>What the player's hands do while the nozzle is out.</summary>
    internal enum NozzlePose
    {
        /// <summary>
        /// Hold an INVISIBLE FIRE EXTINGUISHER. The default.
        ///
        /// The game owns a complete hold, walk, run and idle set for a man carrying one of
        /// these, which is the shape we want. But the real reason to prefer it over the petrol
        /// can is what it leaves room for: an extinguisher is a weapon that SPRAYS. It brings
        /// an aiming camera, a reticle and a trigger, none of which a prop can provide, and
        /// that is exactly the seam Overspray paints through. If fuel is ever to come out of
        /// this nozzle under player control, this is what it will be built on.
        ///
        /// Nothing sprays yet -- Refuel holds the trigger shut while the nozzle is out.
        /// </summary>
        FireExtinguisher,

        /// <summary>An invisible petrol can instead. A one-handed carry with no trigger.</summary>
        PetrolCan,

        /// <summary>Prop in hand, no pose. The arm hangs. Always works.</summary>
        None
    }

    /// <summary>
    /// Everything tunable, read once at start-up from Fumes.ini.
    ///
    /// Every value has a code default, so a missing or half-written ini degrades to something
    /// playable rather than to a divide by zero at 60fps.
    /// </summary>
    internal sealed class Settings
    {
        // ---- general ----------------------------------------------------------
        public bool Enabled = true;
        public LogLevel LogLevel = LogLevel.Info;
        public bool AnnounceOnLoad = true;

        // ---- what burns fuel --------------------------------------------------
        /// <summary>
        /// Multiplies the whole burn model.
        ///
        /// 1.0 is tuned so an ordinary saloon runs its tank down in roughly half an hour of
        /// real driving. Larger = thirstier. See Consumption for why the litres-per-100km
        /// figures below do NOT produce realistic ranges on their own: the map is about a
        /// tenth of real scale, so a realistic engine on a realistic tank would take a whole
        /// evening to need filling once.
        /// </summary>
        public float ConsumptionMultiplier = 1.0f;

        /// <summary>Litres burnt per hour sitting still with the engine running, before the multiplier.</summary>
        public float IdleLitresPerHour = 1.4f;

        public bool AffectBoats = true;
        public bool AffectAircraft = false;

        /// <summary>
        /// Whether ambient traffic burns fuel too.
        ///
        /// Off by default and it should probably stay off: the payoff is cars you will never
        /// look at stalling out of sight, and the cost is a per-frame pass over every vehicle
        /// in the world plus a save file that grows forever.
        /// </summary>
        public bool AffectTraffic = false;

        /// <summary>Fuel a vehicle is found with, as a fraction of its tank. Player-owned cars start full.</summary>
        public float FoundFuelMin = 0.18f;
        public float FoundFuelMax = 0.85f;

        /// <summary>Below this fraction of the tank, the low-fuel warning starts.</summary>
        public float ReserveFraction = 0.12f;

        /// <summary>Whether a shot petrol tank drains onto the road. See Consumption.Leak.</summary>
        public bool TankLeaks = true;

        // ---- running dry ------------------------------------------------------
        /// <summary>Litres left at which the engine starts coughing.</summary>
        public float SputterLitres = 0.6f;
        public bool StallWhenEmpty = true;

        /// <summary>Seconds of grinding starter before a dry engine gives up again.</summary>
        public float DryRestartSeconds = 1.6f;

        // ---- the forecourt ----------------------------------------------------
        public float PumpReach = 2.6f;
        public float CapReach = 1.9f;

        /// <summary>
        /// How close you must be to the pump to hang the nozzle back on it.
        ///
        /// MUCH TIGHTER THAN PumpReach, and they are separate numbers for a reason that only
        /// shows up in play: you park right next to the pump, so the filler is almost always
        /// inside PumpReach too. Sharing one radius meant the hang-up prompt sat on top of the
        /// fill prompt at every station and filling up was a fight.
        /// </summary>
        public float HangUpReach = 1.2f;
        public float LitresPerSecond = 2.2f;
        public float PricePerLitre = 1.55f;

        /// <summary>How far a station price may wander from the base, either way. 0 disables it.</summary>
        public float PriceVariance = 0.18f;

        public bool ShowBlips = true;
        public bool ChargeMoney = true;

        // ---- the nozzle and its hose -----------------------------------------
        public Keys InteractKey = Keys.E;
        public NozzlePose Pose = NozzlePose.FireExtinguisher;
        public HoseMode Hose = HoseMode.Painted;

        /// <summary>How far the nozzle reaches from its pump before it is pulled out of your hand.</summary>
        public float HoseMaxMetres = 9.0f;

        /// <summary>Where the hose starts complaining, as a fraction of the maximum.</summary>
        public float HoseWarnFraction = 0.8f;

        /// <summary>
        /// GTA rope type, 0-8. Different authored textures and thicknesses; nothing else
        /// changes, and none of them can be tinted.
        ///
        /// 5 is the thin metal wire -- dark and slim, which is why Painted mode uses it: the
        /// little of it that shows through the drawn hose reads as shadow rather than as beige
        /// rope. 1 is the tan mooring rope, which is what Rope and Auto modes look like.
        /// </summary>
        public int HoseRopeType = 5;

        /// <summary>The colour of a painted hose. NOT a tint on the rope -- see HoseMode.Painted.</summary>
        public int HoseRed = 16;
        public int HoseGreen = 16;
        public int HoseBlue = 18;

        /// <summary>How thick a painted hose is drawn, in metres across.</summary>
        public float HoseThickness = 0.05f;

        /// <summary>How much slack the hose carries, as a multiple of the straight-line distance.</summary>
        public float HoseSag = 1.22f;

        /// <summary>Whether over-stretching yanks the nozzle out of your hand, or merely stops you.</summary>
        public bool HoseSnaps = true;

        /// <summary>
        /// Where the nozzle sits in the hand, and which way round it points.
        ///
        /// PH_R_Hand wants no offset at all for props authored around it -- a spray can, a
        /// phone -- but prop_cs_fuel_nozle is not one of those. It is a scene prop whose origin
        /// is not its grip, so left at zero it hangs out of the fist like a dropped knife.
        /// These six numbers put it right, and they live in the ini rather than the code
        /// because the only way to find them is to look at it: turn TuneNozzle on and dial
        /// them in with the game running.
        ///
        /// Offsets are metres in the bone's own space; rotations are degrees.
        /// </summary>
        public float NozzleOffsetX = 0.055f;
        public float NozzleOffsetY = 0.02f;
        public float NozzleOffsetZ = 0.0f;
        public float NozzleRotX = 0f;
        public float NozzleRotY = 90f;
        public float NozzleRotZ = 0f;

        /// <summary>
        /// In-game tuning for the six numbers above.
        ///
        /// NumPad5 cycles the axis, NumPad4 and NumPad6 move it, NumPad0 writes the whole set
        /// to the log in ini form. Off by default: it is a workbench, not a feature.
        /// </summary>
        public bool TuneNozzle = false;

        /// <summary>Whether a marker is drawn on the vehicle's filler while you carry the nozzle.</summary>
        public bool ShowFillerMarker = false;

        /// <summary>
        /// Where on the pump the hose is bolted, in the pump's own local space.
        ///
        /// Exposed because the six pump models in the game are not the same shape, and a
        /// number that hangs the hose off the right-hand side of a modern pump hangs it off
        /// thin air on the vintage one. Y is forward, Z is up, in metres.
        /// </summary>
        public float HoseAnchorX = 0.30f;
        public float HoseAnchorY = 0.08f;

        /// <summary>
        /// Height up the pump, and it wants to be near the TOP.
        ///
        /// A real nozzle hangs in a holster at the top of the machine, roughly shoulder height,
        /// not out of its middle -- and a hose leaving at waist height reads as coming out of
        /// the payment panel. The base-game pumps are a little under two metres, so this sits
        /// just below the light box on the lid.
        /// </summary>
        public float HoseAnchorZ = 1.52f;

        // ---- HUD --------------------------------------------------------------
        public bool ShowGauge = true;
        public bool GaugeOnlyInVehicle = true;
        /// <summary>
        /// The gauge, sitting under the minimap.
        ///
        /// STATIC NUMBERS, and deliberately so. Where the minimap actually lands depends on the
        /// player's safe-zone setting and their aspect ratio, and the natives that would let it
        /// be computed exactly are easy to get subtly wrong in a way that puts the gauge
        /// somewhere random rather than somewhere obviously broken. A number in an ini that
        /// anybody can nudge by 0.01 while looking at it beats a formula nobody can check.
        /// These are measured off a real screenshot at this machine's aspect ratio.
        /// </summary>
        public float GaugeX = 0.1425f;
        public float GaugeY = 0.9775f;
        public float GaugeWidth = 0.128f;
        public float GaugeHeight = 0.0195f;
        public Units Units = Units.Litres;
        public bool ShowNumbers = true;

        // ---- hazards ----------------------------------------------------------
        /// <summary>Shooting on a forecourt while the nozzle is out ends the way you would expect.</summary>
        public bool ForecourtHazard = true;

        /// <summary>
        /// Litres per 100km by vehicle class, before the multiplier and before the map-scale
        /// constant in Consumption. Keyed by the GTA.VehicleClass name, so the ini names them
        /// the same way the game does.
        /// </summary>
        public readonly Dictionary<VehicleClass, float> Thirst = new Dictionary<VehicleClass, float>
        {
            { VehicleClass.Compacts,        7.5f },
            { VehicleClass.Sedans,          9.0f },
            { VehicleClass.SUVs,           13.5f },
            { VehicleClass.Coupes,         10.5f },
            { VehicleClass.Muscle,         16.5f },
            { VehicleClass.SportsClassics, 14.0f },
            { VehicleClass.Sports,         13.0f },
            { VehicleClass.Super,          20.0f },
            { VehicleClass.Motorcycles,     4.5f },
            { VehicleClass.OffRoad,        16.0f },
            { VehicleClass.Industrial,     30.0f },
            { VehicleClass.Utility,        22.0f },
            { VehicleClass.Vans,           13.0f },
            { VehicleClass.Cycles,          0.0f },
            { VehicleClass.Boats,          28.0f },
            { VehicleClass.Helicopters,    60.0f },
            { VehicleClass.Planes,         45.0f },
            { VehicleClass.Service,        20.0f },
            { VehicleClass.Emergency,      14.0f },
            { VehicleClass.Military,       35.0f },
            { VehicleClass.Commercial,     32.0f },
            { VehicleClass.Trains,          0.0f },
            { VehicleClass.OpenWheel,      22.0f }
        };

        public static Settings Load()
        {
            var s = new Settings();

            try
            {
                var ini = IniFile.Load(Paths.Ini);

                s.Enabled = ini.GetBool("General", "Enabled", s.Enabled);
                s.AnnounceOnLoad = ini.GetBool("General", "AnnounceOnLoad", s.AnnounceOnLoad);
                s.LogLevel = ParseEnum(ini.GetString("General", "LogLevel", "Info"), s.LogLevel);

                s.ConsumptionMultiplier = ini.GetFloat("Fuel", "ConsumptionMultiplier", s.ConsumptionMultiplier, 0.05f, 20f);
                s.IdleLitresPerHour = ini.GetFloat("Fuel", "IdleLitresPerHour", s.IdleLitresPerHour, 0f, 60f);
                s.AffectBoats = ini.GetBool("Fuel", "AffectBoats", s.AffectBoats);
                s.AffectAircraft = ini.GetBool("Fuel", "AffectAircraft", s.AffectAircraft);
                s.AffectTraffic = ini.GetBool("Fuel", "AffectTraffic", s.AffectTraffic);
                s.FoundFuelMin = ini.GetFloat("Fuel", "FoundFuelMin", s.FoundFuelMin, 0f, 1f);
                s.FoundFuelMax = ini.GetFloat("Fuel", "FoundFuelMax", s.FoundFuelMax, 0f, 1f);
                s.ReserveFraction = ini.GetFloat("Fuel", "ReserveFraction", s.ReserveFraction, 0.01f, 0.6f);
                s.TankLeaks = ini.GetBool("Fuel", "TankLeaks", s.TankLeaks);

                // An inverted pair is a typo, not a range. Swapping beats a Next(hi, lo) throw.
                if (s.FoundFuelMin > s.FoundFuelMax)
                {
                    Log.Warn("[Fuel] FoundFuelMin is above FoundFuelMax - swapping them.");
                    var t = s.FoundFuelMin;
                    s.FoundFuelMin = s.FoundFuelMax;
                    s.FoundFuelMax = t;
                }

                s.SputterLitres = ini.GetFloat("Engine", "SputterLitres", s.SputterLitres, 0f, 20f);
                s.StallWhenEmpty = ini.GetBool("Engine", "StallWhenEmpty", s.StallWhenEmpty);
                s.DryRestartSeconds = ini.GetFloat("Engine", "DryRestartSeconds", s.DryRestartSeconds, 0.2f, 15f);

                s.PumpReach = ini.GetFloat("Station", "PumpReach", s.PumpReach, 0.5f, 12f);
                s.CapReach = ini.GetFloat("Station", "CapReach", s.CapReach, 0.5f, 12f);
                s.HangUpReach = ini.GetFloat("Station", "HangUpReach", s.HangUpReach, 0.4f, 6f);
                s.LitresPerSecond = ini.GetFloat("Station", "LitresPerSecond", s.LitresPerSecond, 0.1f, 60f);
                s.PricePerLitre = ini.GetFloat("Station", "PricePerLitre", s.PricePerLitre, 0f, 200f);
                s.PriceVariance = ini.GetFloat("Station", "PriceVariance", s.PriceVariance, 0f, 0.9f);
                s.ShowBlips = ini.GetBool("Station", "ShowBlips", s.ShowBlips);
                s.ChargeMoney = ini.GetBool("Station", "ChargeMoney", s.ChargeMoney);

                s.InteractKey = ini.GetKey("Nozzle", "InteractKey", s.InteractKey);
                s.Pose = ParseEnum(ini.GetString("Nozzle", "Pose", "PetrolCan"), s.Pose);
                s.Hose = ParseEnum(ini.GetString("Nozzle", "Hose", "Auto"), s.Hose);
                s.HoseMaxMetres = ini.GetFloat("Nozzle", "HoseMaxMetres", s.HoseMaxMetres, 2f, 40f);
                s.HoseWarnFraction = ini.GetFloat("Nozzle", "HoseWarnFraction", s.HoseWarnFraction, 0.2f, 0.98f);
                s.HoseRopeType = ini.GetInt("Nozzle", "HoseRopeType", s.HoseRopeType, 0, 8);
                s.HoseRed = ini.GetInt("Nozzle", "HoseRed", s.HoseRed, 0, 255);
                s.HoseGreen = ini.GetInt("Nozzle", "HoseGreen", s.HoseGreen, 0, 255);
                s.HoseBlue = ini.GetInt("Nozzle", "HoseBlue", s.HoseBlue, 0, 255);
                s.HoseThickness = ini.GetFloat("Nozzle", "HoseThickness", s.HoseThickness, 0.005f, 0.3f);
                s.HoseSag = ini.GetFloat("Nozzle", "HoseSag", s.HoseSag, 1.0f, 2.5f);
                s.HoseSnaps = ini.GetBool("Nozzle", "HoseSnaps", s.HoseSnaps);
                s.HoseAnchorX = ini.GetFloat("Nozzle", "HoseAnchorX", s.HoseAnchorX, -3f, 3f);
                s.HoseAnchorY = ini.GetFloat("Nozzle", "HoseAnchorY", s.HoseAnchorY, -3f, 3f);
                s.HoseAnchorZ = ini.GetFloat("Nozzle", "HoseAnchorZ", s.HoseAnchorZ, 0f, 4f);

                s.NozzleOffsetX = ini.GetFloat("Nozzle", "NozzleOffsetX", s.NozzleOffsetX, -1f, 1f);
                s.NozzleOffsetY = ini.GetFloat("Nozzle", "NozzleOffsetY", s.NozzleOffsetY, -1f, 1f);
                s.NozzleOffsetZ = ini.GetFloat("Nozzle", "NozzleOffsetZ", s.NozzleOffsetZ, -1f, 1f);
                s.NozzleRotX = ini.GetFloat("Nozzle", "NozzleRotX", s.NozzleRotX, -360f, 360f);
                s.NozzleRotY = ini.GetFloat("Nozzle", "NozzleRotY", s.NozzleRotY, -360f, 360f);
                s.NozzleRotZ = ini.GetFloat("Nozzle", "NozzleRotZ", s.NozzleRotZ, -360f, 360f);
                s.TuneNozzle = ini.GetBool("Nozzle", "TuneNozzle", s.TuneNozzle);
                s.ShowFillerMarker = ini.GetBool("Nozzle", "ShowFillerMarker", s.ShowFillerMarker);

                s.ShowGauge = ini.GetBool("HUD", "ShowGauge", s.ShowGauge);
                s.GaugeOnlyInVehicle = ini.GetBool("HUD", "OnlyInVehicle", s.GaugeOnlyInVehicle);
                s.GaugeX = ini.GetFloat("HUD", "X", s.GaugeX, 0f, 1f);
                s.GaugeY = ini.GetFloat("HUD", "Y", s.GaugeY, 0f, 1f);
                s.GaugeWidth = ini.GetFloat("HUD", "Width", s.GaugeWidth, 0.02f, 0.8f);
                s.GaugeHeight = ini.GetFloat("HUD", "Height", s.GaugeHeight, 0.004f, 0.2f);
                s.Units = ParseEnum(ini.GetString("HUD", "Units", "Litres"), s.Units);
                s.ShowNumbers = ini.GetBool("HUD", "ShowNumbers", s.ShowNumbers);

                s.ForecourtHazard = ini.GetBool("Hazard", "ForecourtHazard", s.ForecourtHazard);

                // Per-class thirst, named the way the game names its own classes.
                foreach (VehicleClass c in Enum.GetValues(typeof(VehicleClass)))
                {
                    if (!s.Thirst.ContainsKey(c)) continue;
                    s.Thirst[c] = ini.GetFloat("Consumption", c.ToString(), s.Thirst[c], 0f, 300f);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Settings failed to load; every default applies.", ex);
            }

            Log.Level = s.LogLevel;
            return s;
        }

        private static T ParseEnum<T>(string text, T fallback) where T : struct
        {
            if (string.IsNullOrEmpty(text)) return fallback;
            if (Enum.TryParse(text.Trim(), true, out T parsed) && Enum.IsDefined(typeof(T), parsed)) return parsed;

            Log.Warn("Setting value " + text + " is not a " + typeof(T).Name + " - using " + fallback + ".");
            return fallback;
        }

        /// <summary>Litres per 100km for a class, with a sane number for anything unlisted.</summary>
        public float ThirstFor(VehicleClass c)
        {
            return Thirst.TryGetValue(c, out var v) ? v : 11f;
        }
    }
}
