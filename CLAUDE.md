# CarRental — règles du projet

## Commandes
- Outils locaux (dotnet-ef) : `dotnet tool restore`
- Dépendances locales (SQL Server 2022 sur `localhost,14333`, Azurite) : `docker compose up -d`
- Build : `dotnet build -warnaserror`
- Tests : `dotnet test` (les cinq projets ; Docker requis pour l'intégration et l'E2E)
- Format : `dotnet format` (la CI vérifie `dotnet format --verify-no-changes`)
- Lancer : `dotnet run --project src/CarRental.Web` (https://localhost:7180 ; migrations et données de démonstration appliquées au démarrage en développement)
- Migration : `dotnet ef migrations add <Name> -p src/CarRental.Infrastructure -s src/CarRental.Web -o Persistence/Migrations`
- Fusion des `.resx` : pilote `tools/git/merge-resx.mjs` (fusion à trois voies des entrées par clé), à déclarer une fois par clone : `git config merge.resx.driver "node tools/git/merge-resx.mjs %O %A %B"`.
- Avant chaque commit : `dotnet format`, `dotnet build -warnaserror` et `dotnet test` verts ; commits Conventional Commits en anglais.

## Architecture
- Monolithe modulaire .NET 10 ; modules dans Application/<Module> ; aucune référence directe entre entités de modules.
- Vitrine en rendu statique ; /admin en InteractiveServer avec MudBlazor.
- Événements de domaine → outbox → Hangfire. L'outbox est un mécanisme d'infrastructure : son administration (`/admin/outbox`, service `OutboxAdministrationService`), sa purge et son entrée de santé vivent dans `Infrastructure/Outbox`, seule exception à la recette des cas d'usage (ADR-0028).
- Couches : `Domain` (aucune dépendance) ← `Application` (cas d'usage, ports, EF Core sans fournisseur) ← `Infrastructure` (EF Core SQL Server, Identity, Hangfire, Blob, ACS) ← `Web` (hôte, composants, composition). Vérifié par `tests/CarRental.Architecture.Tests`.
- Modules : Referential, Identity, Fleet, Pricing, Bookings, Customers, Payments, Billing, Cash, Channels, Messaging, Reporting. Pour chacun :
  - `src/CarRental.Domain/<Module>/` : agrégats, objets valeur, événements, `<Module>Errors` ;
  - `src/CarRental.Application/<Module>/<Module>Module.cs` : `Add<Module>Module()`, appelé par `AddApplication()` ;
  - `src/CarRental.Application/<Module>/Contracts/` : seule surface visible des autres modules (`I<Module>Queries`, DTO, identifiants) ;
  - `src/CarRental.Infrastructure/Persistence/Configurations/<Module>/` : configurations EF ; schéma SQL `<module>` (`Schemas`).
- Noyau commun : `Domain/Common` (`Result`, `Error`, `Entity`, `AggregateRoot`, `Money`, `Period`, `AcrissCode`, `TunisTime`, `DocumentNumber`) et `Application/Common` (`IAppDbContext`, `IUnitOfWork`, `IAvailabilityLock`, `IDocumentNumberGenerator`, `IOutbox`, `IUserContext`, `IAuditTrail`, `IDocumentAccessLog`, `IFileStorage`, `IEmailQueue`, `IDataSeeder`, `IRecurringJob`).
- Décisions : `docs/adr` (0011 à 0020 pour le socle ; 0028 exploitation de l'outbox et requêtes fractionnées, 0029 paramètres, permissions par utilisateur et accessibilité du back office). Infrastructure Azure : `infra/README.md`.

## Invariants (ne jamais casser)
- Pas de chevauchement d'affectation véhicule (tampon inclus) ; capacité par catégorie jamais dépassée.
- Écriture de disponibilité uniquement sous sp_getapplock('availability:{categoryId}').
- Devis figé, factures immuables (correction par avoir), paiement confirmé par getOrderStatusExtended uniquement.
- Money en decimal(18,3) TND, arrondi AwayFromZero ; temps via TimeProvider, UTC en base, Africa/Tunis pour les règles.
- Pièces d'identité : accès autorisé et journalisé uniquement.
- Le bot ne crée jamais de dossier et ne cite que des prix venant des outils.
- Journal d'audit en ajout seul : jamais de mise à jour ni de suppression (intercepteur et déclencheurs SQL).
- Numéros `LOC`, `FAC`, `AV` sans trou : `IDocumentNumberGenerator` dans la transaction, jamais de séquence SQL.

## Conventions
- Code, commits et commentaires en anglais ; textes UI en .resx (fr, en ; ar prévu).
- Result/Result<T> pour les erreurs métier ; pas d'exception de contrôle de flux.
- Tests d'abord pour le domaine ; test de référence du prix toujours vert.
- Formats partenaires non confirmés : interface + faux + TODO(spec), jamais d'invention.
- Codes d'erreur stables `module.objet.raison` ; texte affiché par la ressource `Error.<code>` (fr et en).
- Identifiants `Guid.CreateVersion7()` (`Entity.NewId()`), propriétés à setter privé, fabriques statiques qui renvoient `Result<T>`.
- Avertissements traités comme des erreurs ; un `NoWarn` ciblé seulement s'il est justifié dans `Directory.Build.props` ou `.editorconfig`.
- Versions des paquets uniquement dans `Directory.Packages.props` ; pas de dépendance payante ou à licence ambiguë.
- Aucun secret dans le dépôt : valeurs de développement marquées comme telles avec un commentaire `gitleaks:allow` ; secrets Azure dans le Key Vault (`Section--Clé`), identité managée partout, jamais de clé de compte (liens temporaires = SAS de délégation d'utilisateur).
- Migrations rétrocompatibles : elles s'appliquent (bundle) avant le déploiement du nouveau code ; ajouter puis supprimer en deux temps.

## Recettes
- **Entité ou agrégat** : classe `sealed` dans `Domain/<Module>` héritant d'`AggregateRoot` (ou `Entity`), constantes de longueur, invariants vérifiés dans les méthodes, événements `sealed record … : IDomainEvent` levés par `Raise(...)`, `ISoftDeletable` si suppression logique. Configuration `internal sealed class XConfiguration : IEntityTypeConfiguration<X>` dans `Persistence/Configurations/<Module>/` : `builder.ToTemporalTable("Xs", Schemas.<Module>)` pour un historique (sinon `ToTable`), longueurs, index ; `.IsAuditSensitive()` sur une donnée sensible. Puis migration et tests (domaine, intégration).
- **Cas d'usage** : service `sealed` dans `Application/<Module>`, enregistré dans `<Module>Module.cs` ; vérifie `IUserContext.HasPermission(...)` ; écrit dans `IUnitOfWork.ExecuteAsync(...)` dès qu'il y a verrou, numéro ou plusieurs agrégats ; l'opération est rejouée en entier après une panne passagère : charger les données dans le délégué.
- **Effet de bord** (e-mail, WhatsApp, canal) : jamais direct ; `IOutboxHandler<TEvent>` idempotent, déclaré par `services.AddOutboxHandler<TEvent, THandler>()` ; e-mail par `IEmailQueue`.
- **Tâche récurrente** : `IRecurringJob` (`static JobId`, `static CronExpression`, heure de Tunis), déclarée par `services.AddRecurringJob<T>()`.
- **Données initiales** : `IDataSeeder` idempotent, `Stage` `Reference` (tous les environnements) ou `Demo` (seulement si `Seed:Demo`), `Order` : 100 identité, 200 référentiel, 300 utilisateurs de démonstration, 400 flotte, 500 tarifs, 600 clients, 700 dossiers, 800 finance ; `services.AddDataSeeder<T>()`.
- **Paramètre métier** : définition par une fabrique de `SettingDefinitions` (`Duration`, `Number`, `Amount`, `Boolean`, `WholeNumber`, `Text`, `TimeRange` ; ses bornes règlent l'éditeur de l'écran) dans `WellKnownSettings` ou dans les `Contracts` du module (`services.AddSettingDefinitions(...)` dans `Add<Module>Module()`), `.PlatformOnly()` si aucune agence ne doit la remplacer ; lecture par `ISettingsReader` (valeur de l'agence, sinon de la plateforme, sinon par défaut) ; libellé `Setting.<clé>.Label`, aide `Setting.<clé>.Help` et titre `Group.<premier segment>` dans `Resources/Admin/Settings/SettingLabels(.en).resx` (test d'architecture ; sans libellé, l'écran affiche la clé) ; modifiable dans `/admin/settings` (validation par la définition, audit, cache des disponibilités vidé à chaque changement).
- **Permission** : constante et entrée du catalogue dans `Permissions` (et ce qu'elle inclut : gérer inclut consulter), préréglages dans `RolePresets` (fermés par inclusion : test), matrice dans `RolePresetTests`, libellé `Permission.<nom>` dans `Resources/Admin/Identity/PermissionResources(.en).resx` (test) ; un gérant confie des permissions supplémentaires à un utilisateur dans `/admin/users` (revendications de l'utilisateur, auditées, appliquées dès sa requête suivante sans nouvelle connexion).
- **Écran du back office** : `Components/Admin/Pages/<Module>/XPage.razor`, `@page "/admin/..."` et `@attribute [HasPermission(Permissions.X)]` (mise en page et double authentification viennent de `_Imports.razor`) ; remplace l'écran « À venir » de `Admin/Pages/Placeholders` ; entrée de menu dans `AdminNavigation` avec la même permission (test) ; titre `<MudText Typo="Typo.h4" HtmlTag="h1">` ; appels aux services par `IOperationRunner.RunAsync<TService, TResult>(...)`, jamais par injection directe (portée du circuit ; aucun type EF ni de persistance dans un composant : test) ; textes dans `Resources/Admin/...resx` (mêmes clés en fr et en).
- **Accessibilité du back office** (WCAG 2.2 AA, axe sans violation critique ou sérieuse) : la palette `AdminTheme` tient 4,5:1 partout, donc `Color.Success/Warning/Error/Info/Tertiary` s'utilisent sans contournement sur boutons, puces et `MudAlert` (le bloc `Notice` des clients reste possible) ; tableau paginé : `<MudTable RowsPerPage="25">` + `<PagerContent><AdminTablePager /></PagerContent>` (liste des tailles nommée ; `MudTablePager` seul seulement avec `HideRowsPerPage="true"` : test) ; chaque champ a un `Label` (un `MudSelect` nomme sa liste par son `Label`), chaque bouton icône un `aria-label` ; le bouton de titre vide d'un `MudDatePicker` sans valeur est masqué par `admin.css` ; les textes propres à MudBlazor (pagination, calendriers, dialogues…) viennent de `Resources/Admin/MudBlazor` via `MudBlazorLocalizer` ; référence vivante et test : `/admin/ui-gallery` (absente en production) ; test E2E d'un écran : `BackOfficeSession.SignInAsync`, `WaitUntilInteractiveAsync` (attribut `data-interactive` du `main`) puis `AssertNoSeriousAxeViolationAsync`.
- **Page publique** : `Components/Public/Pages`, `@page "/{culture:length(2)}/..."`, rendu statique sans mode interactif, `PageTitle`, description et `hreflang` ; CSS écrite à la main dans `wwwroot/css/site.css` (variables, propriétés logiques, mobile d'abord), aucun MudBlazor ; textes dans `Resources/Public`.
- **Fichiers** : `IFileStorage` et `FileContainers` (conteneurs privés) ; pièce d'identité toujours avec `IDocumentAccessLog`.

## Environnement local
- `docker compose up -d` : SQL Server (`localhost,14333`, compte `sa`, mot de passe de développement dans `docker-compose.yml`) et Azurite. `appsettings.Development.json` pointe dessus.
- Utilisateurs de démonstration (développement, recette et tests seulement) : `gerant@demo.local` (gérant), `agent@demo.local` (agent), `comptable@demo.local` (comptable). Mot de passe et clé TOTP commune : `Seed:DemoUserPassword` et `Seed:DemoAuthenticatorKey` dans `src/CarRental.Web/appsettings.Development.json` ; saisir la clé à la main dans une application d'authentification (TOTP, 6 chiffres, 30 s).
- Adresses : `/fr` et `/en` (vitrine), `/account/login`, `/admin` (tableau du jour), `/admin/requests` (demandes web), `/admin/bookings` (dossiers, création rapide `/admin/bookings/new`), `/admin/planning`, `/admin/fleet` (flotte, catégories, échéances), `/admin/pricing` (plans tarifaires, simulateur `/admin/pricing/simulator`), `/admin/customers`, `/admin/payments`, `/admin/cash`, `/admin/reports` (indicateurs et exports Excel), `/admin/audit`, `/admin/users`, `/admin/settings`, `/admin/outbox` (messages de fond), `/admin/jobs` (Hangfire), `/admin/ui-gallery` (référence d'accessibilité, hors production), `/media/categories/{id}` (photo publique d'une catégorie), `/health` et `/health/ready` (entrée `outbox` « Degraded » si un message est en échec ou attend trop).
- E-mails de développement : fichiers `.eml` dans `src/CarRental.Web/App_Data/mail`.
- Clés Data Protection de développement : `%LOCALAPPDATA%/CarRental/DataProtection-Keys`.

## Tests
- `tests/CarRental.Domain.Tests` (objets valeur, agrégats, propriétés FsCheck), `tests/CarRental.Application.Tests` (services avec `Fakes/`), `tests/CarRental.Architecture.Tests` (couches, modules, conventions), `tests/CarRental.Integration.Tests` et `tests/CarRental.E2E.Tests`.
- Intégration et E2E : un fixture d'assembly démarre SQL Server et Azurite (Testcontainers) et l'application réelle (`tests/Shared/CarRentalAppFactory.cs`, environnement `Testing`). Une classe de test reçoit `IntegrationHost` (ou hérite de `BrowserTest`) par son constructeur ; `host.CreateScope(acteur)` pour appeler un service, `host.CreateClient()` et `Support/HttpSignIn` pour HTTP, `Totp.Compute(...)` pour le code. Tests en parallèle : données uniques par test — un compte par test (`Support/TestStaff.CreateAsync` en intégration, `Support/BackOfficeSession.NewStaffAsync` en E2E), une agence par test pour ses valeurs de paramètres, et les paramètres propres aux tests (`tests/Shared/TestSettings`, clés `tests.*`) pour changer une valeur de la plateforme.
- E2E : Chromium installé par le fixture ; traces et captures des échecs dans `E2E_ARTIFACTS_DIR` (sinon `bin/.../e2e-artifacts`) ; axe sans violation critique ou sérieuse (`BackOfficeSession.AssertNoSeriousAxeViolationAsync`, après `WaitForOpenPopoverAsync` ou `WaitForOpenDialogAsync` : axe mesurerait le contraste d'un texte en cours d'apparition) ; 390 px de large par défaut.
- Filtrer : `dotnet test tests/CarRental.Integration.Tests --filter "FullyQualifiedName~ConcurrencyTests"`.

## Configuration (clés)
| Clé | Rôle |
|---|---|
| `ConnectionStrings:Sql` | Azure SQL (`Authentication=Active Directory Default` sur Azure) |
| `Storage:BlobServiceUri` ou `Storage:ConnectionString` | Blob (identité managée) ou Azurite |
| `DataProtection:BlobUri`, `DataProtection:KeyVaultKeyUri` | Trousseau de clés en blob, protégé par la clé Key Vault sans version |
| `KeyVault:Uri` | Fournisseur de configuration Key Vault (secrets `Section--Clé`) |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | OpenTelemetry vers Azure Monitor |
| `Email:AcsEndpoint`, `Email:SenderAddress` | Azure Communication Services (sinon fichiers `.eml`) |
| `Seed:Demo`, `Seed:DemoUserPassword`, `Seed:DemoAuthenticatorKey` | Données de démonstration (refusées en production) |
| `Identity:InitialManagerEmail` | Premier gérant créé au démarrage s'il n'y en a aucun |
| `HostFiltering:AllowedHosts`, `HostFiltering:CustomDomains` | Hôtes acceptés (`*.azurewebsites.net` et domaines de l'agence) |
| `PublicSite:BaseUrl`, `PublicSite:AgencyName`, `PublicSite:WhatsAppNumber` | Liens absolus des e-mails, nom affiché, bouton WhatsApp |
| `Database:ApplyMigrationsOnStartup` | Migrations au démarrage (développement et tests ; sur Azure : bundle) |
| `Localization:EnableArabic` | Active l'arabe (préparé, désactivé) |
| `PublicSite:AgencyId`, `PublicSite:AllowIndexing` | Agence affichée sur le site public (par défaut la première créée) ; indexation par les moteurs (vrai en production seulement) |
| `Turnstile:SiteKey`, `Turnstile:SecretKey` (`Turnstile--SecretKey` dans le Key Vault) | Cloudflare Turnstile, vérifié côté serveur en échec fermé ; clés de test officielles en développement et en test |
| `RateLimiting:{Search,Submit,Upload}:{PermitLimit,Window,SegmentsPerWindow}` | Politiques `public-search`, `public-submit`, `public-upload` (par adresse client, `CF-Connecting-IP`) |
| `ReverseProxy:TrustCloudflareHeader` | Lit l'adresse client dans `CF-Connecting-IP` (à n'activer que si l'origine n'accepte que Cloudflare) |
| `ClicToPay:{Enabled,UseFake,BaseUrl,UserName,Password,AmountMultiplier,…}` (`ClicToPay--Password` dans le Key Vault) | Passerelle Clic to Pay ; passerelle simulée (`/dev/clictopay`) en développement et en test seulement ; recette : `docs/recette-clictopay.md` |
| `WhatsApp:{Mode,GraphBaseUrl,ApiVersion,PhoneNumberId,BusinessAccountId,Timeout}` (`WhatsApp--AccessToken`, `WhatsApp--AppSecret`, `WhatsApp--VerifyToken` dans le Key Vault) | WhatsApp Cloud API : `Auto` = faux (`FakeWhatsApp`) en développement et en test, désactivé ailleurs ; `Graph` en production ; webhook `/webhooks/whatsapp` (signature `X-Hub-Signature-256`), boîte partagée `/admin/whatsapp` |
