using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexPharma.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduleXDispense : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScheduleXDispenses",
                columns: table => new
                {
                    ScheduleXDispenseId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SaleId = table.Column<int>(type: "INTEGER", nullable: false),
                    SaleItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductId = table.Column<int>(type: "INTEGER", nullable: false),
                    BatchId = table.Column<int>(type: "INTEGER", nullable: false),
                    Qty = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    PatientName = table.Column<string>(type: "TEXT", nullable: false),
                    PatientAddress = table.Column<string>(type: "TEXT", nullable: false),
                    PatientPhone = table.Column<string>(type: "TEXT", nullable: true),
                    PrescriberName = table.Column<string>(type: "TEXT", nullable: false),
                    PrescriberAddress = table.Column<string>(type: "TEXT", nullable: false),
                    PrescriberRegNo = table.Column<string>(type: "TEXT", nullable: false),
                    PrescriptionNumber = table.Column<string>(type: "TEXT", nullable: false),
                    PrescriptionDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PrescriptionRetained = table.Column<bool>(type: "INTEGER", nullable: false),
                    DispensedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleXDispenses", x => x.ScheduleXDispenseId);
                    table.ForeignKey(
                        name: "FK_ScheduleXDispenses_Batches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "Batches",
                        principalColumn: "BatchId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScheduleXDispenses_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "ProductId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScheduleXDispenses_SaleItems_SaleItemId",
                        column: x => x.SaleItemId,
                        principalTable: "SaleItems",
                        principalColumn: "SaleItemId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScheduleXDispenses_Sales_SaleId",
                        column: x => x.SaleId,
                        principalTable: "Sales",
                        principalColumn: "SaleId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScheduleXDispenses_Users_CreatedBy",
                        column: x => x.CreatedBy,
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleXDispenses_BatchId",
                table: "ScheduleXDispenses",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleXDispenses_CreatedBy",
                table: "ScheduleXDispenses",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleXDispenses_ProductId_DispensedAt",
                table: "ScheduleXDispenses",
                columns: new[] { "ProductId", "DispensedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleXDispenses_SaleId",
                table: "ScheduleXDispenses",
                column: "SaleId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleXDispenses_SaleItemId",
                table: "ScheduleXDispenses",
                column: "SaleItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScheduleXDispenses");
        }
    }
}
