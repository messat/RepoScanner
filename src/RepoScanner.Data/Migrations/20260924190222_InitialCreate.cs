using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RepoScanner.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Repositories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AzureDevOpsId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DefaultBranch = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Repositories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Findings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RepositoryId = table.Column<int>(type: "int", nullable: false),
                    Fingerprint = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ScannerType = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    RuleId = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    Severity = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    FilePath = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: true),
                    Message = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    MaskedPreview = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SecretHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    PackageName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    PackageVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    AdvisoryUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ResolvedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SuppressedUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SuppressedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    SuppressionReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Findings", x => x.Id);
                    table.CheckConstraint("CK_Findings_SuppressionReason", "[Status] <> 'Suppressed' OR [SuppressionReason] IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_Findings_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ScanRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RepositoryId = table.Column<int>(type: "int", nullable: false),
                    TriggerType = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    CommitSha = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NewCount = table.Column<int>(type: "int", nullable: false),
                    ReopenedCount = table.Column<int>(type: "int", nullable: false),
                    ResolvedCount = table.Column<int>(type: "int", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScanRuns_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FindingEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FindingId = table.Column<int>(type: "int", nullable: false),
                    ScanRunId = table.Column<int>(type: "int", nullable: true),
                    EventType = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FindingEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FindingEvents_Findings_FindingId",
                        column: x => x.FindingId,
                        principalTable: "Findings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FindingEvents_ScanRuns_ScanRunId",
                        column: x => x.ScanRunId,
                        principalTable: "ScanRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ScannerResults",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ScanRunId = table.Column<int>(type: "int", nullable: false),
                    ScannerType = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    FindingCount = table.Column<int>(type: "int", nullable: false),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScannerResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScannerResults_ScanRuns_ScanRunId",
                        column: x => x.ScanRunId,
                        principalTable: "ScanRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FindingEvents_FindingId_OccurredUtc",
                table: "FindingEvents",
                columns: new[] { "FindingId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FindingEvents_ScanRunId",
                table: "FindingEvents",
                column: "ScanRunId");

            migrationBuilder.CreateIndex(
                name: "IX_Findings_RepositoryId_Fingerprint",
                table: "Findings",
                columns: new[] { "RepositoryId", "Fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Findings_RepositoryId_Status",
                table: "Findings",
                columns: new[] { "RepositoryId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_AzureDevOpsId",
                table: "Repositories",
                column: "AzureDevOpsId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScannerResults_ScanRunId_ScannerType",
                table: "ScannerResults",
                columns: new[] { "ScanRunId", "ScannerType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScanRuns_RepositoryId_StartedUtc",
                table: "ScanRuns",
                columns: new[] { "RepositoryId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ScanRuns_RepositoryId_Status",
                table: "ScanRuns",
                columns: new[] { "RepositoryId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FindingEvents");

            migrationBuilder.DropTable(
                name: "ScannerResults");

            migrationBuilder.DropTable(
                name: "Findings");

            migrationBuilder.DropTable(
                name: "ScanRuns");

            migrationBuilder.DropTable(
                name: "Repositories");
        }
    }
}
