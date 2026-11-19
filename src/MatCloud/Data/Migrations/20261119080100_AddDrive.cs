using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatCloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDrive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "DriveRevision");

            migrationBuilder.CreateTable(
                name: "DriveBlob",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriveBlob", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DriveSpace",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    OwnerUserId = table.Column<long>(type: "bigint", nullable: true),
                    QuotaBytes = table.Column<long>(type: "bigint", nullable: true),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateState = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriveSpace", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DriveSpace_User_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DriveUpload",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Token = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    ParentId = table.Column<long>(type: "bigint", nullable: true),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    OnConflict = table.Column<int>(type: "integer", nullable: false),
                    NodeId = table.Column<long>(type: "bigint", nullable: true),
                    IfMatch = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Received = table.Column<long>(type: "bigint", nullable: false),
                    ExpiresDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    App = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriveUpload", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DriveNode",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DriveId = table.Column<long>(type: "bigint", nullable: false),
                    ParentId = table.Column<long>(type: "bigint", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    NameKey = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Category = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Path = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CurrentVersionId = table.Column<long>(type: "bigint", nullable: true),
                    VersionNumber = table.Column<int>(type: "integer", nullable: true),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ModifiedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TrashedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    InTrash = table.Column<bool>(type: "boolean", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateState = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriveNode", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DriveNode_DriveNode_ParentId",
                        column: x => x.ParentId,
                        principalTable: "DriveNode",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DriveNode_DriveSpace_DriveId",
                        column: x => x.DriveId,
                        principalTable: "DriveSpace",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DriveNodeVersion",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    NodeId = table.Column<long>(type: "bigint", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    App = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriveNodeVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DriveNodeVersion_DriveNode_NodeId",
                        column: x => x.NodeId,
                        principalTable: "DriveNode",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DriveStar",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    NodeId = table.Column<long>(type: "bigint", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriveStar", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DriveStar_DriveNode_NodeId",
                        column: x => x.NodeId,
                        principalTable: "DriveNode",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DriveVisit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    NodeId = table.Column<long>(type: "bigint", nullable: false),
                    VisitDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    App = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriveVisit", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DriveVisit_DriveNode_NodeId",
                        column: x => x.NodeId,
                        principalTable: "DriveNode",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DriveBlob_Sha256",
                table: "DriveBlob",
                column: "Sha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DriveNode_DriveId_Revision",
                table: "DriveNode",
                columns: new[] { "DriveId", "Revision" });

            migrationBuilder.CreateIndex(
                name: "IX_DriveNode_Name",
                table: "DriveNode",
                columns: new[] { "DriveId", "ParentId", "NameKey" },
                unique: true,
                filter: "\"InTrash\" = false AND \"UpdateState\" <> 0")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_DriveNode_ParentId",
                table: "DriveNode",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_DriveNode_Path",
                table: "DriveNode",
                column: "Path")
                .Annotation("Npgsql:IndexOperators", new[] { "text_pattern_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_DriveNodeVersion_NodeId_Number",
                table: "DriveNodeVersion",
                columns: new[] { "NodeId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DriveNodeVersion_Sha256",
                table: "DriveNodeVersion",
                column: "Sha256");

            migrationBuilder.CreateIndex(
                name: "IX_DriveSpace_OwnerUserId",
                table: "DriveSpace",
                column: "OwnerUserId",
                unique: true,
                filter: "\"Kind\" = 1 AND \"UpdateState\" <> 0");

            migrationBuilder.CreateIndex(
                name: "IX_DriveStar_NodeId",
                table: "DriveStar",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_DriveStar_UserId_NodeId",
                table: "DriveStar",
                columns: new[] { "UserId", "NodeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DriveUpload_Token",
                table: "DriveUpload",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DriveUpload_UserId",
                table: "DriveUpload",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_DriveVisit_NodeId",
                table: "DriveVisit",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_DriveVisit_UserId_NodeId",
                table: "DriveVisit",
                columns: new[] { "UserId", "NodeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DriveVisit_UserId_VisitDate",
                table: "DriveVisit",
                columns: new[] { "UserId", "VisitDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DriveBlob");

            migrationBuilder.DropTable(
                name: "DriveNodeVersion");

            migrationBuilder.DropTable(
                name: "DriveStar");

            migrationBuilder.DropTable(
                name: "DriveUpload");

            migrationBuilder.DropTable(
                name: "DriveVisit");

            migrationBuilder.DropTable(
                name: "DriveNode");

            migrationBuilder.DropTable(
                name: "DriveSpace");

            migrationBuilder.DropSequence(
                name: "DriveRevision");
        }
    }
}
