using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NewsQuack.Infrastructure.Persistence;

/// <summary>
/// Design-time only (T-015): lets `dotnet ef migrations` run with this assembly as the startup project, without the App project —
/// so future migrations can be generated without App changes. The connection string is never opened during migration generation.
/// Runtime wiring (production connection string + interceptor) lives in NewsQuack.App's Program.cs.
/// </summary>
public sealed class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=design-time.db")   // never opened — the CLI only reads the model to generate/apply migrations
            .Options);
}
