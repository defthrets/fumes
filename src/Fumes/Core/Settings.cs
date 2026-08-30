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

    /// <summary>Where the forecourt prompts are shown.</summary>
    internal enum PromptStyle
    {
        /// <summary>
        /// The game's help box, TOP LEFT. The default.
        ///
        /// It reads button glyphs too -- ~INPUT_CONTEXT~ resolves to the key or pad button
        /// actually bound, and help text is the only place in the game where it does.
        /// </summary>
        HelpText,

        /// <summary>
        /// The instructional button bar, bottom right.
        ///
        /// Prettier, and fixed where it is: the position is baked into Rockstar's scaleform
        /// and there is no argument that moves it.
        /// </summary>
        ButtonBar
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
        /// <summary>
        /// How close you have to stand for a pump to offer you its nozzle.
        ///
        /// Measured from the pump PROP'S ORIGIN, which sits at the middle of its base -- so a
        /// figure that sounds generous is tighter than it reads, and one that sounds right
        /// lets you take a nozzle from the far side of the forecourt. 1.8 is about arm's reach
        /// of the machine itself.
        /// </summary>
        public float PumpReach = 1.8f;
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

        /// <summary>
        /// Whether the mod corrects its own station coordinates from the pumps it finds.
        ///
        /// The shipped list was written down by hand and some of it is simply wrong -- a blip
        /// on the far side of a block from the forecourt it names. Nothing depends on those
        /// numbers (pumps are found as objects, not by coordinate) so a wrong one only ever
        /// misplaced a marker, but a misplaced marker is still the thing you navigate by.
        ///
        /// With this on, driving within sight of any petrol pump moves the nearest listed
        /// station onto it, and a pump with no station listed anywhere near becomes a new one.
        /// Corrections go to stations.local.json, never to the shipped file.
        /// </summary>
        public bool LearnStations = true;
        public bool ChargeMoney = true;

        // ---- the nozzle and its hose -----------------------------------------
        public Keys InteractKey = Keys.E;
        public NozzlePose Pose = NozzlePose.FireExtinguisher;
        public HoseMode Hose = HoseMode.Auto;

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
        public int HoseRopeType = 6;

        /// <summary>The colour of a painted hose. NOT a tint on the rope -- see HoseMode.Painted.</summary>
        public int HoseRed = 20;
        public int HoseGreen = 20;
        public int HoseBlue = 23;

        /// <summary>
        /// How thick the hose is, in metres across.
        ///
        /// A real forecourt hose is nearer 3cm, and this is deliberately fatter than
        /// that: it is being read at four to eight metres against dark tarmac at night,
        /// where honest scale disappears. Thick enough to be a hose, not so thick it is a
        /// pipe.
        /// </summary>
        public float HoseThickness = 0.045f;

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
        public float NozzleRotY = 270f;
        public float NozzleRotZ = -90f;

        /// <summary>
        /// In-game tuning for the six numbers above.
        ///
        /// NumPad5 cycles the axis, NumPad4 and NumPad6 move it, NumPad0 writes the whole set
        /// to the log in ini form. Off by default: it is a workbench, not a feature.
        /// </summary>
        /// <summary>
        /// Where the hose meets the nozzle, in the NOZZLE'S own space.
        ///
        /// The hose used to end at the hand bone, which is a hand's width from where a hose
        /// actually joins a nozzle -- so it ran into his fist and out the other side. Hung off
        /// the prop instead, it follows the nozzle's own rotation for free, so tilting the
        /// nozzle swings the hose with it exactly as a real one would.
        ///
        /// Negative Y is behind it. Dial it in with the tuner like the rest of the placement.
        /// </summary>
        public float HoseEndX = 0f;
        public float HoseEndY = 0f;
        public float HoseEndZ = 0f;

        /// <summary>
        /// Work the hose attachment out from the nozzle's own SHAPE rather than from numbers.
        ///
        /// Three hand-typed offsets in a prop's local space is a guessing game, and I lost it
        /// four times running: the nozzle is rotated three ways, so which direction "the back
        /// of it" happens to be is not something anybody can hold in their head.
        ///
        /// The model knows though. Its bounding box has a longest axis, and on a fuel nozzle
        /// that axis IS the nozzle -- spout at one end, hose at the other. So the attachment is
        /// taken from the box, and the only thing left to decide is which of the two ends, one
        /// bit of information instead of three numbers.
        /// </summary>
        public bool HoseEndAuto = true;

        /// <summary>Which end of that axis. Flip it if the hose comes out of the spout.</summary>
        public int HoseEndSign = -1;

        /// <summary>How far along toward that end, 1.0 being the very tip of the bounding box.</summary>
        public float HoseEndReach = 0.9f;

        public bool TuneNozzle = false;

        /// <summary>
        /// Which hand the nozzle goes in.
        ///
        /// LEFT, because the filling animation says so. Every clip reaches with one particular
        /// arm, and the handshake reaches with the left -- so a nozzle bolted to the right hand
        /// leaves him extending an empty hand at the car while the nozzle hangs at his other
        /// side. Nothing about the prop is wrong there; it is simply in the wrong hand.
        ///
        /// Changing the fill animation to one that reaches with the right means changing this
        /// back. They are two halves of one decision.
        /// </summary>
        public bool LeftHand = true;

        /// <summary>
        /// How big the gauge's lettering is, as a multiple of the size that exactly fits the
        /// bar's width.
        ///
        /// 1.0 means the percentage comes out the same width as the gauge -- the size is
        /// measured at runtime rather than picked, so it stays fitted whatever the bar's width
        /// is and whatever the number happens to say. Its own setting because "thinner bar" and
        /// "smaller text" turned out to be two different requests, and tying them together
        /// meant neither could be answered without moving the other.
        /// </summary>
        public float GaugeTextScale = 1.0f;

        /// <summary>Whether a marker is drawn on the vehicle's filler while you carry the nozzle.</summary>
        public bool ShowFillerMarker = false;

        /// <summary>
        /// The clip he holds while fuel is going in: one arm out to the car.
        ///
        /// A HANDSHAKE, FROZEN PART WAY THROUGH. Nothing in the game is animated for
        /// refuelling, so the job is to find an existing clip whose shape is right and stop it
        /// there. A handshake reaches forward at waist height with the hand closed -- which is
        /// exactly where a nozzle goes.
        ///
        /// The first attempt was a hold-up pose, and it was wrong for a reason worth writing
        /// down: it aims a pistol, so the arm sits at chest height. Right idea, wrong altitude.
        /// </summary>
        public string FillAnimDict = "mp_common";
        public string FillAnimClip = "givetake1_a";

        /// <summary>
        /// Where in the clip to stop, 0 at the first frame and 1 at the last.
        ///
        /// THE WHOLE POINT. A handshake is a movement -- reach, grip, shake, withdraw -- and
        /// playing it gives an arm that pumps up and down and then drops back to his side.
        /// Frozen at the reach it is a pose, and a pose is what refuelling needs. Negative
        /// lets the clip play through normally.
        /// </summary>
        public float FillAnimPhase = 0.45f;

        /// <summary>
        /// The anim flag. 48-63 is the native's own "upper body, controllable" band, which is
        /// what blends the clip over his legs and leaves the player in charge instead of
        /// planting him in a cutscene. 50 is that band plus hold-last-frame and NOT loop --
        /// looping a handshake shakes forever.
        /// </summary>
        public int FillAnimFlag = 50;

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
        /// Height up the pump, in metres, when it is not being worked out automatically.
        ///
        /// Only used with HoseAnchorAuto off. Typing a height here means guessing at the size
        /// of six different pump models, which is how this ended up needing raising three
        /// times.
        /// </summary>
        public float HoseAnchorZ = 2.10f;

        /// <summary>
        /// Take the hose height from the PUMP'S OWN HEIGHT rather than a typed number.
        ///
        /// The game has six pump models and they are not the same size, so any single figure
        /// is wrong for most of them -- and a figure that is wrong is either a hose leaving the
        /// payment panel or one leaving thin air above the lid. The model's bounding box knows
        /// exactly how tall it is; taking a fraction of that is right for all six without a
        /// number being typed for any.
        /// </summary>
        public bool HoseAnchorAuto = true;

        /// <summary>How far up the pump, 0 at its foot and 1 at the very top of its box.</summary>
        public float HoseAnchorHeight = 0.92f;

        // ---- HUD --------------------------------------------------------------
        /// <summary>Top-left help box, or the bottom-right button bar.</summary>
        public PromptStyle Prompts = PromptStyle.HelpText;

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
        /// <summary>
        /// Upright, down the side of the minimap, rather than a strip beneath it.
        ///
        /// Upright is the better shape for this: a fuel level is a HEIGHT IN A CONTAINER, and
        /// the pump display already draws it as one, so the two readings of the same number now
        /// look like the same instrument. It also stops competing for the sliver of screen
        /// between the minimap and the bottom edge, which was never big enough for a bar and a
        /// line of text at once.
        /// </summary>
        public bool Vertical = true;

        public float GaugeX = 0.1325f;
        public float GaugeY = 0.8240f;
        public float GaugeWidth = 0.0035f;
        public float GaugeHeight = 0.1550f;

        /// <summary>
        /// Live placement for the gauge, the same workbench the nozzle has.
        ///
        /// The numbers above cannot be right for everybody and cannot be worked out from here:
        /// where the minimap lands depends on the player's safe-zone slider and their aspect
        /// ratio, and the game offers no honest way to ask. So rather than a formula nobody can
        /// check, this lets it be dragged into place while looking at it.
        ///
        /// Only listens while you are NOT holding the nozzle, so it cannot fight the nozzle
        /// tuner over the same keys.
        /// </summary>
        public bool TuneGauge = false;
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
                s.LearnStations = ini.GetBool("Station", "LearnStations", s.LearnStations);
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
                s.HoseAnchorAuto = ini.GetBool("Nozzle", "HoseAnchorAuto", s.HoseAnchorAuto);
                s.HoseAnchorHeight = ini.GetFloat("Nozzle", "HoseAnchorHeight", s.HoseAnchorHeight, 0f, 1.2f);

                s.NozzleOffsetX = ini.GetFloat("Nozzle", "NozzleOffsetX", s.NozzleOffsetX, -1f, 1f);
                s.NozzleOffsetY = ini.GetFloat("Nozzle", "NozzleOffsetY", s.NozzleOffsetY, -1f, 1f);
                s.NozzleOffsetZ = ini.GetFloat("Nozzle", "NozzleOffsetZ", s.NozzleOffsetZ, -1f, 1f);
                s.NozzleRotX = ini.GetFloat("Nozzle", "NozzleRotX", s.NozzleRotX, -360f, 360f);
                s.NozzleRotY = ini.GetFloat("Nozzle", "NozzleRotY", s.NozzleRotY, -360f, 360f);
                s.NozzleRotZ = ini.GetFloat("Nozzle", "NozzleRotZ", s.NozzleRotZ, -360f, 360f);
                s.TuneNozzle = ini.GetBool("Nozzle", "TuneNozzle", s.TuneNozzle);
                s.LeftHand = ini.GetBool("Nozzle", "LeftHand", s.LeftHand);
                s.HoseEndX = ini.GetFloat("Nozzle", "HoseEndX", s.HoseEndX, -1f, 1f);
                s.HoseEndY = ini.GetFloat("Nozzle", "HoseEndY", s.HoseEndY, -1f, 1f);
                s.HoseEndZ = ini.GetFloat("Nozzle", "HoseEndZ", s.HoseEndZ, -1f, 1f);
                s.HoseEndAuto = ini.GetBool("Nozzle", "HoseEndAuto", s.HoseEndAuto);
                s.HoseEndSign = ini.GetInt("Nozzle", "HoseEndSign", s.HoseEndSign, -1, 1);
                s.HoseEndReach = ini.GetFloat("Nozzle", "HoseEndReach", s.HoseEndReach, 0f, 1.5f);
                s.ShowFillerMarker = ini.GetBool("Nozzle", "ShowFillerMarker", s.ShowFillerMarker);
                s.FillAnimDict = ini.GetString("Nozzle", "FillAnimDict", s.FillAnimDict);
                s.FillAnimClip = ini.GetString("Nozzle", "FillAnimClip", s.FillAnimClip);
                s.FillAnimPhase = ini.GetFloat("Nozzle", "FillAnimPhase", s.FillAnimPhase, -1f, 1f);
                s.FillAnimFlag = ini.GetInt("Nozzle", "FillAnimFlag", s.FillAnimFlag, 0, 255);

                s.Prompts = ParseEnum(ini.GetString("HUD", "Prompts", "HelpText"), s.Prompts);
                s.ShowGauge = ini.GetBool("HUD", "ShowGauge", s.ShowGauge);
                s.GaugeOnlyInVehicle = ini.GetBool("HUD", "OnlyInVehicle", s.GaugeOnlyInVehicle);
                s.GaugeX = ini.GetFloat("HUD", "X", s.GaugeX, 0f, 1f);
                s.GaugeY = ini.GetFloat("HUD", "Y", s.GaugeY, 0f, 1f);
                s.GaugeWidth = ini.GetFloat("HUD", "Width", s.GaugeWidth, 0.0010f, 0.8f);
                s.GaugeHeight = ini.GetFloat("HUD", "Height", s.GaugeHeight, 0.004f, 0.6f);
                s.Vertical = ini.GetBool("HUD", "Vertical", s.Vertical);
                s.Units = ParseEnum(ini.GetString("HUD", "Units", "Litres"), s.Units);
                s.ShowNumbers = ini.GetBool("HUD", "ShowNumbers", s.ShowNumbers);
                s.GaugeTextScale = ini.GetFloat("HUD", "TextScale", s.GaugeTextScale, 0.1f, 4f);
                s.TuneGauge = ini.GetBool("HUD", "TuneGauge", s.TuneGauge);

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
