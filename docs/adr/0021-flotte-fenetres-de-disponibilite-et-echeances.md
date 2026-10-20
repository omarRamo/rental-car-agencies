# ADR-0021 — Flotte : fenêtres de disponibilité calculées par le domaine, écritures sous verrou et alertes d'échéance

- **Statut** : Acceptée
- **Date** : 2026-10-20
- **Source** : [conception phase 1](../conception/phase-1-exploitation.md) §3 et §5, [ADR-0006](0006-anti-surreservation-verrou-par-categorie.md), [ADR-0017](0017-execution-cas-usage-portee-transactions.md), invariant n° 3 (aucun départ avec une assurance, une vignette ou une visite technique expirée)

## Contexte

La capacité d'une catégorie, base du contrôle anti-surréservation ([ADR-0006](0006-anti-surreservation-verrou-par-categorie.md)), dépend entièrement de la flotte : mise en service, vente, documents obligatoires, entretiens datés et statut de chaque véhicule. Le module Réservations (lot 1E) doit lire cette capacité par le contrat `IFleetQueries`, sans connaître les règles de la flotte, et être prévenu de chaque changement. Les échéances administratives doivent par ailleurs être signalées au gérant avant de bloquer un véhicule.

## Décision

### Fenêtres de disponibilité

- Une **fonction pure du domaine**, `VehicleAvailability.Windows(profil, plage, maintenant)`, calcule les sous-périodes louables d'un véhicule ; l'agrégat (`Vehicle.AvailabilityWindows`) et la requête de contrat (`FleetQueries.GetAvailabilityAsync`, projection sans suivi) l'appellent avec le même profil, sans logique dupliquée en SQL.
- Règles : à partir du début du jour (heure de Tunis) de la mise en service ; jusqu'à la première de ces bornes : vente, fin de conformité ; moins chaque créneau d'entretien `[début, fin)` (planifié ou en cours ; `[début, fin réelle)` une fois terminé ; rien s'il est annulé) ; moins `[maintenant, ∞)` si le statut est `Maintenance` ou `OutOfService` sans créneau daté contenant l'instant présent. Intervalles demi-ouverts, triés, disjoints, bornés à la plage demandée ; un véhicule vendu n'est plus listé.
- **Conformité** (`VehicleCompliance`) : assurance, vignette et visite technique sont obligatoires ; `CompliantUntil` est la fin (exclusive) du jour, heure de Tunis, de la première expiration ; un document obligatoire manquant rend le véhicule non conforme (aucune fenêtre). La carte grise est suivie sans échéance.
- Tests : cas limites exhaustifs et six propriétés FsCheck (3 000 cas chacune) : fenêtres incluses dans la plage, ordonnées et disjointes, jamais pendant un blocage, jamais avant la mise en service ni après la vente ou la fin de conformité, et, point par point, louable exactement quand un oracle naïf le dit.

### Écritures

- Chaque commande passe par `FleetWriter` : chargement de l'agrégat dans l'unité de travail rejouable ([ADR-0017](0017-execution-cas-usage-portee-transactions.md)), application de la règle, puis, si l'opération lève `VehicleAvailabilityChanged`, **verrou `IAvailabilityLock`** sur chaque catégorie touchée (les deux lors d'un changement de catégorie, dans l'ordre des identifiants), enregistrement, validation, et seulement ensuite `IAvailabilityChangeNotifier.CategoryChanged` pour le cache.
- `VehicleAvailabilityChanged` (véhicule, agence, catégorie, catégorie précédente) est levé **une fois par opération** qui modifie la capacité et part par l'outbox.
- **Concurrence** : `Vehicle.Revision` est incrémenté par chaque modification, même d'un enfant (document, entretien, coût, photo), si bien que la `rowversion` du véhicule protège tout l'agrégat ; un conflit devient `fleet.concurrent_update`. L'historique lisible (`fleet.VehicleHistory`) est numéroté par véhicule (`Sequence`) sous la même protection, donc ordonné sans dépendre de l'horloge.
- Règles métier dans l'agrégat : immatriculation normalisée et unique par agence ; kilométrage jamais en baisse, sauf correction motivée ; changements de statut motivés (`Sold` est terminal et annule les entretiens planifiés avec le motif `vehicle.sold`) ; renouvellement d'un document par un nouveau document (l'ancien reste dans l'historique) ; correction motivée.

### Échéances

- Tâche récurrente `fleet.deadline_alerts`, chaque jour à 07:00 heure de Tunis : pour chaque document courant à échéance d'un véhicule non vendu, le seuil atteint (J-30, J-15, J-7, dépassé) donne **un e-mail par (document, seuil)** à chaque gérant actif (contrat `IIdentityQueries.GetActiveUsersInRoleAsync`), via `IEmailQueue` (outbox).
- L'idempotence repose sur `fleet.DeadlineAlerts` (unique sur document et seuil) : relancer la tâche n'envoie rien de plus ; un seuil sauté pendant une interruption n'est pas rattrapé (seul le plus urgent est envoyé) ; un renouvellement crée un nouveau document, donc un nouveau cycle.
- Le tableau de bord du back-office montre les mêmes échéances (rouge : dépassée, manquante ou sous 7 jours ; orange : sous 30 jours) ainsi que les véhicules indisponibles.

## Conséquences

- Réservations calcule la capacité à l'instant t comme le nombre de véhicules dont une fenêtre contient t, contrôle l'invariant n° 3 avec `VehicleInfo.IsCompliantFor` (ou `GetDeadlinesAsync` pour expliquer le blocage) et invalide ses caches sur `VehicleAvailabilityChanged`.
- Une écriture de flotte attend un contrôle de disponibilité en cours sur la même catégorie (et l'inverse) ; un dépassement du délai du verrou devient `fleet.availability_busy`.
- La flotte ne connaît pas les dossiers : une panne ou une échéance peut faire passer la capacité sous l'occupation ; c'est la réalité du parc, et Réservations doit la signaler (planning, réaffectation) plutôt que l'empêcher.
- Les seuils d'alerte (30, 15, 7 jours) sont fixes ; la marge « entretien bientôt dû » est réglable (`fleet.maintenance_due_soon_km`, `fleet.maintenance_due_soon_days`).

## Alternatives écartées

- **Statut « disponible » stocké et recalculé par une tâche** : décalage entre l'état stocké et la réalité, risque de vendre un véhicule dont l'assurance a expiré la nuit.
- **Calcul des fenêtres en SQL** : règles dupliquées entre le domaine et la requête, difficiles à tester par propriétés.
- **Verrou par véhicule** : n'empêche pas une réservation de consommer une capacité que la flotte est en train de retirer à la catégorie.
