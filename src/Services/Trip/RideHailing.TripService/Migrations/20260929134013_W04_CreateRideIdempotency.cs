using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RideHailing.TripService.Migrations
{
    /// <inheritdoc />
    public partial class W04_CreateRideIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "request_idempotency",
                columns: table => new
                {
                    scope = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_request_idempotency", x => new { x.scope, x.key });
                    table.ForeignKey(
                        name: "FK_request_idempotency_trips_resource_id",
                        column: x => x.resource_id,
                        principalTable: "trips",
                        principalColumn: "trip_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_trips_one_active_per_rider",
                table: "trips",
                column: "rider_id",
                unique: true,
                filter: "state IN ('CREATED', 'DRIVER_ACCEPTED', 'DRIVER_RESERVED', 'IN_PROGRESS', 'MATCHING', 'READY_TO_START')");

            migrationBuilder.CreateIndex(
                name: "IX_request_idempotency_resource_id",
                table: "request_idempotency",
                column: "resource_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "request_idempotency");

            migrationBuilder.DropIndex(
                name: "UX_trips_one_active_per_rider",
                table: "trips");
        }
    }
}
