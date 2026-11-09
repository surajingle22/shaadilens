using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShaadiLens.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Reels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReelPhotoCount",
                table: "Events",
                type: "integer",
                nullable: false,
                defaultValue: 12); // existing events: reels on, 12 photos

            migrationBuilder.AddColumn<bool>(
                name: "ReelsEnabled",
                table: "Events",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "Reels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    GuestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    PlanJson = table.Column<string>(type: "jsonb", nullable: false),
                    PhotoCount = table.Column<int>(type: "integer", nullable: false),
                    DurationSeconds = table.Column<float>(type: "real", nullable: false),
                    StorageKey = table.Column<string>(type: "text", nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Reels_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Reels_Guests_GuestId",
                        column: x => x.GuestId,
                        principalTable: "Guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Reels_EventId",
                table: "Reels",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_Reels_GuestId_CreatedAt",
                table: "Reels",
                columns: new[] { "GuestId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Reels");

            migrationBuilder.DropColumn(
                name: "ReelPhotoCount",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "ReelsEnabled",
                table: "Events");
        }
    }
}
