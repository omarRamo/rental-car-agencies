# ADR-0061 — Sauvegardes, rétention longue et factures immuables

- **Statut** : Acceptée (durées de conservation à confirmer avec l'expert-comptable et l'avocat)
- **Date** : 2026-10-21
- **Source** : [prompt](../PROMPT.md) §15 « Transverse — Durcissement » (sauvegardes et rétention longue), [spécifications](../SPEC.md) §14 (sauvegardes, minimisation), [ADR-0003](0003-donnees-azure-sql-tables-temporelles.md), [ADR-0004](0004-hebergement-app-service-b1-cloudflare.md), [ADR-0026](0026-pieces-identite-stockage-et-acces-journalise.md), [points ouverts](../points-ouverts.md) (durée de conservation des contrats, états des lieux et photos)

## Contexte

La spécification prévoit la restauration à l'instant près d'Azure SQL (7 jours en Basic), une rétention longue hebdomadaire et mensuelle, des versions et une suppression réversible sur le stockage. Le socle les a mises en place, avec une restauration à un instant de 14 jours sur les blobs en production. Il manque la conservation longue des pièces comptables : en Tunisie, les livres et pièces justificatives (factures, avoirs) se conservent dix ans — durée à confirmer avec l'expert-comptable —, et une facture émise est immuable (invariant 6) : rien ne doit pouvoir l'effacer ni la remplacer, ni une erreur de l'application, ni un accès indu au stockage. La durée de conservation des contrats, des états des lieux et de leurs photos reste à fixer avec l'avocat. Tout doit rester au coût d'une petite agence ; la recette, jetable, ne garde rien de long.

## Décision

### Base de données

- Restauration à un instant de **7 jours** (maximum du Basic), sauvegardes **géo-redondantes** en production (restauration en France South), locales en recette.
- Rétention longue en production : chaque sauvegarde hebdomadaire **5 semaines**, la première de chaque mois **12 mois**, et désormais **une sauvegarde par an gardée 10 ans** (`sqlLtrYearlyRetention = 'P10Y'`), celle de la **deuxième semaine de janvier** (`sqlLtrWeekOfYear = 2` : l'année close est complète quel que soit le calendrier ISO, la première semaine pouvant commencer fin décembre). Coût : moins d'un dollar par mois au bout de dix ans pour une base de cette taille.
- Pas d'immuabilité des sauvegardes longues par le modèle : Azure SQL ne protège ces sauvegardes qu'une fois l'immuabilité **verrouillée**, ce qui est irréversible et bloque la suppression du serveur ; c'est une décision de l'agence, une fois la durée confirmée (commande dans le README de l'infrastructure, section 16).

### Stockage des fichiers

- Inchangé : versions (anciennes versions supprimées après 90 jours), suppression réversible des blobs et des conteneurs 30 jours, stockage GRS en production.
- **Conteneur `invoices` sous rétention temporelle de 3 650 jours** (`invoiceImmutabilityDays`, immuabilité au niveau du conteneur, non verrouillée) : un blob peut y être créé, mais ni écrasé ni supprimé pendant dix ans après sa création, par personne, application comprise. La politique est déployée **non verrouillée** : un administrateur peut la raccourcir ou la retirer (par exemple si l'expert-comptable fixe une autre durée) ; la verrouiller — conformité de type SEC 17a-4, irréversible — reste une décision de l'agence, jamais du code.
- L'immuabilité au niveau du conteneur plutôt que des versions : elle s'applique et se retire à tout moment sur un conteneur existant, alors que l'immuabilité par version ne se désactive plus une fois activée sur un conteneur.
- **La restauration à un instant des blobs est retirée en production** : Azure Storage la déclare incompatible avec le stockage immuable. Le module la désactive dès qu'un conteneur est immuable (`blobPointInTimeRestoreDays` est ignoré) ; les versions et la suppression réversible restent les moyens de restaurer un blob (script de restauration en lot dans le [runbook](../runbook.md#restaurations)).
- **Contrats, états des lieux, photos** : pas d'immuabilité. Le départ dépose le contrat avant sa transaction et le supprime si elle échoue (compensation, [ADR-0041](0041-depart-retour-etat-des-lieux-et-contrat.md)) : une rétention l'en empêcherait et laisserait des contrats orphelins, pleins de données personnelles, pendant des années. Une fois le départ enregistré, la plateforme ne supprime jamais un contrat ni une photo d'état des lieux ; aucune règle de cycle de vie ne supprime un blob courant. La durée de conservation est à fixer avec l'avocat, après quoi une purge du même modèle que celle des pièces d'identité pourra être ajoutée.
- **Pièces d'identité** : purge applicative inchangée, désactivée par défaut ([ADR-0026](0026-pieces-identite-stockage-et-acces-journalise.md)).
- Pas de passage en niveau froid : moins d'un gigaoctet par an de photos ; le niveau « Cool » ajouterait des frais de lecture et une durée minimale pour quelques centimes d'économie.

### Key Vault

Inchangé : suppression réversible 90 jours et protection contre la purge en production, 7 jours sans protection en recette.

### Recette

Ni rétention longue, ni immuabilité, ni restauration à un instant : la recette se supprime et se recrée.

## Conséquences

- **Contrat pour le lot 3A (facturation)** : un fichier du conteneur `invoices` s'écrit **une seule fois**, à un chemin propre au document (numéro, version), et ne se supprime jamais. Un PDF régénéré (par exemple avec la référence TTN) va à un nouveau chemin. Une écriture faite avant la transaction et compensée par une suppression échouerait (409) : écrire le fichier après la validation (gestionnaire d'outbox idempotent) ou tolérer l'orphelin. À vérifier à la fusion du lot 3A ([revue de sécurité](../securite/revue-owasp-2026-10.md)).
- Une facture déposée par erreur reste dix ans : sans conséquence comptable (la base fait foi), mais elle occupe le conteneur.
- Restaurer de nombreux blobs à un instant se fait version par version (script) au lieu d'une commande ; la base, elle, se restaure toujours à l'instant près.
- Une politique verrouillée plus tard ne se redéploie plus : passer alors `invoiceImmutabilityDays = 0` dans le fichier de paramètres (la politique verrouillée reste) ; de même, un redéploiement réécrirait la politique de rétention longue de la base sans l'immuabilité si l'agence l'active dans le portail.

## Alternatives écartées

- **Restauration à un instant des blobs plutôt que l'immuabilité** : elle annule des erreurs récentes mais ne protège pas une facture d'une suppression délibérée, ni au-delà de 14 jours.
- **Compte de stockage séparé pour les factures** (garder la restauration à un instant ailleurs) : une identité, des rôles et une configuration de plus dans l'application pour un gain faible.
- **Immuabilité verrouillée dès le déploiement** : irréversible avant même que l'expert-comptable ait confirmé la durée.
- **Immuabilité des contrats** : incompatible avec la compensation du départ (ci-dessus).
- **Sauvegardes longues de la base au-delà de 10 ans ou exportées (`.bacpac`) vers un compte immuable** : maximum de la rétention longue Azure SQL, et la base reste reconstituable depuis la sauvegarde annuelle ; un export immuable est possible plus tard si l'expert-comptable l'exige.
