using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexPharma.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReturnTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Cgst",
                table: "SaleReturns",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "SaleItemId",
                table: "SaleReturns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Sgst",
                table: "SaleReturns",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "PurchaseItemId",
                table: "PurchaseReturns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SaleReturns_SaleItemId",
                table: "SaleReturns",
                column: "SaleItemId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturns_PurchaseItemId",
                table: "PurchaseReturns",
                column: "PurchaseItemId");

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseReturns_PurchaseItems_PurchaseItemId",
                table: "PurchaseReturns",
                column: "PurchaseItemId",
                principalTable: "PurchaseItems",
                principalColumn: "PurchaseItemId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SaleReturns_SaleItems_SaleItemId",
                table: "SaleReturns",
                column: "SaleItemId",
                principalTable: "SaleItems",
                principalColumn: "SaleItemId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseReturns_PurchaseItems_PurchaseItemId",
                table: "PurchaseReturns");

            migrationBuilder.DropForeignKey(
                name: "FK_SaleReturns_SaleItems_SaleItemId",
                table: "SaleReturns");

            migrationBuilder.DropIndex(
                name: "IX_SaleReturns_SaleItemId",
                table: "SaleReturns");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseReturns_PurchaseItemId",
                table: "PurchaseReturns");

            migrationBuilder.DropColumn(
                name: "Cgst",
                table: "SaleReturns");

            migrationBuilder.DropColumn(
                name: "SaleItemId",
                table: "SaleReturns");

            migrationBuilder.DropColumn(
                name: "Sgst",
                table: "SaleReturns");

            migrationBuilder.DropColumn(
                name: "PurchaseItemId",
                table: "PurchaseReturns");
        }
    }
}
