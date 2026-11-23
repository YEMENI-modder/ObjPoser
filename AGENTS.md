# AGENTS.md - project guide for AI agents and new contributors

Read this file first. It describes **what ObjPoser is, how the code is organised, the conventions you must respect, and how to build / test / extend it**. (`CLAUDE.md` simply points here.)

> Provenance: this entire code base was written by an AI (Claude Sonnet 5.5, Anthropic) for the project owner **YEMENI-modder**, who directed the work and did all the testing. Keep the credits in `README.md`, `README.ar.md` and the About dialog (`UI.cs`, `AboutDialog`) accurate if you change them.

## 1. What it is
**ObjPoser** is a Windows desktop tool (C#, WinForms, **.NET Framework 4.8**, single exe, no NuGet packages). Input: a Wavefront `.obj` of a humanoid standing in a **T-Pose**. The tool fits a 69-joint skeleton to the mesh automatically, skins the mesh, lets the user choose / edit poses (every joint incl. fingers, toes, face), previews the result and exports a posed `.obj` (or a rigged `.glb`). It can also restore a posed OBJ to its T-Pose, and render screenshots of all poses.

The owner mainly uses it on *Resident Evil 4* character models (low-poly, 3-5k vertices, Y-up, duplicated vertices at UV seams). Never commit game assets.

## 2. Repository map
```
src/Core.cs        Xf (3x3 + translation), Material, ObjData (OBJ parser + restore metadata), AxisMap (up/front detection), Analysis (T-Pose check)
src/Skeleton.cs    Skeleton: joint table (names, parents, mirror, groups, palm frames) + Build() = fit joints to the mesh
src/Pose.cs        Pose (rotations + translations per joint), presets, hand presets, face expressions, JSON, mirror, lerp
src/Skinner.cs     Skinner: skin weights (weld, distance weights, smoothing, top-4) + LBS / DQS posing + normals
src/Export.cs      AppInfo (version/author/repo), ObjExporter, ObjRestore, GlbExporter
src/Textures.cs    TextureLoader (TGASharpLib, DDSReaderSharp, System.Drawing) and file lookup for .mtl map_Kd
src/UI.cs          Program.Main, Viewport (software renderer), BatchDialog, JointPicker, AboutDialog, MainForm
src/ThirdParty/    TGASharpLib.cs, DDSReaderSharp.cs (MIT, unmodified, with their licenses)
src/ObjPoser.csproj  classic csproj, net48, lists the Compile items (add new files here AND in build.bat AND in the CI/test commands if needed)
tests/CoreSelfTest.cs  console self-test (exit code 0 = pass)
samples/sample_tpose.obj  synthetic box figure used by the CI self-test
.github/workflows/build.yml  CI: msbuild + self-test + zip artifact; release on tag v*
assets/icon.ico    application icon (white T-Pose figure on blue)
docs/images/       README screenshots
```
`UI.cs` is intentionally one file. Core classes have **no UI dependency** (only `System.Drawing` is referenced by `Core`-adjacent code through `Textures.cs`/`UI.cs`; `Core.cs`, `Skeleton.cs`, `Pose.cs`, `Skinner.cs`, `Export.cs` compile as a console library).

## 3. Conventions you MUST keep
### Coordinate system ("working space")
- **Y up, +Z = front (face direction), +X = the character's LEFT**, right-handed.
- Files are converted with `AxisMap` (3x3 signed permutation, rows = left, up, front expressed in file coordinates). `AxisMap.Detect` guesses it (thinnest axis = depth, symmetry test for lateral vs up, arm band for up sign, foot position for front sign). `ToWork` / `FromWork` convert; the exporter converts back so the output OBJ is in the original coordinates.
- Everything in `Skeleton`, `Pose`, `Skinner` works in working space. Heights are normalised with `Skeleton.H` (mesh height).

### Skeleton (69 joints, defined once in the static constructor of `Skeleton`)
Order matters (a parent always comes before its children):
`Hips, Spine, Chest, Neck, Head, Jaw, L/R Eye, L/R Eyelid, L/R Brow, Nose, L/R Mouth` then for each side `Clavicle, UpperArm, ForeArm, Hand, (Thumb|Index|Middle|Ring|Pinky)1..3` then for each side `Thigh, Shin, Foot, Toe1..Toe5`.
- Always refer to joints **by name**: `Skeleton.Idx("L UpperArm")`. Never hard-code indices.
- `Skeleton.Mirror[i]` = opposite-side joint (self for centre joints). `Skeleton.Group[i]` drives the UI joint picker. `Skeleton.HasFrame[i]` is true for Hand / finger / toe joints.
- Instance data from `Skeleton.Build`: `Pos[j]` (rest joint position), `End[j]` (segment end used for skin weights), `Radius[j]`, `Frame[j]`.
- **Pitfall:** `End[j]` is usually an *alias of the child's `Pos` array* (so moving a joint with `ApplyOffsets` moves the parent's segment end). `OwnEnd[j]` marks ends that are separate arrays (leaf joints). All `Pos[]` must be assigned **before** the `End[]` pass (a bug here produced null ends once).
- Face and finger positions are estimated from head / hand geometry; hand orientation (`PalmsForward`) is auto-detected (flat hand extent) and can be forced from the Rig tab.

### Pose format
- `Pose.Rot[j]` = Euler degrees, matrix `R = Rx * Ry * Rz` (**Z applied first**), in the *parent's rest-aligned frame* (the T-Pose rest frame has all joint axes aligned with the world axes).
- Joints with a palm frame (`HasFrame`: hands, fingers, toes) use `localRot = F * R * F^T`, where `F` = `Skeleton.Frame[j]` (columns = canonical axes in world coordinates: **X along the bone, Z = curl axis (positive curls toward the palm), Y = Z x X**). So for every finger/toe **Z = curl**. Frames are built with `MakeFrame(boneDir, palmNormal)` (palm down: n = -Y; palms forward: n = +Z; toes: bone +Z, n = -Y).
- `Pose.Trans[j]` = translation of the joint in the parent's frame, as a **fraction of body height**. The Hips translation moves the whole body.
- `Snap` = move the posed mesh so its lowest vertex touches the original floor; `LiftH` = extra lift (fraction of H).
- **Mirror** (`Pose.Mirrored`): swap `Skeleton.Mirror` partners; rotations `(x,-y,-z)` for normal joints and `(-x,-y,z)` for `HasFrame` joints; translations `(-x,y,z)`. `Pose.MirrorRot(j, rot)` does one joint (used by "Symmetric edit").
- JSON (`ToJson` / `FromJson`): joint rotations and translations **by joint name**; zero joints are omitted; unknown / missing joints are ignored, so old files stay loadable.
- Typical arm numbers (left arm): `UpperArm Z -72..-86` = arm hanging down; forearm *bend forward* = `ForeArm Y` negative on the left (positive on the right); legs: `Thigh X -90` raises the leg forward, `Shin X +90` folds the knee.

### Skinning (`Skinner`)
1. Weld vertices with identical positions (UV / normal seams) -> one weight vector per position.
2. Weight of joint `j` = `1 / (s^3 + 0.02)`, `s = distanceToSegment(Pos[j], End[j]) / Radius[j]`, normalised.
3. Optional Laplacian smoothing (`smoothIters`, default 3) over the welded mesh graph.
4. Keep the top 4 influences per vertex (`BIdx`, `BW`).
5. `Apply(pose)`: forward kinematics `G[j] = G[parent] * T(offset + Trans*H) * LocalRot`, then LBS or DQS (dual quaternions, hemisphere-aligned) of `G[j] * T(-Pos[j])`, then ground snap. `SkinNormal` transforms normals consistently.

### OBJ export / restore
- `ObjExporter.Save` **copies every line of the source file** and only rewrites `v` and `vn` lines (extra `v` components such as vertex colours are kept). Faces, `vt`, `usemtl`, groups are untouched. Output line endings are `\n`.
- Normals: each `vn` is skinned with the weights of the first vertex that references it (`ObjData.NormalSrc`).
- **Restore data**: with `embedRestore = true` the file starts with `# OBJPOSER-META ObjPoser <version> pose=<name>` followed by `# OBJPOSER-V <chunk>` lines (and `# OBJPOSER-VN <chunk>` if normals exist). The payload = original `v` (and `vn`) as little-endian float32 xyz, Deflate-compressed, Base64, split in 3000-char chunks. `ObjData.Load` collects it into `RestV` / `RestVN` (only if the count matches the vertex count); `ObjData.ApplyRestMeta()` swaps it in. `ObjRestore.Restore` rewrites a file with the original `v`/`vn` (from the embedded data, or from a reference OBJ with the same vertex count) and strips all `# OBJPOSER-` lines. Re-exporting never duplicates metadata.
- Restore is exact to float32 precision (~1e-6). It cannot un-pose files posed by other software.

### GLB export (`GlbExporter`)
glTF 2.0 binary: one mesh (rest/T-Pose geometry, expanded to unique (position, uv) pairs), one skin with all joints (inverse bind matrices = translation by `-Pos[j]`), `JOINTS_0` ushort x4, `WEIGHTS_0` float x4, `POSITION`, `NORMAL`, optional `TEXCOORD_0` (v flipped). Mode 1 (`animation=false`): pose baked into the joint node rotations/translations (root also gets the ground-snap `dy`). Mode 2 (`animation=true`): rest nodes + one 1-second animation T-Pose -> pose. Output is in working space (Y-up, +Z front). No materials/textures yet.

### UI architecture (`UI.cs`)
- `Viewport` is a **software renderer** on GDI+ (painter's algorithm, flat shading, optional per-triangle textured fill via `DrawImage` + clip, ghost mesh, floor grid, skeleton overlay). `PaintScene(g, w, h, drag)` draws to any Graphics; `Capture(...)` renders to a Bitmap (used for thumbnails and "Screenshots of all poses"). While dragging it draws at most 12000 triangles.
- `MainForm` owns the model (`ObjData`, rest vertices, `Skinner`), the current pose `cur`, `basePose` (for blend), `undo`/`redo` stacks of cloned `Pose`s (push before every edit; trackbars push on `MouseDown`), the pose gallery (`ListView` + thumbnails), menu / toolbar / status bar, and all actions (`OnOpen`, `OnExport`, `OnExportGlb`, `OnRestore`, `OnShotAll`, `OnBatch`...).
- `JointPicker` = group combo + joint combo (filters `Skeleton.Group`).
- `internal` test hooks at the end of `MainForm` (`TestView`, `TestSelectPose`, `TestPickJoint`, `TestSelectTab`) and public `LoadFile(path, interactive)` / `ShotAll(dir, interactive)` exist for automated UI tests.
- Rig edits (joint offsets, hand orientation, smoothing, DQS) rebuild the `Skinner` (`RebuildSkinner`), debounced by a timer for sliders.

## 4. Build, test, run
```
# Windows (no Visual Studio)           build.bat
# Visual Studio / MSBuild              msbuild src\ObjPoser.csproj /p:Configuration=Release
# Mono (Linux / macOS)
mcs -target:winexe -sdk:4.5 -r:System.Windows.Forms.dll -r:System.Drawing.dll -win32icon:assets/icon.ico -out:ObjPoser.exe src/*.cs src/ThirdParty/*.cs
# Self-test (core only, exit code 0 = pass)
mcs -out:selftest.exe tests/CoreSelfTest.cs src/Core.cs src/Skeleton.cs src/Pose.cs src/Skinner.cs src/Export.cs && mono selftest.exe samples/sample_tpose.obj
```
**Headless UI test on Linux** (how the screenshots in `docs/images` were produced): `apt-get install mono-mcs mono-devel xvfb libgdiplus imagemagick`, write a tiny harness that references the same sources with its own `Main` (`-main:YourClass`), create `MainForm`, call `LoadFile`, `TestSelectPose`, `ShotAll`, and run with `xvfb-run`. Take real window screenshots with `import -window root out.png` (inside the same `xvfb-run` session). Under Mono, `Form.DrawToBitmap` returns an empty form but `Control.DrawToBitmap` on the viewport and `Viewport.Capture` work.

What was verified automatically: axis detection on rotated / flipped copies of a real model; all poses finite in LBS and DQS; GLB skinning re-computed in numpy matches the C# result to ~2e-6 (pose-baked and animation); OBJ restore exact to ~1e-6; UI flows under Xvfb. **Not verified: a real Windows run** (the maintainers' own testing covers that).

## 5. Coding rules
- Target **C# 5-compatible syntax** (the code is compiled with both `csc` and Mono `mcs`): no `?.`, `nameof`, string interpolation, tuples, expression-bodied members, `out var`.
- No new dependencies. Third-party code only as bundled, license-preserving MIT source in `src/ThirdParty/` + an entry in `THIRD_PARTY_NOTICES.md` and both READMEs.
- Keep UI code free of heavy logic: put algorithms in the core files so they stay testable by `tests/CoreSelfTest.cs`.
- Numbers in poses are degrees; translations are fractions of body height; positions are in the model's own units.
- Performance budget: `Skinner` construction is O(vertices x joints); it must stay interactive for ~50k vertices. Thumbnails are skipped above 40000 triangles.

## 6. How to extend
- **Add a preset pose**: add a `Pose` in `Pose.BuildPresets()` using `Set("Joint Name", x, y, z)`, `Hand("L"|"R", kind)`, `Curl(...)`, `Expression(...)`. Append to the list (order = gallery order). Keep names unique. Verify visually with the headless harness (`ShotAll`). To solve arm poses numerically (like Facepalm), run a coordinate-descent on `Skinner.ComputeG` and compare a hand joint position / direction with a target, then hard-code the angles.
- **Add a joint**: add it in the `Skeleton` static constructor (parent first), add its position / end / radius in `Skeleton.Build`, decide whether it has a palm frame, extend `GroupNames` handling if it needs a new UI group, and add tests. Old pose JSON files keep working because joints are saved by name.
- **Add an expression / hand preset**: `Pose.Expressions` / `Pose.HandKinds` + the `switch` in `Expression()` / `Hand()`.
- **Add a texture format**: `TextureLoader.Load` / `Extensions`.
- **Add an export format**: new class in `Export.cs`, call it from `MainForm` (single export) and `OnBatch`.
- **Release**: bump `AppInfo.Version` (Export.cs), update `CHANGELOG.md`, push a tag `vX.Y.Z` -> CI builds and publishes `ObjPoser-windows.zip`.

## 7. Known limitations / ideas
Heuristic rig (needs a standing humanoid, arms along X); LBS/DQS deformation limits at shoulders / hips; no mesh subdivision; DDS BC4/5/7 unsupported; GLB has no materials; face bones only move what the mesh contains; restore only for ObjPoser-posed files (or with a reference). Ideas: IK handles for hands / feet, per-joint rotation limits, pose retargeting, auto-subdivision near joints, material export in GLB, more poses (walk cycle, fighting stances), localisation of the UI.
