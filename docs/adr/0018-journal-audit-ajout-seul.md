# ADR-0018 — Journal d'audit applicatif en ajout seul, complété par les tables temporelles

- **Statut** : Acceptée
- **Date** : 2026-10-20
- **Source** : [spécifications](../SPEC.md) (traçabilité, pièces d'identité), [prompt](../PROMPT.md) section 12, [ADR-0003](0003-donnees-azure-sql-tables-temporelles.md)

## Contexte

Chaque action doit pouvoir être attribuée (qui, quand, d'où, quoi avant et après), et chaque consultation d'une pièce d'identité doit être journalisée (invariant n° 9). Les tables temporelles ([ADR-0003](0003-donnees-azure-sql-tables-temporelles.md)) gardent l'historique des lignes, mais pas l'auteur, l'adresse IP ni les actions sans modification de données (connexion, consultation).

## Décision

- **`audit.AuditLogs`** : une ligne par modification d'entité, écrite par l'intercepteur `SaveChangesPipelineInterceptor` **dans le même `SaveChanges`** que la modification (atomique). Action `entity.created`, `entity.updated`, `entity.soft_deleted` ou `entity.deleted` ; type d'entité (nom d'audit configurable) et identifiant ; valeurs avant et après en JSON (seulement les propriétés modifiées pour une mise à jour) ; acteur (`IUserContext.ToAuditActor()` : utilisateur, nom, IP, corrélation).
- **Événements sans entité** (connexion, double authentification, verrouillage, invitation…) par `IAuditTrail.RecordAsync(new AuditEvent(action) { ... })`, avec des actions stables listées dans `AuditActions`.
- **Données sensibles** : une propriété marquée `IsAuditSensitive()` est masquée (`***`) ; `IsAuditIgnored()` l'exclut (horodatages techniques, jetons de concurrence) ; `ExcludeFromAudit()` exclut une table technique (outbox, compteurs, journaux eux-mêmes). Hachage du mot de passe, tampon de sécurité et jetons d'Identity sont masqués.
- **Ajout seul, à deux niveaux** : l'intercepteur refuse toute modification ou suppression d'une ligne de `AuditLogs` ou de `DocumentAccessLogs` (`InvalidOperationException`) ; en base, des déclencheurs `INSTEAD OF UPDATE, DELETE` lèvent une erreur, même pour un accès SQL direct.
- **`audit.DocumentAccessLogs`** : chaque accès à une pièce d'identité (consultation, téléchargement, lien temporaire) est enregistré par `IDocumentAccessLog` avec l'acteur et le motif ; les modules Clients et Réservations l'utiliseront dès la phase 1.
- **Suppression logique** : une entité `ISoftDeletable` supprimée est marquée supprimée (date, acteur dans l'audit) et masquée par le filtre de requête ; elle reste dans l'historique.
- **Consultation** : `/admin/audit` (permission `audit.read`, gérant), pagination côté serveur, filtres par période, utilisateur, action (préfixe avec `*`, par exemple `identity.*`) et entité, détail avant et après.

## Conséquences

- Aucune modification non tracée par l'application ; le journal ne peut pas être réécrit sans désactiver les déclencheurs (action DDL elle-même visible).
- Le volume croît sans limite : la durée de conservation et l'archivage seront fixés avec l'expert-comptable et le dossier INPDP (`TODO(spec)`).
- Tests d'intégration : ligne d'audit à la création avec acteur et valeurs, seules les valeurs modifiées à la mise à jour, suppression logique, refus par EF Core et par la base, masquage des secrets, événements explicites.

## Alternatives écartées

- **Tables temporelles seules** : ni auteur, ni IP, ni actions sans modification.
- **Audit SQL Server** : journal technique difficile à présenter, à filtrer et à relier aux utilisateurs applicatifs.
- **Journaux applicatifs (Application Insights)** : plafonnés, conservés 30 jours, non probants.
