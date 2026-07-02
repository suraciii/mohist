namespace Mohist.Server.Infrastructure.Data.StagePopulation;

/// <summary>
/// One daily stage-population snapshot for a project. Six <see cref="int"/>
/// count columns cover the full ordered stage set the cumulative flow
/// diagram presents: <c>backlog</c>, <c>plan</c>, <c>build</c>, <c>check</c>,
/// <c>integrate</c>, and <c>done</c>. The row is the persisted cache the
/// CFD reads — per-day populations are NOT recomputed from the event
/// stream on render.
/// <para>
/// <see cref="Day"/> is the UTC snapshot day formatted as
/// <c>"yyyy-MM-dd"</c>, matching every other metrics DTO's
/// <c>Boundary</c> format. <see cref="ProjectId"/> is a free-form
/// identifier (the repo-wide convention is <c>HasMaxLength(256)</c>).
/// </para>
/// <para>
/// Uniqueness on <c>(ProjectId, Day)</c> is the idempotency signal the
/// daily job relies on: re-running the snapshot for the same project +
/// day upserts the existing row's counts in place rather than creating
/// a duplicate.
/// </para>
/// </summary>
public class StagePopulationSnapshotRow
{
    public string ProjectId { get; set; } = null!;
    public string Day { get; set; } = null!;
    public int Backlog { get; set; }
    public int Plan { get; set; }
    public int Build { get; set; }
    public int Check { get; set; }
    public int Integrate { get; set; }
    public int Done { get; set; }
}
