using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RideHailing.TripService.Migrations
{
    /// <inheritdoc />
    public partial class W05_TripLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "current_matching_round",
                table: "trips",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "trips",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // Backfill honesty: pre-W5 rows never tracked a last-transition
            // timestamp, so the closest true value is their creation time.
            // Npgsql stores the DateTimeOffset.MinValue DDL default above as
            // '-infinity'; this statement restores real history before any
            // new CHECK below is enforced.
            migrationBuilder.Sql(
                "UPDATE trips SET updated_at = created_at WHERE updated_at = '-infinity';");

            migrationBuilder.AddCheckConstraint(
                name: "CK_trips_assignment_round_agreement",
                table: "trips",
                sql: "(assigned_driver_id IS NULL OR matching_round = current_matching_round)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_trips_assignment_round_positive",
                table: "trips",
                sql: "(matching_round IS NULL OR matching_round >= 1)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_trips_created_without_round",
                table: "trips",
                sql: "(state <> 'CREATED' OR current_matching_round = 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_trips_current_round_non_negative",
                table: "trips",
                sql: "current_matching_round >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_trips_post_created_have_round",
                table: "trips",
                sql: "(state NOT IN ('MATCHING', 'DRIVER_RESERVED', 'DRIVER_ACCEPTED', 'READY_TO_START', 'IN_PROGRESS', 'COMPLETED', 'MATCH_FAILED') OR current_matching_round >= 1)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_trips_assignment_round_agreement",
                table: "trips");

            migrationBuilder.DropCheckConstraint(
                name: "CK_trips_assignment_round_positive",
                table: "trips");

            migrationBuilder.DropCheckConstraint(
                name: "CK_trips_created_without_round",
                table: "trips");

            migrationBuilder.DropCheckConstraint(
                name: "CK_trips_current_round_non_negative",
                table: "trips");

            migrationBuilder.DropCheckConstraint(
                name: "CK_trips_post_created_have_round",
                table: "trips");

            migrationBuilder.DropColumn(
                name: "current_matching_round",
                table: "trips");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "trips");
        }
    }
}
