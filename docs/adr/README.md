# Décisions d'architecture (ADR)

Chaque ADR consigne une décision structurante : son contexte, ce qui a été décidé, et ce que cela implique. Les décisions initiales viennent des [spécifications v1](../SPEC.md) du 20/10/2026.

Une décision ne se modifie pas : si elle change, on écrit un nouvel ADR qui la remplace et on passe l'ancien au statut « Remplacée par ADR-XXXX ».

| N° | Décision | Statut |
|---|---|---|
| [0001](0001-monolithe-modulaire-dotnet.md) | Monolithe modulaire .NET 10 dans un monorepo | Acceptée |
| [0002](0002-front-blazor-mudblazor.md) | Front en Blazor : vitrine en rendu serveur, back office interactif avec MudBlazor | Acceptée |
| [0003](0003-donnees-azure-sql-tables-temporelles.md) | Azure SQL avec tables temporelles, Blob Storage privé | Acceptée |
| [0004](0004-hebergement-app-service-b1-cloudflare.md) | App Service B1 Linux en France Central, Cloudflare Free devant | Acceptée |
| [0005](0005-vente-par-categorie-acriss.md) | Vente par catégorie ACRISS, véhicule affecté ensuite | Acceptée |
| [0006](0006-anti-surreservation-verrou-par-categorie.md) | Anti-surréservation par verrou applicatif par catégorie | Acceptée |
| [0007](0007-paiement-clic-to-pay-pre-autorisation.md) | Clic to Pay : pré-autorisation à la demande, capture à la validation | Acceptée |
| [0008](0008-booking-com-module-canaux.md) | Booking.com via candidature fournisseur et module Canaux | Acceptée |
| [0009](0009-whatsapp-cloud-api-claude-haiku.md) | Bot WhatsApp en Cloud API directe avec Claude Haiku 5.5 et outils | Acceptée |
| [0010](0010-conformite-ttn-inpdp.md) | Conformité intégrée : facture électronique TTN, contribution journalière, INPDP | Acceptée |
| [0011](0011-services-applicatifs-et-result.md) | Cas d'usage en services applicatifs explicites, erreurs métier par `Result` | Acceptée |
| [0012](0012-dbcontext-unique-et-frontieres-de-modules.md) | Un seul DbContext derrière le port `IAppDbContext`, frontières de modules vérifiées | Acceptée |
| [0013](0013-outbox-traitee-par-hangfire.md) | Outbox transactionnelle traitée par Hangfire | Acceptée |
| [0014](0014-identite-2fa-permissions.md) | Identité : double authentification obligatoire, rôles en préréglages de permissions | Acceptée |
| [0015](0015-localisation-routes-par-culture.md) | Localisation par ressources .resx, routes publiques préfixées par la culture | Acceptée |
| [0016](0016-numerotation-continue-table-de-compteurs.md) | Numérotation continue des pièces par table de compteurs | Acceptée |
| [0017](0017-execution-cas-usage-portee-transactions.md) | Exécution d'un cas d'usage : portée par opération, contexte utilisateur, transactions résilientes | Acceptée |
| [0018](0018-journal-audit-ajout-seul.md) | Journal d'audit applicatif en ajout seul, complété par les tables temporelles | Acceptée |
| [0019](0019-strategie-de-tests.md) | Stratégie de tests : xUnit v3 sur VSTest, conteneurs partagés, Playwright | Acceptée |
| [0020](0020-navigation-classique-et-focus.md) | Navigation classique pour les pages statiques, focus géré dans le back office | Acceptée |
| [0025](0025-moteur-de-disponibilite-cache-et-ecriture-sous-verrou.md) | Moteur de disponibilité : balayage des bornes, cache mémoire invalidé par événement, écriture sous verrou | Acceptée |
| [0026](0026-pieces-identite-stockage-et-acces-journalise.md) | Pièces d'identité : stockage privé, accès autorisé et journalisé, rétention désactivée | Acceptée |
| [0027](0027-personnes-dedoublonnage-et-anonymisation.md) | Personnes : dédoublonnage par clés normalisées, export et anonymisation réservés au gérant | Acceptée |

## Modèle

```markdown
# ADR-XXXX — Titre

- **Statut** : Proposée | Acceptée | Remplacée par ADR-YYYY
- **Date** : AAAA-MM-JJ

## Contexte
## Décision
## Conséquences
## Alternatives écartées
```
