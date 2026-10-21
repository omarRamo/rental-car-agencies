# Rental Car Agencies

Plateforme de location de voitures pour une agence tunisienne de 30 véhicules. Elle centralise toutes les locations (site, téléphone, comptoir, WhatsApp et Booking.com) dans une seule application, avec une traçabilité complète et un hébergement Azure à coût minimal.

> **Statut : lot 0 (socle technique) terminé.** Solution .NET 10, comptes avec double authentification obligatoire, rôles et permissions, journal d'audit, outbox, santé, localisation français et anglais, mises en page publique et back office, données de démonstration, intégration continue (GitHub Actions) et infrastructure Azure (Bicep). Le déploiement de la recette attend l'abonnement Azure ([points ouverts](docs/points-ouverts.md), écart n° 7). Prochaine étape : lot 1 (exploitation).

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
| [Spécifications](docs/SPEC.md) | Spécification fonctionnelle et technique complète (v1 du 20/10/2026) |
| [Spécifications (PDF d'origine)](docs/specifications-2026-10-20.pdf) | Version de référence figée |
| [Décisions d'architecture](docs/adr/README.md) | Les ADR, une par décision structurante |
| [Points ouverts](docs/points-ouverts.md) | Démarches de l'agence, décisions à obtenir, hypothèses à confirmer, incohérences à trancher |
| [Conception de la phase 1](docs/conception/phase-1-exploitation.md) | Machine à états du dossier, occupation et capacité, sémantique du moteur de prix, découpage en lots |
| [Conception de la phase 2](docs/conception/phase-2-vitrine-paiement.md) | Parcours public et liens profonds, brouillons de tunnel, flux Clic to Pay, Ma réservation, sécurité et performance du site |
| [Conception de la phase 3](docs/conception/phase-3-finance.md) | Factures et avoirs, facture électronique, amendes et sinistres, dépenses, rapprochements, pré-comptabilité, indicateurs |
| [Conception de la phase 4](docs/conception/phase-4-whatsapp.md) | WhatsApp Cloud API, agent IA (Claude Haiku 5.5) avec outils et garde-fous, boîte partagée, notifications, jeu d'évaluation |
| [Conception de la phase 5](docs/conception/phase-5-canaux.md) | Modèle de canaux, correspondances, quotas et arrêts de vente, adaptateurs (manuel, Booking.com à venir), flux entrants et sortants, banc de contrat |
| [Règles du projet](CLAUDE.md) | Commandes, architecture, invariants, conventions et recettes de développement |
| [Infrastructure Azure](infra/README.md) | Ressources, sécurité, coûts, premier déploiement, CI/CD, exploitation |

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


## Structure du dépôt

```text
/src
  CarRental.Web             hôte ASP.NET Core : vitrine (rendu statique, /fr et /en),
                            back office (/admin, interactif, MudBlazor : utilisateurs et
                            permissions, paramètres, journal d'audit, messages de fond),
                            comptes (/account), santé (/health, /health/ready),
                            tableau de bord Hangfire (/admin/jobs)
  CarRental.Application     cas d'usage par module, ports (IAppDbContext, IUnitOfWork...)
  CarRental.Domain          noyau commun (Result, Money en TND, Period, AcrissCode), agrégats
  CarRental.Infrastructure  EF Core / SQL Server, Identity, outbox et Hangfire, Blob, e-mail,
                            Data Protection, migrations
/tests                      domaine, application, architecture, intégration (SQL Server et
                            Azurite en conteneurs), bout en bout (Playwright)
/infra                      Bicep (voir infra/README.md)
/docs                       spécifications, prompt, ADR, points ouverts
/.github/workflows          intégration continue, CodeQL, déploiement
```

Les règles de travail (commandes, conventions, recettes pour ajouter une entité, un écran ou un test) sont dans [CLAUDE.md](CLAUDE.md).

## Démarrage

Prérequis : SDK .NET 10 (version fixée par `global.json`) et Docker.

```bash
git clone https://github.com/omarRamo/rental-car-agencies.git
cd rental-car-agencies
dotnet tool restore                        # dotnet-ef
docker compose up -d                       # SQL Server 2022 (localhost,14333) et Azurite
dotnet run --project src/CarRental.Web     # https://localhost:7180
```

En développement, l'application applique les migrations et charge les données de démonstration au démarrage : une agence, trois lieux (agence, aéroports TUN et NBE), les paramètres par défaut et trois comptes, `gerant@demo.local`, `agent@demo.local` et `comptable@demo.local`. Leur mot de passe et leur clé TOTP (à saisir dans une application d'authentification) sont dans `src/CarRental.Web/appsettings.Development.json`, valeurs réservées au développement. Les e-mails sont écrits en fichiers `.eml` dans `src/CarRental.Web/App_Data/mail`.

## Tests

```bash
dotnet format --verify-no-changes
dotnet build -warnaserror
dotnet test                                # Docker requis pour l'intégration et le bout en bout
```

Les tests d'intégration et de bout en bout démarrent eux-mêmes SQL Server et Azurite (Testcontainers) ainsi que l'application ; les tests de bout en bout installent Chromium au premier lancement.
