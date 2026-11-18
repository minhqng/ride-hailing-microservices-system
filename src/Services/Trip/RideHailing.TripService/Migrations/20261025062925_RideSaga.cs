using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RideHailing.TripService.Migrations
{
    /// <inheritdoc />
    public partial class RideSaga : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ride_sagas",
                columns: table => new
                {
                    saga_id = table.Column<Guid>(type: "uuid", nullable: false),
                    trip_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    current_step = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    step_version = table.Column<int>(type: "integer", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    authorize_operation_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    capture_operation_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    void_operation_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    last_error_code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    requires_review_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ride_sagas", x => x.saga_id);
                    table.CheckConstraint("CK_ride_sagas_operation_lineage", "(authorize_operation_key IS NULL OR authorize_operation_key = trip_id::text || ':authorize:v1') AND (capture_operation_key IS NULL OR capture_operation_key = trip_id::text || ':capture:v1') AND (void_operation_key IS NULL OR void_operation_key = trip_id::text || ':void:v1')");
                    table.CheckConstraint("CK_ride_sagas_reservation_present", "reservation_id IS NOT NULL AND reservation_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_ride_sagas_status_domain", "status IN ('ACTIVE', 'COMPENSATING', 'COMPLETED', 'COMPENSATED', 'REQUIRES_REVIEW')");
                    table.CheckConstraint("CK_ride_sagas_step_domain", "current_step IN ('AUTHORIZE_PENDING', 'AUTHORIZED', 'CAPTURE_PENDING', 'CAPTURED', 'RELEASE_PENDING', 'VOID_PENDING')");
                    table.CheckConstraint("CK_ride_sagas_step_key_agreement", "((current_step NOT IN ('CAPTURE_PENDING', 'CAPTURED')) OR capture_operation_key IS NOT NULL) AND (current_step <> 'VOID_PENDING' OR void_operation_key IS NOT NULL) AND (status <> 'COMPLETED' OR current_step = 'CAPTURED')");
                    table.CheckConstraint("CK_ride_sagas_step_version_positive", "step_version >= 1");
                });

            migrationBuilder.CreateIndex(
                name: "ix_ride_sagas_status",
                table: "ride_sagas",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "UX_ride_sagas_one_per_trip",
                table: "ride_sagas",
                column: "trip_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ride_sagas");
        }
    }
}
