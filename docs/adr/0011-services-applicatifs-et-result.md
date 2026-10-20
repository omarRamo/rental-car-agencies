# ADR-0011 — Cas d'usage en services applicatifs explicites, erreurs métier par `Result`

- **Statut** : Acceptée
- **Date** : 2026-10-20
- **Source** : [prompt de développement](../PROMPT.md), section 5

## Contexte

Le prompt demande des cas d'usage par module (commandes, requêtes, validateurs) et des erreurs métier portées par `Result` / `Result<T>` avec des codes stables, les exceptions étant réservées aux bugs et aux pannes. Il reste à choisir comment un composant Blazor, un endpoint ou une tâche de fond appelle un cas d'usage, et comment l'autorisation, la transaction et l'audit s'y appliquent.

## Décision

- **Un cas d'usage = une méthode publique d'un service applicatif** (classe `sealed` dans `CarRental.Application.<Module>`, enregistrée par `Add<Module>Module()`), appelée directement. Pas de médiateur ni de comportements de pipeline : chaque préoccupation transverse est visible dans le service :
  - autorisation : `IUserContext.HasPermission(...)`, sinon `Error.Forbidden`, en plus de l'attribut de la page (défense en profondeur) ;
  - transaction : `IUnitOfWork.ExecuteAsync(...)` dès que l'opération écrit plusieurs agrégats, prend le verrou de disponibilité ou un numéro de pièce ([ADR-0016](0016-numerotation-continue-table-de-compteurs.md), [ADR-0017](0017-execution-cas-usage-portee-transactions.md)) ;
  - audit : automatique pour les modifications d'entités ([ADR-0018](0018-journal-audit-ajout-seul.md)) ; `IAuditTrail` pour les événements sans entité (connexion, verrouillage d'un compte…).
- **Requêtes** : services `…Queries` qui projettent directement en DTO avec LINQ sur `IAppDbContext` ([ADR-0012](0012-dbcontext-unique-et-frontieres-de-modules.md)) ; `PagedResult<T>` pour les listes paginées.
- **`Result` et `Error`** (`CarRental.Domain.Common`) : `Error(Code, Message, Kind)`. Le code est stable, en minuscules et pointé, de la forme `module.objet.raison` (validé à la construction) ; `Kind` vaut `Validation`, `NotFound`, `Conflict`, `Forbidden` ou `Failure` et sert à la traduction en statut HTTP ou en message. Les erreurs d'un module sont regroupées dans une classe statique `<Module>Errors`. L'interface affiche la ressource `Error.<code>` (repli : message générique), jamais le message technique.
- **Invariants dans le domaine** : les fabriques et méthodes des agrégats renvoient `Result` ; les services valident les entrées puis délèguent au domaine. Pas de FluentValidation pour l'instant.
- **Exceptions** : uniquement pour les bugs (`ArgumentNullException`, `InvalidOperationException` sur un usage incorrect, comme un verrou demandé hors transaction) et les pannes (base, stockage, réseau).

## Conséquences

- Code lisible et navigable (« aller à la définition » mène au cas d'usage), testable avec des doubles simples (`tests/CarRental.Application.Tests/Fakes`).
- Un peu de répétition (vérification de permission, appel à l'unité de travail), acceptée en échange de l'absence de magie.
- Chaque code d'erreur affiché à l'écran a besoin de ses textes `Error.<code>` en français et en anglais ; un test d'architecture le vérifie pour `IdentityErrors`, à étendre aux erreurs de chaque nouveau module.

## Alternatives écartées

- **MediatR** et comportements de pipeline : indirection inutile à cette taille, et licence commerciale depuis 2025 (règle « pas de licence ambiguë »).
- **Exceptions métier** : contrôle de flux par exceptions, coûteux et invisible dans les signatures.
- **Dépôts génériques par agrégat** : voir [ADR-0012](0012-dbcontext-unique-et-frontieres-de-modules.md).
