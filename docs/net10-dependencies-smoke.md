# Dependency upgrade and runtime verification — 2026-11-02

## Source of truth
.NET 10 experimental branch `net10-slnx`, not stable `master`. Do not merge until real Windows GUI/OpenGL validation.

## Migrated to NuGet PackageReference
| Dependency | Previous reference | New reference | Rationale |
|---|---|---|---|
| SharpZipLib | 1.1.0 from checked-in packages | 1.4.2 NuGet | Supported .NET 6+ assembly, preserves ICSharpCode.SharpZipLib namespaces |
| ZstdSharp.Port | 0.7.2 checked-in net461 DLL | 0.8.8 NuGet | Newer managed Zstd implementation |
| DockPanelSuite + ThemeVS2015 | 3.0.6 net40 DLLs | 3.1.1 NuGet (same version both) | Docking controls with .NET Core / Windows support |

The in-repository `packages.config` and historical DLLs are retained for the .NET Framework master branch. On the migration branch, the three groups above are referenced via `PackageReference`, and no old direct compiler reference should remain. Runtime loader and published output verification are mandatory because older plugins may bind old copies from `Lib`.

## Phased upgrades (do not blindly replace)
- **OpenTK 3.0 / GLControl**: candidate 3.3.3 within the same major API; OpenTK 4 has breaking namespaces, GLControl and graphics behavior changes. Needs actual OpenGL context and rendering tests before upgrade.
- **AssimpNet 4.1 / native Assimp**: API and native ABI must be upgraded together; existing FBX/native libraries are separate.
- **DirectXTexNet RC / mixed mode DLL**: not an ordinary managed-only NuGet swap; requires matching native x64 deployment and VC++ runtime.
- **Syroot BFRES/BNTX, BrawlLib, custom decoder and editor DLLs**: project/game-format-specific builds; need source provenance and known binary fixtures before replacement.
- **BinaryFormatter / Thread.Abort**: runtime-incompatible APIs. Replace with safe explicit serialization and cooperative cancellation; do not install BinaryFormatter compatibility for untrusted resources.

## CI smoke test scope
Run from the **published win-x64 folder** with `Toolbox.exe --migration-smoke`. Verifies Windows runtime dependencies, plugin discovery using `Lib/Plugins`, and blank-file generated BFRES/BNTX identify/load/save/reload paths. These are synthetic fixtures and **do not** assert compatibility with actual Nintendo game model/texture files. The smoke mode deliberately avoids the graphics event loop and does not validate GL rendering, texture upload, thumbnails or real user workflows.

## Manual GUI validation still required
1. Launch the ZIP on an actual Windows desktop with .NET 10 Desktop Runtime.
2. Open real, permitted BFRES/BNTX game samples, inspect textures/models and render GL.
3. Export, import, save, reopen and compare content/hash semantics.
4. Verify plugins, native bridges, compression and audio playback.
5. Confirm file type selection, error handling and memory behavior after repeated operations.
