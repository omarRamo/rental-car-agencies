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
| [0013](0013-outbox-traitee-par-hangfire.md) | Outbox transactionnelle traitée par Hangfire | Acceptée (déclenchement remplacé par ADR-0043) |
| [0014](0014-identite-2fa-permissions.md) | Identité : double authentification obligatoire, rôles en préréglages de permissions | Acceptée |
| [0015](0015-localisation-routes-par-culture.md) | Localisation par ressources .resx, routes publiques préfixées par la culture | Acceptée |
| [0016](0016-numerotation-continue-table-de-compteurs.md) | Numérotation continue des pièces par table de compteurs | Acceptée |
| [0017](0017-execution-cas-usage-portee-transactions.md) | Exécution d'un cas d'usage : portée par opération, contexte utilisateur, transactions résilientes | Acceptée |
| [0018](0018-journal-audit-ajout-seul.md) | Journal d'audit applicatif en ajout seul, complété par les tables temporelles | Acceptée |
| [0019](0019-strategie-de-tests.md) | Stratégie de tests : xUnit v3 sur VSTest, conteneurs partagés, Playwright | Acceptée |
| [0020](0020-navigation-classique-et-focus.md) | Navigation classique pour les pages statiques, focus géré dans le back office | Acceptée (précisée par ADR-0036) |
| [0021](0021-flotte-fenetres-de-disponibilite-et-echeances.md) | Flotte : fenêtres de disponibilité calculées par le domaine, écritures sous verrou et alertes d'échéance | Acceptée |
| [0022](0022-photos-de-la-flotte.md) | Photos de la flotte : stockage privé, photos de catégorie servies par l'application | Acceptée |
| [0023](0023-plans-tarifaires-versionnes.md) | Plans tarifaires versionnés : brouillon, publication, archives | Acceptée |
| [0024](0024-moteur-de-prix-pur-et-son-alimentation.md) | Moteur de prix pur, alimenté par le service de tarification | Acceptée |
| [0025](0025-moteur-de-disponibilite-cache-et-ecriture-sous-verrou.md) | Moteur de disponibilité : balayage des bornes, cache mémoire invalidé par événement, écriture sous verrou | Acceptée |
| [0026](0026-pieces-identite-stockage-et-acces-journalise.md) | Pièces d'identité : stockage privé, accès autorisé et journalisé, rétention désactivée | Acceptée |
| [0027](0027-personnes-dedoublonnage-et-anonymisation.md) | Personnes : dédoublonnage par clés normalisées, export et anonymisation réservés au gérant | Acceptée |
| [0028](0028-exploitation-outbox-et-requetes-fractionnees.md) | Exploitation du socle : outbox purgée, surveillée et relançable, requêtes fractionnées par défaut | Acceptée |
| [0029](0029-administration-parametres-permissions-et-accessibilite.md) | Administration du back office : paramètres typés, permissions par utilisateur, sessions rafraîchies, MudBlazor accessible et traduit | Acceptée |
| [0030](0030-dossier-de-location-agregat-et-chronologie.md) | Dossier de location : agrégat `Booking`, machine à états unique et chronologie métier | Acceptée |
| [0031](0031-devis-figes-versions-et-prolongation.md) | Devis figés : agrégat `Quote` immuable, versions par dossier et règle de prolongation | Acceptée |
| [0032](0032-protocole-ecriture-dossiers-et-taches.md) | Écriture des dossiers : un pipeline unique sous garde, tâches d'expiration et d'escalade, contrats du module | Acceptée |
| [0033](0033-planning-grille-et-affectation.md) | Planning : grille CSS sur mesure, affectation au clavier ou par glisser-déposer, propositions revues | Acceptée |
| [0034](0034-paiements-et-cautions.md) | Paiements et cautions : mouvements typés, caution comme dette, statut Clic to Pay venu de la banque seulement | Acceptée |
| [0035](0035-sessions-de-caisse.md) | Sessions de caisse : une par agent et par jour, espèces de recette et cautions comptées à part, verrouillage définitif | Acceptée |
| [0036](0036-vitrine-rendu-statique-cache-et-referencement.md) | Vitrine : rendu statique sans script Blazor, pages publiques cachables, référencement et budget Lighthouse | Acceptée |
| [0037](0037-vitrine-anti-abus-et-mesure-entonnoir.md) | Vitrine : limitation de débit, Turnstile en échec fermé, mesure anonyme de l'entonnoir | Acceptée |
| [0038](0038-passerelle-clic-to-pay-isolee-et-simulee.md) | Passerelle Clic to Pay : port isolé, client REST défensif, journal des appels et passerelle simulée | Acceptée |
| [0039](0039-paiement-en-ligne-confirmation-idempotence-rattrapage.md) | Paiement en ligne : confirmation serveur à serveur, idempotence par commande, rattrapage et remboursements validés | Acceptée |
| [0040](0040-outbox-messages-sans-gestionnaire.md) | Outbox : messages sans gestionnaire terminés en bloc | Acceptée |
| [0041](0041-depart-retour-etat-des-lieux-et-contrat.md) | Départ et retour : état des lieux, signatures et contrat PDF | Acceptée |
| [0042](0042-frais-de-retour-franchise-et-retard.md) | Frais de retour : proposition, plafond de franchise et tarification du retard | Acceptée |
| [0043](0043-outbox-pompe-en-processus.md) | Outbox : déclenchement immédiat par une pompe en processus | Acceptée |
| [0044](0044-caches-parametres-et-plans-publies.md) | Caches des paramètres et des plans publiés, vidés après chaque validation | Acceptée |
| [0045](0045-instantane-quotidien-des-vehicules.md) | Instantané quotidien des véhicules et répartition du chiffre d'affaires | Acceptée |
| [0046](0046-indicateurs-de-pilotage-et-sources.md) | Indicateurs de pilotage : définitions, sources et ports vers 3A, 3B et la phase 5 | Acceptée |
| [0047](0047-whatsapp-webhook-signe-traitement-asynchrone-et-fenetre-24-h.md) | Canal WhatsApp : webhook signé, traitement asynchrone par l'outbox, fenêtre de 24 h et faux | Acceptée |
| [0048](0048-boite-whatsapp-partagee-et-notifications-avec-consentement.md) | Boîte WhatsApp partagée et notifications : reprise en main, modèles approuvés, consentement, idempotence | Acceptée (noms et langues des modèles à confirmer) |
| [0049](0049-tunnel-sessions-devis-figes-garantie-de-prix.md) | Tunnel de réservation : sessions à jeton, devis figés et garantie de prix | Acceptée |
| [0050](0050-soumissions-publiques-et-donnees-personnelles.md) | Soumissions publiques des conducteurs et protection des données personnelles | Acceptée |
| [0051](0051-ma-reservation-acces-et-liaison-dossier-paiement.md) | « Ma réservation » : accès par lien et code, session limitée au dossier, liaison dossier ↔ paiement en ligne | Acceptée |
| [0052](0052-canaux-de-vente-modele-restrictions-et-adaptateurs.md) | Canaux de vente : modèle, quotas et arrêts de vente dans le moteur, format canonique et adaptateurs sans format inventé | Acceptée |
| [0053](0053-canaux-securite-entrante-idempotence-et-synchronisation.md) | Canaux : sécurité du point d'entrée, journal, idempotence par référence et synchronisation sortante | Acceptée |
| [0054](0054-executeur-des-tours-du-bot-whatsapp.md) | Exécuteur des tours du bot WhatsApp, hors de l'outbox | Acceptée |
| [0055](0055-port-du-modele-sdk-anthropic-cache-et-couts.md) | Agent WhatsApp : port du modèle, SDK Anthropic, cache du prompt et coûts | Acceptée |
| [0056](0056-garde-fous-outils-minimisation-et-evaluation.md) | Agent WhatsApp : outils, garde-fous dans le code, minimisation des données, évaluation | Acceptée |
| [0060](0060-alertes-et-telemetrie-de-sante.md) | Alertes : télémétrie de santé publiée par l'application, règles Azure Monitor à faible coût | Acceptée |
| [0061](0061-sauvegardes-retention-et-immuabilite.md) | Sauvegardes, rétention longue et factures immuables | Acceptée |
| [0062](0062-origine-cloudflare-et-revue-de-securite.md) | Origine réservée à Cloudflare et décisions de la revue de sécurité (passe 1) | Acceptée |

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
