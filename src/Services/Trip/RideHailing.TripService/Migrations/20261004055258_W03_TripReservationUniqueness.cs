using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RideHailing.TripService.Migrations
{
    /// <inheritdoc />
    public partial class W03_TripReservationUniqueness : Migration
    {
        /// <summary>
        /// DSI-001 Trip-side defense-in-depth (Doc 01 §26, A-W02-08 freeze
        /// note): one logical Driver reservation effect is current for at
        /// most one Trip. Driver-side exclusivity stays authoritative
        /// (guarded reserve); this partial unique index makes a
        /// double-claiming trips row uncommittable. Raw SQL because EF Core
        /// cannot map an index onto a ComplexProperty member; the EF model
        /// itself is unchanged (no drift).
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX IF NOT EXISTS ux_trips_reservation_id " +
                "ON trips (reservation_id) WHERE reservation_id IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ux_trips_reservation_id;");
        }
    }
}
