using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aperture.Modules.Sales.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSalesSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The module's schema, created before it owns any table. EF would create the schema
            // implicitly when it writes the sales.__migrations history row, but an explicit
            // EnsureSchema makes the module's ownership of `sales` a deliberate, greppable fact of
            // this migration rather than a side effect of the history table's location. Tables, their
            // scope columns, and the per-table RLS policy (ScopeRlsPolicy.Enable) arrive with the
            // aggregates in P2+.
            migrationBuilder.EnsureSchema(name: "sales");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropSchema(name: "sales");
        }
    }
}
