# Changelog

## 3.2.0
- **Extra poses as JSON** (not built in): new `poses/` folder with 9 poses - sitting covering the face with both hands, arms crossed, hanged corpse, hanged upside down, injured, stealth peeking around a wall, sleeping, driving a car, riding a motorbike / scooter - each with a preview PNG.
- The program loads every `poses/*.json` next to `ObjPoser.exe` (or in the current folder) at start-up (`Pose.LoadFolder`); *Edit > Open extra poses folder...*; the folder is copied next to the exe by the project / release zip.
- Self-test checks the extra poses; `tools/PoseGen.cs` is the generator (numerical arm / hand solver + renderer) used to make them. Poses sheet now shows all 27 poses.

## 3.1.0
- **Restore T-Pose**: exported OBJ files can embed the original T-Pose (`# OBJPOSER-` comment lines); *File > Restore T-Pose* brings it back exactly. Fallback: restore from a reference T-Pose OBJ with the same vertex order. Opening a posed file offers to load its original T-Pose.
- **Screenshots of all poses**: one sheet (`poses_sheet.png`) plus one PNG per pose.
- **New UI**: menu bar, toolbar, pose gallery with thumbnails, section headers, tooltips, status bar with progress, About dialog, application icon, keyboard shortcuts.
- Docs: English + Arabic README, AGENTS.md for AI agents, GitHub Actions workflow, third-party notices.

## 3.0.0
- 69-joint rig: fingers (5 x 3 per hand), toes (5 per foot), jaw, eyes, eyelids, brows, nose, mouth corners, clavicles.
- Joint editor for every joint (rotation + translation), symmetric edit, undo / redo, hand presets, face expressions.
- New poses: standing (natural), attention (military), facepalm, covering face with both hands.
- Texture loading through TGASharpLib (TGA) and DDSReaderSharp (DDS), plus PNG / JPG / BMP / GIF / TIFF.
- GLB export with animation (T-Pose -> pose), batch export for GLB.

## 2.0.0
- Automatic axis detection, T-Pose check, smoothed skin weights, Dual Quaternion skinning, 14 poses, mirror / blend / save-load poses, GLB export, batch export, textures preview.

## 1.0.0
- First version: 17-bone auto-rig, 9 poses, OBJ export, GDI+ preview.
