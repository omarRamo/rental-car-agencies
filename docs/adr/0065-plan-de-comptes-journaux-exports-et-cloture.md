# ADR-0065 — Plan de comptes, journaux, exports et clôture mensuelle

- **Statut** : Acceptée (plan de comptes par défaut et règles d'imputation « à valider par l'expert-comptable » ; format d'export du logiciel de l'expert-comptable : `TODO(spec)`)
- **Date** : 2026-10-22
- **Source** : [prompt](../PROMPT.md) lot 3B « Pré-comptabilité », [spécifications](../SPEC.md) §8 (plan de comptes paramétrable selon le système comptable tunisien, journaux, export CSV ou Excel au format convenu, clôture mensuelle qui verrouille la période) et invariant 8 (une caution n'est jamais un revenu), [ADR-0001](0001-monolithe-modulaire-dotnet.md), [ADR-0006](0006-anti-surreservation-verrou-par-categorie.md), [ADR-0018](0018-journal-audit-ajout-seul.md), [ADR-0057](0057-factures-avoirs-numerotation-et-moments-de-facturation.md), [ADR-0063](0063-depenses-justificatifs-et-couts-des-vehicules.md), [ADR-0064](0064-rapprochements-caisse-clic-to-pay-booking-com.md)

## Contexte

L'agence ne tient pas sa comptabilité dans la plateforme : un expert-comptable la tient dans son logiciel. La plateforme doit lui remettre, chaque mois, des **journaux équilibrés** tirés des faits qu'elle enregistre déjà (factures et avoirs, paiements et remboursements, cautions, caisse, dépenses, versements Booking.com, remises Clic to Pay), dans un format qu'il importe, puis **clore le mois** pour que plus rien n'y soit daté. Le plan de comptes doit suivre le système comptable tunisien sans que l'équipe l'invente : il est paramétrable, livré avec un défaut à faire valider. Une caution n'est jamais un revenu (invariant 8).

## Décision

### Un module Comptabilité

Nouveau module **`Accounting`** (schéma `accounting`) : plan de comptes et correspondances, journaux, exports, rapprochements ([ADR-0064](0064-rapprochements-caisse-clic-to-pay-booking-com.md)) et clôture. Il ne lit les autres modules que par leurs contrats, ajoutés au lot 3B : `IBillingAccountingQueries` (sinistres, dossiers à facturer) et `IBillingQueries.GetSalesDocumentsAsync`, `IPaymentsAccountingQueries` (paiements réglés, mouvements des cautions, paiements en ligne), `ICashAccountingQueries` (mouvements de caisse, dépenses) et `ICashQueries`, `IChannelPayouts` (règlements des canaux), `IBookingNumbers` (numéros des dossiers), `IFleetCostRecords`. Il n'expose aux autres qu'**un** contrat, le crochet de clôture `IAccountingPeriods`.

### Plan de comptes et correspondances

- Un **plan unique pour la plateforme** (`accounting.Accounts`, table temporelle) : numéro de 2 à 10 chiffres sans zéro en tête, jamais modifié (créer un autre compte), libellé, actif ou non. `TODO(spec)` : un plan par agence si les agences sont des sociétés distinctes.
- **Plan par défaut** inspiré du système comptable tunisien, semé dans tous les environnements, chaque compte marqué **« à valider par l'expert-comptable »** jusqu'à sa confirmation (renommer vaut confirmation) : `165` cautions reçues, `401` fournisseurs, `411` clients, `4118` Booking.com à recevoir, `4365` TVA récupérable, `4366`/`43661`/`43662`/`43663` TVA collectée (par taux 19, 13, 7 %), `4367` contribution journalière (loi de finances 2026), `4368` droit de timbre, `4671` débours refacturés (amendes), `471` écarts à justifier, `532` banque, `541` caisse recette, `5412` caisse cautions, `581` virements internes, `5811` Clic to Pay à recevoir, `5812` cautions par empreinte, `5813` cautions par chèque, `6061`… `6632` charges par catégorie de dépense, `6226` commissions Booking.com, `6271` commissions Clic to Pay, `658`/`758` différences de caisse, `7061`… `7069` locations, options, protections, livraisons, kilomètres, carburant, retards, nettoyage, autres frais, `709` remises accordées, `7581` dommages, `7582` frais d'annulation, `7583` frais de gestion.
- **Correspondances** (`accounting.Mappings`, table temporelle) : chaque **type d'opération** du catalogue (`AccountingMappingCatalog`, 49 clés : une par nature de ligne de facture, par taux de TVA, par moyen de trésorerie, par catégorie de dépense, tiers et différences) vers un compte **actif** ; un compte utilisé reste actif. Le semis n'ajoute que ce qui manque et ne touche jamais ce que le comptable a changé.
- Écran `/admin/accounting`, onglets Journaux, Clôture mensuelle, Plan de comptes, Correspondances ; permission **`accounting.manage`**.

### Journaux

Cinq journaux : **VT** ventes, **AC** achats, **BQ** banque, **CA** caisse, **OD** opérations diverses. Chaque écriture a une date, une **pièce** (le numéro du document source), un libellé (toujours en français, la langue des livres), des lignes **équilibrées** — l'entité `JournalEntry` refuse une écriture déséquilibrée, vide ou sur un compte invalide — et une **clé de source** (`sales:{document}`, `payment:{paiement}`…). Lettrage : le **numéro du dossier** sur les lignes clients (facture ↔ paiements ↔ caution retenue), le **numéro de la dépense** sur les lignes fournisseurs (dépense ↔ son paiement).

| Fait (date) | Journal | Écriture |
|---|---|---|
| Facture (émission) | VT | 411 au débit du total dû ; au crédit chaque nature HT, la TVA par taux, le timbre |
| Avoir (émission) | VT | les mêmes comptes, sens inverses (lettrage : le dossier de la facture) |
| Dépense (sa date) | AC | charge HT de sa catégorie et 4365 au débit, 401 au crédit |
| Paiement d'une dépense par banque | BQ | 401 au débit, 532 au crédit |
| Paiement ou remboursement par carte, virement, chèque | BQ | trésorerie du moyen (532 par défaut) contre 411 |
| Paiement Clic to Pay (capture) et son remboursement | BQ | 5811 (encaissement à recevoir) contre 411 |
| Paiement encaissé par le canal (prépayé Booking.com) | OD | 4118 au débit, 411 au crédit |
| Caution par empreinte ou chèque prise | OD | 5812 / 5813 au débit, **165** au crédit — un passif |
| Caution réglée | OD | 165 au débit ; 411 au crédit de la **part retenue** (elle règle la facture des dommages), la garantie au crédit du reste |
| Part retenue encaissée | BQ | 532 au débit, la garantie au crédit |
| Mouvement de caisse (jour de la session) | CA | encaissement 541/411, remboursement 411/541, caution reçue 5412/165, rendue 165/5412, retenue 165 + 541 / 411 + 5412, entrées et sorties diverses 541/581, ajustement et dépense payée (401/541) ou annulée |
| Écart compté d'une session | CA | 541 ou 5412 contre 658 (manquant) ou 758 (excédent) |
| Versement Booking.com (jour de sa saisie) | BQ | 532 du versement et 6226 des commissions déduites au débit, 4118 du brut prépayé au crédit, la différence avec l'attendu en 471 |
| Remise Clic to Pay (jour réglé du relevé) | BQ | 532 du net et 6271 des frais au débit, 5811 du brut au crédit |

Les paiements et cautions en **espèces** ne passent **que** par les mouvements de caisse, jamais deux fois. Aucune caution ne touche un compte de classe 7 (vérifié par les tests). Un type d'opération sans compte (correspondance retirée, compte désactivé) rend le mois **incomplet** : l'écran liste les types en cause, l'export et la clôture sont refusés (`accounting.mapping.missing`).

### Mois clos, mois ouvert, corrections

- Un mois **ouvert** a des journaux **générés à la demande** depuis les faits : ils évoluent jusqu'à la clôture.
- Sa **clôture fige** ses écritures (`accounting.Closings`, `JournalEntries`, `JournalLines`) : un mois clos s'affiche et s'exporte toujours avec **les mêmes lignes**, quoi qu'il arrive ensuite aux faits.
- Un fait **déjà figé** dans un mois clos et changé depuis (un versement corrigé le 5 avril pour mars) est passé dans le mois ouvert comme **la correction** de ce qui fut figé (la différence, compte par compte, libellée « Correction … (figé en mars) »), jamais deux fois ; un fait ressaisi à l'identique ne passe rien.

### Exports

- `GET /admin/accounting/export?agency=&from=yyyy-MM&to=yyyy-MM&journal=VT|AC|BQ|CA|OD|all&format=csv|xlsx` (`accounting.manage`, session à double authentification, `Cache-Control: no-store`), douze mois au plus.
- Port **`IJournalExporter`** (`Format`, `ContentType`, `FileExtension`, `Export(JournalExportDocument)`), deux exporteurs du **format générique** `date, journal, compte, pièce, libellé, débit, crédit, lettrage` avec un **total de contrôle** :
  - **CSV** : UTF-8 avec BOM, `;`, fin de ligne CRLF, en-tête `date;journal;compte;piece;libelle;debit;credit;lettrage`, dates ISO, montants au point et au millime, dernière ligne `TOTAL;;;;;{débits};{crédits};` ;
  - **Excel** (ClosedXML) : feuille « Écritures » (cellules typées, en-tête figé et filtrable, total de contrôle), feuille « Contrôle » (agence, mois, totaux par journal, équilibre, mois figé ou non).
- Toute cellule de texte commençant par `=`, `+`, `-`, `@`, une tabulation ou un retour chariot est **neutralisée** (apostrophe) contre l'injection de formules.
- `TODO(spec)` : le format du logiciel de l'expert-comptable sera un exporteur de plus.

### Clôture mensuelle

- **Par agence et par mois**, seulement **une fois le mois terminé** (le mois en cours n'est jamais clos : l'activité du jour n'est jamais refusée).
- Points vérifiés : **bloquants** — une session de caisse du mois non verrouillée, un écart de rapprochement non justifié ; **à confirmer** — un dossier clôturé ou annulé du mois sans facture, une facture électronique non acceptée par TTN. La clôture refuse un point bloquant, ou un point à confirmer non coché (la liste confirmée est gardée avec la clôture).
- Elle génère les journaux, les vérifie équilibrés (et complets), les **fige**, passe le mois à **Clos** (`accounting.Periods`, table temporelle) et l'écrit au journal d'audit (`accounting.period_closed`).
- **Réouverture** par un gérant seulement (permission **`accounting.reopen`**, qui implique `accounting.manage`), avec un **motif** de 10 à 500 caractères ; auditée (`accounting.period_reopened`) ; la clôture figée reste en base. Une nouvelle clôture fige de nouveau.

### Crochet de clôture

Contrat **`IAccountingPeriods`** :

```csharp
Task<Result> EnsureOpenAsync(Guid agencyId, DateOnly businessDate, CancellationToken cancellationToken = default);
Task<IReadOnlyList<DateOnly>> GetClosedMonthsAsync(Guid agencyId, CancellationToken cancellationToken = default);
```

`EnsureOpenAsync` refuse (`accounting.period.closed`) un fait daté dans un mois clos. Appelé dans l'unité de travail de l'écrivain, il prend d'abord le **verrou partagé** du mois (`sp_getapplock` `accounting:period:{agence}:{yyyy-MM}`, propriétaire la transaction) ; la clôture et la réouverture prennent le **verrou exclusif** : les écrivains en cours finissent avant que le mois ne se fige, et aucun fait ne s'y glisse pendant. Appelé hors transaction (lecture), il ne verrouille rien.

Points d'appel :

| Module | Opération | Date contrôlée |
|---|---|---|
| Caisse | ouverture d'une session ; entrée et sortie diverses, ajustement, comptage (clôture) d'une session ; tout mouvement du tiroir (paiement, remboursement, caution) | jour d'exploitation de la session (le verrouillage n'est pas contrôlé) |
| Caisse — dépenses | enregistrement, modification (ancienne et nouvelle date), suppression ; session payeuse | date de la dépense ; jour de la session |
| Paiements | paiement, remboursement et confirmation au comptoir ; prise, restitution et retenue d'une caution ; capture et remboursement en ligne | aujourd'hui (Tunis) |
| Facturation | émission d'une facture (location, annulation, amende), avant d'en prendre le numéro ; émission d'un avoir | jour d'émission |
| Rapprochements | relevé importé ou saisi, suppression d'un relevé | jours des transactions et des remises |

Une confirmation reçue de la passerelle Clic to Pay et un versement Booking.com saisi sont datés du jour : ils tombent toujours dans le mois ouvert.

## Conséquences

- Le comptable exporte ses journaux du mois en un clic, CSV ou Excel, et l'export d'un mois clos est reproductible à l'octet près (CSV).
- Une erreur découverte après la clôture se corrige dans le mois ouvert (avoir daté du jour, correction d'un versement) ; une vraie erreur du mois clos demande au gérant de le rouvrir, avec un motif tracé.
- La simulation d'un mois complet (test d'intégration « fini quand ») vérifie l'équilibre, chaque compte contre les faits, l'égalité des lignes CSV et Excel, l'accord avec les rapports, la clôture et le refus des écritures tardives.
- Les rapports ([ADR-0046](0046-indicateurs-de-pilotage-et-sources.md)) et les journaux partent des mêmes faits : facturé, encaissé par moyen, cautions détenues, coûts des véhicules concordent.

## Alternatives écartées

- **Écrire les écritures au fil de l'eau** (à chaque fait) : chaque module dépendrait de la comptabilité, et changer une correspondance demanderait de réécrire l'historique ; la génération à la demande puis le gel à la clôture donnent la même trace, plus simplement.
- **Clôturer en interdisant tout fait daté avant une date** (sans verrou) : une écriture en cours pendant la clôture pourrait se glisser dans le mois figé.
- **Inventer le format du logiciel de l'expert-comptable** : `TODO(spec)` ; le format générique s'importe dans les logiciels courants par un modèle d'import.
- **Contrepasser dans le mois clos** : un mois clos ne change plus ; la correction va au mois ouvert.
