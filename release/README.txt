FUMES 0.1.8
Persistent vehicle fuel for GTA V, with a refuel you walk through.
by spitmux


WHAT IT IS
==========

Every vanilla and online vehicle carries a real fuel tank. It empties as you
drive and it is still empty when you come back to that car tomorrow.

Refuelling is not a menu. Get out, walk to the pump, take the nozzle off it --
it goes in your hand and the hose comes with you on a real physics rope. Walk
round to wherever that model's filler actually is, fill up, then walk the nozzle
back and hang it on the pump. Walk too far and the hose pulls it out of your
hands.

Runs on BOTH editions, Legacy and Enhanced, from one build. Pure
ScriptHookVDotNet: no asset mods, no OpenIV, nothing to install into an RPF.


REQUIREMENTS
============

  Script Hook V           (Alexander Blade, for your edition)
  ScriptHookVDotNet 3     any of them - see below

Nothing else. No NativeUI, no LemonUI, no iFruitAddon.

WHICH SCRIPTHOOKVDOTNET
=======================

Any ScriptHookVDotNet 3 from 3.6.0 onwards, including the nightlies and including
the Enhanced fork. Pick whichever suits your game:

  GTA V LEGACY    the regular ScriptHookVDotNet 3, or a nightly, or the fork
      https://github.com/scripthookvdotnet/scripthookvdotnet/releases

  GTA V ENHANCED  the Enhanced fork - it is the one that runs on Enhanced at all
      https://www.gta5-mods.com/tools/script-hook-v-net-enhanced

This used to say the fork and only the fork, and that was true when it was
written. Fumes called one method - Model.GetDimensions - that arrived after 3.6.0,
and that one method was enough: SHVDN is not strong-named, so the mod LOADS under
any of these builds quite happily and then throws the first time a line runs that
the installed build cannot do. Which is worse than not loading, because it fails
in five unrelated places at five unrelated moments and none of them mention a
version.

It now goes through the native underneath instead. Every API it calls is checked
against 3.6.0 stable, a 3.7.0 nightly and the 3.9.0 fork by tools/check_api.py, in
the repository, which is run before a release.

One honest caveat: that is an API check, not a play-test. Every method it uses
exists in all three builds; it has been played on the fork.

IF IT IS NOT LOADING
====================

There is no error and no crash, it simply is not there. Check, in this order:

1. scripts\ScriptHookVDotNet.log, in your GTA V folder. If Fumes is not
   mentioned at all, the dll was never loaded and this is a Script Hook problem,
   not a Fumes one. If it IS mentioned with an exception, that is the answer.

2. The version of ScriptHookVDotNet3.dll. Right-click it, Properties, Details.
   3.6.0.0 or higher is fine. Below that, update it.

   On GTA V ENHANCED it must be the Enhanced fork whatever the number says -
   nothing else runs on that edition at all.

3. That ScriptHookVDotNet.asi, ScriptHookVDotNet2.dll and ScriptHookVDotNet3.dll
   all came from the SAME download. Mixing an old asi with a new dll fails
   quietly in exactly this way.

4. scripts\Fumes\Fumes.log, or Documents\Fumes\Fumes.log if your game folder
   is not writable. If that file exists at all, Fumes is running and the problem
   is something else - say so, and paste it.


INSTALLING
==========

Copy the "scripts" folder from this zip into your GTA V folder, so you end up
with:

  Grand Theft Auto V\scripts\Fumes.dll
  Grand Theft Auto V\scripts\Fumes.ini
  Grand Theft Auto V\scripts\Fumes\stations.json
  Grand Theft Auto V\scripts\Fumes\icons\*.png

The icons folder is not optional decoration -- without it the HUD still works
but has no pictures in it.

To uninstall, delete those. Nothing is written anywhere else, and nothing in the
game's own files is touched.


PLAYING
=======

  E                 take the nozzle, start filling, hang it back up
                    (the game's CONTEXT control, so it follows a rebind and
                     shows the right button on a pad)

  Fuel gauge        the upright bar to the left of the minimap

  Shift + F         the settings menu

  NumPad *          with the nozzle in hand, change which of the game's eight
                    ropes the hose is made of
  NumPad 0          keep the one you are looking at

The rope keys do nothing at any other time, and nothing at all if you never pick
up a nozzle. Set [Nozzle] RopePicker = false to give the keys back once you have
settled on one.

The gauge shows the car you are in, or the one you are filling. Fuel is
persistent per vehicle and survives a save and a reload.

Your money is really taken. Prices differ by station.


THE MENU
========

Shift + F opens it. TAB changes page, UP and DOWN move, LEFT and RIGHT change a
setting, ENTER works a row, BACKSPACE saves and closes.

Everything you change shows on screen as you change it, and is written back into
Fumes.ini when you close the menu - in place, keeping every comment, and only the
lines that actually moved.

The first row on the HUD page is MOVE AND SIZE THE GAUGE. That one matters more
than it looks: where the minimap lands depends on your safe-zone slider and your
aspect ratio, and there is no way for a mod to ask - so the gauge beside it can
only be right on the screen it was tuned on, which is not yours. Press ENTER on
that row and the gauge is yours to place:

  ARROWS            move it
  SHIFT + ARROWS    resize it
  CTRL              hold for fine steps
  ENTER             keep it

It shows what it is in fractions AND in real pixels while you do, because "16 px
wide" is a number both ends of a conversation can check and "0.0046" is not.


SETTINGS
========

Everything the menu shows also lives in scripts\Fumes.ini, along with a great
deal it does not, and every entry is commented in place.
The sections are:

  [General]      units, whether the mod logs
  [Fuel]         tank sizes, how fast things drink, prices
  [Engine]       what an empty tank does to the car
  [Station]      how close you have to be, refuel speed
  [Nozzle]       the nozzle prop, the hose, the filling animation
  [HUD]          the gauge and the pump display
  [Hazard]       the odds a shootout at the pumps sets the vapour off
  [Consumption]  per-class thirst

Two of them are worth knowing about up front:

  [Station] LearnStations   25 forecourts ship with the mod and some of the
                       hand-written coordinates are simply wrong -- a blip on the
                       far side of a block from the forecourt it names. Nothing
                       depends on those numbers, since pumps are found as objects
                       rather than by coordinate, but a misplaced blip is still
                       the thing you navigate by. With this on, driving within
                       sight of any pump moves the nearest listed station onto
                       it, and a pump with no station listed near it becomes a
                       new one. That goes to stations.local.json, never to the
                       shipped file, so an update cannot undo it.

  [HUD] Opacity        the whole gauge, one number.


NOTES
=====

Fuel levels are saved to scripts\Fumes\tanks.json, or to Documents\Fumes if the
game folder is not writable. A log goes beside it when [General] Log is on;
it is the first place to look if something is not behaving.

If a rope type ever crashes the game, Fumes notices on the next launch and puts
that type on [Nozzle] BadRopeTypes so it is never offered again.
