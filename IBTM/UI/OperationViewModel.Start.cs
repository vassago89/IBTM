using System.Windows;
using CommunityToolkit.Mvvm.Input;

namespace IBTM.UI;

public partial class OperationViewModel
{
    private readonly StartPreparationPlan _startPreparations;

    public bool PcbPlacementRecoveryAvailable =>
        _startPreparations.CanOpen(
            StartPreparationType.PcbPlacementRecovery);

    public bool BoltRecoveryAvailable =>
        _startPreparations.CanOpen(
            StartPreparationType.BoltFasteningRecovery);

    [RelayCommand(CanExecute = nameof(CanOpenPcbPlacementRecovery))]
    private void OpenPcbPlacementRecovery() => _startPreparations.Open(
        StartPreparationType.PcbPlacementRecovery,
        Application.Current.MainWindow);

    [RelayCommand(CanExecute = nameof(CanOpenBoltRecovery))]
    private void OpenBoltRecovery() => _startPreparations.Open(
        StartPreparationType.BoltFasteningRecovery,
        Application.Current.MainWindow);

    private bool CanOpenBoltRecovery() => BoltRecoveryAvailable;

    private bool CanOpenPcbPlacementRecovery() =>
        PcbPlacementRecoveryAvailable;
}
