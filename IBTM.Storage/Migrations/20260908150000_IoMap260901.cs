using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IBTM.Storage.Migrations;

// Apply the confirmed wiring once; keep axis teaching, recipes and all other settings.
[DbContext(typeof(MachineDb))]
[Migration("20260908150000_IoMap260901")]
public sealed class IoMap260901 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        UPDATE Settings SET Value = json_set(
            replace(replace(Value, 'PcbSupplyNestForward', 'PcbSupplyGripperClosed'),
                'PcbSupplyNestBackward', 'PcbSupplyGripperOpen'),
            '$.Inputs.PcbSupplyRotated', 20,
            '$.Inputs.PcbSupplyUnrotated', 21,
            '$.Inputs.PcbSupplyGripperClosed', 22,
            '$.Inputs.PcbSupplyGripperOpen', 23,
            '$.Inputs.PcbSupplyIpmFixerForward', 24,
            '$.Inputs.PcbSupplyIpmFixerBackward', 25,
            '$.Outputs.PcbSupplyRotate.Number', 20,
            '$.Outputs.PcbSupplyRotate.OffNumber', 21,
            '$.Outputs.PcbSupplyGripperClosed.Number', 22,
            '$.Outputs.PcbSupplyGripperClosed.OffNumber', 23,
            '$.Outputs.PcbSupplyIpmFixerForward.Number', 24,
            '$.Outputs.PcbSupplyIpmFixerForward.OffNumber', 25)
        WHERE Key = 'PcbSupplyHardwareSettings';

        UPDATE Settings SET Value = json_set(Value,
            '$.Inputs.MainConveyorAutoMode', 53,
            '$.Inputs.MainConveyorEntryCarrierDetected', 91,
            '$.Inputs.MainConveyorExitCarrierDetected', 92)
        WHERE Key = 'ConveyorHardwareSettings';

        UPDATE Settings SET Value = json_set(Value,
            '$.Outputs.NgCarrierPickupDown.Number', 64,
            '$.Outputs.NgCarrierPickupDown.OffNumber', 65,
            '$.Outputs.NgCarrierGripperClose.Number', 66,
            '$.Outputs.NgCarrierGripperClose.OffNumber', 67)
        WHERE Key = 'NgCarrierTransferHardwareSettings';

        UPDATE Settings SET Value = json_set(Value,
            '$.Outputs.NgShuttleDown.Number', 68,
            '$.Outputs.NgShuttleDown.OffNumber', 69)
        WHERE Key = 'NgShuttleHardwareSettings';

        UPDATE Settings SET Value = json_set(
            json_remove(Value, '$.Inputs.NgConveyorPosition3Occupied'),
            '$.Inputs.NgConveyorAutoMode', 83,
            '$.Inputs.NgConveyorPosition1Occupied', 84,
            '$.Inputs.NgConveyorPosition2Occupied', 85,
            '$.Inputs.NgConveyorStopperUp', 87,
            '$.Inputs.NgConveyorStopperDown', 88,
            '$.Inputs.NgCarrierEjectButton', 89,
            '$.Inputs.NgCarrierEjectCompleteButton', 90,
            '$.Outputs.NgConveyorStopperUp.Number', 70,
            '$.Outputs.NgConveyorStopperUp.OffNumber', 71,
            '$.Outputs.NgConveyorRun.Number', 72,
            '$.Outputs.NgConveyorReverse.Number', 73,
            '$.Outputs.NgConveyorNormalSpeed.Number', 74,
            '$.Outputs.NgCarrierEjectLamp.Number', 75,
            '$.Outputs.NgCarrierEjectCompleteLamp.Number', 76)
        WHERE Key = 'NgConveyorHardwareSettings';
        """);
}
