# ADR-0046 — Indicateurs de pilotage : définitions, sources et ports vers 3A, 3B et la phase 5

- **Statut** : Acceptée
- **Date** : 2026-10-21

## Contexte

SPEC §8 liste les indicateurs du gérant sans en fixer le calcul exact : dénominateurs, périodes, comparaisons, traitement des jours en cours, des annulations ou des valeurs nulles. Plusieurs sources n'existent pas encore au moment du lot 3C : factures (lot 3A), dépenses (lot 3B), frais de retour (lot 1F-b), commissions des canaux (phase 5). Les rapports doivent rester justes aujourd'hui, dire ce qui manque, et accueillir ces sources sans être réécrits. Les instantanés quotidiens sont décrits par l'ADR-0045.

## Décision

### Période et comparaison

- Une période est une suite de jours calendaires de Tunis, bornes comprises, de 1 à 366 jours. L'écran s'ouvre sur le **mois en cours** (le mois précédent le 1er du mois, qui n'a encore aucun jour écoulé) ; une période personnalisée se choisit dans l'écran, et la sélection (agence, début, fin) est dans l'adresse.
- **Un jour compte une fois terminé** : les indicateurs portent sur les jours de la période jusqu'à hier (« jours couverts ») ; le rythme des réservations regarde à partir d'aujourd'hui ; les cautions sont celles détenues maintenant.
- **Comparaison** : un mois civil se compare aux mêmes jours écoulés du mois précédent depuis son 1er (coupés à sa fin) ; une période libre aux autant de jours qui la précèdent. L'écran et la synthèse Excel indiquent combien de jours de chaque période ont leurs instantanés ; sans aucun instantané pour la période de comparaison, les indicateurs de flotte ne sont pas comparés.
- **Arithmétique** : un rapport dont le dénominateur est nul n'a pas de valeur (« — »), jamais 0 ni l'infini ; un montant par unité est arrondi au millime ; une évolution vaut (actuel − précédent) ÷ |précédent| et n'existe pas quand le précédent est nul ou absent ; l'écart de deux taux s'exprime en points.

### Indicateurs

| Indicateur | Définition | Source |
|---|---|---|
| Revenu par véhicule disponible et par jour (indicateur principal du gérant) | chiffre d'affaires de location ÷ véhicules-jours disponibles ; égal au taux d'utilisation × prix moyen par jour loué | instantanés |
| Taux d'utilisation | jours loués ÷ véhicules-jours disponibles | instantanés |
| Prix moyen par jour loué | chiffre d'affaires de location ÷ jours loués | instantanés |
| Marge (flotte, véhicule) | chiffre d'affaires − commissions − coûts directs − amortissement ; aussi par véhicule-jour disponible | instantanés |
| Indication « à vendre ou à remplacer ? » | marge négative d'un véhicule sur les `reporting.sell_hint_months` derniers mois (6 par défaut, de 1 à 24) jusqu'au dernier jour couvert, sur les jours qui ont un instantané (leur nombre est affiché) | instantanés |
| Chiffre d'affaires et marge par canal | chiffre d'affaires et commission des parts de location du canal ; dossiers distincts ; jours loués = parts ; coûts directs et amortissement des véhicules-jours loués imputés au canal de la location principale ; ceux des jours sans location à aucun canal (ligne « jours sans location ») : marge des canaux − coûts des jours sans location = marge de la flotte ; le chiffre d'affaires sans véhicule affecté est montré à part | instantanés |
| Rythme des réservations à 30, 60 et 90 jours | pour chaque horizon H, à partir d'aujourd'hui : jours vendus aux dossiers fermes, jours des dossiers en attente (demandes, informations demandées, options, paiements attendus), véhicules-jours louables de la flotte sur les mêmes jours, par catégorie vendue et au total ; remplissage = vendus ÷ louables, avec ou sans l'attente ; comparaison avec les jours vendus un an plus tôt au même délai, chaque dossier pris dans le statut qu'il avait à l'instant d'il y a un an (sans aucun dossier créé à cette date, pas de comparaison) | dossiers et flotte (jamais les instantanés) |
| Conversion du site et du bot | par canal (site web ; WhatsApp, c'est-à-dire le bot) et par source (paramètre `src` normalisé, aucun pour l'accès direct) : visites anonymes distinctes qui ont cherché, puis vu des résultats (événements d'entonnoir, ADR-0037 ; un événement sans visite compte pour une visite) ; demandes envoyées sur la période ; parmi elles, celles confirmées depuis ; taux recherches → demandes, demandes → confirmées, recherches → confirmées | événements d'entonnoir et dossiers |
| Délai de validation et taux de refus | demandes du site et de WhatsApp envoyées sur la période ; délai = temps passé en `PendingValidation` avant la première décision (validation vers `Confirmed` ou `AwaitingPayment`, ou refus), l'attente d'informations demandées au client exclue ; médiane en heures d'horloge et en heures ouvrées (horaires de l'agence `pricing.opening_hours`) ; part décidée en 2 h ouvrées au plus (objectif de SPEC §2) ; refus ÷ décidées ; expirées ÷ envoyées | dossiers (chronologie) |
| Annulations et non-présentés par canal | parmi les dossiers dont le départ prévu tombe dans la période et qui ont été confirmés à un moment : annulés ÷ fermes, non-présentés ÷ fermes ; demandes et options annulées avant toute confirmation comptées à part ; frais d'annulation additionnés (TTC tels qu'enregistrés), jamais du chiffre d'affaires | dossiers |
| Encaissé | par moyen de paiement, sur les jours couverts : reçu, remboursé, net ; comparé à la période précédente | `IPaymentsQueries` (totaux journaliers) |
| Facturé | factures émises moins avoirs : HT, TVA, TTC, nombres | port `IInvoicedAmountsSource` |
| Cautions en cours | cautions détenues aujourd'hui, par mode : une dette envers les clients, jamais un revenu ni un encaissement (invariant 8) | `IPaymentsQueries` |
| Impayés | dossiers rendus ou clôturés dont le retour effectif tombe dans la période : dû = total TTC du devis en vigueur + frais de retour au-delà du devis ; reste dû = dû − payé net (paiements moins remboursements), positif seulement | dossiers, `IPaymentsReportingQueries`, port `IBookingChargesSource` |

Tous les indicateurs sont filtrés par agence. Les événements d'entonnoir du site ne portent pas d'agence : les recherches comptent pour chaque agence (sans effet tant qu'il n'y en a qu'une).

### Contrats et ports

- **Contrats ajoutés aux autres modules** (lecture seule, projections sans suivi) : `IBookingsReportingQueries` (dossiers qui chevauchent une période, demandes envoyées sur une période, avec devis en vigueur, chronologie des statuts et des affectations), `IFleetReportingQueries` (véhicules jour par jour selon leur historique, coûts enregistrés), `IPaymentsReportingQueries` (payé net par dossier). Le module Reporting n'utilise les autres modules que par leurs contrats.
- **Ports du module Reporting**, avec une implémentation par défaut enregistrée par `AddReportingModule` (`TryAdd`) :
  - `IVehicleCostsSource` — plusieurs sources s'additionnent (`TryAddEnumerable`) : les frais de la flotte aujourd'hui (`fleet`) ; le lot **3B** ajoute les dépenses rattachées aux véhicules (`expenses`), HT quand c'est possible, en veillant à ne pas compter deux fois un coût déjà saisi sur le véhicule (facture d'entretien, prime d'assurance) ni les loyers de leasing déjà dans l'amortissement.
  - `IInvoicedAmountsSource` — aucun montant jusqu'au lot **3A**, qui enregistre le sien (`AddScoped`, son enregistrement l'emporte).
  - `IChannelCommissionSource` — aucune commission jusqu'à la **phase 5** (commissions Booking.com HT par dossier).
  - `IBookingChargesSource` — aucun frais au-delà du devis jusqu'au lot **1F-b** (carburant, kilomètres, retard, nettoyage) ; les frais ajoutés comme lignes du devis y sont déjà.
- L'écran et la synthèse Excel disent quels ports ne sont pas encore branchés (« comptent pour zéro », « non disponible ») et d'où viennent les coûts directs.

### Écran et exports

- `/admin/reports`, permission `reports.read` (gérant et comptable) : tuiles avec évolution (flèche et texte signé, jamais la couleur seule), graphiques écrits à la main en HTML et CSS (colonnes et barres fines, palette validée pour les daltonismes, légende dès deux séries, valeur au survol) dont chacun a son **tableau de données** équivalent derrière un bouton, tableaux par véhicule (triables, `aria-sort`), par canal, du rythme, de la conversion, des demandes, des annulations et de la trésorerie ; une tuile du tableau de bord reprend le revenu par véhicule disponible du mois.
- Chaque tableau a son **export Excel** (ClosedXML, licence MIT), construit sur le serveur à partir des mêmes indicateurs que l'écran et téléchargé par `GET /admin/reports/export/{table}?agency=&from=&to=` (MFA et `reports.read`, `no-store`) : une feuille par tableau plus la synthèse (agence, période, comparaison, couverture, indicateurs, notes), dans la langue de l'utilisateur, nombres typés (montants au millime, taux en pourcentage, jours en dates, délais en durées).

## Conséquences

- Les chiffres structurellement nuls aujourd'hui (commissions, facturé, frais de retour) le restent jusqu'au branchement de leur fournisseur, et l'écran le dit ; aucun calcul n'est à reprendre ensuite.
- Le lot 3B doit décider, avec l'expert-comptable, quelles dépenses entrent dans les coûts directs pour éviter les doublons avec les frais de la flotte, et fournir des montants HT.
- Les journées en cours n'entrent dans les indicateurs que le lendemain (après minuit, puis le calcul de 3 h) : un encaissement du jour apparaît le lendemain.
- La conversion par agence restera approximative tant que les événements d'entonnoir ne portent pas d'agence.

## Alternatives écartées

- **Inclure le jour en cours** : ses chiffres changent jusqu'au soir et ses instantanés n'existent pas encore.
- **Comparer un mois en cours au mois précédent entier** : un mois à moitié écoulé paraîtrait toujours en baisse.
- **Une bibliothèque de graphiques en JavaScript ou les graphiques MudBlazor** : moins de maîtrise de l'accessibilité (équivalent tabulaire, contraste, motifs) et du rendu ; quatre formes simples suffisent.
- **Export CSV** : demandé en Excel ; EPPlus (licence commerciale) écarté au profit de ClosedXML.
