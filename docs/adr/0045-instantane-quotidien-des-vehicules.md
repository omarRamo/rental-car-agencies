# ADR-0045 — Instantané quotidien des véhicules et répartition du chiffre d'affaires

- **Statut** : Acceptée
- **Date** : 2026-10-21

## Contexte

Les indicateurs de pilotage (SPEC §8, [conception phase 3](../conception/phase-3-finance.md) §4) se lisent par véhicule et par jour : revenu par véhicule disponible et par jour, taux d'utilisation, prix moyen par jour loué, marge par véhicule, chiffre d'affaires et marge par canal. Les calculer à chaque affichage depuis les dossiers et la flotte coûterait cher sur Azure SQL Basic et donnerait des réponses changeantes : la disponibilité courante de la flotte (ADR-0021) s'évalue à l'instant présent, un véhicule échangé en cours de location, un retour tardif ou un frais saisi après coup modifient le passé. Il faut une base figée, recalculable et explicable, et une règle unique pour dater et répartir le chiffre d'affaires d'un dossier sur ses jours.

## Décision

- **Trois tables dans le schéma `reporting`**, données dérivées jamais modifiées à la main :
  - `VehicleDaySnapshots` : une ligne par véhicule et par jour calendaire de Tunis tant que le véhicule est dans la flotte (de sa mise en service à sa vente) : agence, véhicule, immatriculation, modèle, catégorie et code ACRISS du jour, disponible, loué, dossier principal (identifiant et numéro), canal, chiffre d'affaires de location du jour, commission, coûts directs, amortissement, instant du calcul. Index unique (agence, véhicule, jour). Immatriculation, code de catégorie et numéro de dossier sont recopiés pour que la table se lise seule (exports, futures vues pour Power BI).
  - `VehicleDayRentals` : les parts de location d'un véhicule-jour (chiffre d'affaires et commission de chaque dossier), quand deux dossiers se partagent un jour (un jour facturé après un retour tardif, puis la location suivante) ; la ligne du jour nomme la plus grosse part comme dossier principal.
  - `SnapshotDays` : une ligne par agence et par jour calculé (instant du calcul, nombre de véhicules) avec le chiffre d'affaires qu'aucun véhicule-jour n'a pu porter (dossier compté sans véhicule affecté, jour loué d'un véhicule hors flotte) : signalé à part, jamais perdu.
- **Disponible** : le véhicule pouvait être loué pendant au moins une partie du jour selon son **historique** (mise en service, vente, documents obligatoires valides ce jour-là — renouvellements compris —, blocages de maintenance, périodes passées en statut `Maintenance` ou `OutOfService`), ou il était loué. Le module Flotte fournit ces jours par un contrat (`IFleetReportingQueries`), sans exposer ses entités.
- **Ce qui compte comme chiffre d'affaires** : un dossier ferme — `Confirmed`, `InProgress`, `Returned` ou `Closed`. Les demandes, options et dossiers en attente de paiement sont en attente (le rythme des réservations les montre à part) ; refusés, expirés, annulés et non-présentés ne comptent jamais. Un frais d'annulation n'est pas du chiffre d'affaires de location : il est présenté avec les annulations. Le montant est le total HT du **devis en vigueur** moins la contribution journalière HT (collectée pour l'État) ; la TVA et la caution n'en font jamais partie (invariant 8).
- **Jours loués** : autant d'unités de 24 h que de jours au devis, à partir du départ prévu ; l'unité `i` commence à `départ + i × 24 h` et appartient au jour de Tunis de cet instant (le moteur de prix date ses prix journaliers de la même façon). Elle va au véhicule qui portait le dossier au début de l'unité (chronologie des affectations : un échange de véhicule passe les jours suivants au remplaçant).
- **Répartition uniforme au millime** (`DailySpread`) : chaque jour reçoit le montant divisé par le nombre de jours, tronqué au millime ; le dernier jour reçoit aussi le reste. Les parts d'un dossier font toujours son total exact, et un jour reçoit la même part quelle que soit la plage recalculée. La même règle répartit la commission du canal et tout coût direct couvrant une période.
- **Coûts directs** : ceux de chaque `IVehicleCostsSource` enregistrée (aujourd'hui les frais saisis sur les véhicules : prix des documents sur leur validité — depuis leur émission, ou l'année précédant l'échéance quand l'émission est inconnue —, coût réel des entretiens terminés, autres frais à leur date), répartis sur leurs jours ; la part d'un jour hors flotte n'est imputée à aucun véhicule. **Amortissement** : linéaire, celui du jour selon les données économiques du véhicule (zéro sans données, après la durée d'amortissement et à partir de la vente). **Commission** : celle du port `IChannelCommissionSource`, répartie comme le chiffre d'affaires (aucune jusqu'à la phase 5, ADR-0046).
- **Calcul** : fonction pure du domaine (`SnapshotCalculator`) sur des entrées lues hors transaction par les contrats des autres modules ; l'écriture remplace les jours de la plage d'une agence en une transaction sérialisable (suppression des lignes puis insertion), réessayée par la stratégie d'exécution. Recalculer une plage donne les mêmes chiffres ; deux recalculs simultanés des mêmes jours se heurtent à l'index unique : l'un réussit, l'autre reçoit `reporting.snapshot.concurrent_recompute`.
- **Quand** : tâche récurrente `reporting.daily_snapshot` chaque nuit à 3 h (heure de Tunis) pour la veille et les jours qui la précèdent (`reporting.snapshot_refresh_days`, 7 jours en tout par défaut, de 1 à 31), afin qu'un retour, un non-présenté ou un frais saisi en retard y entre ; recalcul d'une période depuis l'écran des rapports (permission `reports.read`, jours écoulés seulement, un an au plus), inscrit au journal d'audit (`reporting.snapshots_recomputed`). En développement et en recette, l'initialisation calcule les 45 derniers jours de l'agence de démonstration.

## Conséquences

- Les indicateurs de flotte se lisent dans une seule table, sans jointure avec les autres modules ; une ligne s'explique (dossier, part, coûts, amortissement) et se recalcule.
- Un jour déjà calculé ne change plus tout seul au-delà de la fenêtre nocturne : une correction ancienne demande un recalcul de la période depuis l'écran (l'écran signale les jours non calculés de la période et de la période de comparaison).
- Le chiffre d'affaires du jour suit le devis en vigueur au moment du calcul : une prolongation ou une modification de devis s'applique aux jours recalculés.
- Les frais de la flotte sont enregistrés TTC sans ventilation de TVA : les coûts directs sont donc TTC jusqu'à ce que les dépenses du lot 3B fournissent des montants HT (ADR-0046).
- La part d'un coût tombant avant la mise en service ou après la vente n'est imputée à aucun véhicule-jour.

## Alternatives écartées

- **Calculer les indicateurs à la volée depuis les dossiers** : coûteux à chaque affichage et instable dans le temps (fenêtres de flotte évaluées à l'instant présent, échanges, retours tardifs).
- **Une ligne par dossier et par jour** sans ligne par véhicule : les jours sans location (dénominateur du revenu par véhicule disponible) et les coûts des véhicules immobilisés disparaîtraient.
- **Arrondir chaque part au plus proche** : la somme des parts ne retombe pas toujours sur le total du dossier ; le reste au dernier jour garantit l'égalité au millime.
- **Vues SQL pour Power BI dès maintenant** : elles demandent du SQL écrit à la main dans la migration ; les tables, plates et autoportantes, se lisent directement en attendant.
