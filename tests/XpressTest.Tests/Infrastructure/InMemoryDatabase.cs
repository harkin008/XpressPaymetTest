using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace XpressTest.Tests.Infrastructure;

public static class InMemoryDatabase
{
    /// <summary>Overrides the service's Database:UseInMemoryDb / Database:UseSqlServer switch, so tests never touch SQL Server.</summary>
    public static IWebHostBuilder UseInMemoryDatabaseMode(this IWebHostBuilder builder) => builder
        .UseSetting("Database:UseInMemoryDb", "true")
        .UseSetting("Database:UseSqlServer", "false");

    /// <summary>
    /// Gives the test host its own isolated in-memory database (the service's default in-memory
    /// database is shared by every host in the process).
    /// </summary>
    public static void UseInMemoryDatabase<TContext>(this IServiceCollection services, string name) where TContext : DbContext
    {
        services.RemoveAll<IDbContextOptionsConfiguration<TContext>>();
        services.ConfigureDbContext<TContext>(o => o.UseInMemoryDatabase(name));
    }
}
