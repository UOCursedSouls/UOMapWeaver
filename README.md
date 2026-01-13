![UOMapWeaver Logo](./Img/UOMapWeaver.png)
# UOMapWeaver
UOMapWeaver is a cross-platform tool for Ultima Online map files. It converts between
`.mul` terrain data and `.bmp` images for terrain and altitude, and lets you preview
or rebuild maps from images.

## Features
- Convert `map*.mul` (plus optional statics) to `*_Terrain.bmp` and `*_Altitude.bmp`.
- Convert `*_Terrain.bmp` and `*_Altitude.bmp` back to `map*.mul`.
- Optionally generate populated statics from legacy data definitions.
- Auto-detect map size from the `.mul` file.
- MapTrans profile support (built-in and custom profiles).
- Tile Color JSON mapping (8-bit indexed or 24-bit RGB).
- Terrain encoding selector (MapTrans, Tile JSON, or TileIndex RGB).
- Build tile color JSON from one or more `map.mul` files (incremental updates).
- Create blank BMP maps from a chosen size and palette.
- Minimal UI with previews and logs.
- Copy map regions (terrain + optional statics) between maps.
- Save and restore UI fields and options.

## Requirements
- .NET 10

## Notes
- Map sizes must be multiples of 8.
- If a terrain color is missing from a profile, the pixel is written as transparent.
- Tile color JSON can be used in place of MapTrans for conversions.
- TileIndex RGB stores the tileId directly in a 24-bit BMP (`R=tileId>>8`, `G=tileId&0xFF`, `B=0`) for lossless terrain round-trips.
- Static generation uses `UOMapWeaverData` (System/Terrain.xml + Statics/TerrainTypes definitions).
- UI state is saved in `UOMapWeaverData/ui-state.json` when `Save Fields` is enabled.

## Terrain Encodings
- MapTrans: classic 8-bit palette based on MapTrans profiles.
- Tile JSON: palette-driven (Indexed8) or truecolor (RGB24) via a JSON tile table.
- TileIndex RGB: direct tileId encoding in 24-bit BMP (best for lossless edits, requires 24-bit terrain BMP).

## Data Folder
On its first startup, the application automatically creates the `UOMapWeaverData` directory next to the executable. 

**Note on Third-Party Files:** To respect the copyright of original authors and ensure full customization, this application **does not bundle** third-party configuration files, XML definitions, or MapTrans engines. Users must manually provide these files (e.g., from *UO Landscaper* or *Map Creator*) by copying them into the appropriate subfolders.

The folder structure is organized as follows:

* **`UOMapWeaverData/System/MapTrans`**: **[Action Required]** Manually paste your MapTrans profiles and engine XMLs here. These files are necessary for the program to understand how to interpret and translate map data.
* **`UOMapWeaverData/Definitions`**: Contains `map-definitions.json`. You can modify this manually to add custom map sizes or shard-specific parameters.
* **`UOMapWeaverData/Presets`**: Contains `map-presets.json` for Blank BMP size configurations.
* **`UOMapWeaverData/Palettes`**: Place your palette BMPs here (e.g., `GrayscalePalette.bmp`).
* **`UOMapWeaverData/TileColors`**: Storage for generated tile JSON files used for rendering.
* **`UOMapWeaverData/Transitions`, `Statics`, `Photoshop`, `Import Files`, `ExportUOL`**: Legacy data folders. Users should copy their existing project files into these directories to work with them.

> **Disclaimer:** It provides the structure, but the logic files (.xml, .tga, etc.) from authors like Gametec (Map Creator) or dKnight (UO Landscaper) must be supplied by the user.

## Map Copy Guide
The Map Copy tab copies a rectangular region from a source map to a destination map.

1) Select source and destination `map.mul` files.
2) Fill Source area (From X/Y -> To X/Y) and Destination start (X/Y).
3) Choose whether to copy terrain, statics, or both.
4) Click `Generate Preview BMPs` (optional) to visualize the copy.
5) Click `Copy Region`.

### Static Copy Modes
- Cell match (default): Scans each tile cell and copies only statics at that cell.
- Entry translate (alt): Reads each static entry and moves it to the destination by offset.
- Block replace (aligned): Copies whole 8x8 static blocks. Requires 8-aligned source/destination; the UI snaps to /8 when selected.

### Static Layout
Some maps store static blocks with a different ordering. Choose the layout that matches your files.
- Row-major blocks (default): index = blockY * blockWidth + blockX.
- Column-major blocks (alt): index = blockX * blockHeight + blockY.

If statics appear shifted or striped, switch to `Column-major blocks (alt)`.

### Static Z Options
- Keep Z: keeps the original static height.
- Offset by terrain: adjusts Z by the terrain height delta between source and destination.
- Set fixed Z: forces all copied statics to the specified Z.

### Tips
- Map sizes must be divisible by 8 for statics.
- For precise region copies, prefer Block replace (aligned) with 8-aligned coordinates.

## FAQ
**Why are statics shifted or striped after a copy?**  
Try switching the Static layout to `Column-major blocks (alt)`. Some maps store static blocks in a different order, and this fixes “striped” placements.

**Why are some statics floating or sunk?**  
Use `Statics Z` options. `Offset by terrain` adjusts heights based on the terrain delta between source and destination. `Set fixed Z` is useful for flattening or testing.

**Why do my copied statics look incomplete?**  
Make sure `Overwrite statics` is enabled and your source/destination rectangles are within bounds.

## Originality & Intellectual Property
**Source Code:** The source code of this application is entirely original. It has been developed and written by me with the assistance of AI. No code has been copied, decompiled, or extracted from *Map Creator*, *UO Landscaper*, *RadMapCopy*, or any other existing tools.

**Third-Party Tools:** This software acts as an independent orchestrator. It does not include, bundle, or distribute any proprietary binaries (.exe), engines, or configuration files (.xml) from other authors. All credits for external logic (when manually added by the user) belong to their respective creators (Gametec, dKnight, RadstaR, etc.).

## Support the Project
If UOMapWeaver saves you time, please consider a small donation.  
Your support helps cover development time and keeps improvements flowing (better conversions, previews, and tooling).  
No pressure — every contribution helps.
### ☕ Support my work

If you find this project useful, you can support my development by buying me a coffee via crypto:

* **Solana (SOL):** `H4amfKB18QUUwdHxNgCPLbzWxyXCwVnguhGkj8fcTocW`
<!-- * **USDT (Solana/SPL):** `YOUR_SOLANA_ADDRESS_HERE`  -->
<!--     > *Note: Please ensure you are sending USDT via the **Solana (SPL)** network.*  -->

[![Donate with Solana](https://img.shields.io/badge/Donate-Solana-blue?style=for-the-badge&logo=solana)](https://solscan.io/account/H4amfKB18QUUwdHxNgCPLbzWxyXCwVnguhGkj8fcTocW)