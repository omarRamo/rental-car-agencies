# ADR-0023 — Plans tarifaires versionnés : brouillon, publication, archives

- **Statut** : Acceptée
- **Date** : 2026-10-20
- **Source** : [conception phase 1](../conception/phase-1-exploitation.md) §4, [spécifications](../SPEC.md) §7 (règle 13 : « tout changement de grille est historisé, un simulateur permet de tester une grille avant publication ») et §13 (« Tarifs »), [ADR-0003](0003-donnees-azure-sql-tables-temporelles.md)

## Contexte

Un devis figé doit pouvoir dire avec quelle grille il a été calculé, et le gérant doit préparer la grille de la saison suivante sans toucher aux prix en vigueur, la tester, puis la mettre en ligne d'un coup. Les prix varient par canal : parité, majoration (commission Booking.com) ou grille propre.

## Décision

- **Agrégat `RatePlan`** (module Pricing) : agence, canal (`null` pour la **grille par défaut** de l'agence, sinon la **grille propre** d'un canal), nom, statut, version, contenu : saisons datées par niveau (basse, moyenne, haute ; dates de Tunis incluses ; durée minimale), événements prioritaires (priorité, durée minimale), cases de prix TTC par catégorie et par période (prix de base, niveau de saison, événement), paliers de durée et remises propres à une catégorie, catalogue d'options, promotions, règles par canal (grille par défaut seulement).
- **Cycle de vie** : `Draft` → `Published` → `Archived`. Seul un brouillon se modifie. **Publier** donne au brouillon la version suivante de son agence et de son canal (1, 2, 3…) et archive la grille publiée qu'il remplace, dans la même transaction. Une version publiée ne change plus jamais : les devis la désignent par son identifiant et son numéro. Au plus un brouillon et une grille publiée par agence et canal (index uniques filtrés), un numéro de version utilisé une fois.
- **Brouillon** : copie de la grille publiée du canal (nouveaux identifiants, prix des événements remappés), ou de la grille par défaut pour une première grille propre (sans les règles par canal), ou d'une version archivée (restauration), ou vide pour la première grille d'une agence.
- **Édition par section** (saisons, événements, grille de prix, paliers, options, promotions, règles) : chaque méthode remplace la section entière, vérifie ses invariants (saisons sans chevauchement, événements qui se chevauchent avec des priorités différentes, prix positifs et un prix de base par catégorie tarifée, paliers commençant au jour 1 et croissants — contigus par construction, codes uniques, une seule option active par type automatique…), garde l'identité des éléments existants (ligne mise à jour plutôt que supprimée puis recréée), incrémente la **révision** et lève `RatePlanDraftUpdated`. L'éditeur renvoie la révision lue : une révision périmée donne `pricing.plan_modified_concurrently` (modification concurrente), en plus du `rowversion`.
- **Contrôles avant publication** (`Validate`) : bloquant si aucune catégorie n'a de prix de base ; avertissements pour les dates entre deux saisons (prix de base), un événement sans prix, l'absence de saison et, côté application, une règle « grille propre » sans grille publiée pour le canal.
- **Historique** : une table temporelle par partie du plan (`pricing.RatePlans`, `RatePlanSeasons`, `RatePlanEvents`, `RatePlanPrices`, `RatePlanTiers`, `RatePlanTierDiscounts`, `RatePlanExtras`, `RatePlanPromotions`, `RatePlanChannelRules`) et le journal d'audit (auteur, avant/après) pour chaque modification ; les versions publiées restent des lignes.
- **Prix par niveau de saison** et non par période datée : la grille reste lisible (base, basse, moyenne, haute, puis une colonne par événement) quel que soit le nombre de périodes. Une case vide d'événement ou de saison reprend le niveau suivant (événement, saison, prix de base).
- **Permissions** : `pricing.read` pour lire et simuler, `pricing.manage` pour toute modification ; vérifiées par le service applicatif et par les pages.

## Conséquences

- Les prix en vigueur ne changent qu'à la publication ; le simulateur calcule un brouillon (ou une ancienne version) à la place de la grille publiée, côte à côte avec elle.
- Archiver la grille publiée par défaut arrête la tarification de l'agence (confirmation explicite dans l'écran).
- Le module Canaux (phase 5) pourra pousser les nouveaux tarifs à partir de `RatePlanPublished`.
- Une saison de prix différente d'une année sur l'autre passe par une nouvelle version (ou un événement), pas par une case de plus.

## Alternatives écartées

- **Modifier la grille en place avec un historique temporel seul** : pas de préparation hors ligne, pas de simulation avant mise en ligne, et un devis ne pourrait citer qu'une date, pas une version.
- **Contenu du plan en colonnes JSON** : une seule table temporelle, mais le journal d'audit ne verrait plus les changements des parties du plan.
- **Prix par période datée** : grille qui s'allonge avec chaque période et saisie répétitive des mêmes prix.
