using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RideHailing.TripService.Migrations
{
    /// <inheritdoc />
    public partial class W07_DurableCancellationCleanup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "trip_cleanup_intents",
                columns: table => new
                {
                    trip_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    driver_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    matching_round = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trip_cleanup_intents", x => new { x.trip_id, x.key });
                    table.CheckConstraint("CK_cleanup_identity", "(kind = 'ReleaseReservation' AND driver_id IS NOT NULL\n AND driver_id <> '00000000-0000-0000-0000-000000000000'::uuid\n AND reservation_id IS NOT NULL\n AND reservation_id <> '00000000-0000-0000-0000-000000000000'::uuid\n AND key = 'release:' || reservation_id::text)\nOR (kind = 'CancelMatching' AND driver_id IS NULL AND reservation_id IS NULL\n    AND key = 'matching:' || matching_round::text)\nOR (kind = 'ReconcilePayment' AND driver_id IS NULL AND reservation_id IS NULL AND key = 'payment')");
                    table.CheckConstraint("CK_cleanup_round", "matching_round >= 1");
                    table.ForeignKey(
                        name: "FK_trip_cleanup_intents_trips_trip_id",
                        column: x => x.trip_id,
                        principalTable: "trips",
                        principalColumn: "trip_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_trip_cleanup_intents_created_at",
                table: "trip_cleanup_intents",
                column: "created_at",
                filter: "completed_at IS NULL");

            // W6 cancellations retained assignment identity but had no durable
            // work ledger. Recover those obligations during upgrade; never
            // infer that an external release/void has already happened.
            migrationBuilder.Sql("""
                INSERT INTO trip_cleanup_intents
                    (trip_id, key, kind, matching_round, created_at)
                SELECT trip_id, 'matching:' || current_matching_round::text,
                    'CancelMatching', current_matching_round, updated_at
                FROM trips WHERE state = 'CANCELLED' AND current_matching_round >= 1;

                INSERT INTO trip_cleanup_intents
                    (trip_id, key, kind, driver_id, reservation_id, matching_round, created_at)
                SELECT trip_id, 'release:' || reservation_id::text, 'ReleaseReservation',
                    assigned_driver_id, reservation_id, matching_round, updated_at
                FROM trips WHERE state = 'CANCELLED' AND reservation_id IS NOT NULL;

                INSERT INTO trip_cleanup_intents
                    (trip_id, key, kind, matching_round, created_at)
                SELECT trip_id, 'payment', 'ReconcilePayment', current_matching_round, updated_at
                FROM trips WHERE state = 'CANCELLED' AND reservation_id IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "trip_cleanup_intents");
        }
    }
}
