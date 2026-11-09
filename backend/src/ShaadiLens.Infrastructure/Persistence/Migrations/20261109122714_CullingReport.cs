using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShaadiLens.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CullingReport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Photos_CollectionId",
                table: "Photos");

            migrationBuilder.AddColumn<Guid>(
                name: "BurstGroupId",
                table: "Photos",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "BurstPickedByAdmin",
                table: "Photos",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ClosedEyesCount",
                table: "Photos",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsBurstBest",
                table: "Photos",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "PerceptualHash",
                table: "Photos",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "Sharpness",
                table: "Photos",
                type: "real",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "EyeAspectRatio",
                table: "Faces",
                type: "real",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Photos_BurstGroupId",
                table: "Photos",
                column: "BurstGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Photos_CollectionId_TakenAt",
                table: "Photos",
                columns: new[] { "CollectionId", "TakenAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Photos_BurstGroupId",
                table: "Photos");

            migrationBuilder.DropIndex(
                name: "IX_Photos_CollectionId_TakenAt",
                table: "Photos");

            migrationBuilder.DropColumn(
                name: "BurstGroupId",
                table: "Photos");

            migrationBuilder.DropColumn(
                name: "BurstPickedByAdmin",
                table: "Photos");

            migrationBuilder.DropColumn(
                name: "ClosedEyesCount",
                table: "Photos");

            migrationBuilder.DropColumn(
                name: "IsBurstBest",
                table: "Photos");

            migrationBuilder.DropColumn(
                name: "PerceptualHash",
                table: "Photos");

            migrationBuilder.DropColumn(
                name: "Sharpness",
                table: "Photos");

            migrationBuilder.DropColumn(
                name: "EyeAspectRatio",
                table: "Faces");

            migrationBuilder.CreateIndex(
                name: "IX_Photos_CollectionId",
                table: "Photos",
                column: "CollectionId");
        }
    }
}
