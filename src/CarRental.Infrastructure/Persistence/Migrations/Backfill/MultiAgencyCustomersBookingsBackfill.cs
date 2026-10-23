using Microsoft.EntityFrameworkCore.Migrations;

namespace CarRental.Infrastructure.Persistence.Migrations.Backfill;

/// <summary>
/// Data step of the migration <c>MultiAgencyCustomersBookings</c> (ADR-0067): the agency of every customer row and of
/// every child row of the bookings, called right after the new <c>AgencyId</c> columns are added and before their indexes
/// and foreign keys. Literal SQL on purpose (a migration must not change when application code does); it works on an
/// empty database and on a database with data, and leaves the audit tables (append-only, with triggers) alone.
/// </summary>
/// <remarks>
/// A person gets the agency of its latest booking (as a driver or through its individual payer), else of the latest
/// booking it submitted documents for, else the default agency; a company payer the agency of the latest booking it
/// pays for, else the default agency. Every other row gets the agency of its parent row, and the rows without parent the
/// default agency (<see cref="MultiAgencyBackfill.DeclareDefaultAgency"/>).
/// </remarks>
internal static class MultiAgencyCustomersBookingsBackfill
{
    public static void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql(MultiAgencyBackfill.DeclareDefaultAgency + """

            DECLARE @none uniqueidentifier = '00000000-0000-0000-0000-000000000000';

            -- Persons: the agency of their latest booking, as a driver or as the payer through their individual customer.
            UPDATE [p] SET [AgencyId] = [latest].[AgencyId]
            FROM [customers].[Persons] AS [p]
            CROSS APPLY (
                SELECT TOP (1) [b].[AgencyId]
                FROM [bookings].[Bookings] AS [b]
                WHERE [b].[MainDriverId] = [p].[Id]
                   OR [b].[SecondaryDriverId] = [p].[Id]
                   OR [b].[PayerCustomerId] IN (SELECT [c].[Id] FROM [customers].[Customers] AS [c] WHERE [c].[PersonId] = [p].[Id])
                ORDER BY [b].[CreatedAtUtc] DESC, [b].[Id] DESC) AS [latest]
            WHERE [p].[AgencyId] = @none;

            -- Else the agency of the latest booking they submitted documents for (public funnel, "Ma réservation").
            UPDATE [p] SET [AgencyId] = [latest].[AgencyId]
            FROM [customers].[Persons] AS [p]
            CROSS APPLY (
                SELECT TOP (1) [b].[AgencyId]
                FROM [customers].[DriverSubmissions] AS [s]
                JOIN [bookings].[Bookings] AS [b] ON [b].[Id] = [s].[BookingId]
                WHERE [s].[PersonId] = [p].[Id]
                ORDER BY [b].[CreatedAtUtc] DESC, [b].[Id] DESC) AS [latest]
            WHERE [p].[AgencyId] = @none;

            IF @default IS NOT NULL
                UPDATE [customers].[Persons] SET [AgencyId] = @default WHERE [AgencyId] = @none;

            -- Payers: an individual in the agency of its person, a company in the agency of the latest booking it pays.
            UPDATE [c] SET [AgencyId] = [p].[AgencyId]
            FROM [customers].[Customers] AS [c]
            JOIN [customers].[Persons] AS [p] ON [p].[Id] = [c].[PersonId]
            WHERE [c].[AgencyId] = @none;

            UPDATE [c] SET [AgencyId] = [latest].[AgencyId]
            FROM [customers].[Customers] AS [c]
            CROSS APPLY (
                SELECT TOP (1) [b].[AgencyId]
                FROM [bookings].[Bookings] AS [b]
                WHERE [b].[PayerCustomerId] = [c].[Id]
                ORDER BY [b].[CreatedAtUtc] DESC, [b].[Id] DESC) AS [latest]
            WHERE [c].[AgencyId] = @none;

            IF @default IS NOT NULL
                UPDATE [customers].[Customers] SET [AgencyId] = @default WHERE [AgencyId] = @none;

            -- The parts of a person and its public submissions: the agency of the person.
            UPDATE [d] SET [AgencyId] = [p].[AgencyId]
            FROM [customers].[IdentityDocuments] AS [d]
            JOIN [customers].[Persons] AS [p] ON [p].[Id] = [d].[PersonId]
            WHERE [d].[AgencyId] = @none;

            UPDATE [x] SET [AgencyId] = [p].[AgencyId]
            FROM [customers].[Consents] AS [x]
            JOIN [customers].[Persons] AS [p] ON [p].[Id] = [x].[PersonId]
            WHERE [x].[AgencyId] = @none;

            UPDATE [e] SET [AgencyId] = [p].[AgencyId]
            FROM [customers].[BlacklistEntries] AS [e]
            JOIN [customers].[Persons] AS [p] ON [p].[Id] = [e].[PersonId]
            WHERE [e].[AgencyId] = @none;

            UPDATE [s] SET [AgencyId] = [p].[AgencyId]
            FROM [customers].[DriverSubmissions] AS [s]
            JOIN [customers].[Persons] AS [p] ON [p].[Id] = [s].[PersonId]
            WHERE [s].[AgencyId] = @none;

            UPDATE [d] SET [AgencyId] = [s].[AgencyId]
            FROM [customers].[SubmittedDocuments] AS [d]
            JOIN [customers].[DriverSubmissions] AS [s] ON [s].[Id] = [d].[SubmissionId]
            WHERE [d].[AgencyId] = @none;

            -- The photos of the funnel and their staged images: the agency of the funnel session.
            UPDATE [u] SET [AgencyId] = [f].[AgencyId]
            FROM [bookings].[FunnelUploads] AS [u]
            JOIN [bookings].[FunnelSessions] AS [f] ON [f].[Id] = [u].[SessionId]
            WHERE [u].[AgencyId] = @none;

            UPDATE [i] SET [AgencyId] = [session].[AgencyId]
            FROM [customers].[StagedDocumentImages] AS [i]
            CROSS APPLY (
                SELECT TOP (1) [f].[AgencyId]
                FROM [bookings].[FunnelUploads] AS [u]
                JOIN [bookings].[FunnelSessions] AS [f] ON [f].[Id] = [u].[SessionId]
                WHERE [u].[StagedImageId] = [i].[Id]) AS [session]
            WHERE [i].[AgencyId] = @none;

            -- The children of the files: the agency of the file (or of its quote, of its inspection).
            UPDATE [e] SET [AgencyId] = [b].[AgencyId]
            FROM [bookings].[BookingEvents] AS [e]
            JOIN [bookings].[Bookings] AS [b] ON [b].[Id] = [e].[BookingId]
            WHERE [e].[AgencyId] = @none;

            UPDATE [l] SET [AgencyId] = [q].[AgencyId]
            FROM [bookings].[QuoteLines] AS [l]
            JOIN [bookings].[Quotes] AS [q] ON [q].[Id] = [l].[QuoteId]
            WHERE [l].[AgencyId] = @none;

            UPDATE [x] SET [AgencyId] = [i].[AgencyId]
            FROM [bookings].[InspectionPhotos] AS [x]
            JOIN [bookings].[Inspections] AS [i] ON [i].[Id] = [x].[InspectionId]
            WHERE [x].[AgencyId] = @none;

            UPDATE [x] SET [AgencyId] = [i].[AgencyId]
            FROM [bookings].[DamageMarks] AS [x]
            JOIN [bookings].[Inspections] AS [i] ON [i].[Id] = [x].[InspectionId]
            WHERE [x].[AgencyId] = @none;

            UPDATE [x] SET [AgencyId] = [i].[AgencyId]
            FROM [bookings].[InspectionSignatures] AS [x]
            JOIN [bookings].[Inspections] AS [i] ON [i].[Id] = [x].[InspectionId]
            WHERE [x].[AgencyId] = @none;

            UPDATE [x] SET [AgencyId] = [i].[AgencyId]
            FROM [bookings].[ReturnCharges] AS [x]
            JOIN [bookings].[Inspections] AS [i] ON [i].[Id] = [x].[InspectionId]
            WHERE [x].[AgencyId] = @none;

            UPDATE [g] SET [AgencyId] = [b].[AgencyId]
            FROM [bookings].[BookingAccessGrants] AS [g]
            JOIN [bookings].[Bookings] AS [b] ON [b].[Id] = [g].[BookingId]
            WHERE [g].[AgencyId] = @none;

            UPDATE [x] SET [AgencyId] = [b].[AgencyId]
            FROM [bookings].[BookingCustomerProfiles] AS [x]
            JOIN [bookings].[Bookings] AS [b] ON [b].[Id] = [x].[BookingId]
            WHERE [x].[AgencyId] = @none;

            UPDATE [x] SET [AgencyId] = [b].[AgencyId]
            FROM [bookings].[BookingPaymentTracks] AS [x]
            JOIN [bookings].[Bookings] AS [b] ON [b].[Id] = [x].[BookingId]
            WHERE [x].[AgencyId] = @none;

            -- Rows without parent (an access request that matched no booking, a profile or a staged image left by an
            -- abandoned request): the default agency.
            IF @default IS NOT NULL
            BEGIN
                UPDATE [customers].[StagedDocumentImages] SET [AgencyId] = @default WHERE [AgencyId] = @none;
                UPDATE [bookings].[BookingAccessGrants] SET [AgencyId] = @default WHERE [AgencyId] = @none;
                UPDATE [bookings].[BookingCustomerProfiles] SET [AgencyId] = @default WHERE [AgencyId] = @none;
            END
            """);
    }
}
