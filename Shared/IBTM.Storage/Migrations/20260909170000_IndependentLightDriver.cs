using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IBTM.Storage.Migrations;

[DbContext(typeof(MachineDb))]
[Migration("20260909170000_IndependentLightDriver")]
public sealed class IndependentLightDriver : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Preserve the old selection once; later motion changes no longer switch lighting.
        migrationBuilder.Sql("""
            UPDATE Settings SET Value = json_set(Value, '$.Light',
                CASE WHEN json_extract(Value, '$.Control') IN ('Physical', 1)
                    THEN 'Movs' ELSE 'Virtual' END)
            WHERE Key = 'DriverSettings' AND json_type(Value, '$.Light') IS NULL;
            """);
    }
}
