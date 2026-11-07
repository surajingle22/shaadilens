using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ShaadiLens.Infrastructure.Persistence;

/// <summary>
/// Used only by `dotnet ef migrations add …`. Lets the EF tools build the model without starting
/// the whole API (which would try to connect Hangfire to Postgres).
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time", o => o.UseVector())
            .Options;
        return new AppDbContext(options);
    }
}
