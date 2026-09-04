using System;
using System.Collections.Generic;
using System.Windows.Forms;
using GTA;

namespace Fumes.Core
{
    /// <summary>How a volume is written on screen.</summary>
    /// <summary>What has to be held down with the menu key.</summary>
    internal enum MenuModifier
    {
        None,
        Shift,
        Control,
        Alt
    }

    /// <summary>
    /// What the pumps sell. The signs on them have said this all along.
    /// </summary>
    internal enum FuelGrade
    {
        Regular,
        Plus,
        Premium,

        /// <summary>
        /// NOT A GRADE, and it sits in this enum anyway.
        ///
        /// Diesel is a different fuel, not a better petrol -- you choose between the first
        /// three and you do not choose this one, the vehicle does. It lives here because the
        /// three things a fuel type has to do in this mod are exactly the three things a grade
        /// already does: set a price, set an economy, and persist in the tank. A parallel enum
        /// would double every one of those and buy nothing.
        ///
        /// LAST in the list on purpose. Menu.Choice cycles Enum.GetValues, so anything added
        /// here would become selectable; the Grade row is given the three petrol values
        /// explicitly instead, and this stays off the end where nothing iterates into it.
        /// </summary>
        Diesel
    }

    internal enum Units
    {
        Litres,
        Gallons
    }

    /// <summary>How the hose between pump and nozzle is drawn.</summary>
    internal enum HoseMode
    {
        /// <summary>
        /// A REAL TUBE. Rings of vertices around the curve, triangles between the rings.
        ///
        /// The other drawn mode, Painted, is one flat strip turned to face the camera. It is
        /// shaded across its width to imply a cylinder, and it never convinces anybody, because
        /// the eye reads a silhouette before it reads shading and the silhouette of a strip is
        /// a strip. This has no silhouette problem: it is round because it is round.
        ///
        /// Costs polygons -- segments times sides times two triangles, every frame. Back faces
        /// are skipped, which halves it, and the ring count is a setting.
        /// </summary>
        Tube,

        /// <summary>
        /// A real physics rope, but painted by us instead of wearing its own texture.
        ///
        /// NOT THE DEFAULT ANY MORE, because the painting is the problem. The ribbon is one
        /// flat strip turned to face the camera, and a strip has no thickness -- so wherever the
        /// hose runs across the view rather than away from it, it stops being a tube and becomes
        /// a black pane a hand's width across. Shading it like a cylinder does not help; it is
        /// still flat, and the eye reads the silhouette before the shading. Rope 4 is dark
        /// enough on its own, and a real rope is round from every angle for free. GTA has no way to TINT a rope -- the colour comes
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

        /// <summary>
        /// The key that opens the settings menu, and what has to be held with it.
        ///
        /// A MODIFIER BY DEFAULT, and Shift specifically. A bare letter is one keystroke away
        /// from whatever else the player has bound it to, and this game has mods bound to most
        /// of the alphabet -- the hotkey map for this machine alone lists a hundred and twenty
        /// one bindings. Shift+F is free on both installs here and is not a combination any
        /// vanilla control uses.
        /// </summary>
        public Keys MenuKey = Keys.F;
        public MenuModifier MenuModifier = MenuModifier.Shift;

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

        /// <summary>
        /// Cars YOU have driven, left running, keep burning fuel while they are loaded.
        ///
        /// The mod already burns the one you are in or last got out of. This is for the ones
        /// after that -- leave a car idling on the forecourt, drive off in another, and the
        /// first one is still running and still drinking. Only cars the player has actually
        /// been in, so a street full of traffic is not being simulated for free.
        /// </summary>
        public bool AbandonedIdle = true;

        /// <summary>
        /// The longest stretch of unwatched idling that gets charged for, in seconds of real
        /// time, when a car left running comes back into view.
        ///
        /// A car that unloads stops burning, because there is nothing left to burn -- so drive
        /// off and come back and it is exactly as you left it, running, on the fuel it had ten
        /// minutes ago. Catching up on the gap fixes that; capping the catch-up stops it being
        /// a nasty surprise. Ten minutes of real time is five in-game hours, which is a big
        /// enough bite to be felt and small enough not to turn a car you forgot about into an
        /// empty one you cannot explain.
        ///
        /// In-session only. Nothing is written down about it, so quitting and coming back
        /// tomorrow does not present a bill for the night.
        /// </summary>
        public float AbandonedIdleCatchUpSeconds = 600f;

        /// <summary>Fuel a vehicle is found with, as a fraction of its tank. Player-owned cars start full.</summary>
        public float FoundFuelMin = 0.18f;
        public float FoundFuelMax = 0.85f;

        /// <summary>
        /// A full tank for vehicles the game hands you DURING A MISSION, and only those.
        ///
        /// This started as "anything script-owned is full", which made every DLC and online car
        /// arrive at 100% -- script-owned covers everything a trainer spawns. Turning it off
        /// fixed that and created a worse problem: a story mission that gives you a car for a
        /// scripted drive would hand it over at a random 18 to 85 per cent, and a mission
        /// written expecting a full tank can fail through nothing the player did.
        ///
        /// The mission flag separates them, and nothing else does: both kinds are persistent,
        /// only one appears while a mission is running.
        /// </summary>
        public bool MissionTanksFull = true;

        /// <summary>Below this fraction of the tank, the low-fuel warning starts.</summary>
        public float ReserveFraction = 0.12f;

        /// <summary>Whether a shot petrol tank drains onto the road. See Consumption.Leak.</summary>
        public bool TankLeaks = true;

        /// <summary>
        /// A chime when a tank drops into reserve. No notification with it -- see LowFuel.
        /// </summary>
        public bool LowFuelChime = true;

        /// <summary>
        /// The sound, and the set it lives in.
        ///
        /// Settings rather than constants because a ONE-SHOT CANNOT BE PROBED. The filling loop
        /// can be checked with HAS_SOUND_FINISHED -- a loop that is playing has not finished --
        /// but a chime that played and a chime that never existed both report finished a moment
        /// later, and nothing can tell them apart from in here. So it ships with a base-game
        /// pair that needs no DLC bank, and anyone who wants a different one can say so.
        /// </summary>
        public string LowFuelSoundName = "5_SEC_WARNING";
        public string LowFuelSoundSet = "HUD_MINI_GAME_SOUNDSET";
        public string LowFuelSoundBank = "";

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

        /// <summary>
        /// How much nearer the other thing must be before the prompt changes its mind, in metres.
        ///
        /// Filling and hanging up want the same button and you stand within reach of both at
        /// once. Whichever is nearer wins -- but at a pump the two distances are twenty or
        /// thirty centimetres apart, so a bare comparison flips on every step and the prompt
        /// strobes. A choice already made has to be beaten by this much to be replaced.
        /// </summary>
        public float PromptStickiness = 0.5f;
        public float LitresPerSecond = 2.2f;

        /// <summary>
        /// Refuelling from the jerry can the player is carrying.
        ///
        /// Asked for on Nexus, and it fits: the game already has the can, and the can already
        /// has a contents -- its ammo, which is what drains when you pour petrol on the floor.
        /// </summary>
        public bool JerryCan = true;

        /// <summary>How many litres a full can holds.</summary>
        public float JerryCanLitres = 20f;

        /// <summary>
        /// How fast it pours, in litres a second.
        ///
        /// Slower than the pump on purpose. A forecourt sells fuel through a hose; a man tipping
        /// a can into a wing does not, and that difference is most of what keeps the can a last
        /// resort rather than a way to skip the drive to a station.
        /// </summary>
        public float JerryCanLitresPerSecond = 0.8f;

        /// <summary>Drawing fuel out of somebody else's tank and into the can.</summary>
        public bool Siphon = true;

        /// <summary>
        /// He holds the can in his free hand while siphoning, rather than standing it on the
        /// floor. Off puts it back on the ground beside him.
        /// </summary>
        public bool SiphonCanInHand = true;

        /// <summary>
        /// The pose he holds while siphoning: the LEFT arm out at the filler, can in the right.
        ///
        /// HANDEDNESS IS IN THE CLIP NAME HERE, which is the whole reason for this dictionary.
        /// The first attempt used mp_common givetake1_b on the reasoning that the _a and _b of
        /// a pair are the two sides of an exchange and therefore mirrored. They are the two
        /// sides -- one hands over, one receives -- and both do it with the right hand, because
        /// that is how people hand things over. Roles, not mirrors.
        ///
        /// doors@1handed holds l_hand_sweep and r_hand_sweep: the same push on either arm, said
        /// out loud in the name rather than inferred from a role. And the 1handed variant is
        /// the one the game plays for someone carrying a one-handed weapon -- right arm keeps
        /// hold of the object, left arm does the pushing, which is the exact shape wanted here.
        ///
        /// A HAND THAT IS HOLDING SOMETHING, which rules out every gesture: a gesture is a
        /// presented hand and a presented hand is open. He is meant to be gripping a hose, so
        /// the clip has to be one where the left hand is closed around an object.
        ///
        /// Picked by eye in an animation browser rather than by reading names out of a dump,
        /// which is how the four before it were picked and why the four before it were wrong. A
        /// dump gives you a name; it does not tell you where the elbow ends up.
        ///
        /// It is an UPPER-body clip by construction -- the dictionary says so -- which is what
        /// lets it sit over a crouch and over walking rather than replacing them.
        ///
        /// Others, if the reach wants adjusting:
        ///   mp_player_int_wank_01          the other take in the same dictionary
        ///   laddersbase  base_left_hand_up a closed grip, higher and straighter
        /// </summary>
        public string SiphonAnimDict = "mp_player_int_upperwank";
        public string SiphonAnimClip = "mp_player_int_wank_02";

        /// <summary>
        /// Where in the motion to freeze. Mid-stroke, where the arm is out. See FillAnimPhase.
        /// </summary>
        public float SiphonAnimPhase = 0.50f;
        public int SiphonAnimFlag = 50;

        /// <summary>
        /// He can walk about and crouch while siphoning, instead of being planted.
        ///
        /// Costs nothing to allow, which is worth saying: the pose is flag 50, and 50 is
        /// 32 + 16 + 2 -- allow-player-control, upper-body-only, hold-last-frame. The animation
        /// has permitted walking the whole time. The only thing standing still was HoldStill.
        ///
        /// Sprint and jump stay off. A man walking with a siphon hose is fine; a man sprinting
        /// with one has left it behind.
        /// </summary>
        public bool SiphonWalk = true;

        /// <summary>
        /// He crouches while siphoning.
        ///
        /// Stealth movement, because that IS the crouch: the game has no separate on-foot
        /// crouch for the player, the stealth stance is the crouched one, and it is what Ctrl
        /// puts you in. It composes with the reach -- the pose is upper-body-only, so it plays
        /// over whatever the legs are doing, standing or crouched -- and with walking, since a
        /// stealth walk is still a walk.
        ///
        /// Whatever stance he was in before is put back afterwards. A man who was already
        /// creeping up on a car does not stand up because he finished stealing from it.
        /// </summary>
        public bool SiphonCrouch = true;

        /// <summary>
        /// How many presses of the duck control to spend before accepting he will not crouch.
        ///
        /// There is a real chance he cannot -- a man holding a jerry can may simply not be
        /// allowed the stance -- and a mod that keeps pressing a button forever is worse than
        /// one that says so in the log and gets on with it.
        /// </summary>
        public int SiphonCrouchTries = 6;

        /// <summary>
        /// A full can does not stop the siphon -- the overflow goes on the ground.
        ///
        /// The hose has no idea the can is full. It keeps coming, and what does not fit lands
        /// at his feet, which is both the honest behaviour and a reason to watch the meter.
        /// Off restores the old stop-when-full.
        /// </summary>
        public bool SiphonOverflow = true;

        /// <summary>
        /// Draw the spill as a growing pool of petrol.
        ///
        /// The game's OWN petrol decals, the same ones a jerry can leaves behind -- which means
        /// they ignite. That is less a feature this adds than one it declines to remove.
        /// </summary>
        public bool SiphonPool = true;

        /// <summary>
        /// How the pool grows. A decal cannot be resized once it is down, so growth is
        /// successive decals at one spot, each wider than the last.
        ///
        /// The interval and the decal cap are both budget: sixty a second would spend the whole
        /// decal allowance in a second, and that allowance is shared with every scuff, skid and
        /// bullet hole already on the street.
        /// </summary>
        public float SiphonPoolWidth = 0.35f;
        public float SiphonPoolGrowth = 0.07f;
        public float SiphonPoolMaxWidth = 2.2f;
        public float SiphonPoolEverySeconds = 0.30f;
        public int SiphonPoolMaxDecals = 60;

        /// <summary>How far he has to move for it to become a second puddle, in metres.</summary>
        public float SiphonPoolStep = 0.5f;

        /// <summary>
        /// How far from the filler he can get before the hose comes out, in metres.
        ///
        /// This is the leash, and it replaces the reach test the other stages use. Standing
        /// still, "can he touch the cap" is the same question as "is he still doing this";
        /// walking, they come apart, and the second one is the one that matters.
        /// </summary>
        public float SiphonLeash = 1f;

        /// <summary>
        /// Where on the can the line actually meets it, from the middle of the top.
        ///
        /// The top of the bounding box is the top of the can, which is not the same thing as
        /// the spout: the neck sits toward one end, so a line to the centre of the lid enters
        /// the can through its shoulder. Forward moves it along the can, up lifts it clear of
        /// the lid so the last inch of hose is not buried in the model.
        /// </summary>
        public float SiphonSpoutForward = 0.13f;
        public float SiphonSpoutUp = 0.01f;

        /// <summary>
        /// The siphon line's own paint, separate from the pump hose's.
        ///
        /// THE GAME'S OWN ROPE, same as the pump hose, and the dark rope type rather than a
        /// drawn ribbon.
        ///
        /// Painted at pure black did solve the banding -- every band computes to the same zero,
        /// so the cross-width shading contributes nothing. It solved the banding by making the
        /// thing genuinely flat, which is the problem underneath it: a flat black strip that
        /// turns to face the camera is a flat black strip, and at arm's length, where a siphon
        /// line is, there is nothing to hide it. The pump hose is metres away and got away with
        /// it. This one is not.
        ///
        /// So: a real rope, which is round because it is geometry, and rope type 4 because that
        /// is the darkest of the eight. Not black -- a rope carries its own texture and no
        /// native tints one -- but dark, and round beats black at this distance.
        /// </summary>
        public HoseMode SiphonHose = HoseMode.Tube;

        public int SiphonHoseRed, SiphonHoseGreen, SiphonHoseBlue;
        public int SiphonHoseSheen;

        /// <summary>
        /// Read by Painted and by Tube. The rope draws itself and ignores this.
        /// </summary>
        public float SiphonHoseThickness = 0.030f;

        /// <summary>
        /// How many faces round the tube. Six is round enough at arm's length; more is
        /// polygons spent on a curve nobody is measuring.
        /// </summary>
        public int SiphonHoseSides = 7;
        public float SiphonHoseSag = 1.05f;

        /// <summary>
        /// The darkest of the eight rope types -- the same one the pump hose settled on.
        ///
        /// Not 5 any more: 5 was the thinnest, chosen to hide UNDER a drawn ribbon. With the
        /// ribbon gone the rope is what you see, so it wants to be the one that looks right
        /// rather than the one that disappears.
        /// </summary>
        public int SiphonHoseRopeType = 4;

        /// <summary>
        /// He can throw a punch or a kick without stopping what he is doing with the can.
        ///
        /// Only while the CAN is in play -- siphoning or pouring. Not at the pump, where the
        /// same button on a running nozzle is a different problem entirely.
        /// </summary>
        public bool KickWhileFilling = true;

        /// <summary>
        /// How long the pose stays out of the way once he swings, in seconds.
        ///
        /// A window rather than a check for "is he still swinging", because the melee state is
        /// not readable the frame the button goes down -- and a pose reapplied on that frame
        /// cancels the swing before it starts, which is exactly what used to happen.
        /// </summary>
        public float KickSeconds = 1.5f;

        /// <summary>
        /// How fast it siphons, in litres a second.
        ///
        /// Slower than pouring, which is slower than the pump. A hose and a mouthful of petrol
        /// is the least efficient way to move fuel there is, and it should feel like the thing
        /// you do because there is no other option.
        /// </summary>
        public float SiphonLitresPerSecond = 0.5f;
        public float PricePerLitre = 1.55f;

        /// <summary>
        /// Which grade you buy. The price and the economy both follow it.
        ///
        /// The pumps have had REGULAR, PLUS and PREMIUM written down their sides since the game
        /// shipped and it has never meant anything. It is a setting rather than a prompt at the
        /// pump because it is a habit, not a decision you make afresh at every forecourt -- and
        /// because the button it would need is the one that took two goes to stop strobing.
        /// </summary>
        public FuelGrade Grade = FuelGrade.Regular;

        /// <summary>
        /// Diesel vehicles take diesel, and the pump says so.
        ///
        /// Off, everything runs on the grade you picked and trucks fill up with premium
        /// unleaded like everyone else.
        /// </summary>
        public bool DieselVehicles = true;

        /// <summary>
        /// Vehicle classes that are diesel wholesale, and the models that break the rule.
        ///
        /// Emergency is deliberately NOT in the class list: the Fire Truck and Ambulance are
        /// diesel and the eight police cars beside them are not, so the two are named instead
        /// of the eight. Service IS in it, because the only petrol vehicle filed there is the
        /// Taxi. Utility likewise, minus the battery carts and the mower.
        ///
        /// Matched by joaat hash, so these are MODEL names -- phantom, firetruk -- and not the
        /// showroom names the game shows you.
        /// </summary>
        public string DieselClasses = "Commercial, Industrial, Utility, Service, Military";
        public string DieselModels = "firetruk, ambulance";
        public string PetrolModels = "taxi, caddy, caddy2, caddy3, airtug, mower";

        /// <summary>
        /// What diesel costs and what it returns.
        ///
        /// Dearer at the pump and further down the road, which is the shape of it at an
        /// Australian servo and also the more interesting trade -- the same one Premium makes,
        /// only harder.
        /// </summary>
        public float DieselPrice = 1.06f;
        public float DieselEconomy = 0.82f;

        /// <summary>What Plus and Premium cost, as a multiple of the regular price.</summary>
        public float PlusPrice = 1.12f;
        public float PremiumPrice = 1.25f;

        /// <summary>
        /// What they do for consumption, as a multiple. Under 1 goes further.
        ///
        /// Small on purpose. Premium is a tenth better and a quarter dearer, so it is a bad
        /// deal in money and a good one in range -- which is the honest shape of the real
        /// trade, and more interesting than a straight upgrade.
        /// </summary>
        public float PlusEconomy = 0.96f;
        public float PremiumEconomy = 0.90f;

        /// <summary>The multipliers for a grade, without a switch at every call site.</summary>
        public float PriceFor(FuelGrade grade)
        {
            if (grade == FuelGrade.Plus) return PlusPrice;
            if (grade == FuelGrade.Premium) return PremiumPrice;
            if (grade == FuelGrade.Diesel) return DieselPrice;
            return 1f;
        }

        public float EconomyFor(FuelGrade grade)
        {
            if (grade == FuelGrade.Plus) return PlusEconomy;
            if (grade == FuelGrade.Premium) return PremiumEconomy;
            if (grade == FuelGrade.Diesel) return DieselEconomy;
            return 1f;
        }

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

        /// <summary>
        /// Traffic that is low on fuel and ALREADY NEAR a station pulls in, sits at the pumps,
        /// and drives off again.
        ///
        /// Deliberately not "low car seeks out a station": stations are a median six hundred
        /// metres apart, the mod simulates a hundred and twenty, and the game despawns traffic
        /// well before that -- a car sent half a kilometre for fuel vanishes on the way. Keeping
        /// it alive would mean marking it a mission entity, and those are never reclaimed, so
        /// every low NPC would sit in the vehicle pool for the session.
        ///
        /// Inverted, it all happens inside the radius: nothing needs persisting, nothing leaks,
        /// and it lands where the player actually is -- at a forecourt, watching other cars use
        /// it. Independent of AffectTraffic, because tanks are generated part-full whether or
        /// not traffic burns fuel, so there are always some low cars about.
        /// </summary>
        public bool TrafficRefuels = true;

        /// <summary>How empty a car has to be before it will bother, as a fraction.</summary>
        public float TrafficRefuelBelow = 0.22f;

        /// <summary>How near a station it must already be, in metres.</summary>
        public float TrafficRefuelRadius = 160f;

        /// <summary>How long it sits at the pumps, and how long before it gives up trying.</summary>
        public float TrafficRefuelSeconds = 9f;
        public float TrafficRefuelGiveUpSeconds = 45f;

        /// <summary>Most cars doing this at once. Two is ambience; ten is a traffic jam.</summary>
        public int TrafficRefuelMax = 2;

        /// <summary>
        /// The driving style handed to the tasks. GTA's standard "obeys lights and traffic"
        /// set of flags -- a bitfield rather than a number that means anything on its own.
        /// </summary>
        public int TrafficDrivingStyle = 786603;
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
        public int HoseRopeType = 4;

        /// <summary>
        /// The in-game rope picker: NumPad * to cycle, NumPad 0 to keep.
        /// </summary>
        public bool RopePicker = true;

        /// <summary>
        /// Rope types that have crashed this install, comma separated.
        ///
        /// ADD_ROPE does not validate its type -- it indexes a table, and an index past the end
        /// takes the process with it. 8 is here because it did exactly that. RopeProbe adds to
        /// this by surviving the crash rather than by anyone predicting it; clearing an entry
        /// only means it gets tried once more.
        /// </summary>
        public string BadRopeTypes = "8";

        /// <summary>The colour of a painted hose. NOT a tint on the rope -- see HoseMode.Painted.</summary>
        public int HoseRed = 8;
        public int HoseGreen = 8;
        public int HoseBlue = 10;

        /// <summary>
        /// The highlight down the middle of the hose, added on top of the colour above.
        ///
        /// Its own setting because it, and not the colour, is what stops a black hose being
        /// black: the shading that makes a flat ribbon read as a round tube works by ADDING
        /// light along its centre line, so a hose set to 20,20,23 was still being drawn at
        /// nearly 70 up the middle. Turning the colour down without turning this down as well
        /// just moves the grey around.
        ///
        /// 0 is a flat silhouette -- honest black, and it stops looking like an object. This is
        /// the lowest figure that still reads as round.
        /// </summary>
        public int HoseSheen = 24;

        /// <summary>
        /// How thick the hose is, in metres across.
        ///
        /// A real forecourt hose is nearer 3cm, and this is deliberately fatter than
        /// that: it is being read at four to eight metres against dark tarmac at night,
        /// where honest scale disappears. Thick enough to be a hose, not so thick it is a
        /// pipe.
        /// </summary>
        public float HoseThickness = 0.055f;

        /// <summary>How many faces round the tube, when the hose is drawn as one.</summary>
        public int HoseSides = 7;

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
        /// because the only way to find them was to look at it -- there is no working them
        /// out on paper from a prop whose origin is not its grip.
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
        public float HoseEndZ = 0.035f;

        /// <summary>
        /// How far STRAIGHT UP IN THE WORLD the hose joins, in metres.
        ///
        /// The three above are offsets in the NOZZLE'S OWN SPACE, and the nozzle is attached at
        /// a rotation of 0, 270, -90 -- turned through a right angle twice. So none of its axes
        /// points anywhere near up, and "move the hose end up a bit" has meant picking whichever
        /// of X, Y and Z happened to lean that way and guessing at the sign. That is why this
        /// has taken three passes and still was not connecting.
        ///
        /// This one is applied AFTER the offset is turned into a world position, so it is up.
        /// Not up-ish, not up in the prop's opinion: up.
        /// </summary>
        public float HoseEndLift = 0.12f;

        /// <summary>
        /// How far to HIS RIGHT the hose joins, in metres. Negative is his left.
        ///
        /// Along the ped's own right vector rather than a world axis or one of the nozzle's.
        /// A world axis would mean "east", which stops being sideways the moment he turns
        /// round; the nozzle's own axes are the frame that made "up" so hard to hit, since it
        /// is attached turned through a right angle twice. His right is the one direction that
        /// still means the same thing on screen from any angle, because the camera is behind
        /// him whenever anyone is looking at this.
        /// </summary>
        public float HoseEndSide = -0.01f;

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
        public float HoseEndReach = 1.2f;


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
        public bool LeftHand = false;

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
        public float GaugeTextScale = 1.35f;

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
        public float FillAnimPhase = 0.18f;

        /// <summary>
        /// The animation for tipping a jerry can into a tank. Blank uses the built-in list.
        ///
        /// The game's own pouring clip, played directly rather than by handing him the can and
        /// making him fire it -- which is how the animation normally happens, and which also
        /// lays a petrol trail across the forecourt, drains the ammo on the game's schedule
        /// instead of ours, and leaves a lit fuse next to a pump.
        ///
        /// Blank because clip names inside a dictionary cannot be listed from a script. Fumes
        /// tries a handful and checks each with IS_ENTITY_PLAYING_ANIM -- one that is not
        /// playing a moment after being asked for is not in there -- and logs the winner.
        /// </summary>
        public string PourAnimDict = "weapons@misc@jerrycan@";
        public string PourAnimClip = "fire";

        /// <summary>Whether refuelling makes a noise at all.</summary>
        public bool FillSound = true;

        /// <summary>
        /// The sound to play while fuel is flowing, and the set it lives in.
        ///
        /// BLANK ON PURPOSE. PLAY_SOUND_FROM_ENTITY looks these up as strings in the game's
        /// audio metadata, a name that does not exist plays silently and reports nothing, and
        /// there is no native that lists the valid ones. Left blank, Fumes tries a list of
        /// candidates and uses HAS_SOUND_FINISHED to tell which one actually started -- a loop
        /// that is really playing does not finish half a second in. It names the winner in the
        /// log; put it here and the search is skipped.
        ///
        /// Filled in by hand, it is taken on trust and never probed: anyone who types a name
        /// in here knows something this code does not.
        /// </summary>
        public string FillSoundName = "";
        public string FillSoundSet = "";

        /// <summary>
        /// The audio bank a configured sound needs, if it needs one.
        ///
        /// Most of the candidates are DLC sounds, and a DLC sound whose bank is not loaded
        /// behaves EXACTLY like a name that does not exist -- silence, no error. So a name
        /// pinned in the ini without its bank was a trap: the candidate list requests the bank
        /// and the ini path did not, so the same name would work from one and not the other
        /// with nothing anywhere to say why.
        /// </summary>
        public string FillSoundBank = "";

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
        /// The gauge goes when the game's own HUD or radar does.
        ///
        /// Asked for by somebody running a mod that hides the radar: a fuel bar floating beside
        /// a minimap that is not there is worse than no fuel bar. It follows IS_HUD_HIDDEN and
        /// IS_RADAR_HIDDEN, so anything that turns those off -- a screenshot key, a cinematic
        /// mod, the game's own display settings -- takes this with it and needs to know nothing
        /// about Fumes to do it.
        /// </summary>
        public bool GaugeFollowsHud = true;
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

        public float GaugeX = 0.1330f;
        public float GaugeY = 0.8100f;
        public float GaugeWidth = 0.0046f;
        public float GaugeHeight = 0.1678f;

        public Units Units = Units.Litres;
        public bool ShowNumbers = false;

        /// <summary>
        /// The amber tick at the reserve level.
        ///
        /// Off. It was there so "low" would be a PLACE on the gauge rather than a message that
        /// has already gone -- but the bar is a continuous colour ramp now, and the ramp is
        /// already red by the time the tick matters. It was marking a threshold the colour had
        /// announced two hundred pixels earlier, and on a bar this narrow it read as damage.
        /// </summary>
        public bool ShowReserveMark = false;

        /// <summary>
        /// The turned FUEL label inside the upright gauge.
        ///
        /// Off. It was there when the bar had no number in it and needed to say what it was
        /// measuring -- the reading lives inside the bar now, and a gauge with a number in it
        /// beside a car's own dials does not need labelling. It was also the only thing in the
        /// column drawn from a PNG.
        /// </summary>
        public bool ShowGaugeLabel = false;

        /// <summary>
        /// The little pump above the reading, inside the bar.
        ///
        /// It replaces the turned FUEL label and does the job better: it says what the bar is
        /// at a glance without spending a third of the bar's length spelling it.
        /// </summary>
        public bool ShowGaugeIcon = true;

        /// <summary>
        /// Draw the fuel as liquid rather than as a filled rectangle.
        ///
        /// A moving surface and bubbles, the same technique as the glass on the pump display,
        /// so the two things that show the same number look like the same instrument. Off
        /// gives a plain bar, which is cheaper by about a dozen rectangles a frame and looks
        /// like every other bar on the screen.
        /// </summary>
        public bool GaugeLiquid = true;

        /// <summary>
        /// How much faster the fuel moves at speed, and the speed it gets there at.
        ///
        /// Fuel in a tank does what the tank does: still at rest, thrown about at speed. The
        /// It does nothing at all below MotionFromKmh and is flat above MotionFullKmh, because
        /// past a point more speed does not make a liquid slosh faster, it makes it slosh harder
        /// -- and an animation that keeps accelerating with the speedometer stops reading as
        /// fuel and starts reading as a loading spinner.
        /// </summary>
        public float GaugeMotionMax = 3.0f;

        /// <summary>
        /// How fast the fuel moves at a dead stop, and the speed by which it is back to normal.
        ///
        /// A LITTLE slower, not slow motion. Fuel in a parked car is settling rather than being
        /// thrown about, and that is worth a glance -- but an earlier attempt ran the idle at a
        /// quarter speed and the bar read as broken rather than as calm. Seven tenths is the
        /// difference you notice without wondering whether something has stopped working.
        ///
        /// Eased out over the first few km/h rather than snapped off the moment the car moves:
        /// something rolling at walking pace is not parked, and a gauge that changes gear as
        /// the handbrake comes off draws the eye at exactly the wrong moment.
        /// </summary>
        public float GaugeMotionIdle = 0.70f;
        public float GaugeMotionRestKmh = 12f;

        /// <summary>
        /// How long the animation takes to reach a new speed, going up and coming down.
        ///
        /// ASYMMETRIC. Winding up follows the throttle closely because that is something you
        /// did; winding down takes several seconds because fuel that has been thrown about does
        /// not settle the moment you lift off -- and because dropping under 120 for a corner
        /// should not slam the animation back and out again.
        /// </summary>
        /// <summary>
        /// How far the surface moves up and down when the car is not moving, as a fraction of
        /// its full travel -- and the speed at which it reaches that full travel.
        ///
        /// A separate curve from the rate, because they are separate things: fuel creeping
        /// through town is not moving much OR quickly. Full height at 120 km/h, which is the
        /// amplitude the waves were authored at, and no higher -- past that only the rate keeps
        /// climbing, which is the right way round. A tank thrown about harder does not slosh
        /// deeper than the tank is deep; it slops back and forth faster.
        /// </summary>
        public float GaugeSwayIdle = 0.35f;
        public float GaugeSwayFullKmh = 120f;

        /// <summary>
        /// The fuel swings to one side when the car stops suddenly, and settles.
        ///
        /// Triggered on DECELERATION rather than on collision: HasCollided is true for kerbs
        /// and hedges, and a gauge that swings every time you clip a bollard is noise. How hard
        /// the car stopped is the thing being modelled, and it scales -- a scrape barely
        /// registers, a wall at eighty throws the fuel across the tank.
        /// </summary>
        public bool GaugeSloshOnImpact = true;

        /// <summary>How hard a stop counts as one, in g. 4 is a real crash, not hard braking.</summary>
        public float GaugeSloshTriggerG = 4.0f;

        /// <summary>How far the surface leans at full force, as a fraction of the screen.</summary>
        public float GaugeSloshTilt = 0.010f;

        /// <summary>How fast it swings back and forth, and how long it takes to settle.</summary>
        public float GaugeSloshHertz = 1.6f;
        public float GaugeSloshSeconds = 1.1f;

        public float GaugeMotionRiseSeconds = 0.8f;
        public float GaugeMotionFallSeconds = 4.0f;

        /// <summary>
        /// The speeds, IN KM/H, between which the animation winds up. Below the first it runs
        /// at its normal rate and nothing changes at all.
        ///
        /// In km/h rather than the metres a second used elsewhere in this file, because these
        /// two are the ones somebody sets while thinking about a speedometer -- "faster than a
        /// hundred and twenty" is a thought you have in the units on your dash.
        /// </summary>
        public float GaugeMotionFromKmh = 80f;
        public float GaugeMotionFullKmh = 150f;


        /// <summary>
        /// How wide the pump is drawn, as a fraction of the bar's width.
        ///
        /// 0.66 IS NOT A GUESS. The original square icon spent 170 of its 256 pixels on ink and
        /// the rest on empty margin, and it was drawn at the full width of the bar -- so the
        /// pump itself came out at 170/256, or 0.664, of the bar. The file is cropped to its ink
        /// now, so drawing it at 0.66 puts the pump on exactly the same pixels it was on before
        /// the crop, and the only difference left is that it is sharper: the same pump, off a
        /// file with no margin to waste resolution on.
        /// </summary>
        public float GaugeIconScale = 0.90f;

        /// <summary>
        /// Hide the reading at a full tank.
        ///
        /// A full bar already says full, and 100 is the one reading that does not fit: the
        /// digits are sized so two of them span the bar, so a third can only be got in by
        /// shrinking all three. It was the only number in the set drawn at a different size
        /// from the rest, and it looked it.
        /// </summary>
        public bool HideFullReading = true;

        /// <summary>
        /// How solid the whole gauge is, 1 being exactly as drawn and 0 invisible.
        ///
        /// ONE NUMBER FOR THE WHOLE THING, applied to every colour it draws. The alternative is
        /// eight alpha literals scattered through the drawing code that have to be kept in step
        /// by hand, and "make it slightly more see-through" then means editing all eight and
        /// getting the ratios right -- which is how a border ends up more solid than the bar it
        /// surrounds.
        ///
        /// 0.85 sits it about where the game's own health and armour bars are.
        /// </summary>
        public float GaugeOpacity = 0.72f;

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
                s.MenuKey = ini.GetKey("General", "MenuKey", s.MenuKey);
                s.MenuModifier = ParseEnum(ini.GetString("General", "MenuModifier", "Shift"), s.MenuModifier);
                s.LogLevel = ParseEnum(ini.GetString("General", "LogLevel", "Info"), s.LogLevel);

                s.ConsumptionMultiplier = ini.GetFloat("Fuel", "ConsumptionMultiplier", s.ConsumptionMultiplier, 0.05f, 20f);
                s.IdleLitresPerHour = ini.GetFloat("Fuel", "IdleLitresPerHour", s.IdleLitresPerHour, 0f, 60f);
                s.AffectBoats = ini.GetBool("Fuel", "AffectBoats", s.AffectBoats);
                s.AffectAircraft = ini.GetBool("Fuel", "AffectAircraft", s.AffectAircraft);
                s.AffectTraffic = ini.GetBool("Fuel", "AffectTraffic", s.AffectTraffic);
                s.AbandonedIdle = ini.GetBool("Fuel", "AbandonedIdle", s.AbandonedIdle);
                s.AbandonedIdleCatchUpSeconds = ini.GetFloat("Fuel", "AbandonedIdleCatchUpSeconds", s.AbandonedIdleCatchUpSeconds, 0f, 3600f);
                s.FoundFuelMin = ini.GetFloat("Fuel", "FoundFuelMin", s.FoundFuelMin, 0f, 1f);
                s.FoundFuelMax = ini.GetFloat("Fuel", "FoundFuelMax", s.FoundFuelMax, 0f, 1f);
                s.MissionTanksFull = ini.GetBool("Fuel", "MissionTanksFull", s.MissionTanksFull);
                s.ReserveFraction = ini.GetFloat("Fuel", "ReserveFraction", s.ReserveFraction, 0.01f, 0.6f);
                s.TankLeaks = ini.GetBool("Fuel", "TankLeaks", s.TankLeaks);
                s.LowFuelChime = ini.GetBool("Fuel", "LowFuelChime", s.LowFuelChime);
                s.LowFuelSoundName = ini.GetString("Fuel", "LowFuelSoundName", s.LowFuelSoundName);
                s.LowFuelSoundSet = ini.GetString("Fuel", "LowFuelSoundSet", s.LowFuelSoundSet);
                s.LowFuelSoundBank = ini.GetString("Fuel", "LowFuelSoundBank", s.LowFuelSoundBank);

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
                s.PromptStickiness = ini.GetFloat("Station", "PromptStickiness", s.PromptStickiness, 0f, 3f);
                s.LitresPerSecond = ini.GetFloat("Station", "LitresPerSecond", s.LitresPerSecond, 0.1f, 60f);
                s.JerryCan = ini.GetBool("Station", "JerryCan", s.JerryCan);
                s.JerryCanLitres = ini.GetFloat("Station", "JerryCanLitres", s.JerryCanLitres, 1f, 200f);
                s.JerryCanLitresPerSecond = ini.GetFloat("Station", "JerryCanLitresPerSecond", s.JerryCanLitresPerSecond, 0.05f, 20f);
                s.Siphon = ini.GetBool("Station", "Siphon", s.Siphon);
                s.SiphonCanInHand = ini.GetBool("Station", "SiphonCanInHand", s.SiphonCanInHand);
                s.SiphonAnimDict = ini.GetString("Station", "SiphonAnimDict", s.SiphonAnimDict);
                s.SiphonAnimClip = ini.GetString("Station", "SiphonAnimClip", s.SiphonAnimClip);
                s.SiphonAnimPhase = ini.GetFloat("Station", "SiphonAnimPhase", s.SiphonAnimPhase, -1f, 1f);
                s.SiphonAnimFlag = ini.GetInt("Station", "SiphonAnimFlag", s.SiphonAnimFlag, 0, 255);
                s.SiphonSpoutForward = ini.GetFloat("Station", "SiphonSpoutForward", s.SiphonSpoutForward, -0.5f, 0.5f);
                s.SiphonSpoutUp = ini.GetFloat("Station", "SiphonSpoutUp", s.SiphonSpoutUp, -0.5f, 0.5f);
                s.SiphonWalk = ini.GetBool("Station", "SiphonWalk", s.SiphonWalk);
                s.SiphonCrouch = ini.GetBool("Station", "SiphonCrouch", s.SiphonCrouch);
                s.SiphonCrouchTries = ini.GetInt("Station", "SiphonCrouchTries", s.SiphonCrouchTries, 1, 60);
                s.SiphonOverflow = ini.GetBool("Station", "SiphonOverflow", s.SiphonOverflow);
                s.SiphonPool = ini.GetBool("Station", "SiphonPool", s.SiphonPool);
                s.SiphonPoolWidth = ini.GetFloat("Station", "SiphonPoolWidth", s.SiphonPoolWidth, 0.05f, 3f);
                s.SiphonPoolGrowth = ini.GetFloat("Station", "SiphonPoolGrowth", s.SiphonPoolGrowth, 0.005f, 1f);
                s.SiphonPoolMaxWidth = ini.GetFloat("Station", "SiphonPoolMaxWidth", s.SiphonPoolMaxWidth, 0.1f, 8f);
                s.SiphonPoolEverySeconds = ini.GetFloat("Station", "SiphonPoolEverySeconds", s.SiphonPoolEverySeconds, 0.05f, 5f);
                s.SiphonPoolMaxDecals = ini.GetInt("Station", "SiphonPoolMaxDecals", s.SiphonPoolMaxDecals, 1, 400);
                s.SiphonPoolStep = ini.GetFloat("Station", "SiphonPoolStep", s.SiphonPoolStep, 0.1f, 5f);
                s.SiphonLeash = ini.GetFloat("Station", "SiphonLeash", s.SiphonLeash, 0.3f, 10f);
                s.SiphonHose = ParseEnum(ini.GetString("Station", "SiphonHose", "Painted"), s.SiphonHose);
                s.SiphonHoseRed = ini.GetInt("Station", "SiphonHoseRed", s.SiphonHoseRed, 0, 255);
                s.SiphonHoseGreen = ini.GetInt("Station", "SiphonHoseGreen", s.SiphonHoseGreen, 0, 255);
                s.SiphonHoseBlue = ini.GetInt("Station", "SiphonHoseBlue", s.SiphonHoseBlue, 0, 255);
                s.SiphonHoseSheen = ini.GetInt("Station", "SiphonHoseSheen", s.SiphonHoseSheen, 0, 255);
                s.SiphonHoseThickness = ini.GetFloat("Station", "SiphonHoseThickness", s.SiphonHoseThickness, 0.002f, 0.5f);
                s.SiphonHoseSides = ini.GetInt("Station", "SiphonHoseSides", s.SiphonHoseSides, 3, 16);
                s.HoseSides = ini.GetInt("Nozzle", "HoseSides", s.HoseSides, 3, 16);
                s.SiphonHoseSag = ini.GetFloat("Station", "SiphonHoseSag", s.SiphonHoseSag, 1f, 3f);
                s.SiphonHoseRopeType = ini.GetInt("Station", "SiphonHoseRopeType", s.SiphonHoseRopeType, 0, 7);
                s.KickWhileFilling = ini.GetBool("Station", "KickWhileFilling", s.KickWhileFilling);
                s.KickSeconds = ini.GetFloat("Station", "KickSeconds", s.KickSeconds, 0.2f, 6f);
                s.SiphonLitresPerSecond = ini.GetFloat("Station", "SiphonLitresPerSecond", s.SiphonLitresPerSecond, 0.05f, 20f);
                s.PricePerLitre = ini.GetFloat("Station", "PricePerLitre", s.PricePerLitre, 0f, 200f);
                s.Grade = ParseEnum(ini.GetString("Station", "Grade", "Regular"), s.Grade);
                s.DieselVehicles = ini.GetBool("Station", "DieselVehicles", s.DieselVehicles);
                s.DieselClasses = ini.GetString("Station", "DieselClasses", s.DieselClasses);
                s.DieselModels = ini.GetString("Station", "DieselModels", s.DieselModels);
                s.PetrolModels = ini.GetString("Station", "PetrolModels", s.PetrolModels);
                s.DieselPrice = ini.GetFloat("Station", "DieselPrice", s.DieselPrice, 0.1f, 5f);
                s.DieselEconomy = ini.GetFloat("Station", "DieselEconomy", s.DieselEconomy, 0.1f, 3f);
                s.PlusPrice = ini.GetFloat("Station", "PlusPrice", s.PlusPrice, 0.1f, 5f);
                s.PremiumPrice = ini.GetFloat("Station", "PremiumPrice", s.PremiumPrice, 0.1f, 5f);
                s.PlusEconomy = ini.GetFloat("Station", "PlusEconomy", s.PlusEconomy, 0.5f, 2f);
                s.PremiumEconomy = ini.GetFloat("Station", "PremiumEconomy", s.PremiumEconomy, 0.5f, 2f);
                s.PriceVariance = ini.GetFloat("Station", "PriceVariance", s.PriceVariance, 0f, 0.9f);
                s.ShowBlips = ini.GetBool("Station", "ShowBlips", s.ShowBlips);
                s.LearnStations = ini.GetBool("Station", "LearnStations", s.LearnStations);
                s.TrafficRefuels = ini.GetBool("Station", "TrafficRefuels", s.TrafficRefuels);
                s.TrafficRefuelBelow = ini.GetFloat("Station", "TrafficRefuelBelow", s.TrafficRefuelBelow, 0.01f, 1f);
                s.TrafficRefuelRadius = ini.GetFloat("Station", "TrafficRefuelRadius", s.TrafficRefuelRadius, 10f, 400f);
                s.TrafficRefuelSeconds = ini.GetFloat("Station", "TrafficRefuelSeconds", s.TrafficRefuelSeconds, 1f, 120f);
                s.TrafficRefuelGiveUpSeconds = ini.GetFloat("Station", "TrafficRefuelGiveUpSeconds", s.TrafficRefuelGiveUpSeconds, 5f, 300f);
                s.TrafficRefuelMax = ini.GetInt("Station", "TrafficRefuelMax", s.TrafficRefuelMax, 0, 12);
                s.TrafficDrivingStyle = ini.GetInt("Station", "TrafficDrivingStyle", s.TrafficDrivingStyle, 0, int.MaxValue);
                s.ChargeMoney = ini.GetBool("Station", "ChargeMoney", s.ChargeMoney);

                s.InteractKey = ini.GetKey("Nozzle", "InteractKey", s.InteractKey);
                s.Pose = ParseEnum(ini.GetString("Nozzle", "Pose", "PetrolCan"), s.Pose);
                s.Hose = ParseEnum(ini.GetString("Nozzle", "Hose", "Auto"), s.Hose);
                s.HoseMaxMetres = ini.GetFloat("Nozzle", "HoseMaxMetres", s.HoseMaxMetres, 2f, 40f);
                s.HoseWarnFraction = ini.GetFloat("Nozzle", "HoseWarnFraction", s.HoseWarnFraction, 0.2f, 0.98f);
                // 0-7, not 0-8. Type 8 is off the end of the game's rope table and killed the
                // process the first time it was ever asked for.
                s.HoseRopeType = ini.GetInt("Nozzle", "HoseRopeType", s.HoseRopeType, 0, RopeProbe.MaxType);
                s.RopePicker = ini.GetBool("Nozzle", "RopePicker", s.RopePicker);
                s.BadRopeTypes = ini.GetString("Nozzle", "BadRopeTypes", s.BadRopeTypes);
                s.HoseRed = ini.GetInt("Nozzle", "HoseRed", s.HoseRed, 0, 255);
                s.HoseGreen = ini.GetInt("Nozzle", "HoseGreen", s.HoseGreen, 0, 255);
                s.HoseBlue = ini.GetInt("Nozzle", "HoseBlue", s.HoseBlue, 0, 255);
                s.HoseSheen = ini.GetInt("Nozzle", "HoseSheen", s.HoseSheen, 0, 255);
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
                s.LeftHand = ini.GetBool("Nozzle", "LeftHand", s.LeftHand);
                s.HoseEndX = ini.GetFloat("Nozzle", "HoseEndX", s.HoseEndX, -1f, 1f);
                s.HoseEndY = ini.GetFloat("Nozzle", "HoseEndY", s.HoseEndY, -1f, 1f);
                s.HoseEndZ = ini.GetFloat("Nozzle", "HoseEndZ", s.HoseEndZ, -1f, 1f);
                s.HoseEndLift = ini.GetFloat("Nozzle", "HoseEndLift", s.HoseEndLift, -0.5f, 0.5f);
                s.HoseEndSide = ini.GetFloat("Nozzle", "HoseEndSide", s.HoseEndSide, -0.5f, 0.5f);
                s.HoseEndAuto = ini.GetBool("Nozzle", "HoseEndAuto", s.HoseEndAuto);
                s.HoseEndSign = ini.GetInt("Nozzle", "HoseEndSign", s.HoseEndSign, -1, 1);
                s.HoseEndReach = ini.GetFloat("Nozzle", "HoseEndReach", s.HoseEndReach, 0f, 1.5f);
                s.ShowFillerMarker = ini.GetBool("Nozzle", "ShowFillerMarker", s.ShowFillerMarker);
                s.FillAnimDict = ini.GetString("Nozzle", "FillAnimDict", s.FillAnimDict);
                s.FillAnimClip = ini.GetString("Nozzle", "FillAnimClip", s.FillAnimClip);
                s.FillAnimPhase = ini.GetFloat("Nozzle", "FillAnimPhase", s.FillAnimPhase, -1f, 1f);
                s.PourAnimDict = ini.GetString("Nozzle", "PourAnimDict", s.PourAnimDict);
                s.PourAnimClip = ini.GetString("Nozzle", "PourAnimClip", s.PourAnimClip);
                s.FillSound = ini.GetBool("Nozzle", "FillSound", s.FillSound);
                s.FillSoundName = ini.GetString("Nozzle", "FillSoundName", s.FillSoundName);
                s.FillSoundSet = ini.GetString("Nozzle", "FillSoundSet", s.FillSoundSet);
                s.FillSoundBank = ini.GetString("Nozzle", "FillSoundBank", s.FillSoundBank);
                s.FillAnimFlag = ini.GetInt("Nozzle", "FillAnimFlag", s.FillAnimFlag, 0, 255);

                s.Prompts = ParseEnum(ini.GetString("HUD", "Prompts", "HelpText"), s.Prompts);
                s.ShowGauge = ini.GetBool("HUD", "ShowGauge", s.ShowGauge);
                s.GaugeOnlyInVehicle = ini.GetBool("HUD", "OnlyInVehicle", s.GaugeOnlyInVehicle);
                s.GaugeFollowsHud = ini.GetBool("HUD", "FollowsHud", s.GaugeFollowsHud);
                s.GaugeX = ini.GetFloat("HUD", "X", s.GaugeX, 0f, 1f);
                s.GaugeY = ini.GetFloat("HUD", "Y", s.GaugeY, 0f, 1f);
                s.GaugeWidth = ini.GetFloat("HUD", "Width", s.GaugeWidth, 0.0010f, 0.8f);
                s.GaugeHeight = ini.GetFloat("HUD", "Height", s.GaugeHeight, 0.004f, 0.6f);
                s.Vertical = ini.GetBool("HUD", "Vertical", s.Vertical);
                s.Units = ParseEnum(ini.GetString("HUD", "Units", "Litres"), s.Units);
                s.ShowNumbers = ini.GetBool("HUD", "ShowNumbers", s.ShowNumbers);
                s.ShowReserveMark = ini.GetBool("HUD", "ShowReserveMark", s.ShowReserveMark);
                s.ShowGaugeLabel = ini.GetBool("HUD", "ShowGaugeLabel", s.ShowGaugeLabel);
                s.ShowGaugeIcon = ini.GetBool("HUD", "ShowGaugeIcon", s.ShowGaugeIcon);
                s.GaugeLiquid = ini.GetBool("HUD", "GaugeLiquid", s.GaugeLiquid);
                s.GaugeMotionMax = ini.GetFloat("HUD", "MotionMax", s.GaugeMotionMax, 1f, 10f);
                s.GaugeMotionIdle = ini.GetFloat("HUD", "MotionIdle", s.GaugeMotionIdle, 0.05f, 1f);
                s.GaugeMotionRestKmh = ini.GetFloat("HUD", "MotionRestKmh", s.GaugeMotionRestKmh, 0f, 100f);
                s.GaugeSwayIdle = ini.GetFloat("HUD", "SwayIdle", s.GaugeSwayIdle, 0f, 1f);
                s.GaugeSloshOnImpact = ini.GetBool("HUD", "SloshOnImpact", s.GaugeSloshOnImpact);
                s.GaugeSloshTriggerG = ini.GetFloat("HUD", "SloshTriggerG", s.GaugeSloshTriggerG, 0.5f, 30f);
                s.GaugeSloshTilt = ini.GetFloat("HUD", "SloshTilt", s.GaugeSloshTilt, 0f, 0.1f);
                s.GaugeSloshHertz = ini.GetFloat("HUD", "SloshHertz", s.GaugeSloshHertz, 0.1f, 10f);
                s.GaugeSloshSeconds = ini.GetFloat("HUD", "SloshSeconds", s.GaugeSloshSeconds, 0.05f, 10f);
                s.GaugeSwayFullKmh = ini.GetFloat("HUD", "SwayFullKmh", s.GaugeSwayFullKmh, 1f, 400f);
                s.GaugeMotionRiseSeconds = ini.GetFloat("HUD", "MotionRiseSeconds", s.GaugeMotionRiseSeconds, 0f, 30f);
                s.GaugeMotionFallSeconds = ini.GetFloat("HUD", "MotionFallSeconds", s.GaugeMotionFallSeconds, 0f, 60f);
                s.GaugeMotionFromKmh = ini.GetFloat("HUD", "MotionFromKmh", s.GaugeMotionFromKmh, 0f, 400f);
                s.GaugeMotionFullKmh = ini.GetFloat("HUD", "MotionFullKmh", s.GaugeMotionFullKmh, 0f, 500f);
                s.GaugeIconScale = ini.GetFloat("HUD", "IconScale", s.GaugeIconScale, 0.2f, 2f);
                s.HideFullReading = ini.GetBool("HUD", "HideFullReading", s.HideFullReading);
                s.GaugeOpacity = ini.GetFloat("HUD", "Opacity", s.GaugeOpacity, 0.15f, 1f);
                s.GaugeTextScale = ini.GetFloat("HUD", "TextScale", s.GaugeTextScale, 0.1f, 4f);

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
