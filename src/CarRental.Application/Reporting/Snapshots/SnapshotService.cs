using System.Globalization;
using CarRental.Application.Common.Auditing;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Reporting;

namespace CarRental.Application.Reporting.Snapshots;

/// <summary>
/// Recomputation of the daily snapshots of a period from the reports screen (ADR-0045): permission <c>reports.read</c>
/// (the managers and accountants of SPEC §3, "Rapports : gérer"), past days only (up to yesterday, Tunis time), one year at
/// most; the stored days are replaced and the recomputation is written to the audit log.
/// </summary>
public sealed class SnapshotService
{
    private readonly IUserContext _user;
    private readonly IReferentialQueries _referential;
    private readonly SnapshotWriter _writer;
    private readonly IAuditTrail _auditTrail;
    private readonly TimeProvider _clock;

    internal SnapshotService(IUserContext user, IReferentialQueries referential, SnapshotWriter writer, IAuditTrail auditTrail, TimeProvider clock)
    {
        _user = user;
        _referential = referential;
        _writer = writer;
        _auditTrail = auditTrail;
        _clock = clock;
    }

    public async Task<Result<SnapshotRun>> RecomputeAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (!_user.HasPermission(Permissions.ReportsRead))
        {
            return ReportingErrors.Forbidden;
        }

        var period = ReportPeriod.Create(from, to);
        if (period.IsFailure)
        {
            return period.Error;
        }

        if (to >= TunisTime.Today(_clock))
        {
            return ReportingErrors.SnapshotFutureDay;
        }

        if (!(await _referential.GetAgenciesAsync(cancellationToken)).Any(a => a.Id == agencyId))
        {
            return ReportingErrors.AgencyNotFound;
        }

        var run = await _writer.RecomputeAsync(agencyId, from, to, cancellationToken);
        if (run.IsFailure)
        {
            return run.Error;
        }

        await _auditTrail.RecordAsync(
            new AuditEvent(ReportingAuditActions.SnapshotsRecomputed)
            {
                EntityType = nameof(VehicleDaySnapshot),
                EntityId = agencyId.ToString(),
                Details = string.Create(CultureInfo.InvariantCulture, $"{from:yyyy-MM-dd}..{to:yyyy-MM-dd}: {run.Value.Days} day(s), {run.Value.Rows} vehicle-day(s)"),
            },
            cancellationToken);
        return run;
    }
}
