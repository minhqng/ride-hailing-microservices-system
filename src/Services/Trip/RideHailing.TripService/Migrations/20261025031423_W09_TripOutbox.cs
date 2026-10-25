using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RideHailing.TripService.Migrations
{
    /// <inheritdoc />
    public partial class W09_TripOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    message_id = table.Column<string>(type: "text", nullable: false),
                    message_type = table.Column<string>(type: "text", nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: false),
                    causation_id = table.Column<string>(type: "text", nullable: true),
                    aggregate_type = table.Column<string>(type: "text", nullable: true),
                    aggregate_id = table.Column<string>(type: "text", nullable: true),
                    aggregate_version = table.Column<long>(type: "bigint", nullable: true),
                    saga_id = table.Column<string>(type: "text", nullable: true),
                    operation_key = table.Column<string>(type: "text", nullable: true),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    headers_json = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    exchange = table.Column<string>(type: "text", nullable: false),
                    routing_key = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "PENDING"),
                    attempt_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    claimed_by = table.Column<string>(type: "text", nullable: true),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error_code = table.Column<string>(type: "text", nullable: true),
                    last_error_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("outbox_messages_pk", x => x.message_id);
                    table.CheckConstraint("outbox_messages_attempt_count_check", "attempt_count >= 0");
                    table.CheckConstraint("outbox_messages_identity_nonempty", "btrim(message_id) <> '' AND btrim(message_type) <> '' AND btrim(correlation_id) <> '' AND btrim(exchange) <> '' AND btrim(routing_key) <> ''");
                    table.CheckConstraint("outbox_messages_schema_version_check", "schema_version >= 1");
                    table.CheckConstraint("outbox_messages_status_check", "status IN ('PENDING', 'PUBLISHED', 'FAILED_VISIBLE')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_claim",
                table: "outbox_messages",
                columns: new[] { "next_attempt_at", "occurred_at", "message_id" },
                filter: "status = 'PENDING'");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_failed_visible",
                table: "outbox_messages",
                columns: new[] { "occurred_at", "message_id" },
                filter: "status = 'FAILED_VISIBLE'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_messages");
        }
    }
}
