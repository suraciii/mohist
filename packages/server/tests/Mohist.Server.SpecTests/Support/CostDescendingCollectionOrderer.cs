using Xunit;
using Xunit.Abstractions;

[assembly: TestCollectionOrderer(
    "Mohist.Server.SpecTests.Support.CostDescendingCollectionOrderer",
    "Mohist.Server.SpecTests")]

namespace Mohist.Server.SpecTests.Support;

/// <summary>
/// Schedules expensive collections first (longest-processing-time-first).
/// xUnit runs collections in discovery order by default, which queues the
/// fixture-backed integration collections (silo + web host boot, long
/// serial class chains) behind dozens of cheap single-class collections;
/// their chains then dominate the tail of the run while most worker
/// threads sit idle. Starting them first lets the cheap collections fill
/// the remaining threads and shortens the makespan.
/// </summary>
public class CostDescendingCollectionOrderer : ITestCollectionOrderer
{
    private const string DefaultCollectionPrefix = "Test collection for ";

    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections) =>
        testCollections.OrderByDescending(Weight).ThenBy(c => c.DisplayName, StringComparer.Ordinal);

    private static int Weight(ITestCollection collection) =>
        collection.DisplayName.StartsWith(DefaultCollectionPrefix, StringComparison.Ordinal) ? 0 : 1;
}
