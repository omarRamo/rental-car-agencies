using Microsoft.EntityFrameworkCore.Migrations;

namespace CarRental.Infrastructure.Persistence.Migrations.Backfill;

/// <summary>
/// Data steps of the multi-agency migrations (ADR-0067), kept apart from the generated code so that a regenerated
/// migration calls them again at the same place: after its new columns, before its indexes and foreign keys. Literal SQL
/// on purpose (a migration must not change when application code does).
/// </summary>
/// <remarks>
/// The <b>default agency</b> of an existing database is its first active agency by name (the agency the public site
/// showed until then): the rows that had no agency join it.
/// </remarks>
internal static class MultiAgencyBackfill
{
    /// <summary>Declares <c>@default</c>, the agency that receives the rows without agency (NULL in an empty database).</summary>
    public const string DeclareDefaultAgency = """
        DECLARE @default uniqueidentifier = (
            SELECT TOP (1) [Id] FROM [referential].[Agencies]
            WHERE [IsDeleted] = 0
            ORDER BY CASE WHEN [IsActive] = 1 THEN 0 ELSE 1 END, [Name], [Id]);
        """;

    /// <summary>
    /// The public site address of each existing agency (from its name: accents removed, lower case, hyphens, unique,
    /// never a word of the platform) and its default modules; every existing account joins the default agency; the audit
    /// records (append-only: their trigger is paused for this step only) and the document counters too, each agency
    /// continuing the series it shared until then.
    /// </summary>
    public static void Foundation(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql(DeclareDefaultAgency + """

            -- Public site address and modules (PublicSite, Cash, ClaimsAndFines, Accounting, Reports = 121) of each agency.
            DECLARE @id uniqueidentifier, @name nvarchar(120), @plain varchar(200), @slug varchar(60), @stem varchar(60), @character char(1), @position int, @pendingHyphen bit, @suffix int;
            DECLARE agencies CURSOR LOCAL FAST_FORWARD FOR SELECT [Id], [Name] FROM [referential].[Agencies] ORDER BY [Name], [Id];
            OPEN agencies;
            FETCH NEXT FROM agencies INTO @id, @name;
            WHILE @@FETCH_STATUS = 0
            BEGIN
                -- The Greek code page has no accented Latin letter: the conversion keeps the letter without its accent.
                SET @plain = LOWER(CAST(@name COLLATE SQL_Latin1_General_CP1253_CI_AI AS varchar(200)));
                SET @slug = '';
                SET @pendingHyphen = 0;
                SET @position = 1;
                WHILE @position <= LEN(@plain) AND LEN(@slug) < 60
                BEGIN
                    SET @character = SUBSTRING(@plain, @position, 1);
                    IF @character LIKE '[a-z0-9]' COLLATE Latin1_General_BIN
                    BEGIN
                        IF @pendingHyphen = 1 AND LEN(@slug) > 0 AND LEN(@slug) < 59
                            SET @slug = @slug + '-';
                        SET @slug = LEFT(@slug + @character, 60);
                        SET @pendingHyphen = 0;
                    END
                    ELSE
                        SET @pendingHyphen = 1;
                    SET @position = @position + 1;
                END

                WHILE RIGHT(@slug, 1) = '-'
                    SET @slug = LEFT(@slug, LEN(@slug) - 1);
                IF LEN(@slug) < 3 OR @slug IN ('admin', 'account', 'accounts', 'api', 'app', 'assets', 'auth', 'blazor', 'cdn',
                    'connect', 'content', 'contrat', 'culture', 'dev', 'docs', 'documents', 'error', 'errors', 'files', 'fonts',
                    'framework', 'hangfire', 'health', 'help', 'home', 'images', 'img', 'jobs', 'js', 'lib', 'login', 'logout',
                    'mail', 'media', 'not-found', 'payments', 'platform', 'plateforme', 'public', 'robots', 'signin', 'signout',
                    'sitemap', 'static', 'status', 'support', 'webhooks', 'well-known', 'www')
                    SET @slug = LEFT('agence-' + @slug, 60);
                WHILE RIGHT(@slug, 1) = '-'
                    SET @slug = LEFT(@slug, LEN(@slug) - 1);

                SET @stem = @slug;
                SET @suffix = 2;
                WHILE EXISTS (SELECT 1 FROM [referential].[Agencies] WHERE [Slug] = @slug)
                BEGIN
                    SET @slug = LEFT(@stem, 60 - LEN('-' + CAST(@suffix AS varchar(10)))) + '-' + CAST(@suffix AS varchar(10));
                    SET @suffix = @suffix + 1;
                END

                UPDATE [referential].[Agencies] SET [Slug] = @slug, [Modules] = 121 WHERE [Id] = @id;
                FETCH NEXT FROM agencies INTO @id, @name;
            END
            CLOSE agencies;
            DEALLOCATE agencies;

            IF @default IS NOT NULL
            BEGIN
                -- The accounts created until now are the staff of the single agency.
                UPDATE [identity].[Users] SET [AgencyId] = @default WHERE [AgencyId] IS NULL;

                -- The history of the single agency stays in its journal.
                DISABLE TRIGGER [audit].[TR_AuditLogs_AppendOnly] ON [audit].[AuditLogs];
                UPDATE [audit].[AuditLogs] SET [AgencyId] = @default WHERE [AgencyId] IS NULL;
                ENABLE TRIGGER [audit].[TR_AuditLogs_AppendOnly] ON [audit].[AuditLogs];
                DISABLE TRIGGER [audit].[TR_DocumentAccessLogs_AppendOnly] ON [audit].[DocumentAccessLogs];
                UPDATE [audit].[DocumentAccessLogs] SET [AgencyId] = @default WHERE [AgencyId] IS NULL;
                ENABLE TRIGGER [audit].[TR_DocumentAccessLogs_AppendOnly] ON [audit].[DocumentAccessLogs];

                -- Every agency continues the series it shared: no number is issued twice in an agency.
                INSERT INTO [infra].[DocumentCounters] ([AgencyId], [DocumentType], [Year], [LastValue])
                SELECT [a].[Id], [c].[DocumentType], [c].[Year], [c].[LastValue]
                FROM [infra].[DocumentCounters] AS [c]
                CROSS JOIN [referential].[Agencies] AS [a]
                WHERE [c].[AgencyId] = '00000000-0000-0000-0000-000000000000';
            END

            DELETE FROM [infra].[DocumentCounters] WHERE [AgencyId] = '00000000-0000-0000-0000-000000000000';
            """);
    }
}
