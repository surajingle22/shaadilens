using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShaadiLens.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SelectionsWatermarkExpiry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PurgedAt",
                table: "Events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Selections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShareLinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    PhotoId = table.Column<Guid>(type: "uuid", nullable: false),
                    SelectedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Comment = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Selections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Selections_Photos_PhotoId",
                        column: x => x.PhotoId,
                        principalTable: "Photos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Selections_ShareLinks_ShareLinkId",
                        column: x => x.ShareLinkId,
                        principalTable: "ShareLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SelectionSubmissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShareLinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SubmittedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Locked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SelectionSubmissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SelectionSubmissions_ShareLinks_ShareLinkId",
                        column: x => x.ShareLinkId,
                        principalTable: "ShareLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Selections_PhotoId",
                table: "Selections",
                column: "PhotoId");

            migrationBuilder.CreateIndex(
                name: "IX_Selections_ShareLinkId_PhotoId",
                table: "Selections",
                columns: new[] { "ShareLinkId", "PhotoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SelectionSubmissions_ShareLinkId",
                table: "SelectionSubmissions",
                column: "ShareLinkId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Selections");

            migrationBuilder.DropTable(
                name: "SelectionSubmissions");

            migrationBuilder.DropColumn(
                name: "PurgedAt",
                table: "Events");
        }
    }
}
