# ADR-0067 — Plateforme multi-agences : cloisonnement par agence, rôle Support, vitrine par adresse et modules activables

- **Statut** : Acceptée
- **Date** : 2026-10-22
- **Source** : demande du 22/10/2026 (vendre la plateforme en SaaS : le support crée chaque agence et lui laisse la main) ; précise l'[ADR-0014](0014-identite-2fa-permissions.md) (rôles), l'[ADR-0015](0015-localisation-routes-par-culture.md) (routes publiques), l'[ADR-0016](0016-numerotation-continue-table-de-compteurs.md) (numérotation) et l'[ADR-0051](0051-ma-reservation-acces-et-liaison-dossier-paiement.md) (recherche d'un dossier)

## Contexte

La plateforme a été construite pour une agence, avec un modèle « prêt pour plusieurs » : la plupart des données portent déjà une agence, mais rien n'empêchait un utilisateur de lire celles d'une autre, la vitrine affichait une seule agence (`PublicSite:AgencyId`), les comptes n'étaient rattachés à aucune agence et la numérotation des pièces était commune.

Elle devient un service vendu à plusieurs agences de location : l'exploitant de la plateforme (le **support**) crée chaque agence, lui donne l'adresse de sa vitrine et ses modules, invite son gérant, puis lui laisse la main sur son parc, ses tarifs, ses utilisateurs et ses paramètres. **Une agence ne voit jamais ce que fait une autre.** Il y a une seule application, une seule base et une seule adresse de back office.

## Décision

### L'agence est le locataire, dans une base partagée

- Une agence (`Agency`) est une entité juridique : elle a ses propres données, ses utilisateurs, sa vitrine et ses séries de numéros. Toutes les agences partagent la même base et le même schéma ; chaque ligne d'une agence porte `AgencyId`.
- Le domaine marque ces données par trois interfaces :
  - `IAgencyOwned` (`AgencyId` obligatoire) : les données d'une agence (véhicules, dossiers, clients, factures…) et les entités enfants interrogées seules ;
  - `IAgencyShared` (`AgencyId` facultatif) : une valeur commune à la plateforme (`null`) ou propre à une agence, comme un paramètre ; une agence lit les valeurs communes et les siennes ;
  - `IAgencyOrPlatform` (`AgencyId` facultatif) : un enregistrement d'une agence ou de la plateforme elle-même, comme le journal d'audit ; une agence ne lit que les siens.
- Les enfants d'un agrégat reçoivent l'agence de leur racine à la création ; `AgencyId` ne change jamais.

### L'opération travaille pour une agence

- Le contexte utilisateur (`IUserContext`, `UserContextSnapshot`) porte l'**agence de l'opération** (`AgencyId`) et le droit de travailler pour **toutes les agences** (`AllAgencies`) :
  - un membre du personnel travaille pour son agence (`ApplicationUser.AgencyId`, réclamation `agency` de sa session) ;
  - le support (permission `platform.agencies`) travaille pour l'agence qu'il a ouverte depuis les pages de la plateforme, ou pour toutes quand il n'en a ouvert aucune ;
  - un visiteur de la vitrine travaille pour l'agence de l'adresse qu'il consulte ;
  - une tâche de fond ou un gestionnaire de l'outbox travaille pour toutes les agences (`system:…`) et donne à chaque donnée qu'il crée l'agence de la donnée qu'il traite, ou s'ouvre une portée limitée à une agence (`UserContextSnapshot.System(nom, agencyId: …)`).
- Une opération limitée à une agence sans agence (visiteur hors de toute vitrine, compte sans agence) ne voit **aucune** donnée d'agence : le refus est la valeur par défaut.

### Le cloisonnement est appliqué par la couche de persistance

- **Lecture** : chaque entité d'agence reçoit le filtre de requête nommé `Agency` (`ModelConventions.AgencyFilter`), en plus de `SoftDelete`. Le contexte EF le lit à chaque requête (`AgencyFilterEnabled`, `AgencyFilterId`) ; `Agency` elle-même est filtrée par son identifiant. Seule une requête de la plateforme, voulue et relue, lève ce filtre (`IgnoreQueryFilters([ModelConventions.AgencyFilter])`) : résolution de l'adresse d'une vitrine, connexion, pages du support.
- **Écriture** : le pipeline d'enregistrement refuse (`AgencyBoundaryException`) une ligne d'une autre agence écrite par une opération limitée à une agence, une ligne qui changerait d'agence, une donnée d'agence sans agence, une valeur commune écrite par quelqu'un d'autre que le support ou le système, et la création d'une agence hors du support.
- **Numérotation** : un compteur par agence, type et exercice (`DocumentCounters` clé `AgencyId, DocumentType, Year`) ; deux agences émettent chacune `FAC-2026-000001`. Une opération limitée à une agence ne tire que les numéros de son agence. Les numéros ne sont donc uniques que dans une agence : toute recherche par numéro se fait dans une agence (« Ma réservation » cherche dans l'agence de la vitrine, ce qui remplace la règle de l'ADR-0051).
- **Unicités** : les index uniques métier (immatriculation, code de lieu, de catégorie, de canal, numéros, clés de dédoublonnage des personnes…) deviennent uniques **par agence**. L'adresse de vitrine reste unique sur toute la plateforme.
- **Journal d'audit** : chaque ligne porte l'agence de la donnée changée (ou de l'événement) ; le gérant ne lit que le journal de son agence, le support lit tout. Les actions du support dans une agence figurent dans le journal de cette agence.
- **Caches partagés** (paramètres, plans publiés, disponibilités, offres de la vitrine) : chargés par le système pour toutes les agences et indexés par agence ; un lecteur ne demande que les clés de son agence et vérifie l'agence de ce qu'il reçoit.

### Comptes, rôles et support

- Un compte du personnel appartient à **une** agence (`ApplicationUser.AgencyId`) ; son adresse e-mail reste unique sur la plateforme. Les rôles d'agence (`Manager`, `Agent`, `Accountant`, `Preparer`) ne donnent que des permissions d'agence ; le gérant gère les comptes de son agence et ne voit aucun autre compte.
- Le rôle **`Support`** est réservé à l'exploitant : toutes les permissions, dont celles de la nouvelle zone **Plateforme** (`platform.agencies`, et `jobs.dashboard`, `outbox.manage` qui couvrent les tâches et messages de toutes les agences et ne sont plus données au gérant). Un compte support n'appartient à aucune agence. Une permission de la plateforme n'est jamais accordée à un membre d'une agence (retirée de sa session même accordée par erreur).
- Le premier compte support est créé au démarrage depuis `Identity:InitialSupportEmail` (sans mot de passe, comme l'ancien premier gérant) ; le support crée ensuite les agences et invite leur gérant.
- **Entrer dans une agence** : depuis les pages de la plateforme, le support ouvre une agence (`ApplicationUser.SelectedAgencyId`, session renouvelée) et retrouve son back office comme son gérant, avec un bandeau qui rappelle l'agence ouverte ; il la quitte pour revenir à la plateforme. Tant qu'il n'a ouvert aucune agence, les pages d'agence le renvoient vers la plateforme.
- **Agence suspendue** (`Agency.IsActive = false`) : sa vitrine répond « introuvable », son personnel ne peut plus se connecter et ses sessions ouvertes se ferment à leur prochaine validation ; ses données et ses tâches de fond continuent.

### Une vitrine par agence, à l'adresse de l'agence

- Chaque agence a une **adresse de vitrine** (`Agency.Slug`) : minuscules, chiffres et tirets simples, 3 à 60 caractères, proposée depuis le nom (`Agence Démo Tunis` → `agence-demo-tunis`), modifiable par le support, unique sur la plateforme et jamais un mot réservé par la plateforme (`admin`, `account`, `api`…).
- Les pages publiques deviennent `/{agence}/{culture}/…` (`https://rental.app/agence-1/fr`) : l'adresse de l'agence précède la culture de l'ADR-0015, qui ne change pas autrement. `/{agence}` redirige vers la meilleure culture. Une adresse inconnue, une agence suspendue ou une vitrine désactivée répondent 404.
- La racine `/` présente la plateforme sans lister les agences ; le back office (`/admin`) et les pages de compte (`/account`) restent communs à toutes les agences.
- Les liens envoyés aux clients (e-mails, paiement, « Ma réservation ») portent l'adresse de l'agence du dossier ; le plan du site liste les pages de chaque vitrine ouverte.

### Modules activables par agence

- Le support active ou désactive les **modules** d'une agence (`Agency.Modules`, `AgencyModules`) : vitrine, WhatsApp (boîte partagée, assistant, notifications WhatsApp), canaux de vente (Booking.com), caisse et dépenses, sinistres et amendes, pré-comptabilité et rapprochements, tableaux de bord et rapports.
- Le cœur du back office est toujours là : flotte, tarifs, demandes et dossiers, planning, départs et retours, clients, paiements, factures, utilisateurs et paramètres.
- Une nouvelle agence reçoit tous les modules **sauf WhatsApp et les canaux de vente**, qui demandent les comptes partenaires de l'agence et seront ouverts plus tard.
- Un module désactivé disparaît du menu, ses pages et ses points d'entrée (webhook, API) répondent 404 pour l'agence, ses tâches de fond passent l'agence ; ses données sont gardées et réapparaissent quand il est réactivé.

## Conséquences

- Toute nouvelle entité dit à qui elle appartient (`IAgencyOwned`, `IAgencyShared`, `IAgencyOrPlatform`) ou figure dans la liste des données de la plateforme d'un test d'architecture.
- Tout service qui crée une portée DI (`CreateAsyncScope`) y fixe l'acteur : sans acteur, la portée hérite de la requête HTTP en cours ou reste anonyme et ne voit rien.
- Une tâche de fond qui notifie le personnel ne s'adresse qu'aux comptes de l'agence concernée.
- Les partenaires restent configurés pour toute la plateforme (Clic to Pay, WhatsApp, TTN, e-mail) : des comptes partenaires propres à chaque agence sont un point ouvert.
- Une base existante garde ses données : la migration rattache les lignes sans agence à la première agence créée (dérivée d'une donnée liée quand elle existe), donne à chaque agence son adresse de vitrine et ses modules par défaut, et rattache les comptes existants à cette agence.

## Alternatives écartées

- **Une base par agence** : isolation maximale, mais migrations, sauvegardes, tâches de fond et coûts multipliés par le nombre d'agences, pour une plateforme qui vise des agences de 30 véhicules.
- **Un schéma par agence** : mêmes inconvénients, sans le gain de l'isolation des sauvegardes.
- **Filtrer à la main dans chaque requête** : un oubli suffit à montrer les données d'une autre agence ; le filtre global et le refus à l'écriture font du cloisonnement la règle par défaut.
- **Sécurité au niveau des lignes de SQL Server** (`SESSION_CONTEXT`) : défense en profondeur possible plus tard, mais complexe avec le pool de connexions, Hangfire et les tables temporelles ; le filtre EF Core et le garde d'écriture suffisent au lancement.
- **Un sous-domaine par agence** (`agence-1.rental.app`) : certificats et DNS par agence derrière Cloudflare ; l'adresse par chemin demandée suffit.
- **Le support voit toutes les agences mélangées** dans les pages d'agence : les formulaires de création n'auraient pas d'agence évidente ; il ouvre une agence à la fois.
