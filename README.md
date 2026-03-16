![UOMapWeaver Logo](./Img/UOMapWeaver.png)
# UOMapWeaver

UOMapWeaver is a cross-platform desktop application for creating and editing
Ultima Online map files. Built with .NET 10 and [Avalonia UI](https://avaloniaui.net/),
it runs on Windows, Linux, and macOS.

The core idea is simple: Ultima Online stores terrain in binary `.mul` files that
are hard to edit directly. UOMapWeaver converts those files to standard `.bmp`
images you can open in any image editor, and converts them back when you are done.
On top of that it provides tools for generating statics, copying map regions,
managing color palettes, and working with the newer `.uop` archive format.

---

## Table of Contents

- [How It Works](#how-it-works)
- [Features at a Glance](#features-at-a-glance)
- [Requirements](#requirements)
- [Building and Running](#building-and-running)
- [Terrain Encodings Explained](#terrain-encodings-explained)
- [MUL to BMP (Terrain Export)](#mul-to-bmp-terrain-export)
- [BMP to MUL (Terrain Import)](#bmp-to-mul-terrain-import)
- [GenStatics (Static Generation)](#genstatics-static-generation)
- [Blank BMP (New Map)](#blank-bmp-new-map)
- [Tile Color Map (Color Table Builder)](#tile-color-map-color-table-builder)
- [Map Copy (Region Transfer)](#map-copy-region-transfer)
- [UOP Tools (Archive Pack / Extract)](#uop-tools-archive-pack--extract)
- [Data Folder Reference](#data-folder-reference)
- [Binary Format Reference](#binary-format-reference)
- [FAQ](#faq)
- [Originality & Intellectual Property](#originality--intellectual-property)
- [Support the Project](#support-the-project)

---

## How It Works

Ultima Online maps are stored as a grid of **8×8 tile blocks**. Each tile has a
**terrain ID** (what the ground looks like) and a **Z altitude** (how high or low
it is). This data lives in `map*.mul` — a flat binary file where every tile takes
exactly 3 bytes (2 for the tile ID, 1 for the altitude).

UOMapWeaver reads that binary stream and turns it into two BMP images:

| Image | What it encodes | Pixel format |
|---|---|---|
| **Terrain.bmp** | Each pixel represents one tile's terrain ID, encoded as a color | 8-bit indexed or 24-bit RGB |
| **Altitude.bmp** | Each pixel represents one tile's Z altitude | 8-bit grayscale (Z + 128) or 24-bit via Altitude.xml colors |

You edit these images in any graphics tool, then feed them back into UOMapWeaver
to produce a new `map*.mul`.

The same idea applies to **statics** (trees, rocks, decorations): the binary
`staidx.mul` + `statics.mul` pair is read and can be regenerated from terrain
definitions and placement rules.

---

## Features at a Glance

- **MUL → BMP**: export terrain and altitude to editable images.
- **BMP → MUL**: import edited images back to binary map files.
- **GenStatics**: procedurally generate statics from terrain/biome definitions.
- **Blank BMP**: create empty maps from presets (Felucca, Trammel, Ilshenar, etc.).
- **Tile Color Map**: build JSON color tables from `.mul` files or XML definitions.
- **Map Copy**: copy rectangular regions (terrain + statics) between maps.
- **UOP Tools**: extract and pack `.uop` archives (map, art, gump, sound).
- **Preview**: inline zoom/pan preview of generated images.
- **UI Persistence**: save and restore all fields and options between sessions.
- **Auto-detection**: map dimensions are detected from `.mul` file size.

---

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (or newer)

---

## Building and Running

```bash
# Clone and build
git clone https://github.com/<your-user>/UOMapWeaver.git
cd UOMapWeaver
dotnet build

# Run
dotnet run --project UOMapWeaver.App

# Run tests
dotnet test
```

On first startup the app creates a `UOMapWeaverData` folder next to the
executable with README files in every subfolder. No data files are shipped —
you supply your own profiles, palettes, and definitions.

---

## Terrain Encodings Explained

UOMapWeaver supports four ways to map a tile ID to a pixel color and back.
Choosing the right encoding depends on your workflow.

### MapTrans (8-bit palette)

The classic approach inherited from the MapTrans community tools.
A **MapTrans profile** (`.txt` or `.xml`) maps each palette index (0–255) to one
or more tile IDs and an altitude range:

```
ColorIndex  TileID1 TileID2 ...
00  0x0003 0x0022 0x0023
01  0x0150 0x0151 0x0152
```

During **export** (MUL → BMP) the app looks up the tile ID in the profile and
writes the matching palette index. During **import** (BMP → MUL) it reverses the
lookup: palette index → tile ID, picking the tile whose altitude range best
matches the Altitude BMP value. When multiple tiles share a color, the formula
`(x + y) % tileCount` selects one, producing a natural-looking distribution.

Limitation: only 256 unique tile groups (one byte per pixel).

### Tile JSON (8-bit indexed or 24-bit RGB)

A more flexible palette defined in a JSON file. Two modes:

- **Indexed8**: like MapTrans but built automatically — each tile ID is assigned a
  unique palette slot (0–254; 255 is reserved for unknowns).
- **RGB24**: each tile ID is mapped to a unique RGB color generated via a hash
  (`seed × 47`, `seed × 97`, `seed × 151` mod 256). No 256-tile limit.

Use the **Tile Color Map** tab to scan one or more `map.mul` files and build the
JSON automatically.

### TileIndex RGB (24-bit, lossless)

Stores the raw tile ID directly in the pixel:

```
Red   = tileId >> 8      (high byte)
Green = tileId & 0xFF    (low byte)
Blue  = 0                (reserved)
```

This encoding is **perfectly lossless** — every tile ID survives a round-trip
with no ambiguity. The BMP looks like colored noise but is ideal when you want to
edit altitudes without risking terrain data loss.

### Terrain XML (24-bit)

Uses `Terrain.xml` definitions and `Transitions/` rules to produce a full-color
24-bit image with terrain blending. Each terrain type is assigned an RGB color,
and transition tiles are encoded via neighbor analysis (a 3×3 grid hash).

Best for previewing the final look of a map. Import is supported but requires the
same XML definitions to decode colors back to tile IDs.

---

## MUL to BMP (Terrain Export)

Converts a binary `map*.mul` file into editable BMP images.

### Inputs

| Field | Required | Description |
|---|---|---|
| Map.mul | Yes | The terrain data file to convert |
| StaIdx.mul | No | Statics index (for static preview overlay) |
| Statics.mul | No | Statics data (for static preview overlay) |
| Output folder | Yes | Where Terrain.bmp and Altitude.bmp will be saved |
| Output name | No | Base filename (defaults to the map.mul name) |

### Options

- **Map size** (Width × Height): auto-detected from file size or entered manually.
  Must be a multiple of 8.
- **Terrain encoding**: MapTrans, Tile JSON, TileIndex RGB, or Terrain XML.
- **MapTrans profile**: required for the MapTrans encoding.
- **Palette BMP**: 8-bit palette for indexed output. Can be imported from a
  Photoshop `.act` file or generated (grayscale / random / from profile).
- **Tile Color JSON**: required for the Tile JSON encoding.
- **Tile Replace JSON**: optional tile ID remapping applied before color lookup.
- **Include statics**: overlay static objects on the terrain preview.
- **Altitude Color**: use `Altitude.xml` to color-code altitude in 24-bit mode
  instead of plain grayscale.
- **Stop on error**: halt conversion on the first missing color instead of
  logging and continuing.

### How it works

1. The app reads `map.mul` block by block. Each 196-byte block contains a 4-byte
   header and 64 tiles (8×8). Each tile is 3 bytes: 2 for the tile ID (UInt16 LE)
   and 1 for Z altitude (signed byte).
2. For every tile, the selected encoding resolves `tileId → pixel color`.
   Missing IDs fall back to the nearest known ID or are written as magenta.
3. Z altitude is encoded as `byte = Z + 128` (so −128 maps to 0 and +127 maps
   to 255). This produces a grayscale image where mid-gray is sea level.
4. Pixel rows are written into BMP format (bottom-up, 4-byte row alignment).
5. Two files are saved: `*_Terrain.bmp` and `*_Altitude.bmp`.

### Preview

After conversion you can load the generated BMP directly in the preview pane
(zoom with scroll, pan with drag).

---

## BMP to MUL (Terrain Import)

Converts edited BMP images back into a binary `map*.mul` file.

### Inputs

| Field | Required | Description |
|---|---|---|
| Terrain.bmp | Yes | The terrain image (8-bit or 24-bit depending on encoding) |
| Altitude.bmp | No | The altitude image. If omitted, all tiles get Z = 0 |
| Output folder | Yes | Where the new map.mul will be saved |

### Options

- Same encoding, profile, palette, and Tile Color JSON settings as MUL → BMP
  (they must match what was used during export).
- **Convert Terrain to 24-bit**: utility to convert an 8-bit terrain BMP to
  24-bit before import (needed for TileIndex RGB).
- **Transition statics**: optionally generate transition statics while importing,
  using `Terrain.xml` and `Altitude.bmp`.
- **Stop on error**.

### How it works

1. The app reads the terrain BMP pixel by pixel.
2. Each pixel color is resolved back to a tile ID using the reverse of the
   selected encoding (palette index → tile ID, or RGB → tile ID).
3. When a MapTrans color maps to multiple tile IDs, altitude matching picks the
   best one: the entry whose altitude range is closest to the value in
   Altitude.bmp at the same coordinate.
4. Altitude pixels are decoded as `Z = value − 128`.
5. Tiles are packed into 196-byte blocks and written sequentially to `map.mul`.

---

## GenStatics (Static Generation)

Procedurally generates static objects (trees, rocks, bushes, etc.) based on the
terrain in a map.

### Inputs

| Field | Required | Description |
|---|---|---|
| Map.mul | Yes | The terrain file to read tile IDs from |
| Output folder | Yes | Where staidx.mul and statics.mul will be saved |
| Terrain.bmp | For transitions | Needed if Transition statics is enabled |
| Altitude.bmp | For transitions | Needed if Transition statics is enabled |

### Generation Modes

You can enable one or more at the same time:

- **Random statics**: places objects based on biome definitions. Each terrain
  type (grass, sand, forest, etc.) has associated static groups with spawn
  chances and weighted item selection.
- **Transition statics**: places objects at terrain boundaries (e.g., sand
  meeting grass) using transition rules from `Transitions/` XMLs.
- **Import statics**: reads static placements from `Import Files/` XMLs.
- **Empty statics**: creates valid but empty `staidx.mul` and `statics.mul`.

### How Random Statics Work

1. For each tile in the map, the tile ID is resolved to a terrain name via
   `Terrain.xml` (e.g., tile 0x0003 → "Grass").
2. The terrain name is matched to a static placement definition (from
   `TerrainTypes/` or `Statics/` XMLs) that specifies a **spawn chance** (%) and
   one or more **groups** of static items.
3. A hash-based pseudo-random check (`hash(x, y, tileId, seed) % 100 < chance`)
   decides whether to place anything. This is deterministic — the same inputs
   always produce the same result.
4. If placement occurs, a **weighted random selection** picks one group. Each
   group can contain multiple items (e.g., a tree trunk + canopy at specific
   offsets).
5. Each item's world position is calculated: `worldX = tileX + itemOffsetX`,
   and its Z is clamped to `terrainZ + itemZ` (range −128 to +127).
6. Items are packed into 7-byte static entries and organized into blocks matching
   the map's block grid.

### Biome Overrides

The **Overrides** sub-tab lets you remap any terrain type to use a different
static definition XML and/or adjust its spawn chance. This is useful when you
want more trees in a specific biome or want to swap decoration sets without
editing the XML files.

Enable the `Override` checkbox to apply your changes. Missing biome XMLs are
reported in the log.

### Static Layout

Static blocks can be stored in two orderings:

- **Row-major** (default): `blockIndex = blockX × blockHeight + blockY`
- **Column-major** (alt): `blockIndex = blockY × blockWidth + blockX`

If generated statics appear shifted or striped in the game client, switch layout.

### Output

Two files in the output folder: `staidx.mul` (12-byte index records per block)
and `statics.mul` (7-byte entries packed sequentially).

---

## Blank BMP (New Map)

Creates fresh, empty Terrain.bmp and Altitude.bmp files as a starting point for
a new map.

### Options

- **Preset**: pre-configured sizes for standard UO maps:
  Felucca (7168×4096), Trammel (7168×4096), Ilshenar (2304×1600),
  Malas (2560×2048), Tokuno (1448×1448), Ter Mur (1280×4096), and several
  custom sizes up to 24000×20000.
- **Width × Height**: manual entry (must be multiples of 8).
- **Output mode**: 8-bit indexed (with palette) or 24-bit RGB.
- **Fill terrain**: solid color index, RGB value, or terrain type from a dropdown.
- **Fill altitude**: a fixed Z value applied to every pixel.
- **Palette BMP**: the palette to embed when using 8-bit mode.
- **Output folder** and **Output name**.

### Output

Two BMP files filled with the specified color/altitude, ready to be edited in
an image editor and imported with BMP → MUL.

---

## Tile Color Map (Color Table Builder)

Builds and manages JSON files that map tile IDs to colors. These JSON files are
required by the **Tile JSON** encoding.

### Tab 1 — Tile Colors

Scans one or more `map.mul` files and builds a `TileColors.json`:

1. Add map files to the list.
2. Choose mode: **Indexed8** (palette slots, max 255 tiles) or **RGB24**
   (unique hash-generated colors, unlimited tiles).
3. Click **Build JSON**.

The builder processes tiles in streaming fashion, reporting progress and doing
partial saves every 15 seconds. The output JSON contains forward and reverse
lookups (tile → color and color → tile).

### Tab 2 — Import Terrain

Converts `Terrain.xml` and `TerrainTypes/` XMLs into JSON format for use with
the Terrain XML encoding:

1. Point to `Terrain.xml` and the `TerrainTypes/` folder.
2. Click **Convert to JSON**.
3. Use **Validate JSON** to check for errors.

### Tab 3 — XML Folders

Batch-converts `Transitions/`, `Templates/`, and `RoughEdge/` XML folders to
compact JSON:

1. Point to each folder.
2. Click **Convert to JSON**.
3. Toggle **Compact JSON** for smaller output.

---

## Map Copy (Region Transfer)

Copies a rectangular area from one map to another, with full control over
terrain, statics, tile validation, and remapping.

### Tab 1 — Paths

**Source** (left panel):
- `map.mul`, `staidx.mul`, `statics.mul` paths.
- Copy region: From X/Y → To X/Y (the rectangle to copy).

**Settings** (center):
- Copy terrain / Copy statics checkboxes.
- Overwrite terrain / Overwrite statics checkboxes.
- Static copy mode (see below).
- Static layout (row-major or column-major).
- Static Z mode (see below).

**Destination** (right panel):
- `map.mul`, `staidx.mul`, `statics.mul` paths.
- Paste start X/Y (top-left corner where the region will be placed).

**Actions**:
- **Generate Preview BMPs**: renders source and destination as images with a
  colored overlay showing the copied region.
- **Copy Region**: performs the actual copy and writes new files to the output
  folder.

### Static Copy Modes

| Mode | Behavior |
|---|---|
| **Cell match** (default) | Iterates each tile cell in the region and copies statics at that exact position. Most flexible. |
| **Entry translate** | Reads each static entry in affected blocks and offsets its position by the source→destination delta. |
| **Block replace** (aligned) | Copies entire 8×8 blocks. Requires coordinates aligned to block boundaries (the UI snaps for you). Most efficient for large areas. |

### Static Z Options

| Option | Behavior |
|---|---|
| **Keep Z** | Static heights are copied unchanged. |
| **Offset by terrain** | Z is adjusted by the altitude difference between source and destination terrain. Statics follow the ground. |
| **Set fixed Z** | All copied statics are forced to a specified Z value. |

### Tab 2 — Tile Validation

Scans the source region for tile IDs that may not exist in the destination
client. Lists missing terrain IDs and missing static IDs separately. You can
load missing tiles directly into the remap table.

### Tab 3 — Tile Remap

Define replacement rules for tiles that don't exist in the destination:

- Separate tables for terrain and static tile IDs.
- Can auto-populate from validation results.
- Load/save remap profiles as JSON.

### Tab 4 — Preview

Three interactive panels (zoom/pan):

- **Source**: shows the source map with a cyan rectangle marking the copy area.
- **Destination**: shows the destination map with a yellow rectangle marking
  where the region will be pasted.
- **Combined**: shows the final result with the copied region overlaid.

---

## UOP Tools (Archive Pack / Extract)

Works with `.uop` (Ultima Online Package) archives, the compressed container
format used by modern UO clients.

### Extract

1. Select a `.uop` file.
2. Choose extract type: **Auto** (detected from filename), **Map**, **Art**,
   **Gump**, **Sound**, **MultiCollection**, or **Generic**.
3. Set output folder and optional name prefix.
4. Click **Extract**.

Output: one or more `.mul` / `.idx` files depending on the archive type.

### Pack

1. Add input files (`.mul`, `.dat`, `.bin`) to the list.
2. Choose pack mode: **Single file** (concatenate) or **Mul + Idx** (paired).
3. Select a preset name or enter a custom one.
4. Optionally select a template `.uop` to match an existing archive's structure.
5. Set chunk size if needed (default: 0xC4000 = 806,400 bytes).
6. Click **Pack**.

Output: a `.uop` archive ready for the game client.

---

## Data Folder Reference

On first startup, `UOMapWeaverData/` is created next to the executable.
Each subfolder contains a `README.txt` explaining its purpose. No data files are
shipped — you supply your own.

| Folder | Contents | Used by |
|---|---|---|
| `MapTrans/` | MapTrans profiles (`.txt`, `.xml`, `.json`) | MUL ↔ BMP conversions |
| `Palettes/` | 8-bit palette BMPs (256 colors) | All tabs with indexed output |
| `ColorTables/ACT/` | Photoshop `.act` palettes | Importable into Palettes |
| `Transitions/` | Terrain transition XMLs (Natural / Citified / 3-way) | GenStatics, Terrain XML encoding |
| `TerrainTypes/` | Terrain type XMLs with RandomStatics rules | GenStatics |
| `Templates/` | 2-way and 3-way transition templates | GenStatics |
| `RoughEdge/` | Edge blending XMLs (Left, Top, Corner) | GenStatics |
| `Statics/` | Custom static placement XMLs per terrain | GenStatics |
| `Import Files/` | Static import XMLs | GenStatics (import mode) |
| `Definitions/` | `map-definitions.json`, `terrain-definitions.json` | Internal lookups |
| `Presets/` | `map-presets.json` (auto-generated) | Blank BMP tab |
| `TileColors/` | Generated `TileColors.json` | Tile JSON encoding |
| `JsonTileReplace/` | Tile replacement JSON | MUL ↔ BMP, Map Copy |
| `System/`, `Logger/`, `ExportUOL/`, `Developer/`, `Photoshop/` | Optional / legacy folders | — |

Root-level files in `UOMapWeaverData/`:

- `Terrain.xml` — terrain name ↔ tile ID definitions (required for Terrain XML
  encoding and GenStatics).
- `Altitude.xml` — altitude color rules (optional, for 24-bit altitude coloring).
- `ui-state.json` — auto-saved UI state (when `Save Fields` is enabled).

**Regenerate Defaults** recreates the folder structure and README files only;
it never overwrites your data files.

---

## Binary Format Reference

For those who want to understand what the tool reads and writes.

### map*.mul (Terrain)

A flat binary file divided into 8×8 tile blocks.

```
Block layout:
  4 bytes   header (unused by modern clients)
  64 × 3 bytes = 192 bytes   tile data
  Total: 196 bytes per block

Tile (3 bytes):
  [0–1]  TileID   UInt16 LE   terrain type (0x0000–0xFFFF)
  [2]    Z        Int8        altitude (−128 to +127)

Block ordering: blocks are stored column-first:
  offset = (blockX × blockHeight + blockY) × 196
```

Map dimensions must be multiples of 8. Total blocks = (width / 8) × (height / 8).

### staidx.mul + statics*.mul (Statics)

Two-file architecture: an index and a data file.

```
staidx.mul — one record per block (12 bytes):
  [0–3]  Lookup   Int32 LE   byte offset into statics.mul (−1 = empty)
  [4–7]  Length   Int32 LE   total bytes for this block
  [8–11] Extra    Int32 LE   unused

statics*.mul — packed entries (7 bytes each):
  [0–1]  TileID   UInt16 LE
  [2]    X        Byte        local X within block (0–7)
  [3]    Y        Byte        local Y within block (0–7)
  [4]    Z        Int8        altitude
  [5–6]  Hue      UInt16 LE   color tint
```

### *.uop (UOP Archives)

Compressed containers with a block-based entry table.

```
Header (28+ bytes):
  Magic      0x0050594D ("MYP" LE)
  Version    UInt32
  Timestamp  UInt32
  NextBlock  Int64    offset to first entry block
  Capacity   Int32    max entries per block
  FileCount  Int32

Each entry block contains N entries pointing to compressed
(GZip/ZLib) or raw data chunks. Default map chunk size: 0xC4000.
```

---

## FAQ

**Why are statics shifted or striped after a copy?**
Switch the Static layout to `Column-major blocks (alt)`. Some maps store blocks
in a different order, and mismatched layout causes striped placements.

**Why are some statics floating or sunk?**
Use the `Offset by terrain` Z option so static heights are adjusted by the
elevation delta between source and destination. `Set fixed Z` is useful for
flattening or testing.

**Why do my copied statics look incomplete?**
Make sure `Overwrite statics` is enabled and your source/destination rectangles
are within map bounds.

**Why does the terrain BMP look like colored noise?**
You are using **TileIndex RGB** encoding, which stores raw tile IDs as pixel
values. This is intentional — it is lossless and meant for round-trip editing,
not visual preview.

**How do I create a color table for Tile JSON?**
Go to the **Tile Color Map** tab, add your `map.mul` files, choose Indexed8 or
RGB24 mode, and click **Build JSON**. The output is saved in `TileColors/`.

**What altitude does gray represent?**
Altitude is encoded as `Z + 128`. So palette index 128 (mid-gray) is Z = 0
(sea level), 0 (black) is Z = −128, and 255 (white) is Z = +127.

**Can I use this without any XML files?**
Yes. The **TileIndex RGB** and **Tile JSON** encodings work without any XML.
MapTrans requires a profile, and Terrain XML requires `Terrain.xml` plus
transition definitions.

---

## Originality & Intellectual Property

**Source Code:** The source code of this application is entirely original. It has been developed and written by me with the assistance of AI. No code has been copied, decompiled, or extracted from *Map Creator*, *UO Landscaper*, *RadMapCopy*, or any other existing tools.

**Third-Party Tools:** This software acts as an independent orchestrator. It does not include, bundle, or distribute any proprietary binaries (.exe), engines, or configuration files (.xml) from other authors. All credits for external logic (when manually added by the user) belong to their respective creators (Gametec, dKnight, RadstaR, etc.).

---

## Support the Project

If UOMapWeaver saves you time, please consider a small donation.
Your support helps cover development time and keeps improvements flowing (better
conversions, previews, and tooling). No pressure — every contribution helps.

### ☕ Support my work

If you find this project useful, you can support my development by buying me a coffee via crypto:

* **Solana (SOL):** `H4amfKB18QUUwdHxNgCPLbzWxyXCwVnguhGkj8fcTocW`
<!-- * **USDT (Solana/SPL):** `YOUR_SOLANA_ADDRESS_HERE`  -->
<!--     > *Note: Please ensure you are sending USDT via the **Solana (SPL)** network.*  -->

[![Donate with Solana](https://img.shields.io/badge/Donate-Solana-blue?style=for-the-badge&logo=solana)](https://solscan.io/account/H4amfKB18QUUwdHxNgCPLbzWxyXCwVnguhGkj8fcTocW)
