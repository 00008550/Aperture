using System;
using Aperture.SharedKernel.Data.RowLevelSecurity;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aperture.Modules.Sales.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "sales");

            migrationBuilder.CreateTable(
                name: "accounts",
                schema: "sales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    tax_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    credit_limit = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    payment_terms_days = table.Column<int>(type: "integer", nullable: false),
                    region_id = table.Column<Guid>(type: "uuid", nullable: true),
                    team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_accounts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_accounts_tenant_id_created_at_id",
                schema: "sales",
                table: "accounts",
                columns: new[] { "tenant_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_accounts_tenant_id_tax_id",
                schema: "sales",
                table: "accounts",
                columns: new[] { "tenant_id", "tax_id" },
                unique: true);

            // Adopt the row-security convention (009-P3) on the first real Sales table. This is where the
            // reader-role GRANT USAGE/SELECT and the RLS policy deferred from P1 land: the least-privilege
            // aperture_reader role gains SELECT on sales.accounts, bound by a policy that re-asserts tenant
            // + scope on every row below the SQL string. RLS is NO FORCE, so the owner role EF and
            // migrations use bypasses it — this is invisible to EF and deploys while old code runs.
            migrationBuilder.Sql(ScopeRlsPolicy.Enable("sales", "accounts"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "accounts",
                schema: "sales");
        }
    }
}
