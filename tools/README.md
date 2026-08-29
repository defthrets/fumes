# tools/

Self-contained build toolchain. **Not committed** -- restore it with the script below.

`build.ps1` does not use `dotnet build`, because the .NET SDK on this machine is broken:
`C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.28` is a partial install (3 files
where 8.0.19 has 184), so every `dotnet` invocation fails on a missing `hostpolicy.dll`.
`dotnet --list-runtimes` still lists 8.0.28, which hides the cause.

Instead the build drives a Roslyn `csc.exe` that runs on .NET Framework directly. No SDK,
no Visual Studio, no admin rights. The same pattern is used by the hoodrich and overspray
repositories and is reusable for any net48 project on this box.

## Restore

```powershell
$tools = $PSScriptRoot
$pkgs = @{
  "roslyn" = "https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/4.8.0/microsoft.net.compilers.toolset.4.8.0.nupkg"
  "refasm" = "https://api.nuget.org/v3-flatcontainer/microsoft.netframework.referenceassemblies.net48/1.0.3/microsoft.netframework.referenceassemblies.net48.1.0.3.nupkg"
}
foreach ($k in $pkgs.Keys) {
  $zip = Join-Path $tools "$k.zip"
  Invoke-WebRequest -Uri $pkgs[$k] -OutFile $zip -UseBasicParsing
  Expand-Archive $zip (Join-Path $tools $k) -Force
  Remove-Item $zip
}
```

Produces:

- `tools\roslyn\tasks\net472\csc.exe` -- the C# compiler
- `tools\refasm\build\.NETFramework\v4.8\*.dll` -- net48 reference assemblies

The only other thing the build needs is `ScriptHookVDotNet3.dll`, which it takes from
whichever GTA V install is present. Both editions ship the identical file.
