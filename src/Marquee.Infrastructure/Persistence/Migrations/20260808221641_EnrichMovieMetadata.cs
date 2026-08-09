using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marquee.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnrichMovieMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OriginalLanguage",
                table: "movies",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalTitle",
                table: "movies",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ReleaseDate",
                table: "movies",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Runtime",
                table: "movies",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "countries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Iso3166Code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_countries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "movie_countries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieId = table.Column<Guid>(type: "uuid", nullable: false),
                    CountryId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_movie_countries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_movie_countries_countries_CountryId",
                        column: x => x.CountryId,
                        principalTable: "countries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_movie_countries_movies_MovieId",
                        column: x => x.MovieId,
                        principalTable: "movies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_movies_OriginalLanguage",
                table: "movies",
                column: "OriginalLanguage");

            migrationBuilder.CreateIndex(
                name: "IX_movies_ReleaseYear",
                table: "movies",
                column: "ReleaseYear");

            migrationBuilder.CreateIndex(
                name: "IX_countries_Iso3166Code",
                table: "countries",
                column: "Iso3166Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_movie_countries_CountryId",
                table: "movie_countries",
                column: "CountryId");

            migrationBuilder.CreateIndex(
                name: "IX_movie_countries_MovieId_CountryId",
                table: "movie_countries",
                columns: new[] { "MovieId", "CountryId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "movie_countries");

            migrationBuilder.DropTable(
                name: "countries");

            migrationBuilder.DropIndex(
                name: "IX_movies_OriginalLanguage",
                table: "movies");

            migrationBuilder.DropIndex(
                name: "IX_movies_ReleaseYear",
                table: "movies");

            migrationBuilder.DropColumn(
                name: "OriginalLanguage",
                table: "movies");

            migrationBuilder.DropColumn(
                name: "OriginalTitle",
                table: "movies");

            migrationBuilder.DropColumn(
                name: "ReleaseDate",
                table: "movies");

            migrationBuilder.DropColumn(
                name: "Runtime",
                table: "movies");
        }
    }
}
