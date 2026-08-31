using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aperture.Modules.Access.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddScopeReaderRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The least-privilege login role the raw-SQL read path connects as (009-P3). Row-security
            // policies bind to it so an unscoped raw read cannot be expressed, and an unconfigured
            // connection returns nothing. Deliberately NOT the table owner and NOBYPASSRLS: the owner
            // role EF and migrations use bypasses RLS (policies are left NO FORCE), so this change is
            // invisible to EF — its blast radius is this role and nothing else.
            //
            // Idempotent: safe to re-run, and safe to deploy while old code runs (nothing connects as
            // this role until 009-P4 wires the second connection string).
            //
            // Created LOGIN but WITHOUT a password, so it cannot authenticate until one is set. The
            // password is provisioned out of band from configuration — `ALTER ROLE aperture_reader
            // PASSWORD '…'` from the deploy secret — never committed here. Per-table SELECT/USAGE
            // grants are added by the row-security policy convention (ScopeRlsPolicy.Enable), not here.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'aperture_reader') THEN
                        CREATE ROLE aperture_reader LOGIN;
                    END IF;
                END
                $$;
                ALTER ROLE aperture_reader
                    NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOINHERIT;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Safe only because no production table has adopted a policy that grants to this role yet
            // (this portion enables RLS on the test probe table only). A domain table that adopts the
            // convention must have its policy dropped before this role can be dropped.
            migrationBuilder.Sql("DROP ROLE IF EXISTS aperture_reader;");
        }
    }
}
