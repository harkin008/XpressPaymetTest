namespace XpressTest.Shared.Models;

/// <summary>Properties common to every persisted model. Timestamps are UTC.</summary>
public interface IBaseEntity
{
    /// <summary>Unique across the whole application; see <see cref="IdGenerator"/>.</summary>
    long Id { get; set; }

    DateTime CreatedAt { get; set; }
    DateTime UpdatedAt { get; set; }
    bool IsDeleted { get; set; }
}

/// <summary>
/// Base model for entities. The id is assigned from <see cref="IdGenerator"/> when the entity is
/// added to a DbContext (see <c>UseApplicationIds</c>). Entities that must inherit another base class,
/// such as <c>IdentityUser&lt;long&gt;</c>, implement <see cref="IBaseEntity"/> instead.
/// </summary>
public abstract class BaseEntity : IBaseEntity
{
    public long Id { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
}
