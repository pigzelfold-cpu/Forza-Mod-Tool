# Forza Mod Tool — Community Fork Additions (based on v1.3.0.6)

Changes added on top of upstream v1.3.0.6 in this fork.

## Car Editor: Traffic Cars in Autoshow (FH2 – FH6)

- Added **＋ Traffic cars** and **− Traffic cars** buttons in Car Editor to unlock and revert drivable traffic cars across **FH2, FH3, FH4, FH5, and FH6** via offline GameDB edits (`TrafficCarAutoshowService`).
- Preserves the free-roam `TrafficCars` table (AI traffic keeps spawning normally) and excludes null/placeholder entries (`1215`, `1477`, `IsNullCar=1`).
- Updates per-game visibility/drivability flags (`IsDrivable`, `IsPurchased`, `IsInstalled`, `IsUnicorn`, `IsArcade`, `NotAvailableInAutoshow`, `VisibleOnlyIfOwned`, `NotAvailableInAuctionHouse`, and FH2 `ContentId=0`), cleanly removes traffic IDs from `UnobtainableCars` in FH4–FH6 without foreign-key violations or view/backup schema drift, preserves stock `BaseCost`, avoids duplicate `OnDiscContent` rows, and assigns valid child `CarBucket` categories (keeping existing valid buckets such as FH3 `2408`).
- Enabled single-car and multi-select **Available in Autoshow** toggling for traffic cars (including FH2 traffic cars), **Restore selected car** stock recovery, and full non-destructive revert (**− Traffic cars**) that preserves engine swaps, tuning, and custom prices.

## Multi-Game Expansion (FH3, FH4, FM7, FM6 Apex, FH3 OpusDev)

- Added authenticated TransformIT GameDB decryption and re-encryption (`LegacyGameDb`, `LegacyTfit`, `LegacyKeys`) for **FH3**, **FH4**, **FM7**, and **FM6 Apex** in Car Editor, Crypto, and DB Browser.
- Added **FH3** path/filename de-obfuscation and re-obfuscation (`Fh3NameCodec`) for `.~b1` / obfuscated install layouts.
- Packaged embedded stock baselines (`FH3` build `1231512575` with 542 cars; `FH4` build `-1558258184` with 783 cars), per-game localized car display names (`FH3`, `FH4`, `FH5`), `CarBucketCatalog` localized bucket loading, stock thumbnail auto-discovery, and native FH3/FH4 drift steering-angle and whitewall/Edition tire options.
- Added authenticated legacy asset (`File`, `ConfigFile`, `Profile`, `Reward`, `Photo`, `Dynamic`, `SFS`) and ZIP archive decrypt/re-encrypt workflows (`LegacyAssetContainer`, `LegacyZipArchive`) for **FH3**, **FH3 OpusDev**, **FH4**, **FM7**, and **FM6 Apex**.

## FH5 Profile Editor, Festival Playlist, Garage Viewer & Save Swap

- Unlocked direct **FH5 `ProfileData` editing** and automatic re-encryption in **Profile Editor** (Credits, Total Credits, Driver XP / `/Main/TotalXP`, Skill Points, Wheelspins, Super Wheelspins, and paginated profile database table browsing).
- Added **FH5 Festival Playlist** completion support (`PlaylistPointsState` / `PlaylistActivityState` / `PlaylistCompletionState` v8 layout with 4-byte IDs and multi-track Mainland, Rally Adventure, Hot Wheels, and Hide & Seek maxima).
- Updated **Driver XP** editing (`ProfileXpEdit`) to keep `/Main/TotalXP` synchronized with `/Main/XP`, `/Main/TotalSkills`, and `/CompareStats/WristbandLevel`.
- Extended **Garage Viewer** to load, edit, and export **FH5 `ProfileData`** saves with FH5 stock thumbnail detection alongside FH6 and FH2/Azimuth.
- Extended **Save Swap** (`MainWindow.SaveSwap`) to support both **FH5 `ProfileData`** and **FH6 `C_ProfileData`** donor → target transfers, with **Find save** (`SaveLocator`) auto-discovery across Steam, MS Store / Xbox (WGS & PGS), OnlineFix, EMPRESS, and RUNE save folders and automatic FH5 profile template matching.

## Car Editor Performance & Engine Upgrade Visibility Fix

- Optimized bulk engine/motor swap batches (`AddEngineSwaps`, `AddMotorSwaps`) by preloading existing IDs, warming manufacturer caches, reusing prepared statements, and building temporary `Ordinal` indexes during batch runs.
- Added missing Engine-category upgrade generation (`EnsureEngineUpgrades`, `EnsureAllEnginesHaveUpgrades`, **Unlock engine upgrades**) so stock and swapped combustion engines (including affected FH3 cars) expose upgrades in-game.

---

# Forza Mod Tool v1.3.0.6

- Fixed FH6 Collection Journal entries all showing under Drift Attack.
- The Profile Editor can read packed English string tables and use built-in labels when game strings are missing.
- Rebuilding a string table from JSON preserves existing entries stored by hash.
- Setting a combustion engine as stock on an EV fits geared transmissions and keeps the existing drive layouts.

# Forza Mod Tool v1.3.0.5

- Fixed FH6 update merges for the water/methanol add-on. The merge carries over its upgrades, preset values and garage column.
- Update merges keep the new game's row when a mod independently added the same ID. The preview lists conflicts.
- Garage Viewer removes any FH6 model, including current, pending and barn-find cars. Keep at least one car.
- Removed the model lock in the FH2 Garage Viewer. Current, pending and barn-find save references still protect individual cars.
- Ctrl+A followed by Remove keeps the current car in FH6 and all protected cars in FH2.

# Forza Mod Tool v1.3.0.4

- Race and Drift Apply now rebuild missing suspension physics from the same car’s stock suspension or matching stock reference. Existing upgrade IDs are kept.
- Fixed all-car batches failing on upgrades with missing physics rows.
- Missing-source errors identify the car, upgrade and axle; failed batches keep their rollback protection.

# Forza Mod Tool v1.3.0.3

- Added the latest FH6 stock database and nine new cars for Car Editor comparison, restore and update merges.
- Added support for the updated FH6 save and Playlist layout, including schedules with multiple monthly rivals.
- New Journal, badge, clothing and horn definitions load from the installed game data.
- Restart to install now appears in bright green. A themed window shows the changes after an update.
- Improved car DB import compatibility.
- Added string-table editing in Crypto. Decode .str files to JSON, edit or add names, then rebuild them.
- Garage Viewer recognizes added cars from your installed or selected FH6 GameDB, including their names and manufacturers.
- Unknown garage models can now be removed; protected cars stay locked.
- Added Repair earned bonuses for stuck Collection Journal bonuses. It checks all categories without selecting rows; completing challenges also checks for missing bonuses.

# Forza Mod Tool v1.3.0.2

- Fixed Festival Playlist showing as unsupported in some FH6 saves.
- Older Playlist progress no longer blocks completion when challenges or point totals have changed.
- Completion syncs the selected Playlist with loaded game data while preserving older records and unclaimed rewards.

# Forza Mod Tool v1.3.0.1

- Fixed Reset fitment to stock deleting factory upgrade options. It now restores the matching embedded fitment catalogue and original part IDs.
- Restores edited or missing factory fitment rows, removes added choices, and supports batch repair.
- Uses loaded values when no embedded fitment baseline exists.

# Forza Mod Tool v1.3.0

Changes since v1.2.6.3.

## FH6

- Fixed Driver XP reverting after earning XP. Tested after earning XP and restarting.
- Added Online XP editing with level and linked Driver XP previews.
- Added one-click Playlist completion, including older series without saved progress. Seasons, activities and History points update; rewards stay unclaimed.
- Fixed Playlist dropdown labels and added a nearby game-data chooser for installs that are not detected automatically.
- Added support for newer unlocks, Playlists and Journal definitions from matching installed game files.
- Journal Promo completion now marks the matching cars photographed.
- Adding or duplicating supported garage cars now updates their non-DLC Car Collection entries. DLC cars can still be added.
- Fixed stock-car additions being disabled when the current-car reference cannot be resolved.
- Improved Forza Link phrase compatibility and stopped invalid garage thumbnails interrupting scrolling.
- Added stock track-width editing for newly added widebodies.

## FH5

- Added GameDB and Car Editor support, including an embedded stock DB for comparison and restore.
- Added native stock thumbnails with automatic game-folder detection.
- Added decrypt/re-encrypt support for text assets such as PhysicsSettings.ini and supported ZIPs, including engine audio ZIPs.
- Added profile-save decryption in Crypto. Save viewing, editing, re-encryption and Save Swap remain unavailable.

## FH2 / Azimuth

- Added GameDB and Car Editor support, with an embedded stock DB.
- Added Race/Rally suspension and a custom drift option with an editable, reversible steering angle.
- Added native stock thumbnails in Car Editor and Garage Viewer.
- Added save loading and export in Profile Editor and Garage Viewer.
- Added credits, wheelspins, spendable skill points and Driver XP editing with a level/wristband preview.
- Added mainland and Storm Island boards and road discovery.
- Added perk unlocks, owned-car photography and camera discovery.
- Added mainland championship completion, finales and post-story repair so completed championships no longer block progression.
- Added Storm Island championship completion, excluding its final gauntlet and story.
- Added garage add, duplicate, remove and owner/history editing, including batch Original Owner changes.
- Confirmed compatibility with FH2 Extra Options. Track-width and tire-profile controls stay disabled for FH2; use the separate wheel mod.
- Added installation instructions to replace the contents of both revision folders after backing up.

## Car Editor and databases

- Fixed Reset fitment to stock leaving added rim sizes behind. Stock rim rows are kept, and old queued sizes are cleared. Works with batches.
- FH5/FH6 track widths now use mm per side, with in-game size labels, a default 5 mm step and a custom increment.
- Existing track-width upgrades can be edited after reloading the DB. Reapplying also repairs older size labels, including batches.
- Exported string tables use the mediapc\stripped\stringtables layout. Correct installed tables are reused.
- Fixed unused string-table ZIPs blocking DB export when loose overrides are installed. Unreadable tables now report the file path.
- Added an Export string tables checkbox for FH5/FH6. Turn it off to export only the DB and keep all edits.
- Added a manual string-table folder picker, with an Auto option to restore game-folder detection.
- Added car-bucket selection by name.
- Added stock engine labels in car name - engine name format, plus stock-name restore. Generic engines keep their names; blank stock names use the donor car name.
- Expanded Restore selected car to include related DB Browser edits. Engine names, donor engines and shared records are preserved.
- Fixed older generated suspension tuning ranges when reapplying Race/Drift upgrades.
- Expanded car DB export to include the related physics, upgrades, content records and thumbnail paths needed for sharing. External files stay separate.
- Extended merge, update merge, car import/export and body-kit import to same-game FH2/FH5/FH6/FM8 databases.
- Fixed update merges being blocked by unchanged old tables that are absent from the new DB.
- Engine choices come from the open DB. The separate Bugatti engine/audio importer has been removed.

## Interface and help

- Added Check for updates, app-package download and restart-to-install. Other files are preserved, with backups for replaced tool files.
- Added a resizable DB Browser scratch pad with TXT import/export. Notes clear when the tool closes.
- Added editable help for every tab. Help windows stay open while using the tool and match its theme.
- Matched tab header sizes and moved Garage Viewer actions into its header.
- Renamed the save export buttons to Export save; exported filenames are unchanged.
- Removed Garage Viewer's manual GameDB-catalog button.
- Profile decryption now writes the complete decrypted profile without creating a separate SQLite file.
- Added support and Discord buttons beside the version number.

## Known limits

- Online XP retention after earning XP and restarting is still unconfirmed.
- The reported Car Collection total of 4,500 / 4,480 remains unresolved.
- Large power or handling changes can still raise PI to X999.
- FH5 saves are decrypt-only. FH2 Storm Island finale/story completion is excluded.
- FH2 saves and FM8 LZX operations need a separate x64 xcompress64.dll; see [setup](NATIVE_RUNTIME.md).

# Forza Mod Tool v1.2.6.1

Changes since v1.2.6.

## Car Editor

- Added the default car preview beneath a half-height Activity Log, with a draggable divider. Auto-detects FH6 installs and custom Steam libraries, reads regular RC/HiRes ZIPs in memory, prefers HiRes, and preserves transparency. Manual folder/ZIP selection and offline fallback remain supported.
- Car Editor and Garage Viewer share approved English in-game car names instead of guessed MediaName codenames. The Toyota displays as **2026 Toyota GR GT (Prototype)**.
- Hide only the three approved Initial Drive variants from FH6 car selection and Garage Add stock selection: 679 selectable models, with all 682 definitions retained. Existing owned variants remain visible and labelled for cleanup. IDs, MediaNames, database/save rows and stored years are unchanged; unknown/custom models keep the existing formatter.
- Added **Remove release date**, a checkbox below Autoshow. Checking clears the selected FH6 cars' existing scheduled UTC dates; unchecking restores the exact input values captured on load. Displays the date, supports mapped-car batches with rollback, and disables for unmapped cars/FM8. Does not change DLC ownership or create offer/mapping rows; in-game results still need testing.
- All **Wheels & Fitment** and **Tires & Stance** checkboxes start unchecked and reset unchecked on a new database load, including Drift suspension-limit removal and FM direct offsets. Existing values remain visible; only explicitly selected options are applied.

## Garage Viewer

- Actual per-instance cached thumbnails take priority. Missing/unreadable cache images use transparent stock previews in both list rows and the large selected preview, clearly labelled as stock rather than saved paint/design/modifications.
- Auto-detects stock packs from registered FH6 installs, Steam library metadata and common install paths. A manual stock-folder preference is remembered separately from Car Editor; Auto-find refreshes the garage cache manifest. Inputs remain read-only and images decode lazily at bounded sizes.
- Owned Initial Drive instances stay visible for inspection/removal, retaining current/pending, barn-find and model-removal protections.

## Packaging and compatibility

- UI/product/assembly/file version is **1.2.6.1**. Protected self-contained EXE and clean GitHub source upload are packaged separately. Crypto keys/private CMS code remain in the unchanged protected runtime DLL; no raw key source is included.
- README retains existing workflows, screenshots and credits; download names and changelogs are updated for this release. Required third-party licenses remain included.
- Existing Save Swap, encryption algorithms, FM8 support, merge behavior and stock comparison baselines are retained. Editing still uses private working copies and requires export; no files are installed automatically.
