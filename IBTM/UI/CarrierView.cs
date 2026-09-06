using System.Windows;
using System.Windows.Controls;
using IBTM.Core;

namespace IBTM.UI;

public sealed class CarrierView : ContentControl
{
    public static readonly DependencyProperty HeatSink1PresentProperty =
        DependencyProperty.Register(nameof(HeatSink1Present), typeof(bool), typeof(CarrierView));
    public static readonly DependencyProperty HeatSink2PresentProperty =
        DependencyProperty.Register(nameof(HeatSink2Present), typeof(bool), typeof(CarrierView));
    public static readonly DependencyProperty Pcb1PlacedProperty =
        DependencyProperty.Register(nameof(Pcb1Placed), typeof(bool), typeof(CarrierView));
    public static readonly DependencyProperty Pcb2PlacedProperty =
        DependencyProperty.Register(nameof(Pcb2Placed), typeof(bool), typeof(CarrierView));
    public static readonly DependencyProperty ShowPcbSlotsProperty =
        DependencyProperty.Register(nameof(ShowPcbSlots), typeof(bool), typeof(CarrierView));
    public static readonly DependencyProperty ActiveHeatSinkProperty =
        DependencyProperty.Register(nameof(ActiveHeatSink), typeof(HeatSinkSlot?), typeof(CarrierView));
    public static readonly DependencyProperty HeatSink1ResultProperty =
        DependencyProperty.Register(nameof(HeatSink1Result), typeof(AssemblyResult), typeof(CarrierView));
    public static readonly DependencyProperty HeatSink2ResultProperty =
        DependencyProperty.Register(nameof(HeatSink2Result), typeof(AssemblyResult), typeof(CarrierView));

    public bool HeatSink1Present
    {
        get => (bool)GetValue(HeatSink1PresentProperty);
        set => SetValue(HeatSink1PresentProperty, value);
    }

    public bool HeatSink2Present
    {
        get => (bool)GetValue(HeatSink2PresentProperty);
        set => SetValue(HeatSink2PresentProperty, value);
    }

    public bool Pcb1Placed
    {
        get => (bool)GetValue(Pcb1PlacedProperty);
        set => SetValue(Pcb1PlacedProperty, value);
    }

    public bool Pcb2Placed
    {
        get => (bool)GetValue(Pcb2PlacedProperty);
        set => SetValue(Pcb2PlacedProperty, value);
    }

    public bool ShowPcbSlots
    {
        get => (bool)GetValue(ShowPcbSlotsProperty);
        set => SetValue(ShowPcbSlotsProperty, value);
    }

    public HeatSinkSlot? ActiveHeatSink
    {
        get => (HeatSinkSlot?)GetValue(ActiveHeatSinkProperty);
        set => SetValue(ActiveHeatSinkProperty, value);
    }

    public AssemblyResult HeatSink1Result
    {
        get => (AssemblyResult)GetValue(HeatSink1ResultProperty);
        set => SetValue(HeatSink1ResultProperty, value);
    }

    public AssemblyResult HeatSink2Result
    {
        get => (AssemblyResult)GetValue(HeatSink2ResultProperty);
        set => SetValue(HeatSink2ResultProperty, value);
    }
}
