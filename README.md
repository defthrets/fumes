# Fumes

A fuel mod for GTA V. Every vanilla and online vehicle carries a real tank that
empties as you drive and is still empty when you come back to it tomorrow.

Refuelling is the point of the mod, and it is not a menu.

> Get out. Walk to the pump. **Take the nozzle off the pump** — it goes in
> Franklin's hand and the hose comes with him. Walk it round to the side of the
> car, to wherever that model's filler actually is. Fill up. Walk the nozzle
> back and hang it up.

Walk too far and the hose pulls the nozzle out of your hands.

Runs on **both** GTA V editions — Legacy and Enhanced — from one build. Pure
ScriptHookVDotNet, no asset mods, no OpenIV, nothing to install into an RPF.

---

## What it does

**Fuel**

- Every land vehicle and boat (aircraft optional) has a tank sized from the
  model's own `fPetrolTankVolume`, so a Blista, a Phantom and a Bati all differ.
- Consumption is physical: distance × litres-per-100km for the class, plus an
  idle burn, times engine load taken from RPM. A damaged engine drinks more.
- Fuel persists per vehicle, keyed on model + plate, across sessions.
- A shot petrol tank leaks onto the road whether the engine is running or not.
- The last half-litre is an engine that keeps catching and dropping. On empty
  it stalls, and the starter will turn over as long as you keep trying it.
- Electric vehicles read `CHARGE` instead of `FUEL`.

**The forecourt**

- Pumps are found by asking the game which pump *object* you are standing at,
  not by a coordinate list — so it works at every station, including ones added
  by map mods.
- A **black physics hose** runs from the pump to your hand and sags, swings and
  drapes on its own. GTA cannot tint a rope, so the rope does the physics and
  the colour is painted along its own vertices. If ropes will not come up on
  your install it draws one instead, and says so in the log.
- You carry the nozzle by holding an invisible fire extinguisher — the game's
  own carry animations, and a trigger left free for a future "spray it" option.
- Prices vary by station. Fill by the litre and watch the total climb.
- Fire at the pumps while you are stood there with the hose out ends the way
  you would expect — but never out of nowhere.

**HUD**

- A gauge under the minimap with a reserve mark, its readout drawn inside the
  bar. Litres or gallons.
- A forecourt display while filling: station, litres, running total, and a tank
  bar that fills to **TANK FULL** with a highlight travelling along the fuel.
- Prompts use the game's own instructional button bar, so the glyph is **E** on
  a keyboard and the right D-pad on a controller, automatically.
- Icons are ordinary PNGs loaded from disk at runtime (`data/icons/`), drawn
  with a slow bob and sway. No `.ytd`, no OpenIV, nothing to install.

---

## Install

1. You need [ScriptHookV](http://www.dev-c.com/gtav/scripthookv/) and
   **ScriptHookVDotNet 3, any build from 3.6.0 up** — the
   [releases](https://github.com/scripthookvdotnet/scripthookvdotnet/releases),
   the nightlies, or on GTA V Enhanced the
   [Enhanced fork](https://www.gta5-mods.com/tools/script-hook-v-net-enhanced).

   **There is no "3.9" to download.** The Enhanced fork reports an assembly
   version of 3.9.0.0 but is released as **v1.1.x**; people search for a 3.9,
   find nothing, and give up. Since 0.1.8 the dll is built against 3.6.0, so
   every host from there up loads it. To see what you have: right-click
   `ScriptHookVDotNet3.dll`, Properties, Details.
2. Drop the contents of the zip over your GTA V folder. That puts `Fumes.dll`,
   `Fumes.ini` and the `scripts\Fumes\` folder (`stations.json` and the HUD's
   icons) where they belong.

Nothing else. No RPF edits, no limit adjuster, no gameconfig.

**Remove any other fuel mod first.** Two fuel mods will both cut your engine and
both charge you, and neither will know about the other.

---

## Controls

| Key | Where | What |
|---|---|---|
| `E` | at a pump | take the nozzle |
| `E` | at a vehicle's filler | start and stop filling |
| `E` | back at the pump | hang the nozzle up |
| `Q` | holding the nozzle | drop it (or hang it up, at the pump) |

A controller's context button works everywhere `E` does, always, whatever the
ini says — and the on-screen prompt shows whichever you are actually holding.
`E` is rebindable in `[Nozzle] InteractKey`.

**Filling beats hanging up.** You park right next to the pump, so the filler is
usually within reach of both — if a car is in reach and not full, `E` fills it.
`[Station] HangUpReach` is deliberately much tighter than `PumpReach` for the
same reason.

---

## Tuning

Everything is in `Fumes.ini`, which is commented line by line. The three worth
knowing about:

- `[Fuel] ConsumptionMultiplier` — how fast a tank empties. 1.0 is about half an
  hour of driving; 0.5 is about an hour.
- `[Station] CapReach` — how precisely you have to find the filler. Raise it if
  you are hunting for the exact spot.
- `[Nozzle] HoseAnchorX/Y/Z` — where the hose leaves the pump. The game has six
  pump models and they are not the same shape.

If the nozzle sits wrong in his hand, `[Nozzle] NozzleOffsetX/Y/Z` and
`NozzleRot*` are the six numbers that place it. `prop_cs_fuel_nozle` is a scene
prop whose origin is not its grip, so they cannot be worked out on paper — the
shipped values were found by looking.

`scripts\Fumes\stations.json` is the station list, used **only** for map blips
and pricing. Refuelling does not read it, so a wrong coordinate cannot break
anything — and you can add your own stations to it without a rebuild.

---

## Building

The build does not use `dotnet build`, because the .NET SDK on the machine this
was written on is broken. It drives a self-contained Roslyn `csc.exe` from
`tools\` instead: no SDK, no Visual Studio, no admin rights. See
[tools/README.md](tools/README.md) to restore the toolchain.

```powershell
.\build.ps1                 # build to .\build\Fumes.dll
.\build.ps1 -Deploy         # ...and install into both GTA V editions
.\build.ps1 -Package        # ...or make a release zip
```

**You can deploy while the game is running.** SHVDN shadow-copies script
assemblies into the .NET download cache and runs them from there, so the dll in
`scripts\` is not locked — `-Deploy` overwrites it and tells you which key to
press to reload:

| Install | SHVDN reload key |
|---|---|
| Legacy | `Pause` |
| Enhanced | `Insert` |

Those come from each install's own `ScriptHookVDotNet.ini` (`ReloadKeyBinding`),
read at deploy time rather than assumed — the two do not agree, and a wrong key
in a reminder is worse than no reminder. If the dll ever genuinely is locked the
deploy says `LOCKED` and still updates the data files.

Add `-FreshData` when `data\` has changed; without it the deploy prints `KEEP`
and leaves your edited `stations.json` alone.

---

## Where things are

| | |
|---|---|
| `src/Fumes/Core` | logging, paths, ini, json, settings |
| `src/Fumes/Fuel` | tanks, persistence, the burn model, running dry |
| `src/Fumes/Station` | pumps, the nozzle, the hose, the refuel interaction |
| `src/Fumes/UI` | the gauge, the pump display, the button bar, icons |
| `data/stations.json` | station coordinates, for blips and prices only |
| `data/icons/` | HUD artwork, regenerated by `tools/make_icons.py` |

Icons are white silhouettes tinted at draw time, so one PNG serves every colour
the HUD wants. Regenerate with `python tools/make_icons.py`, then deploy with
`-FreshData`.

Zero external runtime dependencies — only the BCL and SHVDN. No Newtonsoft, no
LemonUI, no NativeUI. A GTA `scripts\` folder is one shared assembly-resolution
namespace and every library in it is a version fight waiting to happen with
somebody else's mod.

`Core/Json.cs` and `Core/JsonFile.cs` are lifted from Hoodrich with the
namespace changed. Same author, same box; a json parser is not worth writing
twice. Fix a bug in one and copy it to the other.

---

by spitmux

---

## Translations

Eight languages, one json each in `data/lang/`, switched from the settings menu.
The Brazilian Portuguese is **lirounando's** -- they translated 0.1.1 in full and
sent it back, and it is the reason the language setting exists at all. The other
six are the author's own and have not been checked by a native speaker: a
correction is one line in one file, and `tools/lang/README.md` says how.

## Licence

MIT. See `LICENSE`. The mod ships no Rockstar assets and does not bundle
ScriptHookV or ScriptHookVDotNet, which carry their own licences.
