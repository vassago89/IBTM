using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IBTM.Storage.Migrations
{
    /// <inheritdoc />
    public partial class InitialMachine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Recipes",
                columns: table => new
                {
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Recipes", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "RecipeImages",
                columns: table => new
                {
                    RecipeName = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    Image = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeImages", x => new { x.RecipeName, x.Number });
                    table.ForeignKey(
                        name: "FK_RecipeImages_Recipes_RecipeName",
                        column: x => x.RecipeName,
                        principalTable: "Recipes",
                        principalColumn: "Name",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecipeImages");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "Recipes");
        }
    }
}
