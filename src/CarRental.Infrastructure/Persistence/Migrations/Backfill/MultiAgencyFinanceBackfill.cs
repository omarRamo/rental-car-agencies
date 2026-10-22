using Microsoft.EntityFrameworkCore.Migrations;

namespace CarRental.Infrastructure.Persistence.Migrations.Backfill;

/// <summary>
/// Data step of the migration <c>MultiAgencyFinance</c> (ADR-0067): the agency of the rows of Payments, Billing, Cash and
/// Accounting that had none. Called after the new columns, before the indexes and foreign keys; literal SQL on purpose (a
/// migration must not change when application code does). Works on an empty database and on one with data.
/// </summary>
internal static class MultiAgencyFinanceBackfill
{
    public static void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql(MultiAgencyBackfill.DeclareDefaultAgency + """

            DECLARE @none uniqueidentifier = '00000000-0000-0000-0000-000000000000';

            -- The children take the agency of their root.
            UPDATE [l] SET [l].[AgencyId] = [i].[AgencyId]
            FROM [billing].[InvoiceLines] AS [l] JOIN [billing].[Invoices] AS [i] ON [i].[Id] = [l].[InvoiceId];
            UPDATE [l] SET [l].[AgencyId] = [c].[AgencyId]
            FROM [billing].[CreditNoteLines] AS [l] JOIN [billing].[CreditNotes] AS [c] ON [c].[Id] = [l].[CreditNoteId];
            UPDATE [k] SET [k].[AgencyId] = [c].[AgencyId]
            FROM [billing].[ClaimCosts] AS [k] JOIN [billing].[Claims] AS [c] ON [c].[Id] = [k].[ClaimId];
            UPDATE [r] SET [r].[AgencyId] = [c].[AgencyId]
            FROM [billing].[ClaimReimbursements] AS [r] JOIN [billing].[Claims] AS [c] ON [c].[Id] = [r].[ClaimId];
            UPDATE [d] SET [d].[AgencyId] = [c].[AgencyId]
            FROM [billing].[ClaimDocuments] AS [d] JOIN [billing].[Claims] AS [c] ON [c].[Id] = [d].[ClaimId];
            UPDATE [m] SET [m].[AgencyId] = [s].[AgencyId]
            FROM [cash].[CashMovements] AS [m] JOIN [cash].[CashSessions] AS [s] ON [s].[Id] = [m].[SessionId];
            UPDATE [l] SET [l].[AgencyId] = [s].[AgencyId]
            FROM [accounting].[StatementLines] AS [l] JOIN [accounting].[Statements] AS [s] ON [s].[Id] = [l].[StatementId];

            -- The frozen journals: the agency of their closing (an entry without closing is never stored; one would join the
            -- default agency), then their lines.
            UPDATE [e] SET [e].[AgencyId] = [c].[AgencyId]
            FROM [accounting].[JournalEntries] AS [e] JOIN [accounting].[Closings] AS [c] ON [c].[Id] = [e].[ClosingId];
            IF @default IS NOT NULL
                UPDATE [accounting].[JournalEntries] SET [AgencyId] = @default WHERE [AgencyId] = @none;
            UPDATE [l] SET [l].[AgencyId] = [e].[AgencyId]
            FROM [accounting].[JournalLines] AS [l] JOIN [accounting].[JournalEntries] AS [e] ON [e].[Id] = [l].[EntryId];

            -- The calls to the gateway: the agency of the payment of their order; the others stay records of the platform.
            UPDATE [g] SET [g].[AgencyId] = [p].[AgencyId]
            FROM [payments].[GatewayCallLogs] AS [g] JOIN [payments].[Payments] AS [p] ON [p].[Id] = [g].[PaymentId];

            -- The chart of accounts and the mapping become the agencies' own: the platform's go to the default agency and every
            -- other agency gets a copy, so that each one keeps posting to the accounts it posted to until now. Without any
            -- agency, there is nobody to post: the chart of each agency is provisioned when it is registered.
            IF @default IS NULL
            BEGIN
                DELETE FROM [accounting].[Mappings];
                DELETE FROM [accounting].[Accounts];
            END
            ELSE
            BEGIN
                UPDATE [accounting].[Accounts] SET [AgencyId] = @default WHERE [AgencyId] = @none;
                UPDATE [accounting].[Mappings] SET [AgencyId] = @default WHERE [AgencyId] = @none;

                INSERT INTO [accounting].[Accounts] ([Id], [AgencyId], [Number], [Label], [IsActive], [IsDefault], [NeedsValidation], [CreatedAtUtc], [ChangedAtUtc], [ChangedBy])
                SELECT NEWID(), [g].[Id], [a].[Number], [a].[Label], [a].[IsActive], [a].[IsDefault], [a].[NeedsValidation], [a].[CreatedAtUtc], [a].[ChangedAtUtc], [a].[ChangedBy]
                FROM [accounting].[Accounts] AS [a]
                CROSS JOIN [referential].[Agencies] AS [g]
                WHERE [a].[AgencyId] = @default AND [g].[Id] <> @default AND [g].[IsDeleted] = 0;

                INSERT INTO [accounting].[Mappings] ([Id], [AgencyId], [Key], [AccountNumber], [ChangedAtUtc], [ChangedBy])
                SELECT NEWID(), [g].[Id], [m].[Key], [m].[AccountNumber], [m].[ChangedAtUtc], [m].[ChangedBy]
                FROM [accounting].[Mappings] AS [m]
                CROSS JOIN [referential].[Agencies] AS [g]
                WHERE [m].[AgencyId] = @default AND [g].[Id] <> @default AND [g].[IsDeleted] = 0;
            END
            """);
    }
}
