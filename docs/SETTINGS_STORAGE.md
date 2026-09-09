# Settings ownership and persistence

## Ownership

| Owner | Machine configuration | Product recipe |
| --- | --- | --- |
| Host / Device | Drivers, enabled units, safety options, Home, last recipe selection | Recipe name and captured carrier image placement/scale |
| Each hardware unit | Its own DI/DO/feedback map, axis numbers, ranges, pulse scale | None |
| PCB Supply | Motion speeds, Rotation Z, carrier rail Y, buffer handoff and clearance | PCB 1/2 pickup X/Z |
| PCB Buffer | Shared handoff boundaries | None |
| PCB Placement | Motion speeds, Buffer Entry Z, buffer handoff | Heat Sink 1/2 placement coordinates |
| Bolt Fastening | Motion speeds, Safe Z, bolt pickup, each head's locating pins | Bolt positions, head/heat sink selection and three ADC presets |
| Bolt Feeder | Pickup/shooting supply timeouts | None |
| Inspection | Gantry speeds, shared NG transfer pickup/place positions and speed | ROI, minimum mask area ratio, exposure, gain, light level and scan overlap |
| Core coordinates | Shared upper-left/lower-right physical locating pins | Carrier-relative work points remain product data |
| NG Conveyor | Full/alarm carrier count | None |
| Hardware drivers | Independent light driver and COM/baud/data bits/parity/stop bits/write timeout/channel; camera ID/timeout/preview rate, ADC serial bus, AJIN/AlphaMotion configuration | Drivers receive acquisition values for each capture/live start; they do not know Recipe |
| Inspection Training | Separate training DB and model | Model mask threshold, dataset ROI/polygon, inspection image collection mode and epoch/batch/optimizer settings are training data, not machine or product recipes |

Physical clearances, limits and fixed handoff/pin coordinates stay machine-owned.
Changing a product does not replace these safety/geometry values. Carrier image
millimetres-per-pixel remains with the saved images so an existing image layout
retains its scale. No coordinate or mechanical sequence was changed by this move.

## Boundaries

`IBTM.Storage` references only `IBTM.Core` and the SQLite/EF packages. Unit projects
do not reference Storage or EF. Their existing typed settings objects are unchanged
apart from moving product acquisition values to `BoltInspectionRecipe`.

`Setting` identifies a machine-owned section but has no file/DB methods. The host
loads sections and injects each into its owner. `MachineStore` owns persistence,
short-lived DbContexts and transactions. `RecipeStore` is the host's WPF bitmap/PNG
conversion boundary. It does not own directories or loose image files anymore.
The common teaching view model saves machine sections; `RecipeEditor` saves product
recipes and its last-selection preference. Neither unit loops nor motion/IO calls
query the database. Actual material/axis state is not persisted as configuration.

## Databases

- `Data/Machine.db`: Settings (section key + JSON), Recipes (name + JSON),
  RecipeImages (recipe name + image number + lossless PNG BLOB).
- `TrainingData/BoltTraining.db`: existing independent training database.

The training DB also holds optional automatic inspection originals. Collection
mode (`Off` / `All` / `NgOnly`) belongs to its training settings row. Per-sample
inspection metadata is separate from the user's annotation and is not a label.
The host connects inspection completion to the training-owned collector; no unit
or inspection project gains a reverse reference to Training/EF/Storage.

Recipe names are case-insensitive. Image metadata stays in recipe JSON; the image
table contains only identity and bytes. Image lists load metadata, not every BLOB.
Save As copies the referenced images, recapture replaces the image set, and a normal
save removes images no longer referenced. Recapture encodes and inserts one image
at a time, clearing EF tracking after each insert. Save As copies PNG BLOBs inside
SQLite, without loading the source image set into application memory. Each
operation commits the recipe and its image changes together; cancellation during
replacement rolls back the entire set. The capture token continues through saving.
The live recipe's name/image list changes only after
successful storage. Setting batches, including multi-unit buffer teaching, use one
SaveChanges transaction. Bulk load/save, image conversion and backup work run off
the UI thread; recipe-name enumeration remains a small metadata-only query.
Contexts are never retained for the lifetime of a screen or unit.

Changing recipes stops live preview so the previous product's optical conditions
are not presented under the new recipe. The next preview/capture applies the current
recipe's exposure, gain and lighting.
Recipe replacement notifies its views before persisting the last-selection
preference, so failure to save that preference does not leave the UI on the old
recipe. New/Save/Load use idle-Manual availability, not Home status; these are data
operations and do not relax motion or output interlocks.

Schema changes use EF migrations. JSON field/type renames still require an explicit
data upgrade; EF cannot infer semantic changes inside JSON. Do not rename a section
type without migrating its persisted key. Missing sections on a fresh installation
use their declared defaults. Invalid saved JSON fails loading rather than resetting
the machine silently. Startup storage errors are displayed before hardware is initialized.

## Existing installations

On startup, an empty machine DB can import the former `Settings/*.json`,
`Recipes/*/Recipe.json` and referenced carrier PNGs. `LegacyMachineImport` is the
only remaining reader for that format. It moves former camera exposure/gain,
light level, scan overlap, ROI size and minimum mask area ratio into each recipe only
where that recipe does not already specify them.

Import is a single transaction. Images are read and inserted one at a time, with
tracking cleared between images. A missing image or invalid configuration aborts
the entire import. A populated database is never merged with the old files again.
Original files are not changed or deleted. Runtime saves go only to the database.

## Backup and restore

Settings provides **Back Up Database** and **Restore and Exit**, available only in
idle Manual mode. They hold an operation scope so Auto Start cannot race storage
work. Backup uses SQLite's backup API, not a copy of an open DB file. It contains
saved settings/recipes/images, not unsaved edits or the separate training database.

AJIN's vendor `.mot` file is also separate: `AxmMotLoadParaAll` consumes a filesystem
path. `AjinSettings.MotionParameterFile` is persisted in the machine DB, but the
vendor file itself is neither rewritten nor embedded. Back it up separately.
Do not delete the old Settings directory indiscriminately; it may contain this file.

Restore validates the selected machine database, prepares `Machine.db.restore`,
and queues the normal window close after the restore command finishes. The next
startup keeps the current DB as `Machine.db.previous`, applies the prepared backup
through SQLite, removes the applied staging file, then loads configuration normally.
It never switches the live application's settings underneath existing unit objects.
The original selected backup is untouched. Back up the training DB separately while
the application is closed; SQLite may also have journal/WAL/SHM sidecars.

The Virtual build has its own output directory and therefore its own databases.
It seeds demo data only when neither imported nor saved machine data exists.
