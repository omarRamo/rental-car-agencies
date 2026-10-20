# ADR-0017 — Exécution d'un cas d'usage : portée par opération, contexte utilisateur, transactions résilientes

- **Statut** : Acceptée
- **Date** : 2026-10-20
- **Source** : [ADR-0006](0006-anti-surreservation-verrou-par-categorie.md) (verrou), contrat d'infrastructure (Azure SQL, nouvelles tentatives)

## Contexte

Un cas d'usage s'exécute dans trois contextes : requête HTTP (pages statiques, endpoints), circuit Blazor Server (`/admin`) et tâche de fond (outbox, tâches récurrentes, données initiales). Trois difficultés :

- dans un circuit, la portée DI dure autant que le circuit (des heures) : un `DbContext` qui y vivrait accumulerait des entités suivies et pourrait être utilisé par deux événements en même temps ;
- l'audit et l'autorisation ont besoin de l'acteur (utilisateur, IP, corrélation) quel que soit le contexte ;
- Azure SQL coupe parfois des connexions : EF Core est configuré avec `EnableRetryOnFailure`, et cette stratégie refuse une transaction ouverte par l'application hors de `Database.CreateExecutionStrategy().ExecuteAsync(...)`.

## Décision

- **Contexte utilisateur** : `IUserContext` (identifiant, nom, IP, corrélation, `HasPermission`, `ToAuditActor`) implémenté par `ScopedUserContext`, initialisé une fois par portée :
  - requête HTTP : fournisseur ambiant à partir des revendications, de l'IP du client (`CF-Connecting-IP` derrière Cloudflare) et de l'identifiant de trace ;
  - circuit Blazor : `UserContextCircuitHandler` à l'ouverture du circuit ;
  - tâches de fond : acteur système `UserContextSnapshot.System("system:<nom>")` (`system:outbox`, `system:seed`…), qui a toutes les permissions.
- **Une portée DI par opération dans les composants interactifs** : un composant de `/admin` n'injecte pas les services applicatifs ; il passe par `IOperationRunner.RunAsync<TService, TResult>((service, ct) => service.MethodAsync(..., ct))`, qui crée une portée neuve (donc un `DbContext` neuf), y copie l'utilisateur du circuit avec un nouvel identifiant de corrélation, et transmet l'adresse de base du site (liens des e-mails). Les pages statiques, qui vivent le temps d'une requête, injectent directement les services.
- **Transactions par `IUnitOfWork.ExecuteAsync(operation, isolation)`** :
  - sans transaction en cours : exécution dans la stratégie de nouvelles tentatives ; ouverture de la transaction ; `Result` en échec → annulation et suivi des modifications vidé ; succès → `SaveChanges` puis validation ;
  - sur une panne passagère, **toute l'opération est rejouée** sur un suivi des modifications vide : l'opération doit donc charger ses données à l'intérieur du délégué et ne produire d'effet externe que par l'outbox ;
  - appel imbriqué : il rejoint la transaction en cours ; seule l'unité de travail la plus externe valide ou annule.
- **Verrou de disponibilité et numérotation** exigent une transaction ouverte (`InvalidOperationException` sinon) et ne s'utilisent donc que dans `ExecuteAsync` : `IAvailabilityLock.AcquireAsync(categoryId)` (`sp_getapplock` exclusif sur `availability:{categoryId}`, détenu par la transaction, 15 secondes par défaut, erreurs `Timeout` et `Deadlock` en `Result`) et `IDocumentNumberGenerator` ([ADR-0016](0016-numerotation-continue-table-de-compteurs.md)).

## Conséquences

- Pas de `DbContext` de longue durée dans les circuits ; chaque opération est isolée, auditée avec le bon acteur et corrélée dans les traces.
- Tests d'intégration : verrou exigeant une transaction, deux transactions sérialisées sur une même catégorie, catégories indépendantes, délai dépassé, transaction imbriquée, et nouvelle tentative après une panne simulée qui rejoue l'opération sans doublon ni trou de numérotation.
- Un composant interactif qui injecterait directement un service utilisant le `DbContext` serait un bug : à surveiller en revue (pas encore de règle automatique).

## Alternatives écartées

- **`IDbContextFactory` dans chaque service** : dispersion de la gestion de portée et de l'acteur dans tout le code applicatif.
- **Désactiver les nouvelles tentatives** : les coupures passagères d'Azure SQL deviendraient des erreurs visibles.
- **`TransactionScope`** : moins bien pris en charge par la stratégie de nouvelles tentatives et par le code asynchrone.
