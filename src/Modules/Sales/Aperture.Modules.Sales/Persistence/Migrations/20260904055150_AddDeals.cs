using System;
using Aperture.SharedKernel.Data.RowLevelSecurity;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aperture.Modules.Sales.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "deals",
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
                    stage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    discount_pct = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    frozen_price_list_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    pending_approval = table.Column<bool>(type: "boolean", nullable: false),
                    pending_approval_discount_pct = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
                    lost_reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deals", x => x.id);
                    table.ForeignKey(
                        name: "FK_deals_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "sales",
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "deal_lines",
                schema: "sales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    price_list_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deal_lines", x => x.id);
                    table.ForeignKey(
                        name: "FK_deal_lines_deals_deal_id",
                        column: x => x.deal_id,
                        principalSchema: "sales",
                        principalTable: "deals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_deal_lines_deal_id",
                schema: "sales",
                table: "deal_lines",
                column: "deal_id");

            migrationBuilder.CreateIndex(
                name: "IX_deals_account_id",
                schema: "sales",
                table: "deals",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_deals_tenant_id_created_at_id",
                schema: "sales",
                table: "deals",
                columns: new[] { "tenant_id", "created_at", "id" });

            // Adopt the row-security convention (009-P3) on sales.deals, exactly as sales.accounts and
            // sales.contacts: the least-privilege aperture_reader role gains SELECT, bound by a policy that
            // re-asserts tenant + scope on every row below the SQL string. A deal's five scope columns are
            // denormalised from its account, so the single-table USING predicate admits a deal under the
            // same grants that admit its parent account (scope inheritance). RLS is NO FORCE, so EF (owner
            // role) and migrations bypass it — invisible to EF, deployable while old code runs.
            //
            // deal_lines is deliberately NOT scope-enforced: it carries no owner/team/region/account
            // columns (its scope is its deal's) and is never read through the reader-role grid on its own —
            // it is loaded only with its parent deal, which is itself scope-filtered.
            migrationBuilder.Sql(ScopeRlsPolicy.Enable("sales", "deals"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deal_lines",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "deals",
                schema: "sales");
        }
    }
}
