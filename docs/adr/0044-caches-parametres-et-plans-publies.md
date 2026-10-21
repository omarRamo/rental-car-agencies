# ADR-0044 — Caches des paramètres et des plans publiés, vidés après chaque validation

- **Statut** : Acceptée
- **Date** : 2026-10-21

## Contexte

Un devis lisait le plan publié et ses huit collections (neuf requêtes), puis chaque paramètre utile (TVA, contribution, tolérance de retard, arrondi, âge jeune conducteur, horaires… une requête chacun) : une vingtaine de requêtes par devis. La page de résultats chiffre chaque catégorie, le bot WhatsApp et les canaux interrogeront les prix en continu, et la production tourne sur Azure SQL Basic (5 DTU). Ces données changent rarement.

## Décision

- **Signal « modifications validées »** : le pipeline EF note les types d'entités insérées, modifiées ou supprimées par chaque enregistrement et, une fois la transaction validée (ou l'enregistrement validé hors transaction), les transmet aux écouteurs `ICommittedChangesListener`. Une annulation ne transmet rien. Le signal couvre tous les chemins d'écriture passant par le suivi des modifications : services, initialisation des données, tests. Il ne couvre pas les mises à jour en masse (`ExecuteUpdate`, `ExecuteDelete`), à ne pas utiliser sur ces données.
- **`SettingsCache`** : un instantané de toutes les valeurs stockées (quelques dizaines de lignes), chargé en une requête, vidé dès qu'une ligne `Setting` est validée, et au plus tard après 5 minutes.
- **`PublishedPlanCache`** : les plans publiés par agence et par canal, chargés dans un contexte jetable puis détachés, lus seulement ; vidés à toute validation d'une entité du domaine Tarifs, et au plus tard après 10 minutes.
- **Dans une transaction, pas de cache** : `SettingsReader` et la source des plans lisent alors la base avec la transaction. Un écrivain voit ainsi ses propres modifications non validées, rien de non validé n'est jamais mis en cache, et une transaction n'attend jamais un chargement de cache fait sur une autre connexion.
- Un chargement interrompu par une invalidation ne publie pas son résultat : le lecteur suivant recharge.

## Conséquences

- Un devis répété n'interroge plus la base que pour la catégorie, les lieux et, le cas échéant, le véhicule du modèle garanti.
- Une modification validée de paramètre ou de grille s'applique au devis suivant, sur cette instance. Sur plusieurs instances, la durée de vie (5 et 10 minutes) borne le décalage : à revoir si l'application passe à plusieurs instances (ADR-0004).
- Les objets mis en cache sont partagés entre requêtes : le moteur de prix et le calculateur ne doivent jamais modifier un plan publié (ce qu'ils ne font pas : seuls les services d'administration modifient des plans, toujours chargés par leur propre contexte).
