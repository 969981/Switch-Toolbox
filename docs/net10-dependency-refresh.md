# .NET 10 dependency refresh experiment (2026-11-02)

**Branch:** `net10-deps-refresh` based on `net10-slnx`. This is not a replacement for `master` or a stable binary.

## Tested upgrade candidate
- **OpenTK** and **OpenTK.GLControl**: replace three projects' checked-in 3.0.1.0 DLL compiler references with NuGet **3.3.3** `PackageReference`.
- Keep the OpenTK 3.x namespaces and GLControl API; upgrading to **OpenTK 4** requires a separate migration of renderer, controls, plugins and math types.
- Keep original DLLs tracked for historical builds; the experimental publish step now overwrites old `Lib/OpenTK*.dll` copies with matching verified NuGet 3.3.3 assemblies. The top-level published copies are checked too.

## CI gates
The Windows pipeline must restore/build `Toolbox.slnx`, publish the Windows x64 executable, check both root OpenTK assemblies report 3.3.3.x, align `Lib` copies, run `--migration-smoke` from the published folder, then publish a **prerelease** with tag `net10-deps-<commit>`.

## Limitations
- OpenTK 3.3.3 and its GLControl package still target .NET Framework, even though they may restore in a modern Windows SDK-style application. CI may report NU1701; do not treat a clean compile as proof of runtime correctness.
- The smoke test does **not** create a real OpenGL desktop context. OpenGL drawing, plugin binary binding and BFRES/BNTX import/export using genuine permitted game assets must be tested manually on Windows.
- The workflow retains all original native and format-specific assemblies (Assimp, DirectXTexNet, Syroot, BrawlLib, FBX SDK). These are not upgraded blindly.

## Other upgrade candidates
| Candidate | Current risk | Next experiment |
|---|---|---|
| Newtonsoft.Json 6.x | Broad plugin and saved-format impact | NuGet 13.x only after reference and serializer compatibility testing |
| Octokit 0.31 | Old updater API calls | Upgrade together with updater integration tests |
| NAudio 1.8.4 | Audio and device APIs changed | Separate audio-playback experiments |
| CsvHelper 8 | Many breaking API changes | Separate CSV import/export fixtures |
| AssimpNet 4.1 | Native DLL ABI coupling | Upgrade managed + native together |
| K4os LZ4, Syroot, custom BNTX/BFRES decoders | Binary format regression potential | Source provenance and sample roundtrip tests first |

**Outcome:** pending the CI run triggered by this commit. Do not merge into `net10-slnx` until the real GUI/runtime checks are complete.
