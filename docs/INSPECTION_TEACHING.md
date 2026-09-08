# Inspection teaching

Station Teaching is the operating inspection screen. Bolt Training owns database
samples, per-sample ROI/polygon labels, model training and validation only.

## Backup plate during teaching

Use the station's Backup Plate Up output in the teaching IO panel: ON raises it,
OFF lowers it. Station 1, 2 and 3 each expose only their own plate. Both feedback
inputs remain visible, and the shared output command waits for the mapped feedback.
Inspection's plate control is available even when NG Transfer is disabled.
The station IO group appears first so its plate buttons are visible without
scrolling past the handlers. Plate controls use the same manual IO conditions as
the Output window; they do not require homed/servo-on axes or an empty buffer area.
Handler outputs retain their existing motion and interference conditions.
Changing the teaching motion group returns the IO list to the top; changing a
teaching point within the same group leaves the scroll position alone.

The operator raises the plate before teaching carrier work positions. There is no
automatic plate movement or additional plate-based teaching/capture restriction.
Existing motion interlocks, manual-mode conditions and STOP behavior are unchanged.

## Carrier image and camera

Teach the backup-plate upper-left and lower-right pins, then Scan Carrier. Captures
remain individual images in the recipe database; WPF places them at their recorded
camera centers using the recipe mm/px scale. No stitched bitmap is required.

Physical FOV has no separate setting: frame width/height in pixels multiplied by
the recipe mm/px determines it. The camera reports its configured frame size when
initialized; saved-image editing uses that image's dimensions. Scan spacing,
barcode fit checks and the displayed FOV use this same calculation. Scan overlap
must be smaller than both FOV dimensions.

The dashed FOV is read-only and follows actual inspection XY feedback. There is no
FOV position to teach or drag. Select a taught bolt or Data Matrix region and use
Move & Inspect: the camera moves to that center before capturing. The carrier map
remains visible while the separate camera pane shows the capture or live image.
The existing gantry interlock and cancellation path still own every move.
Starting another carrier scan clears the previous inspection frame and judgment,
even if the same bolt stays selected. A cancelled or failed scan restores the
previous saved carrier images, not the old inspection result.

## Shared PCB teaching

PCB 1 and PCB 2 are the same product in the same orientation. The recipe stores
one `PcbLayout`: shared width/height, bolt points and Data Matrix region, plus
the two carrier-relative PCB origins. It does not store a second copy of the pattern.

1. Select PCB location 1 and **PCB Region**. Drag the PCB rectangle on the carrier
   image. Its upper-left corner becomes the PCB-local origin.
2. Select PCB location 2 and **PCB Region**. Click the matching upper-left corner.
   The shared rectangle appears there; its width and height are not taught again.
3. Add the bolt points and barcode region on either PCB. Edits apply to both.
   Clicking a bolt center or drawing a barcode rectangle selects the PCB under it
   before storing the local coordinates. Points outside both PCB rectangles and
   barcode rectangles crossing PCB boundaries are not accepted.
   Select a PCB location to move/test that physical instance; selection itself
   never moves hardware.

The rectangle is manually taught, not detected from the image. Changing its size
does not scale the bolt pattern. Moving an origin moves the entire pattern on that
PCB. The map shows both PCB regions and both sets of bolt targets. Bolt and
barcode list coordinates are PCB-local centers; the PCB Region row is the
carrier-relative origin. Machine pin rows remain machine coordinates.
Image hover coordinates for bolts/barcodes are PCB-local.

Saved-image teaching, bolt Add/Remove and recipe parameter edits require idle
Manual mode, not Home or Servo On. Live starts under the existing manual IO
conditions, without requiring homed or servo-on axes. It stays active during
manual teaching moves, but stops on Auto mode, a safety trip or an alarm.
Stopping Live clears only the live frame; it does not reload the carrier-map tiles.
Same-size live frames do not refresh FOV geometry or image-teaching commands.
The carrier map's empty-image notice is independent of the live-camera pane.
Move & Inspect, Scan Carrier and Collect Bolt Images still require motion
readiness and the existing gantry interlocks. Handler IO keeps its own interlocks.

New/Load starts the teaching selection at PCB 1. A nonblank recipe name is required
for image-point/region autosave and carrier scanning, just as for Save Recipe.
Recipe save/load failures appear once in the shell; edits remain in memory for
retry. A successful retry or New clears the message. Storage exceptions are handled
at `RecipeEditor`, not separately at every teaching point.
Cancelling Load before the loaded recipe is applied keeps the current recipe.
Machine-position saves use the shared teaching error display. A failed pin save
does not advance to the next pin; retry keeps the current taught value.

At execution, PCB-local point + PCB origin gives a carrier-relative point.
Inspection adds the camera's upper-left reference pin. Fastening then uses the
existing two-pin transform for each head. Both heads fasten at the machine's
shared Safe Z using their head cylinders; bolt points contain only X/Y.
Station 2 lists the calculated positions for Move To verification, not duplicate
teaching. The IPM feeder pickup keeps its separate XYZ teaching.
`BoltPoint` owns the definition; `BoltTarget` refers to it and its PCB placement
without copying coordinates. The runtime processes only sensor-present heat sinks.
Bolt numbers repeat per PCB, but fastening and inspection results remain per PCB.

Existing duplicated carrier-coordinate recipes are not guessed or migrated.
Their old DB rows are not deleted; teach the PCB pattern before saving them in
the new format. Automatic start requires both PCB origins and the shared dimensions.

## Bolts

Select a bolt and click its center on the carrier image. Move & Inspect moves to
that center, captures with the active exposure/gain/light settings, crops the
central ROI and resizes it to 128 x 128 for Tiny U-Net. The mask is displayed over
the corresponding area of the original image.

Reteaching a position clears its previous capture and judgment, even if the selected
row stays the same. Each reinspection discards the previous prediction before
starting, so a cancelled or failed run cannot restore an old judgment when thresholds
change. The captured original remains available for retry without another move.

- Mask Threshold belongs to Bolt Training and is saved in Training.db with the
  model/training settings. Recipe New/Load does not change it. Its editor refreshes
  the training review from existing probabilities without rerunning U-Net.
- Minimum Mask (%) changes only the judgment. 0.1 means 0.1%, stored as 0.001.
- Leaving the ROI size field reinspects the same captured original. No move or new
  camera capture is performed. Reinspect Image also performs this operation.
- Exposure, gain and lighting apply on the next capture or live-view restart.
- Save Recipe persists ROI, area limit and acquisition conditions, not the model
  threshold. Labels and weights are not modified.

Collect Bolt Images captures the taught points for currently detected heat sinks
and saves full-resolution originals directly to the training DB, one at a time.
Refresh Images in Bolt Training reloads that list. Automatic collection policy
remains Off / All / NG Only, configured under Bolt Training > Inspection Images.

## Data Matrix

There is one shared PCB-local Data Matrix region. Select the PCB location and
its **Data Matrix** teaching entry, then drag
a rectangle around the code including its clear surrounding margin. Esc cancels
an unfinished rectangle. The region center and size are PCB-relative mm and
saved with the recipe. The region must fit one camera FOV.

Move & Inspect centers the camera on the rectangle and decodes the original
resolution central crop. Unlike the U-Net input, this crop is not resized. Pixel
size comes from the same recipe mm/px scale used for the carrier image.
Changing mm/px refreshes the preview crop and clears its previous decode result;
Reinspect Image reads that same captured frame again without moving or capturing.
The manual pane displays the decoded string or Not Read; it does not record an
automatic production result. No physical motion is triggered by selecting a row.

Automatic inspection reads each present PCB's Data Matrix before bolt inspection.
The decoded value belongs to that PCB's HeatSinkAssembly.PcbBarcode. A read failure
raises the existing Inspection alarm and stops automatic operation; it does not
mark the carrier NG, finish inspection, or let the carrier leave. After operator
confirmation/reset, Start repeats incomplete Station 3 inspection from the start,
including the barcode reads. No silent retries or substituted barcode values exist.

On restart, only incomplete inspection is reset. Its targets are selected again
from the current heat-sink inputs; a completed carrier waiting for discharge is
not inspected again. Cancelling after a capture prevents that unfinished result
from being recorded. An empty carrier completes as NG without camera capture.

The first-use Virtual check now reloads the recipe created by the teaching UI
and runs it through `MachineController`: PCB-specific barcode reads, the trained
Tiny U-Net with the taught ROI, Stop/restart with changed heat-sink inputs, barcode
failure/reset, and an empty carrier's NG pickup/shuttle/P3-to-P1 transfer. The
existing Station 3 flow tests separately cover OK rear-SMEMA discharge and missing
bolts routed to NG storage, with distinct PCB barcodes in both cases.

The Virtual camera renders actual Data Matrix symbols, decoded by the same managed
ZXing.Net reader. Synthetic tests verify plumbing, cancellation and alarm flow, not
real-camera readability or Tiny U-Net production accuracy. Check focus, resolution,
mm/px and code margins with the actual camera at commissioning.
