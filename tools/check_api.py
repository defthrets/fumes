"""
Checks every ScriptHookVDotNet API the mod calls against other builds of SHVDN.

WHY THIS EXISTS. SHVDN is not strong-named -- its PublicKeyToken is null -- so the
CLR binds it by simple name and ignores the version entirely. A build compiled
against the 3.9.0.0 Enhanced fork therefore LOADS quite happily under 3.6.0 stable
or a 3.7.0 nightly. Nothing complains, and the mod appears to work.

It appears to work until a line runs that calls a method the installed build does
not have, and then it throws MissingMethodException at that moment and only that
moment. Model.GetDimensions was in five places here: the nozzle, the filler guess,
the can going down, the can in his hand, the pump anchor. On stable that is not one
bug, it is five, each turning up somewhere unrelated, none of them at start-up, and
none of them saying what they have in common.

So: dump the public surface of every SHVDN build you can get hold of, dump what the
source actually calls, and compare. It found the one real gap in a minute; reading
the code would not have found it at all, because the code looks fine.

USAGE
    python tools/check_api.py <dir-of-ScriptHookVDotNet3.dll> [more dirs...]

Each directory needs a ScriptHookVDotNet3.dll. The first is treated as the
reference -- the one the mod is built against -- and the rest are compared to it.
Each is loaded in its own PowerShell process, because the CLR caches an assembly by
simple name and would hand back the first one for all of them.
"""

import glob
import io
import os
import re
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

# Every SHVDN member the mod is known to lean on. Kept as a list rather than
# derived from the IL, because reading member references out of a compiled
# assembly needs a metadata reader that is not in the box -- and this catches what
# matters with a grep and no dependencies.
#
# The cost is that it only knows what it is told, so a new API wants a line here.
WATCH = {
    "GTA.Game": ["SetControlValueNormalized", "IsControlJustPressed", "IsControlPressed",
                 "IsKeyPressed", "GenerateHash", "LastFrameTime", "GameTime",
                 "IsMissionActive", "DisableControlThisFrame", "Player"],
    "GTA.Ped": ["IsInStealthMode", "Bones", "Weapons", "CurrentVehicle", "LastVehicle"],
    "GTA.Entity": ["GetOffsetPosition", "AttachTo", "Detach", "IsAttached", "Model"],
    "GTA.Model": ["GetDimensions", "Request", "IsLoaded", "IsValid", "MarkAsNoLongerNeeded",
                  "IsElectricVehicle"],
    "GTA.WeaponCollection": ["CurrentWeaponObject", "Current", "Select"],
    "GTA.World": ["DrawPolygon", "DrawMarker", "DrawLine", "CreateProp", "GetNearbyVehicles"],
    "GTA.EntityBone": ["Index", "Position", "IsValid"],
    "GTA.UI.Screen": ["Resolution", "AspectRatio"],
    "GTA.Vehicle": ["ClassType", "LocalizedName", "Driver", "IsEngineRunning", "IsPersistent",
                    "FuelLevel", "PetrolTankVolume"],
    "GTA.Native.OutputArgument": ["GetResult"],
    "GTA.Rope": ["Length", "VertexCount", "GetVertexCoord", "ActivatePhysics"],
    "GTA.Prop": ["IsPositionFrozen"],
}

DUMP = r"""
param([string]$Dll, [string]$Out)
$a = [Reflection.Assembly]::LoadFrom($Dll)
try { $types = $a.GetTypes() }
catch [Reflection.ReflectionTypeLoadException] { $types = $_.Exception.Types | Where-Object { $_ -ne $null } }
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# " + $a.GetName().Version)
foreach ($t in $types) {
  if (-not $t.IsPublic) { continue }
  $lines.Add("T " + $t.FullName)
  try {
    foreach ($m in $t.GetMembers([Reflection.BindingFlags]'Public,Instance,Static,DeclaredOnly')) {
      $lines.Add("M " + $t.FullName + "." + $m.Name)
    }
  } catch {}
}
[IO.File]::WriteAllLines($Out, ($lines | Sort-Object -Unique))
"""


def surface(dll, script):
    """The public members of one SHVDN, in its own process so the CLR cannot cache it."""
    out = tempfile.mktemp(suffix=".txt")

    subprocess.run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass",
                    "-File", script, "-Dll", dll, "-Out", out],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    if not os.path.exists(out):
        return None, {}

    version, members = "?", {}

    for line in io.open(out, encoding="utf-8", errors="replace"):
        line = line.rstrip("\n")
        if line.startswith("# "):
            version = line[2:]
        elif line.startswith("M "):
            t, _, m = line[2:].rpartition(".")
            members.setdefault(t, set()).add(m)

    os.remove(out)
    return version, members


def source():
    """The mod's code with comments stripped -- a doc comment naming a method is not a call."""
    out = []

    for f in glob.glob(os.path.join(ROOT, "src", "**", "*.cs"), recursive=True):
        t = io.open(f, encoding="utf-8").read()
        t = re.sub(r"/\*.*?\*/", "", t, flags=re.S)
        t = "\n".join(l for l in t.split("\n") if not l.lstrip().startswith("//"))
        out.append(t)

    return "\n".join(out)


def main():
    dirs = sys.argv[1:]
    if len(dirs) < 2:
        raise SystemExit(__doc__.strip().split("USAGE")[1].strip())

    script = tempfile.mktemp(suffix=".ps1")
    io.open(script, "w", encoding="utf-8").write(DUMP)

    src = source()

    ref_version, ref = surface(os.path.join(dirs[0], "ScriptHookVDotNet3.dll"), script)
    print("  reference  %-12s %s" % (ref_version, dirs[0]))

    others = []
    for d in dirs[1:]:
        v, m = surface(os.path.join(d, "ScriptHookVDotNet3.dll"), script)
        print("  against    %-12s %s" % (v, d))
        others.append((v, m))

    print()

    gaps = 0
    checked = 0

    for t in sorted(WATCH):
        for n in WATCH[t]:
            if n not in ref.get(t, set()):
                continue
            if not re.search(r"\b" + re.escape(n) + r"\b", src):
                continue

            checked += 1

            for v, m in others:
                if n not in m.get(t, set()):
                    gaps += 1
                    print("  MISSING in %-12s %s.%s" % (v, t, n))

    os.remove(script)

    print()
    print("  %d call(s) checked, %d gap(s)" % (checked, gaps))

    return 1 if gaps else 0


if __name__ == "__main__":
    sys.exit(main())
