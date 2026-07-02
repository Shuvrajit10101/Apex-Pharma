using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexPharma.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDayEndClose : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DayEndCloses",
                columns: table => new
                {
                    DayEndCloseId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    OpeningFloat = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    CashSales = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    CashReceipts = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    CashRefunds = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    CashSupplierPayments = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ExpectedCash = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    CountedCash = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Variance = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ClosingCarryForward = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DayEndCloses", x => x.DayEndCloseId);
                    table.ForeignKey(
                        name: "FK_DayEndCloses_Users_CreatedBy",
                        column: x => x.CreatedBy,
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DayEndCloses_BusinessDate",
                table: "DayEndCloses",
                column: "BusinessDate",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DayEndCloses_CreatedBy",
                table: "DayEndCloses",
                column: "CreatedBy");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DayEndCloses");
        }
    }
}
