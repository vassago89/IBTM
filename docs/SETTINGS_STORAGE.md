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

## JSON storage and fresh installations

Each concrete settings class is serialized in full into one `Settings` row:
the class name is `Key`, and its JSON text is `Value`. I/O mappings are members
of that JSON object, not separate DB rows or tables. Each recipe is likewise
one complete JSON value in `Recipes`. Original PNGs remain in `RecipeImages`
so image data is not repeatedly encoded into the recipe JSON.

`MachineStore` creates these three tables automatically with `EnsureCreated`
when the database is absent. Adding a setting or recipe property does not change
the table schema and does not require an EF migration. The old migration files
and legacy JSON/PNG importer have been removed; existing files are never deleted
or silently replaced at startup.

Loading uses ordinary `JsonSerializer.Deserialize`. Missing settings sections
use `new T()`. Missing class properties keep constructor/initializer defaults;
unknown class properties are ignored. Renamed properties do not inherit old
values. There is no old-name alias, fixed-name annotation or data-conversion layer.
Unknown enum dictionary keys and invalid value types still fail loading rather
than being guessed. Backward compatibility with older saved data is not maintained.
For an incompatible installation, archive the old DB while the application is
closed and start with a fresh DB, then configure the equipment again.

Channel, axis-number, speed and teaching-value edits only require Save. Restart
after hardware mapping changes so device objects use the saved mapping. Changing
an input's physical meaning also requires checking its polarity and safety logic.

## Backup and restore

Settings provides **Back Up Database** and **Restore and Exit**, available only in
idle Manual mode. They hold an operation scope so Auto Start cannot race storage
work. Backup uses SQLite's backup API, not a copy of an open DB file. It contains
saved settings/recipes/images, not unsaved edits or the separate training database.

AJIN now opens with `AxlOpenNoReset`; startup does not load a `.mot` file.
There is no motion-parameter-file setting. Any existing vendor file is neither
rewritten nor embedded. Back it up separately if still needed.
Do not delete the old Settings directory indiscriminately; it may contain this file.

Restore validates the selected machine database, prepares `Machine.db.restore`,
and queues the normal window close after the restore command finishes. The next
startup keeps the current DB as `Machine.db.previous`, applies the prepared backup
through SQLite, removes the applied staging file, then loads configuration normally.
It never switches the live application's settings underneath existing unit objects.
The original selected backup is untouched. Back up the training DB separately while
the application is closed; SQLite may also have journal/WAL/SHM sidecars.

The Virtual build has its own output directory and therefore its own databases.
It seeds demo data only when no saved machine data exists.
