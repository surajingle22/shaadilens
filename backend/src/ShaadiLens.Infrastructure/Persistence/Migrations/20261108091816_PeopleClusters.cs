using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShaadiLens.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PeopleClusters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Phase 1 never filled ClusterId (int); Postgres can't cast integer → uuid, so recreate the column.
            migrationBuilder.DropColumn(name: "ClusterId", table: "Faces");
            migrationBuilder.AddColumn<Guid>(name: "ClusterId", table: "Faces", type: "uuid", nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClusteringStartedAt",
                table: "Events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClustersUpdatedAt",
                table: "Events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PersonClusters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    GuestId = table.Column<Guid>(type: "uuid", nullable: true),
                    FaceCount = table.Column<int>(type: "integer", nullable: false),
                    PhotoCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonClusters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PersonClusters_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PersonClusters_Guests_GuestId",
                        column: x => x.GuestId,
                        principalTable: "Guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Faces_ClusterId",
                table: "Faces",
                column: "ClusterId");

            migrationBuilder.CreateIndex(
                name: "IX_PersonClusters_EventId_Number",
                table: "PersonClusters",
                columns: new[] { "EventId", "Number" });

            migrationBuilder.CreateIndex(
                name: "IX_PersonClusters_GuestId",
                table: "PersonClusters",
                column: "GuestId");

            migrationBuilder.AddForeignKey(
                name: "FK_Faces_PersonClusters_ClusterId",
                table: "Faces",
                column: "ClusterId",
                principalTable: "PersonClusters",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Faces_PersonClusters_ClusterId",
                table: "Faces");

            migrationBuilder.DropTable(
                name: "PersonClusters");

            migrationBuilder.DropIndex(
                name: "IX_Faces_ClusterId",
                table: "Faces");

            migrationBuilder.DropColumn(
                name: "ClusteringStartedAt",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "ClustersUpdatedAt",
                table: "Events");

            migrationBuilder.DropColumn(name: "ClusterId", table: "Faces");
            migrationBuilder.AddColumn<int>(name: "ClusterId", table: "Faces", type: "integer", nullable: true);
        }
    }
}
