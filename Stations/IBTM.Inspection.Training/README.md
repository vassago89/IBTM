# Bolt Recess Training

The training project owns one SQLite database at
`TrainingData/BoltTraining.db` below the application directory. Original images,
ROI definitions, polygon labels, sample membership and the active Tiny U-Net
weights are stored inside it. No image directory, mask files, model files,
temporary weight files, imports or exports are used by the training workflow.
SQLite may create its own transaction journal or `-wal` / `-shm` files alongside
the database. These are database internals, not separate training assets. Close
the application before copying the database for a backup.

## Database implementation

`BoltTrainingDb` owns the EF Core mapping for Samples, Model and TrainingSettings.
Its entities are internal to this project. `BoltTrainingStore` keeps the existing
training-specific operations; no generic repository or additional interface is
used. Each operation creates and disposes a short-lived context. No context is
shared between the UI and training worker.

Sample lists project metadata only, and single-image reads do not track entities.
Label, split and inclusion updates do not load image BLOBs. Saving a model checks
only whether the active row exists, then writes its replacement without reading
the previous weights. Settings remain JSON in their existing database row.

The store uses `EnsureCreated` to create a fresh database automatically. Settings
are whole-class JSON, so adding a training setting does not change table columns.
There are no migration files, model snapshots or old-schema adoption paths.
Opening an existing database does not erase its images or model. An incompatible
old schema requires a fresh database; archive the old file before replacing it.

## Capture and browse

In `Station Teaching`, seat a carrier at Station 3 and select `Collect Bolt Images`. Only points for
detected heat sinks are captured. Each full-resolution image is immediately
encoded losslessly to an in-memory PNG and inserted as a BLOB, together with
its name and the active inspection recipe's central ROI side length. Capturing never accumulates
all full-resolution frames in a list. Completed captures remain in the DB when
capture is cancelled, even if they have not been labeled.

The sample selector lists DB metadata without loading the images. Select a sample
to edit its central ROI and polygon on one full-resolution image. Only the selected
original is retained by the editor. `Previous` and `Next` navigate saved samples.
`Close Sample` releases the selection and its unsaved edit; it does not delete
anything. `Refresh Images` reloads the list after collection in Station Teaching. Hardware capture
requires the normal Station 3 motion and carrier conditions.

## Collect automatic inspection images

`Bolt Training > Inspection Images` selects `Off`, `All Inspections` or `NG Only`.
The default is Off. Save Settings saves the mode in the training DB; it does not start
model training. This is an acquisition setting in `BoltTrainingSettings`, shared
with the collector through DI, not a product-recipe setting. Change it in idle
Manual mode before returning to automatic operation. Turning collection Off
does not delete any samples. There is no automatic count limit or retention deletion.

`BoltInspector.Inspected` supplies the original frame used for each completed
bolt judgment. The host wires this to `BoltImageCollector` in the training project;
the inspection project never references the training DB. Collection uses that same
frame without another camera capture. Carrier-image scans and manual previews do
not trigger collection. NG Only means that bolt's missing-bolt judgment, not the
carrier's final route or an earlier fastening NG result.

The collector encodes/inserts one lossless full-resolution PNG on the inspection
worker before the next point. It does not run PNG/DB work on the UI thread or keep
an unbounded frame queue. Storage time is therefore part of the inspection cycle.
Samples start Unlabeled regardless of inspection result; collected NG is not a
ground-truth Empty label. Only user-labeled, included samples enter training.

Each automatically collected sample also stores recipe name, bolt number, heat
sink, capture time, original inspection ROI and OK/NG result in nullable inspection
metadata. Later annotation ROI/polygon edits leave that inspection evidence intact.
Manual samples have no inspection metadata.
The selector shows inspection result/ROI separately from the label.

A storage/encoding failure stops further collection for the current session and
displays its cause in the shared application banner. It does not change the bolt
judgment or stop automatic operation. After resolving the problem, Save Settings clears
the collection error and resumes the chosen mode. Startup uses the saved mode;
the temporary error itself is not persisted. No automatic retry loop is added.
The header shows collected inspection count and allocated DB megabytes when samples
are loaded. SQLite WAL/journal space is additional to that data-size figure.

## ROI and polygon

`Station Teaching > Inspection Gantry > Inspection Recipe` holds the product's
`Central ROI Size (px)`, `Minimum Mask Ratio`, exposure, gain and
light level. Scan overlap beside the carrier image also belongs to this recipe.
They belong to `BoltInspectionRecipe` in `IBTM.Inspection`, saved in the Recipes
table of `Data/Machine.db` by the existing recipe Save button. They are
editable in idle Manual mode, even before Home; editing them does not move hardware.
Camera identity and light controller connection/channel remain in Settings.
The one-time machine importer preserves applicable values from former JSON files;
normal operation does not read/write them. New recipes start with declared defaults.
`Mask Threshold` belongs to `BoltTrainingSettings`, alongside epoch, batch size,
learning rate and patience. `Save Settings` in Bolt Training persists these values
and collection mode in the independent training DB without retraining. Training
also saves them when it starts. Changing the model threshold refreshes existing
review masks immediately; recipe changes leave it unchanged. Per-sample labels
remain in that same training DB.

`Central ROI Size (px)` is the side length in the original source image.
`BoltImageInput` extracts that centered square and bilinearly resizes it to
128 x 128, the model's fixed input size. Automatic inspection uses the same
preprocessing. Each saved sample retains its own ROI size; changing the inspection
recipe affects future captures and inspection, not previously labeled samples.
An ROI change alone does not require retraining.

In Bolt Training, drag any ROI corner or use `ROI (px)` and its slider to resize
the central square from 1 pixel up to the original image's shorter side. Label
directly inside that outline. The center remains fixed. The mouse wheel zooms
around the pointer, middle-button drag pans, and `Fit Image` / `Fit ROI` restore
useful views. Display transforms do not modify image or label coordinates.
WPF draws the original directly rather than allocating resized images on edits.
Training and inspection still use the shared 128 x 128 preprocessing internally.

Changing ROI size repositions the polygon in input coordinates so it continues
to mark the same original-image locations, including odd-sized crops. Vertices
outside a smaller ROI are retained; only the generated mask is clipped. Enlarging
the ROI restores those vertices. ROI changes do not erase a label or change the
operating inspection recipe.

Draw one polygon around the visible bolt recess. Left-click vertices, then click
the first point, double-click, press Enter, or select `Close Polygon`.
Right-click, Backspace or `Undo Point` removes the last vertex. `Clear` or Escape
discards the current polygon without ending the session. A closed polygon with
interior pixels enables `Save Mask & Next`.

Saving writes the ROI size, polygon vertices in 128 x 128 input coordinates and
a Bolt label in one update. `No Bolt & Next` saves the ROI with an explicit Empty
label and an empty polygon. `Close Sample` discards unsaved ROI and polygon edits.
Unlabeled
captures are distinct from Empty. Binary masks are generated from the polygon,
never persisted separately. Reopening a labeled sample restores the polygon
for editing. Advancing loads the next sample's own annotation.

`Exclude Sample` omits a sample from training/validation-set review without deleting it.
`Use: Training / Validation` changes a labeled sample's split. Initial labeling
assigns every fifth sample within each label to validation, starting with the
first. Relabeling the same class preserves its split. Training requires at least
one included, labeled image in each split and some bolt-recess pixels in the
training masks. Bolt-only images can satisfy this: polygons mark the recess as 1
and the surrounding background as 0. Empty images are optional all-zero masks;
neither split requires them, and there is no five-image minimum. Unlabeled samples
are not used. These are execution requirements, not evidence of adequate accuracy.

## Training and inspection

Start with Max Epochs 50, Batch Size 8, Learning Rate 0.001 and Early Stop
Patience 10. Only Max Epochs is shown by default; the other three fields are in
Advanced. They are saved to the TrainingSettings row when Train is pressed and
restored when the training view model is created. These are starting values, not
optically validated settings. There are no separate settings files.

Training ends at the epoch limit or after Patience consecutive epochs without
a strictly lower validation loss. A tied loss is not an improvement. A new best
loss resets that count. Early stopping is successful completion: it reviews and
publishes the best weights, not the final epoch's weights. The screen shows the
current and best epoch and distinguishes Completed / Completed · Early Stop from
Cancelled. Cancel still discards the current attempt and preserves the old model.

Tiny U-Net is trained from scratch on CPU. During `Preparing Training Data`, the
dataset reads each included labeled original once, crops/resizes it to 128 x 128,
and generates its polygon mask. It retains only BGR bytes and a byte mask (64 KiB
per sample, about 62.5 MiB for 1,000 samples, excluding metadata and active batches).
Full originals and all-sample float tensors are not retained. Epochs reuse these
inputs without rereading the DB, decoding PNGs or rasterizing polygons. Float
conversion/tensors remain batch-scoped. Preparation, epochs and final review run
off the UI thread. Session data becomes collectible on completion or cancellation;
no forced GC, global cache, persistent preprocessed files or extra Dispose wrapper
is added. Starting training again prepares the current saved ROIs/labels afresh.
Validation loss selects the best weights.

TorchSharp serializes those weights into memory. After validation and the final
cancellation check, a single SQLite statement replaces the active model BLOB and
records the completed epoch count, best validation loss and UTC training time.
The operational segmenter reloads directly from the DB through a memory stream.
Reload releases the previous model first. If loading fails, inspection remains
unready until the stored model can be loaded; it never falls back to stale weights.
There is no intermediate model file. Cancellation before publication leaves the
existing DB model intact. The short publication/reload step completes once begun.

Training does not automatically tune the model threshold or recipe area limit. `Review Validation
Set` reads DB samples and weights without retraining or moving hardware.
It loads only included, labeled validation images. One image is sufficient;
training-set size and class balance are requirements for Train, not model review.
Review combines the training setting's mask threshold with the current recipe's
area limit; it refreshes when the model threshold changes or the training page is
reopened. Validation images retain their saved ROI.
Recipe New/Load replaces the inspection conditions used by the detector and review;
neither keeps a reference to an old recipe's conditions. A TinyUnet
inspection driver with no stored model blocks automatic inspection with a setup
message, while machine setup and homing remain available.

## Inspect one saved image

Select any saved sample and press `Inspect Selected Image`. This uses the saved
Tiny U-Net, even in Virtual mode, without a camera capture or hardware movement.
It does not require a label, inclusion in training, or a valid training/validation
split. A missing model is reported through the existing training error display.

The displayed central `ROI (px)` is cropped and resized through `BoltImageInput`,
then inferred on the worker thread. Unsaved ROI edits can be tried without writing
them to the sample or changing the operating recipe. `Current` shows OK/NG,
`Mask Area` shows the thresholded pixel ratio, and the overlay shows the detected
mask. For single-image review, that overlay is drawn inside the editor's ROI rather
than in a second image pane. `Prediction` toggles it without changing the polygon.
Validation-set review retains its separate image because it may show a different
sample from the one being edited. The model threshold comes from training settings,
while the area limit comes from the current inspection recipe. Changing the ROI,
changing the selected sample or closing it clears that single-image result.

`Recorded` shows the original inspection judgment and ROI, when present. It is
historical evidence, not a ground-truth label; changed models or conditions may
produce a different current result. Match/Mismatch is shown only for an explicitly
user-labeled sample. Dataset navigation and aggregate matches remain exclusive to
validation-set review. Both review modes share the same mask/result presentation.
Reinspection never saves labels, ROI edits, weights or inspection results. Cancellation
discards an unfinished review rather than publishing a late result.

## Operating inspection is separate

Station Teaching owns motion-assisted capture, bolt ROI and OK/NG tuning,
and Data Matrix region teaching/reading. The training view has no motion commands
or operating-recipe save/tuning controls. Its ROI belongs to the selected training
sample, not to the operating recipe.

Training validation reads the current recipe's area limit without modifying it.
Both inspection and validation use `BoltPrediction` for inclusive probability
thresholding and pixel ratios. Training, labels and saved weights are unchanged
when the operating recipe is tuned.

Image labeling, DB work, training and model review belong to this project.
Hardware capture stays with `BoltInspector`; the host teaching screen invokes it.
See [Inspection Teaching](../../docs/INSPECTION_TEACHING.md) for the operator workflow.
