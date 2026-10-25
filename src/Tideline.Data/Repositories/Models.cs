using Tideline.Core;

namespace Tideline.Data.Repositories;

public enum ImportSource { Csv, Simplefin, Manual }

public sealed record Account(
    long Id,
    string Name,
    AccountKind Kind,
    string? Institution,
    string? BankNumber,
    long? OpeningBalanceCents,
    DateOnly? OpeningOn,
    bool IsInSpread,
    bool IsClosed,
    DateOnly? CsvUntilOn,
    DateOnly? SimplefinFromOn);

public sealed record NewAccount(
    string Name,
    AccountKind Kind,
    string? Institution = null,
    string? BankNumber = null,
    long? OpeningBalanceCents = null,
    DateOnly? OpeningOn = null,
    bool IsInSpread = true,
    DateOnly? CsvUntilOn = null,
    DateOnly? SimplefinFromOn = null);

public sealed record Category(long Id, string Name, LineType DefaultType, bool IsArchived);

public sealed record NewRule(
    string Name,
    int Priority,
    IReadOnlyList<(string Field, string Op, string ValueJson)> Conditions,
    long? SetCategoryId = null,
    LineType? SetType = null,
    string? SetPayee = null,
    long? SetRecurringId = null,
    long? SetPlanId = null,
    IReadOnlyList<long>? TagIds = null,
    bool IsActive = true);

/// <summary>Everything written when a bank line enters the ledger. Bank fields are never updated afterwards.</summary>
public sealed record NewTransaction(
    long AccountId,
    DateOnly PostedOn,
    long AmountCents,
    string OriginalDescription,
    string ExternalId,
    ImportSource Source,
    long? BankBalanceCents = null,
    string? CheckNumber = null,
    long? ImportBatchId = null,
    string? RawJson = null,
    string? Payee = null,
    long? CategoryId = null,
    LineType? Type = null,
    long? MoveId = null,
    long? RecurringId = null,
    long? PlanId = null,
    long? RuleId = null,
    IReadOnlyList<long>? TagIds = null);

public sealed record TransactionRow(
    long Id,
    long AccountId,
    DateOnly PostedOn,
    long AmountCents,
    string OriginalDescription,
    string ExternalId,
    string? Payee,
    long? CategoryId,
    LineType? Type,
    long? MoveId,
    long? RuleId,
    bool IsManualOverride);

/// <summary>A row that belongs to a move that has only this one leg so far.</summary>
public sealed record PendingLeg(long TransactionId, long AccountId, DateOnly PostedOn, long AmountCents, string Description, long MoveId);

public sealed record ImportBatchSummary(long Id, string Status, int RowsRead, int RowsInserted, int RowsExisting, int RowsReview, string? Error);
