# Shop Portrait Overhaul

A Stardew Valley framework mod that displays large HD character portraits on the left side of the screen when a shop menu is open. SPO is a **framework** — it does nothing on its own and requires a content pack to provide portraits.

This repository holds the C# source code for the framework. The mod itself is released on Nexus Mods.

---

## What it does

- Renders a portrait in the bottom-left area of the screen whenever a shop menu opens
- Shifts the shop menu to the right to make room for the portrait, scaled to the viewport so it works at any resolution and window size
- Lets players choose which installed portrait pack to use per shop, or fall back to vanilla, through Generic Mod Config Menu
- Supports per-shop position/scale defaults defined by pack authors, with player offset and scale controls layered on top
- Supports animated portraits via sprite sheets
- Works with mouse, keyboard, and controller input, including controller cursor snapping
- Handles Android (no menu shift, separate per-shop Android offsets)

---

## How it works

SPO registers a custom Content Patcher asset, `Custom/ShopPortraitOverhaul/Packs`, which content packs write into via `EditData`. Each pack declares the shops it covers and optional per-shop settings. When a shop opens, SPO resolves the active pack for that shop, loads the portrait texture from `Custom/{packId}/ShopPortraits/{shopId}`, computes the layout, and draws it.

The menu shift is applied persistently (not per-frame) so that drawing space and hit-test space always match — this is what keeps mouse, hover, controller snap, and scroll input consistent without per-input-method translation.

---

## Project structure

- `ModEntry.cs` — entry point, GMCM registration, portrait resolution, layout computation, animation, and the author export command
- `ShopMenuPatches.cs` — Harmony patches that apply the persistent menu shift and suppress the vanilla portrait
- `PackRegistration.cs` — data models for pack registration, per-shop settings, and animation settings
- `ModConfig.cs` — player config model
- `ModApis.cs` — interfaces for GMCM and Content Patcher integration

---

## Building

1. Clone the repo
2. Make sure the `Pathoschild.Stardew.ModBuildConfig` NuGet package can locate your Stardew Valley install (set `GamePath` in the `.csproj` if it isn't auto-detected)
3. Build — the compiled mod is copied to your `Mods` folder automatically

Targets .NET 6, as required by the game.

---

## Dependencies

- SMAPI
- Content Patcher (content packs are built on it)
- Generic Mod Config Menu (optional at runtime — settings UI only)

---

## Creating a portrait pack

Portrait packs are Content Patcher content packs — no C# required. See the pack author guide on the Nexus mod page for the full content.json format, the per-shop settings, animation setup, and the complete shop ID reference.

---

## Credits

Framework design and content by SerDakota. C# implementation with help from the Stardew Valley modding community.
