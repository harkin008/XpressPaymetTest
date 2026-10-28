using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace XpressTest.Shared.Data;

public enum DatabaseMode
{
    InMemory,
    SqlServer
}

public static class DatabaseProviderExtensions
{
    /// <summary>
    /// Reads <c>Database:UseInMemoryDb</c> and <c>Database:UseSqlServer</c>; exactly one must be true.
    /// InMemory data lives only as long as the process; SqlServer uses a connection string.
    /// </summary>
    public static DatabaseMode GetDatabaseMode(this IConfiguration configuration)
    {
        var inMemory = configuration.GetValue<bool>("Database:UseInMemoryDb");
        var sqlServer = configuration.GetValue<bool>("Database:UseSqlServer");
        return (inMemory, sqlServer) switch
        {
            (true, false) => DatabaseMode.InMemory,
            (false, true) => DatabaseMode.SqlServer,
            _ => throw new InvalidOperationException(
                "Set exactly one of Database:UseInMemoryDb and Database:UseSqlServer to true " +
                $"(currently UseInMemoryDb={inMemory}, UseSqlServer={sqlServer}).")
        };
    }

    public static string GetRequiredConnectionString(this IConfiguration configuration, string name) =>
        configuration.GetConnectionString(name)
        ?? throw new InvalidOperationException($"ConnectionStrings:{name} is not configured.");

    /// <summary>
    /// Configures the context for the mode in <see cref="GetDatabaseMode"/>. SqlServer uses
    /// <c>ConnectionStrings:{connectionStringName}</c>; InMemory uses a database of that name.
    /// </summary>
    public static DbContextOptionsBuilder UseConfiguredDatabase(
        this DbContextOptionsBuilder options, IConfiguration configuration, string connectionStringName) =>
        configuration.GetDatabaseMode() == DatabaseMode.SqlServer
            ? options.UseSqlServer(configuration.GetRequiredConnectionString(connectionStringName))
            : options.UseInMemoryDatabase(connectionStringName);

    /// <summary>
    /// Creates the connection string's database if it does not exist yet, for storage that only
    /// creates its own schema (e.g. Hangfire). EF contexts use Migrate instead.
    /// </summary>
    public static void EnsureSqlServerDatabaseExists(string connectionString)
    {
        var target = new SqlConnectionStringBuilder(connectionString);
        var database = target.InitialCatalog;
        if (string.IsNullOrEmpty(database))
            throw new InvalidOperationException("The connection string must name a database.");

        var master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" };
        using var connection = new SqlConnection(master.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            IF DB_ID(@name) IS NULL
            BEGIN
                DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@name);
                EXEC (@sql);
            END
            """;
        command.Parameters.AddWithValue("@name", database);
        command.ExecuteNonQuery();
    }
}
