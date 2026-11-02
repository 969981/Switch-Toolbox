# Switch-Toolbox .NET 10 migration

This branch is an **experimental** migration from the historical .NET Framework 4.8 application. The stable legacy Windows build and its upstream syncing workflow remain on `master`.

## Scope
- All five projects changed to the SDK project format and `net10.0-windows`.
- WinForms and WPF desktop references enabled as appropriate.
- Explicit source, resource, and output items retained rather than importing them implicitly.
- `Toolbox.slnx` is the new .NET 10 solution; `Toolbox.sln` is retained only for historical reference in this branch.
- Old fixed paths of assembly references repaired where corresponding tracked assemblies exist.
- Windows CI runs restore, build, publish, verifies `Toolbox.exe` and `Toolbox.Library.dll`, and uploads the complete archive. GitHub prerelease only on a successful build.

## Important risks
- Third-party managed DLLs in `Toolbox/Lib`, COM / Windows Media Player interop and native DLLs were originally developed for .NET Framework. Compilation alone does not establish runtime compatibility.
- Some legacy APIs such as `System.Activities`, `System.Deployment`, `System.Messaging`, and `System.Design` may need API replacements or source-level work.
- WPF/WinForms components, plugins, resource loading, Assimp, OpenTK, audio and decoder paths require real Windows smoke tests.

## Validation
Open `Toolbox.slnx` with the latest Visual Studio / .NET 10 SDK, or execute:
```powershell
dotnet restore Toolbox.slnx
dotnet build Toolbox.slnx -c Release
dotnet publish Toolbox/Toolbox.csproj -c Release -r win-x64 --self-contained false
```

## Upstream sync policy
Upstream: `https://github.com/KillzXGaming/Switch-Toolbox`, branch `master`. The existing automatic sync/release workflow is preserved on the stable `master` branch. Do not replace the legacy build or make this branch a primary release source until the .NET 10 migration passes compile and runtime testing.

**Repository settings:** GitHub scheduled workflows execute on the repository *default branch*. Set the default branch to `master` under Settings → Branches if it currently points elsewhere; otherwise the stable automatic schedule may not run as intended.

## Temporary warnings policy
The migration currently suppresses only `WFO1000` (WinForms designer serialization) and `SYSLIB0011` (BinaryFormatter obsoletion) while porting the source. This is **not evidence of runtime compatibility**. .NET 10 cannot use the in-box BinaryFormatter implementation at runtime: legacy serialization call paths must be replaced with safe serializers before those features can work. Do **not** install the unsafe BinaryFormatter compatibility package for untrusted game resources. All other compiler errors remain enabled, and the release is marked experimental.
