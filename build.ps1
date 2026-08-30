<#
  Fumes build script.

  Drives the self-contained Roslyn compiler in tools\ rather than `dotnet build`, because the
  .NET SDK on this machine is broken (Microsoft.NETCore.App\8.0.28 is a partial install, so
  every `dotnet` invocation dies on a missing hostpolicy.dll). This path needs no SDK, no
  Visual Studio and no admin rights. Same approach as the Hoodrich and Overspray repos.

  Usage:
    .\build.ps1                        # build to .\build\Fumes.dll
    .\build.ps1 -Deploy                # build, then install into both GTA V editions
    .\build.ps1 -Deploy -Target Legacy # ...into one of them
    .\build.ps1 -Deploy -FreshData     # ...and overwrite the installed data files
    .\build.ps1 -Package               # build a release zip in .\release\
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [switch]$Deploy,
    [switch]$Package,

    # Which install(s) -Deploy writes to. Fumes is a pure SHVDN script with no asset
    # dependencies and both editions ship the identical ScriptHookVDotNet3.dll, so one build
    # runs on both.
    [ValidateSet('Legacy', 'Enhanced', 'Both')]
    [string]$Target = 'Both',

    # Overwrite the installed data files with the ones just built. Off by default so a
    # player's hand-edited stations.json survives an update.
    [switch]$FreshData,

    [string]$GtaDir = 'C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V',
    [string]$EnhancedDir = 'C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V Enhanced'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$csc    = Join-Path $root 'tools\roslyn\tasks\net472\csc.exe'
$refDir = Join-Path $root 'tools\refasm\build\.NETFramework\v4.8'
$srcDir = Join-Path $root 'src\Fumes'
$outDir = Join-Path $root 'build'
$outDll = Join-Path $outDir 'Fumes.dll'

if (-not (Test-Path $csc))    { throw "Compiler missing: $csc  (see tools\README.md)" }
if (-not (Test-Path $refDir)) { throw "net48 reference assemblies missing: $refDir" }

# SHVDN is taken from whichever install is actually there. It is the same file in both.
$shvdn = $null
foreach ($dir in @($GtaDir, $EnhancedDir)) {
    $candidate = Join-Path $dir 'ScriptHookVDotNet3.dll'
    if (Test-Path $candidate) { $shvdn = $candidate; break }
}
if (-not $shvdn) { throw "ScriptHookVDotNet3.dll not found in either install." }

New-Item -ItemType Directory -Force $outDir | Out-Null

# --- references -------------------------------------------------------------
# Deliberately minimal. Fumes has ZERO external runtime dependencies: only the BCL and
# SHVDN. No Newtonsoft, no LemonUI, no NativeUI -- nothing that can lose a version fight
# with another mod sharing the same scripts\ folder.
$refNames = @(
    'mscorlib.dll'
    'System.dll'
    'System.Core.dll'
    'System.Drawing.dll'
    'System.Windows.Forms.dll'
)
$refs = @()
foreach ($n in $refNames) {
    $p = Join-Path $refDir $n
    if (-not (Test-Path $p)) { throw "Reference assembly missing: $p" }
    $refs += "/reference:`"$p`""
}
$refs += "/reference:`"$shvdn`""

# --- sources ----------------------------------------------------------------
$sources = Get-ChildItem $srcDir -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
    ForEach-Object { $_.FullName }

if (-not $sources) { throw "No .cs sources found under $srcDir" }

# --- compiler options -------------------------------------------------------
$opts = @(
    '/target:library'
    '/platform:x64'
    '/langversion:9.0'
    '/nologo'
    '/warnaserror-'
    '/warn:4'
    '/nostdlib+'
    '/utf8output'
    "/out:`"$outDll`""
)
if ($Configuration -eq 'Debug') {
    $opts += '/debug:portable', '/define:DEBUG;TRACE', '/optimize-'
} else {
    $opts += '/debug-', '/optimize+'
}

$rsp = Join-Path $outDir 'build.rsp'
($opts + $refs + ($sources | ForEach-Object { "`"$_`"" })) | Set-Content -Path $rsp -Encoding UTF8

Write-Host "Compiling $($sources.Count) source files -> $outDll ($Configuration)" -ForegroundColor Cyan
$sw = [Diagnostics.Stopwatch]::StartNew()
& $csc "@$rsp"
$exit = $LASTEXITCODE
$sw.Stop()

if ($exit -ne 0) { throw "Compilation failed (csc exit $exit)." }
Write-Host ("OK  {0:N0} bytes in {1:N1}s" -f (Get-Item $outDll).Length, $sw.Elapsed.TotalSeconds) -ForegroundColor Green

# --- deploy -----------------------------------------------------------------
function Get-ReloadKey([string]$gameDir) {
    <#
        Whatever SHVDN is actually set to reload on, per install. Not assumed: the two
        installs on this machine disagree, and a wrong key in a reminder is worse than none.
    #>
    $ini = Join-Path $gameDir 'ScriptHookVDotNet.ini'
    if (-not (Test-Path $ini)) { return $null }

    $hit = Select-String -Path $ini -Pattern '^\s*ReloadKeyBinding\s*=\s*(\S+)' |
           Select-Object -First 1

    if ($hit) { return $hit.Matches[0].Groups[1].Value }
    return $null
}

function Deploy-To([string]$gameDir, [string]$label) {
    if (-not (Test-Path $gameDir)) {
        Write-Host "skip $label - not installed at $gameDir" -ForegroundColor DarkGray
        return
    }

    $scripts = Join-Path $gameDir 'scripts'
    if (-not (Test-Path $scripts)) {
        Write-Host "skip $label - no scripts folder (ScriptHookVDotNet not installed?)" -ForegroundColor Yellow
        return
    }

    Write-Host "$label -> $scripts" -ForegroundColor Cyan

    # THE COPY IS ATTEMPTED, NOT PRE-REFUSED.
    #
    # This used to check whether the game was running and give up if it was, which was a
    # guess dressed up as a rule: SHVDN SHADOW-COPIES script assemblies into the .NET
    # download cache and runs them from there, so the dll sitting in scripts\ is very often
    # not locked at all. Refusing on the strength of a process name meant closing the game
    # for every change for no reason.
    #
    # So it tries. A genuine lock throws, and that is reported for what it is.
    $locked = $false
    try {
        Copy-Item $outDll $scripts -Force -ErrorAction Stop
    } catch {
        $locked = $true
        Write-Host "  LOCKED Fumes.dll is in use - the data files below still went." -ForegroundColor Yellow
        Write-Host "         Close the game and re-run to update the dll." -ForegroundColor DarkGray
    }

    $pdb = Join-Path $outDir 'Fumes.pdb'
    if (-not $locked -and (Test-Path $pdb)) {
        try { Copy-Item $pdb $scripts -Force -ErrorAction Stop } catch { }
    }

    if (-not $locked -and (Get-Process GTA5, GTA5_Enhanced -ErrorAction SilentlyContinue)) {
        # The reload key is read from SHVDN's own ini rather than assumed. The two installs
        # on this machine do not agree on it -- Legacy is Pause, Enhanced is Insert -- so a
        # hardcoded reminder would be wrong half the time.
        $key = Get-ReloadKey $gameDir
        $named = if ($key) { $key } else { "the SHVDN reload key" }
        Write-Host "  LIVE   dll replaced while the game runs - press $named in game to reload." -ForegroundColor Green
    }

    $dataSrc = Join-Path $root 'data'
    $dataDst = Join-Path $scripts 'Fumes'
    New-Item -ItemType Directory -Force $dataDst | Out-Null

    # THE ONE FILE THE MOD ITSELF WRITES. It is the player's saved fuel for every vehicle they
    # own, it lives in the folder being managed here, and it looks exactly like a data file we
    # stopped shipping. Anything else added to Paths that the mod WRITES has to be added to
    # this list in the same change, or the next deploy destroys it and nothing says why.
    $ours = @('tanks.json', 'tanks.json.bak', 'stations.local.json', 'stations.local.json.bak',
              'Fumes.log', 'Fumes.log.1')

    Get-ChildItem $dataSrc -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($dataSrc.Length).TrimStart('\')
        $dst = Join-Path $dataDst $rel
        New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null

        if (-not (Test-Path $dst)) {
            Copy-Item $_.FullName $dst
            Write-Host "  new    $rel" -ForegroundColor DarkGray
            return
        }

        if ((Get-FileHash $_.FullName).Hash -eq (Get-FileHash $dst).Hash) {
            Write-Host "  same   $rel" -ForegroundColor DarkGray
        } elseif ($FreshData) {
            Copy-Item $_.FullName $dst -Force
            Write-Host "  update $rel" -ForegroundColor Green
        } else {
            Write-Host "  KEEP   $rel  (differs from source - re-run with -FreshData to overwrite)" -ForegroundColor Yellow
        }
    }

    # The ini is never overwritten: it is the file players hand-edit. Say what is missing from
    # it instead, so a new setting is not silently on its default forever.
    $iniSrc = Join-Path $root 'Fumes.ini'
    $iniDst = Join-Path $scripts 'Fumes.ini'

    if (-not (Test-Path $iniSrc)) { return }

    if (-not (Test-Path $iniDst)) {
        Copy-Item $iniSrc $iniDst
        Write-Host "  new    Fumes.ini" -ForegroundColor DarkGray
        return
    }

    $srcKeys = Read-IniKeys $iniSrc
    $dstKeys = Read-IniKeys $iniDst

    $absent = $srcKeys | Where-Object { $dstKeys -notcontains $_ }
    $extra  = $dstKeys | Where-Object { $srcKeys -notcontains $_ }

    if ($absent) {
        Write-Host "  STALE  Fumes.ini is missing $($absent.Count) setting(s):" -ForegroundColor Yellow
        Write-Host "         $($absent -join ', ')" -ForegroundColor DarkGray
        Write-Host "         Defaults apply until they are added." -ForegroundColor DarkGray
    }
    if ($extra) {
        Write-Host "  STALE  Fumes.ini has $($extra.Count) setting(s) nothing reads:" -ForegroundColor Yellow
        Write-Host "         $($extra -join ', ')" -ForegroundColor DarkGray
    }
    if (-not $absent -and -not $extra) {
        Write-Host "  keep   Fumes.ini ($($srcKeys.Count) settings, all current)" -ForegroundColor DarkGray
    }
}

function Read-IniKeys {
    <#
        Every "Section.Key" in an ini, so two of them can be compared by what they actually
        SET rather than by which headings they happen to have. A section-only comparison
        reports a file as current while it carries twenty dead keys inside the right headings.
    #>
    param([string]$Path)

    $section = ''
    $keys = New-Object System.Collections.Generic.List[string]

    foreach ($line in (Get-Content -LiteralPath $Path)) {
        $t = $line.Trim()

        if ($t -match '^\[(.+)\]$') { $section = $Matches[1]; continue }
        if ($t.StartsWith(';') -or $t.StartsWith('#') -or -not $t.Contains('=')) { continue }
        if (-not $section) { continue }

        $keys.Add("$section.$($t.Split('=')[0].Trim())")
    }

    return $keys
}

if ($Deploy) {
    # No process check at all any more. Deploy-To attempts the copy and reports a real lock
    # if it hits one; a running game is usually not a reason to stop, because SHVDN runs
    # scripts out of a shadow copy rather than out of scripts\.
    if ($Target -in 'Legacy', 'Both')   { Deploy-To $GtaDir      'Legacy' }
    if ($Target -in 'Enhanced', 'Both') { Deploy-To $EnhancedDir 'Enhanced' }

    Write-Host "Deploy complete." -ForegroundColor Green
}

# --- packaging ---------------------------------------------------------------
# A zip that merges straight over the GTA V folder, because that is the one install
# instruction nobody gets wrong. Built only from the repo -- never from the game folder, or a
# release ships whatever this machine happens to be testing with, including somebody's save.
if ($Package) {
    $version = (Select-String -Path (Join-Path $root 'src\Fumes\Core\Log.cs') `
                              -Pattern 'Version = "([^"]+)"').Matches[0].Groups[1].Value

    $relDir = Join-Path $root 'release'
    $stage  = Join-Path $relDir "Fumes-$version"
    $zip    = Join-Path $relDir "Fumes-$version.zip"

    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

    $scripts = Join-Path $stage 'scripts'
    $dataOut = Join-Path $scripts 'Fumes'
    New-Item -ItemType Directory -Force -Path $dataOut | Out-Null

    Copy-Item $outDll (Join-Path $scripts 'Fumes.dll')
    Copy-Item (Join-Path $root 'Fumes.ini') (Join-Path $scripts 'Fumes.ini')
    Copy-Item (Join-Path $root 'data\*.json') $dataOut

    # The icons, which are named outright rather than swept up by a wildcard.
    #
    # data\*.json above catches the data and nothing else, so a folder added later goes to
    # nobody -- every download is silently missing it, and the one machine that cannot notice
    # is this one, where the files are already in place from being deployed. That exact thing
    # happened to Overspray's voice pack; this line is here so it does not happen again.
    $icons = Join-Path $root 'data\icons'
    if (Test-Path $icons) { Copy-Item $icons $dataOut -Recurse }

    foreach ($doc in @('README.txt', 'CHANGES.txt')) {
        $p = Join-Path $relDir $doc
        if (Test-Path $p) { Copy-Item $p $stage }
    }

    # Belt and braces: a save or a log in a release zip would overwrite the first thing a
    # player did with the mod.
    Get-ChildItem $stage -Recurse -Include 'tanks.json', '*.log', '*.bak' |
        ForEach-Object { Remove-Item $_.FullName -Force }

    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal

    $files = (Get-ChildItem $stage -Recurse -File).Count
    $size  = [math]::Round((Get-Item $zip).Length / 1KB)

    Write-Host ""
    Write-Host "Packaged  $zip" -ForegroundColor Green
    Write-Host "          $files files, $size KB, version $version"
}
