using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;

namespace Tideline.Data;

/// <summary>Runs the embedded migrations against a SQLite database. Used by <c>tideline migrate</c> and at startup.</summary>
public static class Migrator
{
    public static void MigrateUp(string connectionString)
    {
        using var services = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddSQLite()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(Migrator).Assembly).For.Migrations())
            .BuildServiceProvider(validateScopes: false);

        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
    }
}
