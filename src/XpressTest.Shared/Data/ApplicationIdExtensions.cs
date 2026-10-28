using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.ValueGeneration;
using XpressTest.Shared.Models;

namespace XpressTest.Shared.Data;

/// <summary>Assigns <see cref="IdGenerator"/> ids when an entity is added, so the id is known before SaveChanges.</summary>
public sealed class ApplicationIdValueGenerator : ValueGenerator<long>
{
    public override bool GeneratesTemporaryValues => false;

    public override long Next(EntityEntry entry) => IdGenerator.NewId();
}

public static class ApplicationIdExtensions
{
    /// <summary>
    /// Gives every entity whose primary key is a single <c>long</c> (BaseEntity models and the Identity
    /// user and role) an application-wide unique id. Call at the end of OnModelCreating.
    /// </summary>
    public static ModelBuilder UseApplicationIds(this ModelBuilder builder)
    {
        foreach (var entity in builder.Model.GetEntityTypes())
        {
            if (entity.FindPrimaryKey()?.Properties is [{ ClrType: var type } key] && type == typeof(long))
            {
                var property = builder.Entity(entity.ClrType).Property(key.Name)
                    .HasValueGenerator<ApplicationIdValueGenerator>()
                    .ValueGeneratedOnAdd();
                // The application supplies the id, so the column must not be an IDENTITY column.
                property.Metadata.SetValueGenerationStrategy(SqlServerValueGenerationStrategy.None);
            }
        }
        return builder;
    }
}
