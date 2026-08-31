# Bolt Recess Training

This library labels bolt-recess images and trains the 128 x 128 Tiny U-Net used
by automatic bolt inspection. It uses TorchSharp CPU and saves the best
validation weights in TorchSharp's state-dictionary format.

## Dataset

Seat a carrier at Station 3, then select `Capture Taught Points`. The
inspection gantry captures the taught bolt points for each detected heat sink and
the page shows their centered 128 x 128 regions. Mark only the visible bolt
recess with the left mouse button and erase with the right mouse button. Use
`Empty & Next` for an empty-hole image. `Complete & Next` creates the matching
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
Training page. The best weights replace `BoltInspectionSettings.ModelFile` and
automatic inspection reloads them when training completes. The validation mask
determines the expected Bolt or Empty result. The review compares it with the
prediction, and the minimum detected-mask ratio is selected automatically from
the validation results.
