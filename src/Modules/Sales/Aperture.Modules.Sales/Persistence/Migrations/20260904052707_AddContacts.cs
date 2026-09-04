using System;
using Aperture.SharedKernel.Data.RowLevelSecurity;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aperture.Modules.Sales.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddContacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "contacts",
                schema: "sales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    region_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    phone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    messenger = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    is_departed = table.Column<bool>(type: "boolean", nullable: false),
                    departed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contacts", x => x.id);
                    table.ForeignKey(
                        name: "FK_contacts_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "sales",
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_contacts_account_id",
                schema: "sales",
                table: "contacts",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_contacts_tenant_id_created_at_id",
                schema: "sales",
                table: "contacts",
                columns: new[] { "tenant_id", "created_at", "id" });

            // Adopt the row-security convention (009-P3) on sales.contacts, exactly as sales.accounts: the
            // least-privilege aperture_reader role gains SELECT, bound by a policy that re-asserts tenant +
            // scope on every row below the SQL string. A contact's five scope columns are denormalised from
            // its account, so the single-table USING predicate admits a contact under the same grants that
            // admit its parent account (scope inheritance, edge 7). RLS is NO FORCE, so EF (owner role) and
            // migrations bypass it — invisible to EF, deployable while old code runs.
            migrationBuilder.Sql(ScopeRlsPolicy.Enable("sales", "contacts"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contacts",
                schema: "sales");
        }
    }
}
