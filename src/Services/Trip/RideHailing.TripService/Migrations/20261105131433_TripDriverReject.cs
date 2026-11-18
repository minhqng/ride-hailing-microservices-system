using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RideHailing.TripService.Migrations
{
    /// <inheritdoc />
    public partial class TripDriverReject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "failure_reason",
                table: "trips",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_trips_failure_reason_only_match_failed",
                table: "trips",
                sql: "(failure_reason IS NULL OR state = 'MATCH_FAILED')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_trips_failure_reason_valid",
                table: "trips",
                sql: "(failure_reason IS NULL OR failure_reason IN ('NO_DRIVER_AVAILABLE', 'DRIVER_REJECTED'))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_trips_failure_reason_only_match_failed",
                table: "trips");

            migrationBuilder.DropCheckConstraint(
                name: "CK_trips_failure_reason_valid",
                table: "trips");

            migrationBuilder.DropColumn(
                name: "failure_reason",
                table: "trips");
        }
    }
}
