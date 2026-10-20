# ADR-0034 — Paiements et cautions : mouvements typés, caution comme dette, statut Clic to Pay venu de la banque seulement

- **Statut** : Acceptée
- **Date** : 2026-10-20
- **Source** : [spécifications](../SPEC.md) §3 (matrice des droits : encaissements et cautions), §6 (départ et retour), §8 (« Paiements et cautions », « Rapprochements »), §13 (« Argent ») ; [prompt](../PROMPT.md) §6, invariants 7 et 8, et §9.1 ; [conception phase 1](../conception/phase-1-exploitation.md) §7 (lot 1F)

## Contexte

Le lot 1F-a livre l'encaissement au comptoir et les cautions. Le lot 1F-b les appellera au départ (solde, prise de caution) et au retour (restitution ou retenue) ; la phase 2 y ajoutera le paiement en ligne Clic to Pay (pré-autorisation, capture, annulation) ; la phase 3 rapprochera les paiements des relevés (TPE, Clic to Pay, versements Booking.com, caisse) et les exportera. Les dossiers (lot 1E) sont écrits en parallèle : le module Paiements ne peut pas dépendre de leurs contrats.

## Décision

### Paiement (`Payment`)

- **Un mouvement d'argent par ligne** : sens (encaissement `Incoming` ou remboursement `Refund`), moyen (`Cash`, `CardTerminal` — TPE, `Cheque`, `BankTransfer`, `ChannelPrepaid` — prépayé au canal, `ClicToPay`), montant `Money` positif (au plus 1 000 000 TND, garde contre les fautes de frappe), statut, référence externe, notes, auteur et horodatages, session de caisse pour les espèces. Le dossier est référencé par son identifiant seulement (pas de clé étrangère vers un module construit en parallèle) ; l'agence a une clé étrangère vers le référentiel.
- **Statuts** (SPEC §8) : `Pending` (en attente), `PreAuthorized`, `Captured` (encaissé ; pour un remboursement : rendu au client), `Cancelled`, `Refunded` (encaissement entièrement remboursé), `Failed`. Au comptoir, un paiement est enregistré `Captured` dès que l'argent est en main ; il n'est `Pending` que pour un virement annoncé ou un chèque non encore encaissé, puis confirmé (`Captured`) ou annulé avec un motif (`Cancelled`). Toute autre transition est refusée.
- **Références** : obligatoires pour le chèque (numéro), le virement et le prépayé canal (référence de réservation) — elles servent aux rapprochements ; facultatives pour les espèces et le TPE, rapproché par lot quotidien.
- **Remboursement** : un paiement de sens `Refund`, lié à l'encaissement d'origine et créé par `Payment.RecordRefund` sur celui-ci, qui vérifie la limite « jamais plus que capturé − déjà remboursé » et tient `RefundedAmount` ; l'origine passe `Refunded` quand tout est rendu. Les deux lignes sont écrites dans la même transaction ; la `rowversion` de l'origine sérialise deux remboursements simultanés (vérifié sur SQL Server). Moyens de remboursement au comptoir : espèces, TPE, chèque, virement.
- **Totaux** (jamais de caution dedans) : encaissé = encaissements `Captured` ou `Refunded` (montant capturé) ; remboursé = remboursements effectués ; net payé = la différence ; en attente et pré-autorisé à part.

### Invariant 7 et point d'entrée de la phase 2

- `Payment.StartOnline` crée un paiement Clic to Pay `Pending`. Son statut ne change **que** par `ApplyConfirmedGatewayStatus(ConfirmedGatewayStatus)`, construit par le client de la passerelle à partir de la réponse de `getOrderStatusExtended.do`, jamais des paramètres de l'adresse de retour : `Registered` (rien ne change), `PreAuthorized` (montant égal au montant demandé), `Deposited` (`Captured`, montant débité au plus égal, éventuellement inférieur à la pré-autorisation), `Reversed` (`Cancelled`), `Declined` (`Failed`). Rejouer le statut courant est accepté : le traitement est idempotent par commande. Les opérations de comptoir refusent un paiement Clic to Pay, et le comptoir ne peut pas en créer.
- **Numéro de commande** Clic to Pay = identifiant du paiement en 32 caractères hexadécimaux (unique, connu avant l'appel à `register.do`) ; l'identifiant de commande renvoyé par la banque devient la référence externe.
- `TODO(spec)` phase 2 : codes de statut SMT au-delà de 0, 1 et 2 ; remboursement en ligne (`refund.do`) par un remboursement `Pending` dont le montant est réservé sur l'origine puis confirmé ou libéré ; validation du gérant au-delà d'un seuil (SPEC §9).

### Caution (`DepositHold`)

- **Une dette envers le client** (invariant 8) : jamais un paiement, jamais dans les totaux d'encaissement ; le passif ouvert est la somme des cautions `Held`.
- **Modes** : empreinte TPE (numéro d'autorisation obligatoire), espèces (comptées à part dans la caisse de l'agent, [ADR-0035](0035-sessions-de-caisse.md)), chèque (numéro obligatoire).
- **Règlement unique** : `Held` → `Released` (restituée), `PartiallyRetained` (retenue partielle, le reste est restitué) ou `Retained` (retenue totale) ; une seconde restitution ou retenue est refusée. Une retenue exige une justification (3 à 1 000 caractères) et accepte une référence de justificatif (devis, facture, photo).
- Une retenue n'est pas un chiffre d'affaires de location : la phase 3 facture les dommages et y impute la retenue. Une empreinte retenue apparaît dans le lot TPE du jour : la phase 3 la rapproche des cautions `CardImprint` retenues ce jour-là.

### Contrats, droits, événements

- `ICounterPayments` et `IDeposits` (écritures sous `payments.manage`, lectures sous `payments.read`) ; `IPaymentsQueries` sans permission pour les autres modules (résumé d'un dossier, totaux par jour de Tunis et par moyen, cautions détenues). Agent, gérant et comptable gèrent encaissements et cautions (matrice SPEC §3), le préparateur n'y a pas accès : aucune permission nouvelle.
- Les espèces passent par `ICashDrawer` (contrat du module Caisse) dans la même unité de travail : sans session ouverte de l'utilisateur dans l'agence, pas d'espèces.
- Appelées dans l'unité de travail d'un autre module (départ, retour), ces méthodes la rejoignent ; un échec doit faire échouer l'appelant, ses modifications suivies n'étant pas annulées par l'appel imbriqué.
- Événements de domaine (identifiants et discriminants seulement, vers l'outbox) : `PaymentRecorded`, `PaymentStatusChanged`, `PaymentRefundRecorded`, `DepositTaken`, `DepositReleased`, `DepositRetained`. Chaque écriture est auditée automatiquement.
- Persistance : schéma `payments`, tables temporelles `Payments` et `DepositHolds`, `decimal(18,3)`, index par dossier, par agence et date, par date d'encaissement (filtré) et sur les cautions détenues (filtré).

## Conséquences

- Lot 1F-b : solde au départ = total du devis − `IPaymentsQueries.GetBookingSummaryAsync(...).Payments.NetPaid`, encaissé par `ICounterPayments.RecordPaymentAsync` ; caution prise par `IDeposits.TakeAsync`, restituée ou retenue par `ReleaseAsync` et `RetainAsync` ; la clôture du dossier attend un net payé suffisant et `Deposits.HeldCount = 0`. Le composant `BookingPaymentsPanel` porte les mêmes actions à l'écran.
- L'existence du dossier est garantie par l'appelant ; tant que le contrat des dossiers n'est pas fusionné, les écrans montrent l'identifiant court du dossier.
- Un chèque impayé après encaissement n'est pas modélisé : rapprochement bancaire de la phase 3.

## Alternatives écartées

- **Remboursements en lignes filles du paiement d'origine** : un mouvement par sens et par moyen se rapproche et s'exporte plus simplement ; la limite reste vérifiée par l'agrégat d'origine, dans la même transaction.
- **Caution comme paiement d'un type particulier** : mêle dette et encaissement, contraire à l'invariant 8.
- **Statut de paiement modifiable par le back office** : contraire à l'invariant 7 pour le paiement en ligne.
