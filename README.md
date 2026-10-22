# Rental Car Agencies

Plateforme de location de voitures vendue en service (SaaS) à des agences tunisiennes d'une trentaine de véhicules. Pour chaque agence, elle centralise toutes les locations (site, téléphone, comptoir, WhatsApp et Booking.com) dans une seule application, avec une traçabilité complète et un hébergement Azure à coût minimal. Une agence ne voit jamais ce que fait une autre ; l'exploitant de la plateforme (le support) crée chaque agence, choisit ses modules et lui laisse la main.

> **Statut : lot 0 (socle technique) terminé.** Solution .NET 10, comptes avec double authentification obligatoire, rôles et permissions, journal d'audit, outbox, santé, localisation français et anglais, mises en page publique et back office, données de démonstration, intégration continue (GitHub Actions) et infrastructure Azure (Bicep). Le déploiement de la recette attend l'abonnement Azure ([points ouverts](docs/points-ouverts.md), écart n° 7). Prochaine étape : lot 1 (exploitation).

## Ce que couvre la plateforme

- **Vitrine** mobile d'abord : recherche, prix total TTC immédiat, réservation en trois étapes sans compte, espace « Ma réservation » par lien magique.
- **Back office** : demandes web à valider, dossiers, planning de la flotte, tarifs et saisons, départs et retours avec état des lieux numérique, paiements, cautions, factures, caisse, dépenses, maintenance, rapports.
- **Intégrations** : paiement Clic to Pay, bot WhatsApp avec IA, connexion Booking.com, facture électronique TTN, e-mail.
- **Plateforme multi-agences** : le support crée les agences et l'adresse de leur vitrine, active ou désactive leurs modules (vitrine, WhatsApp, canaux, caisse, sinistres et amendes, pré-comptabilité, rapports), suspend une agence, invite son gérant et entre dans une agence pour l'aider ; chaque gérant gère son parc, ses tarifs, ses utilisateurs et ses paramètres dans un back office commun, limité à son agence.

## Décisions clés

| Sujet | Décision |
|---|---|
| Architecture | Monolithe modulaire .NET 10 (LTS), monorepo, une seule application web |
| Multi-agences | Une base partagée, cloisonnement par agence appliqué par la persistance (filtre de requête et garde d'écriture), numérotation par agence, rôle Support réservé à l'exploitant, vitrine à l'adresse de l'agence, modules activables |
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
| [Journée type de haute saison](docs/scenarios/journee-haute-saison.md) | Scénario de bout en bout qui clôt la phase 1 : ce qu'il joue, les invariants qu'il prouve, comment le lancer, ce qu'il ne couvre pas |
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

### État d'avancement (22 octobre 2026)

| Lot | Livré | En cours ou à venir |
|---|---|---|
| 0 — Socle | CI (format, build, tests, intégration sur conteneurs, E2E, couverture, secrets, Bicep, Lighthouse), Bicep, identité avec double authentification, permissions, audit, santé, Data Protection, localisation FR/EN, données de démonstration | Déploiement Azure (non lancé : attend les accès de l'agence) |
| 1 — Exploitation | Flotte et échéances, moteur de prix (test de référence vert), disponibilité sans surréservation, clients et pièces, dossiers, planning, création rapide, paiements, cautions et caisse, départs et retours (état des lieux, signatures, contrat PDF, frais de retour, clôture), scénario « journée type de haute saison » en intégration et dans le navigateur ([`docs/scenarios/journee-haute-saison.md`](docs/scenarios/journee-haute-saison.md)) | — |
| 2 — Vitrine et paiement | Vitrine (pages, recherche, liens profonds, SEO, Turnstile, limitation de débit), passerelle Clic to Pay isolée avec simulateur, tunnel en 3 étapes (devis figés, pièces, garantie en ligne), « Ma réservation » (lien et code, pièces, solde, annulation, contrat), API publique des devis | Recette Clic to Pay avec la banque |
| 3 — Finance et pilotage | Instantané quotidien, indicateurs, exports Excel ; factures et avoirs (numérotation continue, TVA par taux, timbre, PDF, envoi), facture électronique en régime papier transitoire (port TTN prêt), amendes et fiche de désignation, sinistres ; dépenses et justificatifs, rapprochements (caisse, Clic to Pay, Booking.com), plan de comptes, journaux VT/AC/BQ/CA/OD équilibrés, exports CSV et Excel, clôture mensuelle (mois simulé : 29 écritures, débit = crédit) | Schéma TEIF, barème du timbre, plan de comptes et format d'export de l'expert-comptable, formats des relevés SMT et Booking.com |
| 4 — WhatsApp IA | Webhook signé, conversations, fenêtre de 24 h, boîte partagée avec reprise humaine, notifications par modèles approuvés, agent IA (Claude Haiku 5.5 : exécuteur hors outbox, 5 outils, garde-fous dans le code, cache du prompt, coûts et plafonds, base de connaissances validée, 30 conversations d'évaluation sur modèle scripté) | Évaluation contre le vrai modèle (clé de l'agence), ouverture à des numéros pilotes |
| 5 — Canaux | Module Canaux : correspondances, quotas et arrêts de vente dans le moteur, adaptateur manuel, point d'entrée authentifié, synchronisation et rejeu, commissions, banc de contrat | Connecteur Booking.com (attend le cahier des charges du fournisseur) |
| Multi-agences | Agences cloisonnées dans une base partagée (filtre EF Core, garde d'écriture, index uniques et numéros par agence) pour tous les modules, rôle Support et écrans de la plateforme (`/admin/platform` : agences, adresses de vitrine, modules, suspension, invitation des gérants, comptes support, paramètres et journal de la plateforme), entrée du support dans une agence, vitrine par agence, migrations avec rattrapage des données existantes, deux agences de démonstration ([ADR-0067](docs/adr/0067-plateforme-multi-agences-cloisonnement-support-modules.md)) | Partenaires propres à chaque agence (Clic to Pay, WhatsApp, Booking.com, TTN, e-mail, domaine) : [points ouverts](docs/points-ouverts.md) |
| Durcissement | En-têtes et CSP, limitation de débit des pages de compte et des sondes, sessions de 12 h, alertes Azure Monitor (santé publiée par l'application), sauvegarde annuelle gardée 10 ans, factures immuables, restriction de l'origine à Cloudflare (à activer), revue OWASP en deux passes ([`docs/securite/`](docs/securite/revue-owasp-2026-10.md)), [`docs/runbook.md`](docs/runbook.md) | Activation en production : domaine derrière Cloudflare et `restrictToCloudflare`, destinataires des alertes, clés Turnstile, secrets dans le Key Vault (runbook, « Avant la mise en production ») |


## Structure du dépôt

```text
/src
  CarRental.Web             hôte ASP.NET Core : vitrine de chaque agence (rendu statique,
                            /{agence}/fr et /{agence}/en ; page de la plateforme à /),
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
dotnet run --project src/CarRental.Web     # https://localhost:7180 (vitrine : /agence-demo-tunis/fr)
```

En développement, l'application applique les migrations et charge les données de démonstration au démarrage : deux agences, « Agence Démo Tunis » (trois lieux : agence, aéroports TUN et NBE ; comptes `gerant@demo.local`, `agent@demo.local` et `comptable@demo.local`) et « Agence Démo Sousse » (deux lieux, sa flotte, ses tarifs, quelques clients et dossiers ; compte `gerant.sousse@demo.local`), les paramètres par défaut et le compte de l'exploitant, `support@demo.local`, qui ouvre la plateforme (`/admin/platform`). WhatsApp et les canaux de vente sont fermés pour les agences de démonstration comme pour toute nouvelle agence : le support les ouvre depuis la fiche de l'agence. Leur mot de passe est dans `src/CarRental.Web/appsettings.Development.json`, valeur réservée au développement. En développement, la double authentification est désactivée (`Identity:TwoFactor:Required=false`, refusé hors de l'environnement Development) ; partout ailleurs, tests compris, elle reste obligatoire. Les e-mails sont écrits en fichiers `.eml` dans `src/CarRental.Web/App_Data/mail`.

**Sans Docker, au plus près de la production** : avec un SQL Server local (Express suffit), les scripts de [`deploy/local`](deploy/local/README.md) publient le build Release, créent et migrent la base par le bundle de migrations, puis lancent l'application et Azurite en arrière-plan (`install.ps1`, `start.ps1`, `stop.ps1`).

## Tests

```bash
dotnet format --verify-no-changes
dotnet build -warnaserror
dotnet test                                # Docker requis pour l'intégration et le bout en bout
```

Les tests d'intégration et de bout en bout démarrent eux-mêmes SQL Server et Azurite (Testcontainers) ainsi que l'application ; les tests de bout en bout installent Chromium au premier lancement.

Sans Docker, ils peuvent utiliser des serveurs déjà démarrés : SQL Server 2019 ou plus récent (LocalDB suffit) et Azurite. Les bases reçoivent un suffixe propre à l'exécution et sont supprimées à la fin.

```bash
npx azurite --inMemoryPersistence --skipApiVersionCheck --silent &   # Azurite sur 10000-10002
export CARRENTAL_TEST_SQL='Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true'
export CARRENTAL_TEST_STORAGE='UseDevelopmentStorage=true'
dotnet test
```
