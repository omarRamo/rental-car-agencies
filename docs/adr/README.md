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
