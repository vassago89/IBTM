using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IBTM.Storage.Migrations;

[DbContext(typeof(MachineDb))]
[Migration("20260909160000_MotionSettingsUnits")]
public sealed class MotionSettingsUnits : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Convert the old common settings once. Coordinates and pulse lengths are untouched.
        foreach (var key in new[]
        {
            "PcbSupplySettings",
            "PcbPlacementHandlerSettings",
            "BoltFasteningSettings",
            "InspectionGantrySettings"
        })
        {
            migrationBuilder.Sql(
                $"""
                INSERT OR IGNORE INTO Settings (Key, Value)
                SELECT '{key}', json_object() WHERE EXISTS (SELECT 1 FROM Settings);
                UPDATE Settings SET Value = json_set(Value,
                    '$.Motion.AccelerationSeconds', COALESCE(json_extract(Value, '$.Motion.AccelerationSeconds'),
                        1.0 / COALESCE((SELECT json_extract(Value, '$.AccelerationMultiplier') FROM Settings WHERE Key = 'AjinSettings'), 2.0)),
                    '$.Motion.DecelerationSeconds', COALESCE(json_extract(Value, '$.Motion.DecelerationSeconds'),
                        1.0 / COALESCE((SELECT json_extract(Value, '$.AccelerationMultiplier') FROM Settings WHERE Key = 'AjinSettings'), 2.0)))
                WHERE Key = '{key}';
                """);
            foreach (var (name, oldSpeed, defaultSpeed) in new[] { (
                "HorizontalHome",
                "HorizontalSpeed",
                15), (
                    "ZHome",
                    "ZSpeed",
                    10) })
            {
                migrationBuilder.Sql(
                    $"""
                    WITH Old AS (SELECT
                        COALESCE((SELECT json_extract(Value, '$.{oldSpeed}') FROM Settings WHERE Key = 'HomeSettings'), {defaultSpeed}) AS Speed,
                        COALESCE((SELECT json_extract(Value, '$.HomeSecondVelocityRatio') FROM Settings WHERE Key = 'AjinSettings'), 0.2) AS R2,
                        COALESCE((SELECT json_extract(Value, '$.HomeThirdVelocityRatio') FROM Settings WHERE Key = 'AjinSettings'), 0.1) AS R3,
                        COALESCE((SELECT json_extract(Value, '$.HomeLastVelocityRatio') FROM Settings WHERE Key = 'AjinSettings'), 0.01) AS R4,
                        COALESCE((SELECT json_extract(Value, '$.HomeSecondAccelerationRatio') FROM Settings WHERE Key = 'AjinSettings'), 0.1) AS A2)
                    UPDATE Settings SET Value = json_set(Value, '$.Motion.{name}',
                        (SELECT json_object('SearchSpeed', Speed, 'DetectionSpeed', Speed * R2,
                            'ApproachSpeed', Speed * R3, 'FineSpeed', Speed * R4,
                            'SearchAccelerationSeconds', 1.0, 'DetectionAccelerationSeconds', R2 / A2) FROM Old))
                    WHERE Key = '{key}' AND json_type(Value, '$.Motion.{name}') IS NULL;
                    """);
            }
        }

        migrationBuilder.Sql("""
            DELETE FROM Settings WHERE Key = 'HomeSettings';
            UPDATE Settings SET Value = json_remove(Value, '$.AccelerationMultiplier',
                '$.HomeSecondVelocityRatio', '$.HomeThirdVelocityRatio', '$.HomeLastVelocityRatio',
                '$.HomeSecondAccelerationRatio') WHERE Key = 'AjinSettings';
            """);
    }
}
