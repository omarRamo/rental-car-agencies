# Rental Car Agencies

Plateforme de location de voitures pour une agence tunisienne de 30 véhicules. Elle centralise toutes les locations (site, téléphone, comptoir, WhatsApp et Booking.com) dans une seule application, avec une traçabilité complète et un hébergement Azure à coût minimal.

> **Statut : phase de spécification.** Le dépôt ne contient pas encore de code. Le développement commencera avec le lot 0 (socle technique).

## Ce que couvre la plateforme

- **Vitrine** mobile d'abord : recherche, prix total TTC immédiat, réservation en trois étapes sans compte, espace « Ma réservation » par lien magique.
- **Back office** : demandes web à valider, dossiers, planning de la flotte, tarifs et saisons, départs et retours avec état des lieux numérique, paiements, cautions, factures, caisse, dépenses, maintenance, rapports.
- **Intégrations** : paiement Clic to Pay, bot WhatsApp avec IA, connexion Booking.com, facture électronique TTN, e-mail.

## Décisions clés

| Sujet | Décision |
|---|---|
| Architecture | Monolithe modulaire .NET 10 (LTS), monorepo, une seule application web |
| Front | Blazor : vitrine en rendu serveur, back office interactif avec MudBlazor |
| Données | Azure SQL avec tables temporelles, Blob Storage privé |
| Hébergement | App Service B1 Linux en France Central, Cloudflare Free devant, environ 20 à 35 USD par mois |
| Vente | Par catégorie ACRISS, véhicule affecté ensuite |
| Paiement | Clic to Pay : pré-autorisation à la demande, capture à la validation |
| Booking.com | Candidature fournisseur, puis module Canaux en XML/API |
| WhatsApp | Cloud API directe, Claude Haiku 5.5 avec outils, jamais de réservation directe |
| Conformité | Facture électronique TTN, contribution de 2 TND par jour, autorisation INPDP |

Le détail et la justification de chaque décision sont dans les [ADR](docs/adr/README.md).

## Documentation

| Document | Contenu |
|---|---|
| [Spécifications](docs/specifications.md) | Spécification fonctionnelle et technique complète (v1 du 20/10/2026) |
| [Spécifications (PDF d'origine)](docs/specifications-2026-10-20.pdf) | Version de référence figée |
| [Décisions d'architecture](docs/adr/README.md) | Les ADR, une par décision structurante |
| [Points ouverts](docs/points-ouverts.md) | Démarches de l'agence, décisions à obtenir, hypothèses à confirmer, incohérences à trancher |

## Feuille de route

Six lots sur environ 20 semaines. Les démarches de l'agence partent toutes en semaine 1 : un retard décale le point de passage qu'elles débloquent, pas le développement.

| Semaines | Lot | Point de passage |
|---|---|---|
| S1–S2 | Lot 0 — Socle technique : dépôt, CI/CD, Azure, comptes, audit, référentiels | — |
| S3–S7 | Lot 1 — Exploitation : flotte, tarifs, dossiers, planning, départs et retours | Mise en service interne |
| S8–S11 | Lot 2 — Vitrine et paiement : site, tunnel, Clic to Pay, e-mails, Ma réservation | Ouverture au public |
| S12–S14 | Lot 3 — Finance et pilotage : factures TTN, caisse, dépenses, exports, indicateurs | Factures électroniques |
| S15–S17 | Lot 4 — WhatsApp IA : bot, boîte partagée, modèles Meta, jeu de tests | Bot WhatsApp en ligne |
| S18–S20 | Lot 5 — Booking.com : module Canaux, connecteur XML/API, certification | Vente sur Booking.com |

## Structure prévue du dépôt

```text
/src
  CarRental.Web             hôte ASP.NET Core : vitrine (SSR), back office (/admin),
                            API d'intégration, webhooks, tâches de fond
  CarRental.Application     cas d'usage par module : commandes, requêtes, règles
  CarRental.Domain          agrégats, objets valeur (Money en TND, Period), invariants
  CarRental.Infrastructure  EF Core / SQL, Blob, e-mail, Clic to Pay, WhatsApp,
                            Claude, TTN, Booking.com
/tests                      unitaires, intégration (SQL réel en conteneur),
                            bout en bout (Playwright)
/infra                      Bicep
/docs                       spécifications, ADR, points ouverts
/.github/workflows          intégration et déploiement continus
```

Seul `/docs` existe pour l'instant.

## Démarrage

```bash
git clone https://github.com/omarRamo/rental-car-agencies.git
cd rental-car-agencies
```
