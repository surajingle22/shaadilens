using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShaadiLens.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReferenceFaceAndImage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "FaceId",
                table: "GuestReferences",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageKey",
                table: "GuestReferences",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            // Phase 1 guests have exactly one selfie reference: link it to the stored selfie file.
            migrationBuilder.Sql("""
                UPDATE "GuestReferences" r SET "ImageKey" = g."SelfieKey"
                FROM "Guests" g
                WHERE g."Id" = r."GuestId" AND r."Source" = 0 AND r."ImageKey" IS NULL AND g."SelfieKey" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FaceId",
                table: "GuestReferences");

            migrationBuilder.DropColumn(
                name: "ImageKey",
                table: "GuestReferences");
        }
    }
}
