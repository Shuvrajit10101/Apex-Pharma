using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexPharma.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBatchProductBatchNoIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Batches_ProductId",
                table: "Batches");

            migrationBuilder.CreateIndex(
                name: "IX_Batches_ProductId_BatchNo",
                table: "Batches",
                columns: new[] { "ProductId", "BatchNo" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Batches_ProductId_BatchNo",
                table: "Batches");

            migrationBuilder.CreateIndex(
                name: "IX_Batches_ProductId",
                table: "Batches",
                column: "ProductId");
        }
    }
}
