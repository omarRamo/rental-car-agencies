# ADR-0012 — Un seul DbContext derrière le port `IAppDbContext`, frontières de modules vérifiées

- **Statut** : Acceptée
- **Date** : 2026-10-20
- **Source** : [ADR-0001](0001-monolithe-modulaire-dotnet.md), [prompt](../PROMPT.md) section 5

## Contexte

Le monolithe modulaire ([ADR-0001](0001-monolithe-modulaire-dotnet.md)) partage une base Azure SQL. Les cas d'usage (couche Application) doivent lire et écrire sans dépendre de l'infrastructure, et un module ne doit jamais utiliser les entités d'un autre module.

## Décision

- **Un seul `CarRentalDbContext`** (`IdentityDbContext<ApplicationUser, ApplicationRole, Guid>`) dans `CarRental.Infrastructure/Persistence`, **un schéma SQL par module** (classe `Schemas` : `identity`, `audit`, `infra`, `referential`, `fleet`, `pricing`, `bookings`…), une configuration `IEntityTypeConfiguration<T>` par entité dans `Persistence/Configurations/<Module>/`, appliquées par `ApplyConfigurationsFromAssembly`.
- **Conventions communes** (`Persistence/Conventions`) : `decimal(18,3)` ; `Money` (TND) et `AcrissCode` convertis ; `rowversion` sur chaque agrégat ; identifiants générés par l'application (`Guid.CreateVersion7()`) ; filtre de requête nommé `SoftDelete` sur `ISoftDeletable` (contournable par `IgnoreQueryFilters(["SoftDelete"])`) ; aide `ToTemporalTable(nom, schéma)` pour les tables temporelles ([ADR-0003](0003-donnees-azure-sql-tables-temporelles.md)).
- **Port `IAppDbContext`** (`Set<TEntity>()`, `SaveChangesAsync`) et **`IUnitOfWork`** ([ADR-0017](0017-execution-cas-usage-portee-transactions.md)) déclarés dans `CarRental.Application/Common/Persistence`. L'Application référence le seul paquet `Microsoft.EntityFrameworkCore` (LINQ asynchrone, `DbSet`), jamais le fournisseur SQL Server. Pas de dépôt générique : les services interrogent `Set<T>()` et projettent en DTO.
- **Frontières vérifiées** par `CarRental.Architecture.Tests`, avec des règles génériques sur la liste des modules :
  - un module du domaine (`CarRental.Domain.<Module>`) ne dépend que du noyau `CarRental.Domain.Common` ;
  - un module applicatif n'utilise un autre module qu'à travers `CarRental.Application.<Autre>.Contracts` (identifiants, DTO, `I<Autre>Queries`), et ces contrats n'exposent aucun type du domaine ;
  - le noyau commun (`Domain.Common`, `Application.Common`) ne dépend d'aucun module ;
  - couches : le domaine ne référence que la bibliothèque de base de .NET ; l'Application ne dépend ni de l'Infrastructure ni du Web ; l'Infrastructure ne dépend pas du Web ;
  - chaque module a `Application/<Module>/<Module>Module.cs` (méthode `Add<Module>Module()`, appelée par `AddApplication()`) et un espace de noms `Contracts`.
- **Références entre modules par identifiant** (`Guid`), sans propriété de navigation. Une clé étrangère entre modules reste permise en base pour l'intégrité, configurée sans navigation (`HasOne<T>().WithMany().HasForeignKey(...)`).
- Le module **Referential** (agences, lieux, paramètres) est un module comme les autres ; le journal d'audit relève du module **Identity**.

## Conséquences

- Une seule migration par évolution ; transactions locales entre modules sans coordination.
- EF Core est visible dans la couche Application (`IQueryable`, `Include`) : accepté, c'est le prix de requêtes simples et efficaces.
- Les règles couvrent un nouveau module dès qu'il contient des types ; un contrôle positif vérifie que la détection des dépendances fonctionne (une règle qui ne trouverait rien passerait à tort).

## Alternatives écartées

- **Un DbContext par module** : migrations multipliées et transactions entre modules plus complexes, sans bénéfice pour une seule base.
- **Dépôts par agrégat et spécifications** : cérémonie sans gain avec EF Core.
- **Un assembly par module** : coût de build et de structure disproportionné pour 30 véhicules ; les tests d'architecture suffisent.
