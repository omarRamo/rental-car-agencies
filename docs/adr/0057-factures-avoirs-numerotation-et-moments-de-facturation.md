# ADR-0057 — Factures et avoirs : documents figés, numérotation continue, TVA et moments de facturation

- **Statut** : Acceptée
- **Date** : 2026-10-21
- **Source** : [spécifications](../SPEC.md) §8 (facturation, TVA, timbre, régime transitoire), [prompt](../PROMPT.md) §6 (invariants), §9.4 et §16, [ADR-0016](0016-numerotation-continue-table-de-compteurs.md), [ADR-0024](0024-moteur-de-prix-pur-et-son-alimentation.md) et [conception phase 1](../conception/phase-1-exploitation.md) §4 « Taxes », [ADR-0042](0042-frais-de-retour-franchise-et-retard.md), [ADR-0046](0046-indicateurs-de-pilotage-et-sources.md)

## Contexte

Le lot 3A fait de la plateforme l'émetteur des factures de l'agence. Une facture tunisienne doit être numérotée sans trou par exercice, porter l'identité du vendeur (raison sociale, matricule fiscal, adresse) et de l'acheteur, la TVA par taux et le droit de timbre (hors TVA), et ne plus jamais changer une fois émise : une correction passe par un avoir. Les montants existent déjà ailleurs — le devis figé du dossier (moteur de prix), les frais de retour du lot 1F-b, les paiements et les cautions — et la facture ne doit pas les recalculer autrement. Plusieurs déclencheurs peuvent arriver deux fois (rejeux de l'outbox, clic répété d'un comptable) : un dossier ne doit jamais avoir deux factures.

## Décision

### Documents

- **`Invoice`** et **`CreditNote`** (schéma `billing`, tables temporelles, base commune `BillingDocument`) : numéro, exercice et rang, date d'émission (jour de Tunis), émis le et par, **vendeur** et **acheteur figés** (types complexes `SellerIdentity`, `BuyerIdentity`), lignes, totaux HT, TVA, TTC, droit de timbre, net à payer, mentions, langue (`fr`), état de la facturation électronique, chemin, empreinte et taille du PDF, envoi par e-mail. Une facture porte aussi son type (`Rental`, `CancellationFee`, `FineReinvoicing`), le dossier ou l'amende, la période de location et la situation des paiements à l'émission (déjà payé, caution imputée, solde).
- **Figement** : les documents n'ont aucune méthode qui change leurs montants, leurs parties ou leurs lignes ; seuls s'ajoutent, une fois chacun, la transmission TTN, le PDF et l'envoi par e-mail (ADR-0058). Toute correction est un avoir.
- **Nature comptable** de chaque ligne (`InvoiceLineNature` : location, remise, options, protection, livraison, contribution journalière, kilomètres, carburant, retard, nettoyage, autre frais, dommage, frais d'annulation, amende refacturée, frais de gestion) et code de la source, pour les exports comptables du lot 3B.
- **Identité du vendeur** : raison sociale, matricule fiscal, numéro RNE, adresse du siège et pied de page des factures sont des attributs de l'agence (Referential, écran `/admin/agency`, `settings.manage`, audités). Sans raison sociale, matricule fiscal et adresse (celle du siège, à défaut celle de l'agence), **aucune facture n'est émise** (`billing.seller_identity_incomplete`) : le dossier attend dans la liste « À facturer ».
- **Acheteur** : le payeur du dossier — une société avec son matricule fiscal et son adresse de facturation, sinon la personne ; son e-mail reçoit le document.

### Numérotation

- `FAC-AAAA-NNNNNN` et `AV-AAAA-NNNNNN` par **exercice de Tunis**, pris dans la table de compteurs de l'ADR-0016, **dans la transaction du document et en dernier** : un document refusé (contrôle, identité incomplète, conflit) rend son numéro, et deux émissions concurrentes attendent le verrou de la ligne du compteur. Une série unique pour la plateforme (`TODO(spec)` : une série par agence si les agences sont des sociétés distinctes).

### TVA et timbre

- **Mêmes règles que le moteur de prix** (ADR-0024, conception §4) : les lignes saisies TTC sont groupées par taux ; le HT d'un groupe est `round(TTC / (1 + taux))` sur le total du groupe, réparti sur ses lignes (chacune arrondie, le reste sur la plus grande) ; `TVA = TTC − HT`. La contribution journalière, saisie HT, garde son HT et sa TVA. Le TTC d'une ligne ne change jamais : **une facture totalise exactement le devis courant et les frais de retour** (référence : 681,400 TTC = 572,605 HT + 108,795 TVA).
- **Droit de timbre** hors TVA, ajouté au net à payer de chaque facture (`billing.stamp_duty`, 0,000 par défaut, `TODO(spec)` : barème de l'expert-comptable). Les avoirs n'en reprennent pas (`TODO(spec)`).

### Moments de facturation

- **Clôture d'un dossier** (`BookingStatusChanged` → `Closed`, par l'outbox) : devis courant et frais de retour (plafond de franchise compris) ; déjà payé (net des remboursements) et caution retenue (un règlement, jamais un revenu — invariant 8).
- **Annulation avec frais** (`Cancelled` avec frais positifs) : une facture des frais d'annulation (`TODO(spec)` : TVA des frais d'annulation).
- **Refacturation d'une amende** (décision de l'agence, ADR-0059) : l'amende et les frais de gestion.
- L'émission automatique se coupe par agence (`billing.issue_automatically`) ; les dossiers clôturés ou annulés avec frais sans facture sont listés dans l'onglet « À facturer » avec le motif qui les bloque, et un comptable (`invoices.issue`) les émet d'un clic.
- **Une facture par dossier et par type** : verrou applicatif SQL (`sp_getapplock`, `billing:booking:{id}`), contrôle d'existence dans la transaction, puis index unique filtré `(BookingId, Kind)` en dernier rempart. Une demande répétée rend la facture existante (`AlreadyIssued`). Le gestionnaire d'outbox ne rejoue pas les refus définitifs (identité incomplète, pas de payeur…) et rejoue les autres.
- `TODO(spec)` : facturation d'un dossier Booking.com (payeur, commission) selon le contrat du canal ; seule la référence du canal est imprimée.

### Avoirs

- **Total** (tout ce qui reste créditable) ou **partiel** (un montant TTC par ligne positive, au plus ce qui reste de la ligne), motif obligatoire, permission `invoices.manage`, sous le verrou de la facture (`billing:invoice:{id}`). Jamais plus que le restant (`billing.credit_note.*`, `billing.nothing_to_credit`). Totaux négatifs, mêmes règles de TVA.

### Contrats

- `IBillingQueries` pour le lot 3B (journal des ventes, document, factures d'un dossier, encours par client) et `IInvoicedAmountsSource` pour le Reporting (factures moins avoirs des jours de la période, hors timbre).

## Conséquences

- Le montant facturé est celui que le client a vu : la facture ne recalcule ni prix ni frais, elle les reprend et les ventile.
- Le droit de timbre n'entre pas dans le solde de clôture du dossier (lot 1F-b) : une facture de dossier soldé affiche le timbre comme reste à payer (`TODO(spec)` : barème, puis encaissement au comptoir ou intégration au solde).
- Les factures restent conservées après l'anonymisation d'un client (obligation comptable ; `TODO(spec)` avec l'avocat).
- Pas de facture manuelle hors dossier dans ce lot.

## Alternatives écartées

- **Numéro à la création d'un brouillon** : un brouillon abandonné laisserait un trou.
- **Séquence SQL** : perd des valeurs à l'annulation et au redémarrage (ADR-0016).
- **Recalcul de la TVA ligne à ligne** : diverge du devis de quelques millimes ; le client paierait un autre total que celui affiché.
- **Facture modifiable jusqu'à son envoi** : contraire à l'immuabilité exigée ; l'avoir trace chaque correction.
- **Une facture par paiement** : la SPEC facture la location à sa clôture ; les paiements sont des règlements.
