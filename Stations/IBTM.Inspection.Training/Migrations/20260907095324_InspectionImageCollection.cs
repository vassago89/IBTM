using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace IBTM.Inspection.Training.Migrations
{
    /// <inheritdoc/>
    public partial class InspectionImageCollection : Migration
    {
        /// <inheritdoc/>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Inspection",
                table: "Samples",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc/>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "Inspection", table: "Samples");
        }
    }
}
