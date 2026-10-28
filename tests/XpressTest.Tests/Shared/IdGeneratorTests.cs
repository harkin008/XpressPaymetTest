using System.Collections.Concurrent;
using XpressTest.Shared.Models;

namespace XpressTest.Tests.Shared;

public class IdGeneratorTests
{
    [Fact]
    public void Ids_are_positive_unique_and_increasing_across_threads()
    {
        IdGenerator.Configure(7);
        var ids = new ConcurrentBag<long>();
        Parallel.For(0, 50_000, _ => ids.Add(IdGenerator.NewId()));

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.True(id > 0));

        var sequential = Enumerable.Range(0, 10_000).Select(_ => IdGenerator.NewId()).ToList();
        Assert.Equal(sequential.Order(), sequential);
    }
}
