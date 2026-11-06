# Switch-Toolbox net10-slnx: dependency remediation and real-resource acceptance

> Experimental branch only. Do **not** merge this work into `master` or label it stable until the runtime gates below have been met.

## Audit baseline and deterministic remediation

Source: successful [run 42144446405](https://github.com/969981/Switch-Toolbox/actions/runs/42144446405), 2026-11-06: **156** published DLL files, **85** unique names, **14** different-binary same-name groups, including **12** `needs-review` groups and **2** intended x86/x64 native variants.

The following 12 groups were observed and are normalized in the published .NET 10 package from the actual root assembly (not a blind package upgrade):

| Published DLL | Divergent historical location | Decision |
|---|---|---|
| AssimpNet.dll | `Lib/`, `Lib/Plugins/` | Pin historical copies to root build; test native import/export ABI separately |
| BarsLibrary.dll | `Lib/Plugins/` | Pin to root |
| ByamlExt.dll | `Lib/Plugins/` | Pin to root |
| CsvHelper.dll | `Lib/` | Pin to root |
| DirectXTexNet.dll | `Lib/Plugins/` | Pin managed bridge; separately validate native architecture and VC++ runtime |
| EditorCoreCommon.dll | `Lib/Plugins/` | Pin to root |
| LZ4.dll | `Lib/` | Pin to root |
| SARCExt.dll | `Lib/Plugins/` | Pin to root |
| Syroot.Maths.dll | `Lib/Plugins/` | Pin to root, run model/material readback |
| Syroot.NintenTools.Bfres.dll | `Lib/Plugins/` | Pin to root, run native BFRES readback |
| Syroot.NintenTools.NSW.Bfres.dll | `Lib/Plugins/` | Pin to root, run Switch BFRES readback |
| Syroot.NintenTools.NSW.Bntx.dll | `Lib/Plugins/` | Pin to root, run real BNTX readback |

The two deliberately unmodified native pairs are `x64/x86/DirectXTexNetImpl.dll` and `x64/x86/libzstd.dll`. They are not managed dependency conflicts.

Publishing runs `Scripts/Normalize-Net10PublishedDependencies.ps1` after copying the existing `Lib` tree and before packaging. It only overwrites **pre-existing** `Lib/` and `Lib/Plugins/` copies of the 12 allowlisted names, with the built root DLL as canonical. It records original/final SHA-256 values in `net10-resolved-dlls.json`. `Scripts/Audit-Net10Dependencies.ps1` independently regenerates `net10-dependencies.json` and fails if either DockPanelSuite DLL differs between the app root and `Lib/`. `Toolbox/MigrationSmokeTests.cs` also verifies actual runtime assembly resolution and public type loading.

**Acceptance:** The new run must have `DockPanelSuite.Passed=true`, `ReviewRequired=0` and no unexpected managed collision, followed by successful plugin discovery, native bridge loading and real BNTX tests. Identical bytes are **not** a guarantee of API/ABI compatibility; do not upgrade the remaining libraries until their component tests pass.

## Real editing gates (still required)

| Area | Minimum required test | Current evidence |
|---|---|---|
| Texture pixels | Load real BNTX; decode RGBA pixels; modify actual pixel values; re-encode/swizzle; save/reopen; verify changed pixels at their coordinates, metadata, and untouched mip/array slices | Existing real SV/ZA test **only changes the texture name** and asserts original mip payloads survive |
| GPU/OpenGL | On a Windows machine with an actual OpenGL context: compile shaders, upload actual textures, render model/framebuffer, verify image/readback and no GL errors | Headless WinForms smoke is insufficient |
| Skeleton animation | Load real skeleton, bind skin weights, animate multiple keyframe times; compare global bone matrices and rendered deformation | SV Trinity `.trmdl/.trmsh/.trskl/.tranm` are **not** native BFRES files or edited by Switch-Toolbox today |
| Model edits | Change a test vertex/mesh property in a supported native format, save/reopen and compare geometry/indices/transforms | Synthetic BFRES identify/save/reload is not a real game edit |
| Material edits | Change actual material parameters/texture references, save/reopen and verify material data and rendered visual result | Existing metadata-only BNTX test is insufficient |

### Texture format caveat

`STGenericTexture.CompressBlock` currently returns `null` for ASTC formats, so a successful ASTC pixel replacement **cannot be asserted** by a test that merely changes a name, copies compressed bytes, or adjusts metadata. Introduce a proven ASTC encoder and test the CPU encode/decode + BNTX reswizzle pipeline with real textures before enabling the pixel-edit acceptance gate.

### Dependency migration after baseline is stable

* **DockPanelSuite 3.1.1**: retain synchronized core/theme NuGet references, require identical published bytes and actual type loading; do not use `Lib` DLLs from 3.0.6.
* **OpenTK / GLControl**: first exercise the existing API on .NET 10 in a real context, then test a separately scoped same-major upgrade. OpenTK 4 is a breaking migration.
* **AssimpNet**: test the managed API, native `assimp` ABI, and FBX/DAE import/export together; archive reference fixtures and compare actual mesh/skin/material properties.
* **Syroot**: preserve source provenance for each game-specific assembly and validate BFRES/BNTX read/write semantics independently. Do not blanket-upgrade.
* **NuGet**: keep known stable package versions unless tests or security evidence require a version change.

## Release policy

`net10-slnx` may emit clearly labeled **experimental prereleases**. `master` remains the stable .NET Framework line, including its upstream sync/release behavior. Only promote after real hardware rendering, real edit/save/readback, plugin compatibility, and reproducible architecture-specific packaging pass.

## Empirical deep test results — 2026-11-06

The independent validation workflows deliberately preserve failing tests; an experimental release is **not** a claim of complete editor migration.

* [Deep pixel/GL/Assimp/Syroot run 42150776580](https://github.com/969981/Switch-Toolbox/actions/runs/42150776580):
  * **PASS:** genuine SV `pm0025.bntx` BC2 compressed-block copy -> BNTX save -> reload -> decoded RGBA comparison (**48 changed bytes**).
  * **PASS:** genuine ZA `pm0025.bntx` BC7 compressed-block copy -> BNTX save -> reload -> decoded RGBA comparison (**28 changed bytes**).
  * **PASS:** OpenTK 3 GLControl on GitHub Windows 2022 runner; real WGL context with **OpenGL 1.1.0 GDI Generic**; framebuffer clear readback `32,64,128,255`, and a drawn triangle readback `255,0,0,255`.
  * **UNSUPPORTED/FAIL:** GLSL Shader compilation. The hosted software OpenGL **1.1** driver cannot run modern GLSL; `GL.CreateShader` causes an uncatchable native access violation if executed. The test now checks the OpenGL version first and reports `NotSupportedException` without crashing.
  * **PASS:** AssimpNet 4.1 managed/native bridge with its pinned Windows x64 `assimp.dll`, importing a real ASCII STL fixture into one mesh, 3 vertices.
  * **PASS:** direct Syroot BNTX parser on both actual SV and ZA samples, one texture each. A prior reflection-based optional-constructor test was incorrect and has been fixed.
  * The separate workflow is **red because GLSL is unsupported**, while the other six deep checks pass. None of these tests asserts game model rendering or arbitrary ASTC image encoding.

* [SV Trinity glTF animation/geometry/material run 42150623368](https://github.com/969981/Switch-Toolbox/actions/runs/42150623368):
  * **PASS:** gftool exported actual `pm0025` data to glTF with **21 meshes, 81 skin joints, 102 animated bone channels and 5 materials**.
  * **PASS:** sampled bone world matrices at `0.0`, `0.56666666`, `1.13333333` seconds; **57 joints** had non-static transforms.
  * **PASS:** validated `JOINTS_0` and `WEIGHTS_0`, inverse bind matrices and CPU skinned **1,644 sampled vertices**; **1,644 vertices changed** between samples.
  * **PASS:** edited a real position accessor and saved an independent copied glTF binary buffer, then reloaded and checked the changed coordinate and preservation of all other bytes. Applied the same test to a real SV backpack (6 meshes, 2 materials).
  * **PASS:** edited glTF PBR material factor and reloaded it without changing buffers/skins/animations.
  * **NOT VERIFIED:** Switch-Toolbox editing or saving native `.trmdl/.trmsh/.trskl/.tranm`; full BFRES model/material editing; OpenGL rendering of real game meshes; GPU skinning; ASTC encoder and arbitrary pixel replacement.

The native Assimp library is now explicitly staged in the **experimental** release package, based on the pinned `packages/AssimpNet.4.1.0/runtimes/win-x64/native/assimp.dll` that passed the ABI smoke test. Keep `master` untouched.

### Remaining release gates

1. Run shader compilation, real model loading and model frame-buffer readback on a Windows runner with a valid desktop OpenGL 3.x/4.x context and hardware/driver support. GDI Generic 1.1 is not sufficient.
2. Implement/test an ASTC encode path for arbitrary bitmap replacements; copying existing valid compressed blocks only verifies the binary read/write and image decoding chain.
3. Add direct native BFRES model+material editing and readback, and separate Switch-Toolbox Trinity adapters if native `.tr*` support is desired.
4. Build real FBX/DAE import/export regression fixtures for Assimp ABI beyond the minimal STL test; validate OpenTK 3.x and Syroot separately before any package upgrade.
