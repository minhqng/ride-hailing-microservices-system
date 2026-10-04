using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RideHailing.TripService.Migrations
{
    /// <inheritdoc />
    public partial class W04_IdempotencyKeyCollation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "scope",
                table: "request_idempotency",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                collation: "C",
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<string>(
                name: "key",
                table: "request_idempotency",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                collation: "C",
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "scope",
                table: "request_idempotency",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldCollation: "C");

            migrationBuilder.AlterColumn<string>(
                name: "key",
                table: "request_idempotency",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldCollation: "C");
        }
    }
}
