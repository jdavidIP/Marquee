using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marquee.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailConfirmation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "EmailConfirmedAt",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            // Issue #29 changes what unconfirmed means going forward, not retroactively: every
            // account that already exists in a database this migration runs against was created
            // before confirmation existed, so backfilling with CreatedAt is what "grandfather them in
            // as confirmed" looks like. Without this, upgrading a dev database would silently strand
            // every existing user — including the seeded admin — as an anonymous participant.
            migrationBuilder.Sql(
                "UPDATE users SET \"EmailConfirmedAt\" = \"CreatedAt\" WHERE \"EmailConfirmedAt\" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailConfirmedAt",
                table: "users");
        }
    }
}
