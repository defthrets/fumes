# Running on Fumes

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
- A physics hose runs from the pump to your hand and sags, swings and drapes on
  its own. If ropes will not come up on your install it draws one instead, and
  says so in the log.
- Prices vary by station. Fill by the litre and watch the total climb.
- Jerry cans can be bought at any pump without taking the nozzle.
- Fire at the pumps while you are stood there with the hose out ends the way
  you would expect — but never out of nowhere.

**HUD**

A gauge above the minimap with a reserve mark, and a pump display while filling.
Litres or gallons.

---

## Install

1. You need [ScriptHookV](http://www.dev-c.com/gtav/scripthookv/) and
   ScriptHookVDotNet 3.
2. Drop the contents of the zip over your GTA V folder. That puts
   `Fumes.dll`, `Fumes.ini` and `scripts\Fumes\stations.json` where they belong.

Nothing else. No RPF edits, no limit adjuster, no gameconfig.

**Remove any other fuel mod first.** Two fuel mods will both cut your engine and
both charge you, and neither will know about the other.

---

## Controls

| Key | Where | What |
|---|---|---|
| `E` | at a pump | take the nozzle / hang it up |
| `E` | at a vehicle's filler | start and stop filling |
| `Q` | at a pump | buy a full jerry can |
| `Q` | holding the nozzle | drop it |

A controller's context button works everywhere `E` does, always, whatever the
ini says. `E` is rebindable in `[Nozzle] InteractKey`.

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

`-Deploy` refuses to run while GTA V is open, because the dll is locked. Add
`-FreshData` when `data\` has changed; without it the deploy prints `KEEP` and
leaves your edited `stations.json` alone.

---

## Where things are

| | |
|---|---|
| `src/Fumes/Core` | logging, paths, ini, json, settings |
| `src/Fumes/Fuel` | tanks, persistence, the burn model, running dry |
| `src/Fumes/Station` | pumps, the nozzle, the hose, the refuel interaction |
| `src/Fumes/UI` | the gauge and the pump display |
| `data/stations.json` | station coordinates, for blips and prices only |

Zero external runtime dependencies — only the BCL and SHVDN. No Newtonsoft, no
LemonUI, no NativeUI. A GTA `scripts\` folder is one shared assembly-resolution
namespace and every library in it is a version fight waiting to happen with
somebody else's mod.

`Core/Json.cs` and `Core/JsonFile.cs` are lifted from Hoodrich with the
namespace changed. Same author, same box; a json parser is not worth writing
twice. Fix a bug in one and copy it to the other.

---

by spitmux
