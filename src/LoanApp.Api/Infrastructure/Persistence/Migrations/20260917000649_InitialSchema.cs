using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LoanApp.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    Component = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Action = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ActorRef = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    EntityType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ReasonCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    PolicyRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    OutboxEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                    table.CheckConstraint("CK_Audit_Outcome", "\"Outcome\" IN ('Succeeded','Denied')");
                });

            migrationBuilder.CreateTable(
                name: "Customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Ssn = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    FirstName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LastName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AddressLine1 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AddressLine2 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    City = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    State = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    PostalCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CompanyName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                    table.CheckConstraint("CK_Customer_Ssn", "\"Ssn\" ~ '^[0-9]{9}$'");
                });

            migrationBuilder.CreateTable(
                name: "PolicyRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    BaseRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    DocumentJson = table.Column<string>(type: "jsonb", nullable: false),
                    DraftVersion = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyRevisions", x => x.Id);
                    table.CheckConstraint("CK_Policy_Kind", "\"Kind\" IN ('Draft','Published')");
                    table.CheckConstraint("CK_Policy_Publication", "(\"Kind\" = 'Published') = (\"PublishedAtUtc\" IS NOT NULL)");
                    table.CheckConstraint("CK_Policy_Version", "\"DraftVersion\" >= 1 AND \"SchemaVersion\" = 1");
                    table.ForeignKey(
                        name: "FK_PolicyRevisions_PolicyRevisions_BaseRevisionId",
                        column: x => x.BaseRevisionId,
                        principalTable: "PolicyRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Applications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedAmount = table.Column<decimal>(type: "numeric(11,2)", precision: 11, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    LastPolicyRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Applications", x => x.Id);
                    table.CheckConstraint("CK_Application_Amount", "\"RequestedAmount\" > 0");
                    table.CheckConstraint("CK_Application_Currency", "\"Currency\" = 'USD'");
                    table.CheckConstraint("CK_Application_Version", "\"Version\" >= 1");
                    table.ForeignKey(
                        name: "FK_Applications_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Applications_PolicyRevisions_LastPolicyRevisionId",
                        column: x => x.LastPolicyRevisionId,
                        principalTable: "PolicyRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PolicyHeads",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    ActiveRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    DraftRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    BaselineRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyHeads", x => x.Id);
                    table.CheckConstraint("CK_PolicyHead_Singleton", "\"Id\" = 1 AND \"Version\" >= 1");
                    table.ForeignKey(
                        name: "FK_PolicyHeads_PolicyRevisions_ActiveRevisionId",
                        column: x => x.ActiveRevisionId,
                        principalTable: "PolicyRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PolicyHeads_PolicyRevisions_BaselineRevisionId",
                        column: x => x.BaselineRevisionId,
                        principalTable: "PolicyRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PolicyHeads_PolicyRevisions_DraftRevisionId",
                        column: x => x.DraftRevisionId,
                        principalTable: "PolicyRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationVersion = table.Column<long>(type: "bigint", nullable: false),
                    PolicyRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Operation = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeliveredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastErrorCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                    table.CheckConstraint("CK_Outbox_Lease", "(\"LeaseToken\" IS NULL) = (\"LeaseExpiresAtUtc\" IS NULL)");
                    table.CheckConstraint("CK_Outbox_Operation", "\"Operation\" IN ('Created','Updated')");
                    table.CheckConstraint("CK_Outbox_Status", "\"Status\" IN ('Pending','Failed','Delivered')");
                    table.CheckConstraint("CK_Outbox_Versions", "\"SchemaVersion\" = 1 AND \"ApplicationVersion\" >= 1 AND \"AttemptCount\" >= 0");
                    table.ForeignKey(
                        name: "FK_OutboxMessages_Applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "Applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OutboxMessages_PolicyRevisions_PolicyRevisionId",
                        column: x => x.PolicyRevisionId,
                        principalTable: "PolicyRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "PolicyHeads",
                columns: new[] { "Id", "ActiveRevisionId", "BaselineRevisionId", "DraftRevisionId", "Version" },
                values: new object[] { 1, null, null, null, 1L });

            migrationBuilder.CreateIndex(
                name: "IX_Applications_CustomerId",
                table: "Applications",
                column: "CustomerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Applications_LastPolicyRevisionId",
                table: "Applications",
                column: "LastPolicyRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_CorrelationId",
                table: "AuditEvents",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_EntityType_EntityId_OccurredAtUtc_Id",
                table: "AuditEvents",
                columns: new[] { "EntityType", "EntityId", "OccurredAtUtc", "Id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_OccurredAtUtc_Id",
                table: "AuditEvents",
                columns: new[] { "OccurredAtUtc", "Id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_OutboxEventId",
                table: "AuditEvents",
                column: "OutboxEventId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_Ssn",
                table: "Customers",
                column: "Ssn",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_ApplicationId_ApplicationVersion",
                table: "OutboxMessages",
                columns: new[] { "ApplicationId", "ApplicationVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_ApplicationId_ApplicationVersion_Status",
                table: "OutboxMessages",
                columns: new[] { "ApplicationId", "ApplicationVersion", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_PolicyRevisionId",
                table: "OutboxMessages",
                column: "PolicyRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Status_NextAttemptAtUtc",
                table: "OutboxMessages",
                columns: new[] { "Status", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PolicyHeads_ActiveRevisionId",
                table: "PolicyHeads",
                column: "ActiveRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyHeads_BaselineRevisionId",
                table: "PolicyHeads",
                column: "BaselineRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyHeads_DraftRevisionId",
                table: "PolicyHeads",
                column: "DraftRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyRevisions_BaseRevisionId",
                table: "PolicyRevisions",
                column: "BaseRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyRevisions_PublishedAtUtc_Id",
                table: "PolicyRevisions",
                columns: new[] { "PublishedAtUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "OutboxMessages");

            migrationBuilder.DropTable(
                name: "PolicyHeads");

            migrationBuilder.DropTable(
                name: "Applications");

            migrationBuilder.DropTable(
                name: "Customers");

            migrationBuilder.DropTable(
                name: "PolicyRevisions");
        }
    }
}
