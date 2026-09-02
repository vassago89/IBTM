using IBTM.Core;
using TorchSharp;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace IBTM.Inspection.Training;

internal sealed class TinyUnet : Module<Tensor, Tensor>
{
    private readonly Module<Tensor, Tensor> _encoder1 =
        Block(ImageFrame.ColorChannelCount, 8);
    private readonly Module<Tensor, Tensor> _encoder2 = Block(8, 16);
    private readonly Module<Tensor, Tensor> _encoder3 = Block(16, 32);
    private readonly Module<Tensor, Tensor> _bridge = Block(32, 64);
    private readonly Module<Tensor, Tensor> _pool = MaxPool2d(2);
    private readonly Module<Tensor, Tensor> _up3 = ConvTranspose2d(64, 32, 2, 2);
    private readonly Module<Tensor, Tensor> _decoder3 = Block(64, 32);
    private readonly Module<Tensor, Tensor> _up2 = ConvTranspose2d(32, 16, 2, 2);
    private readonly Module<Tensor, Tensor> _decoder2 = Block(32, 16);
    private readonly Module<Tensor, Tensor> _up1 = ConvTranspose2d(16, 8, 2, 2);
    private readonly Module<Tensor, Tensor> _decoder1 = Block(16, 8);
    private readonly Module<Tensor, Tensor> _output = Conv2d(8, 1, 1);

    public TinyUnet() : base(nameof(TinyUnet))
    {
        RegisterComponents();
    }

    public override Tensor forward(Tensor input)
    {
        using var scope = NewDisposeScope();
        var encoder1 = _encoder1.call(input);
        var encoder2 = _encoder2.call(_pool.call(encoder1));
        var encoder3 = _encoder3.call(_pool.call(encoder2));
        var bridge = _bridge.call(_pool.call(encoder3));
        var decoder3 = _decoder3.call(cat([_up3.call(bridge), encoder3], 1));
        var decoder2 = _decoder2.call(cat([_up2.call(decoder3), encoder2], 1));
        var decoder1 = _decoder1.call(cat([_up1.call(decoder2), encoder1], 1));
        return _output.call(decoder1).MoveToOuterDisposeScope();
    }

    private static Module<Tensor, Tensor> Block(long input, long output) =>
        Sequential(
            Conv2d(input, output, 3, padding: 1),
            ReLU(inplace: true),
            Conv2d(output, output, 3, padding: 1),
            ReLU(inplace: true));
}
