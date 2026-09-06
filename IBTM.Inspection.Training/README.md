# Bolt Recess Training

This library labels bolt-recess images and trains the 128 x 128 Tiny U-Net used
by automatic bolt inspection. It uses TorchSharp CPU and saves the best
validation weights in TorchSharp's state-dictionary format.

## Dataset

Seat a carrier at Station 3, then select `Capture Taught Points`. The
inspection gantry captures the taught bolt points for each detected heat sink and
the page shows their centered 128 x 128 regions. Without a camera or carrier,
select `Add Images` to choose one or more PNG, JPEG, BMP or TIFF files. Each file
is loaded once at its original resolution and enters the same labeling queue;
files must be at least 128 x 128 pixels, with the bolt recess in the center.
The source files are never changed. The training samples remain the centered
128 x 128 image and its matching mask.

While labeling, the dataset folder can be corrected after a save error.
`Cancel` discards only the remaining unsaved images and masks; saved samples and
original files are unchanged. Capturing taught points uses one machine-operation
token for the entire batch, so machine Stop also stops between captures.

Mark only the visible bolt
recess with the left mouse button and erase with the right mouse button. Use
`No Bolt & Next` for an empty-hole image. `Save Mask & Next` creates the matching
files:

```text
BoltDataset/
  Images/
    0001.png
    0002.png
  Masks/
    0001.png
    0002.png
```

Mask pixels are white for the visible bolt recess and black everywhere else.
Include both installed-bolt images and empty-hole images. Each class is split
independently so both training and validation contain installed and empty
examples.

Select the dataset directory and start training from the application's Bolt Model
Training page. Intermediate weights are written to a `.training` file next to
`BoltInspectionSettings.ModelFile`. Cancelling training or validation leaves the
operating model and decision threshold unchanged and removes the temporary file.
Once validation finishes, `Applying Model` publishes the weights, reloads the
operating model and saves the threshold. This short publishing step completes
before the session is released. Changing ModelFile is reflected by the next load;
training and operation use the same current setting. The validation mask
determines the expected Bolt or Empty result. The review compares it with the
prediction, and the minimum detected-mask ratio is selected automatically from
the validation results.
