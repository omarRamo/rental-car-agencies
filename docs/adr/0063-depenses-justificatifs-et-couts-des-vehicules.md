# ADR-0063 — Dépenses, justificatifs et coûts des véhicules sans double comptage

- **Statut** : Acceptée (catégories, taux de TVA déductible et durée de conservation des justificatifs à valider par l'expert-comptable)
- **Date** : 2026-10-22
- **Source** : [prompt](../PROMPT.md) lot 3B « Dépenses », [spécifications](../SPEC.md) §8 (dépenses, rapprochements, pré-comptabilité), [ADR-0035](0035-sessions-de-caisse.md), [ADR-0046](0046-indicateurs-de-pilotage-et-sources.md), [ADR-0059](0059-amendes-designation-du-conducteur-et-sinistres.md), [ADR-0061](0061-sauvegardes-retention-et-immuabilite.md), [ADR-0065](0065-plan-de-comptes-journaux-exports-et-cloture.md)

## Contexte

L'agence paie du carburant, l'entretien et les réparations de ses véhicules, des assurances, des vignettes, des loyers de leasing, des commissions (Booking.com, Clic to Pay), le loyer et les charges de ses locaux. La spécification demande de les saisir avec leur fournisseur, leurs montants hors taxes, TVA et TTC, leur moyen de paiement et leur justificatif, de les relier au besoin à un véhicule, à un entretien ou à un sinistre, et de les faire entrer dans les journaux (achats, banque, caisse) et dans la rentabilité des véhicules. Deux pièges : une dépense payée en espèces doit sortir de la caisse réelle de l'utilisateur, et un même coût ne doit jamais compter deux fois dans les marges (l'entretien terminé est déjà un coût de la source `fleet`, le sinistre un coût de la source `claims`, l'assurance et la vignette sont les prix des documents du véhicule).

## Décision

### Place dans les modules

- Les dépenses appartiennent au **module Caisse** (`CarRental.Application.Cash.Expenses`) : une dépense en espèces et son mouvement de caisse s'écrivent dans **la même transaction** que la session de caisse, sans contrat supplémentaire entre modules. Le module expose `ICashAccountingQueries.GetExpensesAsync` (et les mouvements de caisse) à la pré-comptabilité.
- Écran `/admin/expenses`, permission **`expenses.manage`** (comptable et gérant) ; une dépense en espèces demande aussi **`cash.own`** (la caisse de l'utilisateur).
- Liens vérifiés par les contrats des autres modules : `IFleetQueries` (véhicule de l'agence, hors vendus), `IFleetCostRecords.GetMaintenancesAsync` (entretiens du véhicule), `IBillingAccountingQueries.GetClaimsAsync` (sinistres du véhicule).

### Une dépense

- **Numéro** `DEP-{année}-{séquence sur 6 chiffres}`, par agence et par année de la date de la dépense (`cash.ExpenseSequences`). Un trou est sans conséquence (contrairement aux factures) ; deux dépenses enregistrées en même temps prennent leur numéro **l'une après l'autre** sous un verrou applicatif exclusif `cash:expense-number:{agence}:{année}` (`sp_getapplock`, propriétaire la transaction), plutôt que de se heurter sur le compteur.
- **Montants**, en dinars au millime, arrondis comme le moteur de prix (`MidpointRounding.AwayFromZero`) :
  - TTC et taux : HT = arrondi(TTC ÷ (1 + taux)), TVA = TTC − HT ;
  - HT et taux : TVA = arrondi(HT × taux), TTC = HT + TVA ;
  - HT et TVA « comme sur la facture du fournisseur » : TTC = HT + TVA, taux déduit (4 décimales) ou absent.
- **Catégories** : carburant, entretien, réparations, assurance, vignette, leasing, commission Booking.com, commission Clic to Pay, loyer des locaux, eau-électricité, autres (une catégorie par compte de charge du [plan par défaut](0065-plan-de-comptes-journaux-exports-et-cloture.md)).
- **Moyens de paiement** : espèces (sortie de la caisse ouverte de l'utilisateur, mouvement `ExpensePaid`), carte, virement, chèque (numéro obligatoire) — ces trois derniers passent par la banque.
- **Date** comprise entre le 01/01/2000 et aujourd'hui (Tunis). Fournisseur obligatoire, matricule fiscal, référence de la facture du fournisseur et description facultatifs.
- **Modifiable tant que son mois n'est pas clos** (crochet de clôture, [ADR-0065](0065-plan-de-comptes-journaux-exports-et-cloture.md)) : ni l'ancien mois ni le nouveau ne doivent être clos. Une dépense en espèces garde sa date, ses montants et son moyen de paiement (la caisse les a déjà comptés) ; une dépense par banque ne devient pas une dépense en espèces.
- **Suppression logique**, avec un motif (5 à 500 caractères, vérifié avant tout mouvement de caisse) : la dépense reste visible comme supprimée (qui, quand, pourquoi), hors de tout total ; une dépense en espèces remet son montant dans la caisse ouverte de l'utilisateur (mouvement `ExpenseReversed`, qui nomme la session d'origine). Journal d'audit automatique (`entity.soft_deleted`) et table temporelle `cash.Expenses`.

### Justificatif

- Photo ou PDF : **type réel lu dans les premiers octets** (JPEG, PNG, WebP, PDF), 10 Mo au plus — mêmes règles que les fichiers des sinistres.
- Stocké dans le conteneur privé **`invoices`**, sous `expenses/{année}/{dépense}/{identifiant}.{ext}` : une pièce comptable, gardée avec les factures (conteneur immuable 10 ans, [ADR-0061](0061-sauvegardes-retention-et-immuabilite.md)). Chaque dépôt va à un **chemin neuf**, jamais écrasé ; un justificatif remplacé reste dans le conteneur, le précédent n'étant plus référencé. Empreinte SHA-256, taille, type et nom envoyé par l'appareil (affichage seulement) sont gardés.
- Servi par `GET /admin/expenses/{id}/receipt` (`expenses.manage`, session à double authentification) : `Cache-Control: private, no-cache`, `ETag` tiré de l'empreinte, `Content-Disposition: inline`.
- Un justificatif peut arriver **après la clôture** du mois : il ne change aucun montant.

### Coûts des véhicules sans double comptage

La source de coûts `expenses` (`IVehicleCostsSource`, [ADR-0046](0046-indicateurs-de-pilotage-et-sources.md)) compte une dépense **hors taxes**, à sa date, pour son véhicule, selon la règle `ExpenseVehicleCosts.RuleOf` affichée dans le formulaire :

| Dépense d'un véhicule | Comptée par | Raison |
|---|---|---|
| Carburant, entretien, réparations, autres, sans lien | **`expenses`** | Seule à la connaître |
| Liée à un entretien du véhicule | `fleet` (coût réel de l'entretien terminé) | L'entretien porte déjà son coût |
| Liée à un sinistre du véhicule | `claims` (coûts et remboursements du sinistre) | Le sinistre porte déjà ses coûts |
| Assurance, vignette | `fleet` (prix des documents du véhicule) | Le document porte déjà son prix |
| Leasing | amortissement du véhicule | Le loyer est la valeur du véhicule |
| Sans véhicule, ou supprimée | jamais | — |

Un entretien ou un sinistre ne se relie qu'avec un véhicule, et jamais les deux à la fois.

## Conséquences

- La caisse du comptoir reflète les dépenses réelles : une session ne peut pas payer plus qu'elle ne contient (`cash.movement.insufficient_cash`), et le rapprochement de caisse affiche les dépenses de chaque session ([ADR-0064](0064-rapprochements-caisse-clic-to-pay-booking-com.md)).
- Une dépense en espèces payée sur une autre session que celle de son jour (une facture fournisseur du 20 réglée le 3 du mois suivant) entre au journal d'achats à la date de sa facture et au journal de caisse le jour de la session, lettrées par son numéro.
- La marge d'un véhicule inclut désormais son carburant et ses petites réparations sans gonfler l'entretien ni les sinistres.

## Alternatives écartées

- **Un module « Dépenses » à part** : il lui faudrait un contrat pour écrire dans la session de caisse dans la même transaction, pour un seul consommateur.
- **Compter toutes les dépenses d'un véhicule et retirer les entretiens de la source `fleet`** : la flotte connaît le coût réel d'un entretien même payé hors dépense (facture d'un garage réglée par le siège) ; la règle par lien garde chaque source exacte.
- **Écraser le justificatif remplacé** : interdit par l'immuabilité du conteneur, et une pièce comptable ne se détruit pas.
- **Numérotation sans trou partagée avec les factures** (compteurs de l'ADR-0016) : inutile pour une pièce d'achat, et un compteur global serait un point de contention de plus.
