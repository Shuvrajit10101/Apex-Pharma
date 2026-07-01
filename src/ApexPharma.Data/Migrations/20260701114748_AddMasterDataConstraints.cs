using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexPharma.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMasterDataConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Products_Barcode",
                table: "Products");

            // Default TRUE so any rows that predate this column stay active on upgrade;
            // new inserts always set IsActive explicitly via the services (plan.md §6.1).
            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "Suppliers",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Manufacturers",
                type: "TEXT",
                nullable: false,
                collation: "NOCASE",
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "Manufacturers",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Categories",
                type: "TEXT",
                nullable: false,
                collation: "NOCASE",
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "Categories",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_Barcode",
                table: "Products",
                column: "Barcode",
                unique: true,
                filter: "[Barcode] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Manufacturers_Name",
                table: "Manufacturers",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Categories_Name",
                table: "Categories",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Products_Barcode",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Manufacturers_Name",
                table: "Manufacturers");

            migrationBuilder.DropIndex(
                name: "IX_Categories_Name",
                table: "Categories");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "Manufacturers");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "Categories");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Manufacturers",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldCollation: "NOCASE");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Categories",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldCollation: "NOCASE");

            migrationBuilder.CreateIndex(
                name: "IX_Products_Barcode",
                table: "Products",
                column: "Barcode");
        }
    }
}
