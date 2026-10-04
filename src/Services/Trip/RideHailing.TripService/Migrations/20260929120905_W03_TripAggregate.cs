using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RideHailing.TripService.Migrations
{
    /// <inheritdoc />
    public partial class W03_TripAggregate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "trips",
                columns: table => new
                {
                    trip_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rider_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    assigned_driver_id = table.Column<Guid>(type: "uuid", nullable: true),
                    matching_round = table.Column<int>(type: "integer", nullable: true),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    dropoff_latitude = table.Column<double>(type: "double precision", nullable: false),
                    dropoff_longitude = table.Column<double>(type: "double precision", nullable: false),
                    fare_amount = table.Column<decimal>(type: "numeric", nullable: false),
                    fare_created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fare_currency = table.Column<string>(type: "text", nullable: false),
                    fare_policy_version = table.Column<string>(type: "text", nullable: false),
                    pickup_latitude = table.Column<double>(type: "double precision", nullable: false),
                    pickup_longitude = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trips", x => x.trip_id);
                    table.CheckConstraint("CK_trips_assignment_coherence", "(assigned_driver_id IS NULL AND reservation_id IS NULL AND matching_round IS NULL) OR (assigned_driver_id IS NOT NULL AND reservation_id IS NOT NULL AND matching_round IS NOT NULL)");
                    table.CheckConstraint("CK_trips_fare_amount_non_negative", "fare_amount >= 0");
                    table.CheckConstraint("CK_trips_state_assignment_coherence", "(state NOT IN ('DRIVER_RESERVED', 'DRIVER_ACCEPTED', 'READY_TO_START', 'IN_PROGRESS', 'COMPLETED') OR (assigned_driver_id IS NOT NULL AND reservation_id IS NOT NULL AND matching_round IS NOT NULL)) AND (state NOT IN ('CREATED', 'MATCHING', 'MATCH_FAILED') OR (assigned_driver_id IS NULL AND reservation_id IS NULL AND matching_round IS NULL))");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "trips");
        }
    }
}
