using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RideHailing.TripService.Migrations
{
    /// <inheritdoc />
    public partial class W08_TripMessageReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "trip_message_receipts",
                columns: table => new
                {
                    consumer = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    message_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    trip_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    envelope_json = table.Column<string>(type: "jsonb", nullable: false),
                    disposition = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trip_message_receipts", x => new { x.consumer, x.message_id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_trip_message_receipts_disposition_recorded_at",
                table: "trip_message_receipts",
                columns: new[] { "disposition", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "IX_trip_message_receipts_trip_id",
                table: "trip_message_receipts",
                column: "trip_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "trip_message_receipts");
        }
    }
}
