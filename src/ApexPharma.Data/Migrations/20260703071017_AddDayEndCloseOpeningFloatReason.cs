using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexPharma.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDayEndCloseOpeningFloatReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OpeningFloatReason",
                table: "DayEndCloses",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OpeningFloatReason",
                table: "DayEndCloses");
        }
    }
}
