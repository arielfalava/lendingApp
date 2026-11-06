using System.Text.Json;
using LendingApp.Api.Cqrs;
using LendingApp.Api.Domain;
using LendingApp.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LendingApp.Api.Features.History;

public record HistoryEntryDto(
    long Id, string EntityType, Guid EntityId, Guid? LoanId, Guid? BorrowerId,
    AuditAction Action, string Summary, List<FieldChange> Changes, DateTime Timestamp);

public record PagedResult<T>(List<T> Items, int Total, int Page, int PageSize);

/// <summary>
/// Read-only query over the audit log. Filter by entity type/action, or scope to one loan
/// (loan + its payments) or one borrower (borrower + their loans + payments).
/// </summary>
public record GetHistoryQuery(
    string? EntityType, AuditAction? Action, Guid? EntityId, Guid? LoanId, Guid? BorrowerId,
    int Page = 1, int PageSize = 25) : IQuery<PagedResult<HistoryEntryDto>>;

public class GetHistoryHandler(LendingDbContext db) : IQueryHandler<GetHistoryQuery, PagedResult<HistoryEntryDto>>
{
    public async Task<PagedResult<HistoryEntryDto>> Handle(GetHistoryQuery q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 200);

        var query = db.AuditEntries.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q.EntityType)) query = query.Where(a => a.EntityType == q.EntityType);
        if (q.Action is { } action) query = query.Where(a => a.Action == action);
        if (q.EntityId is { } entityId) query = query.Where(a => a.EntityId == entityId);
        if (q.LoanId is { } loanId) query = query.Where(a => a.LoanId == loanId);
        if (q.BorrowerId is { } borrowerId) query = query.Where(a => a.BorrowerId == borrowerId);

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(a => a.Timestamp).ThenByDescending(a => a.Id)
            .Skip((page - 1) * size).Take(size)
            .ToListAsync(ct);

        var items = rows.Select(a => new HistoryEntryDto(
            a.Id, a.EntityType, a.EntityId, a.LoanId, a.BorrowerId, a.Action, a.Summary,
            JsonSerializer.Deserialize<List<FieldChange>>(a.Changes, JsonSerializerOptions.Web) ?? [],
            a.Timestamp)).ToList();

        return new PagedResult<HistoryEntryDto>(items, total, page, size);
    }
}

public static class HistoryEndpoints
{
    public static void MapHistory(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/history", (string? entityType, AuditAction? action, Guid? entityId, Guid? loanId, Guid? borrowerId,
                int? page, int? pageSize, IDispatcher d, CancellationToken ct)
            => d.Query(new GetHistoryQuery(entityType, action, entityId, loanId, borrowerId, page ?? 1, pageSize ?? 25), ct))
            .WithTags("History");
    }
}
