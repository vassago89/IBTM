using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace IBTM.Inspection.Training.Migrations
{
    /// <inheritdoc/>
    public partial class InitialTraining : Migration
    {
        /// <inheritdoc/>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Adopt the existing tables without rewriting source images or model weights.
            // Later schema changes belong in new migrations, not in this baseline.
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS Samples (
                    Id INTEGER NOT NULL CONSTRAINT PK_Samples PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    Image BLOB NOT NULL,
                    RegionSize INTEGER NOT NULL,
                    Label INTEGER NOT NULL DEFAULT 0,
                    SampleUse INTEGER NOT NULL DEFAULT 0,
                    Included INTEGER NOT NULL DEFAULT 1,
                    Polygon TEXT NOT NULL DEFAULT '[]');
                CREATE TABLE IF NOT EXISTS Model (
                    Id INTEGER NOT NULL CONSTRAINT PK_Model PRIMARY KEY,
                    Weights BLOB NOT NULL,
                    Epochs INTEGER NOT NULL,
                    ValidationLoss REAL NOT NULL,
                    TrainedAt TEXT NOT NULL,
                    CONSTRAINT CK_Model_Id CHECK (Id = 1));
                CREATE TABLE IF NOT EXISTS TrainingSettings (
                    Id INTEGER NOT NULL CONSTRAINT PK_TrainingSettings PRIMARY KEY,
                    Value TEXT NOT NULL,
                    CONSTRAINT CK_TrainingSettings_Id CHECK (Id = 1));
                """);
        }

        /// <inheritdoc/>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "Model");

            migrationBuilder.DropTable(name: "Samples");

            migrationBuilder.DropTable(name: "TrainingSettings");
        }
    }
}
