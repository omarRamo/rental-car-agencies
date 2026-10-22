# ADR-0064 — Rapprochements : caisse, Clic to Pay, Booking.com et justification des écarts

- **Statut** : Acceptée (format du relevé marchand de SMT et du relevé des versements Booking.com : `TODO(spec)`)
- **Date** : 2026-10-22
- **Source** : [prompt](../PROMPT.md) lot 3B « Rapprochements », [spécifications](../SPEC.md) §8 (rapprochement des encaissements Clic to Pay, des versements Booking.com et de la caisse), [ADR-0035](0035-sessions-de-caisse.md), [ADR-0039](0039-paiement-en-ligne-confirmation-idempotence-rattrapage.md), [ADR-0052](0052-canaux-de-vente-modele-restrictions-et-adaptateurs.md), [ADR-0065](0065-plan-de-comptes-journaux-exports-et-cloture.md)

## Contexte

Avant de clore un mois, le comptable vérifie que l'argent enregistré est l'argent reçu : les espèces de chaque session de caisse, les paiements en ligne contre ce que la banque a réglé, les versements de Booking.com contre ce que le canal devait payer. Les écarts existent (une pièce égarée, une commission bancaire, un remboursement en retard) : il faut les voir, les expliquer et garder l'explication. Le format du relevé marchand de Clic to Pay (SMT) n'est pas connu, pas plus que celui du relevé des versements de Booking.com ; l'écran de rapprochement des canaux existe déjà (lot 5A).

## Décision

### Écran et permission

`/admin/reconciliation`, permission **`reconciliation.manage`** (comptable, gérant), par agence et par mois — le mois qui vient de se terminer par défaut. Trois onglets : **Caisse**, **Clic to Pay**, **Booking.com**. Chaque onglet liste ses **écarts** ; un mois clos reste consultable, sans nouvelle justification ni nouveau relevé.

### Caisse

Les sessions du mois (`ICashQueries.GetSessionTotalsAsync`) contre les paiements en espèces enregistrés dans chacune (`IPaymentsAccountingQueries.GetCashPaymentsOfSessionsAsync`) :

- **écart d'encaissement** (`cash:receipts:{session}`) : paiements reçus moins remboursements enregistrés, comparés aux mouvements du tiroir ;
- **écart compté** (`cash:variance:{session}:revenue` et `…:deposit`) d'une session clôturée ou verrouillée, avec la justification du caissier à côté.

### Clic to Pay

Les paiements en ligne du mois (`IPaymentsAccountingQueries.GetOnlinePaymentsAsync`) :

- **les signalements des contrôles de la passerelle** (lot 2B : statut inconnu, désaccord de statut ou de montant, opération non confirmée…) sont des écarts (`clictopay:attention:{paiement}`) ;
- **contre le relevé marchand** : chaque ligne du relevé se rapproche d'un paiement réglé dans le mois par sa **référence** — notre numéro de commande (l'identifiant du paiement, 32 caractères hexadécimaux) ou l'identifiant de commande de Clic to Pay, sans tenir compte de la casse ; une ligne de remboursement se rapproche d'un remboursement par la commande du paiement remboursé. Une ligne n'est rapprochée qu'une fois, de préférence avec le paiement du même montant. Il en sort : **rapprochée**, **montant différent** (`clictopay:amount:{paiement}`, écart signé), **absente du relevé** (`clictopay:missing:{paiement}`), **ligne en trop** (`clictopay:extra:{ligne}`). Sans relevé pour le mois, seuls les signalements comptent.

Le relevé entre de deux façons, jamais pour un jour d'un mois clos :

- **import d'un fichier** par le port `IMerchantStatementImporter` (un importeur par format, `Format` et `Read(byte[])`). `TODO(spec)` : l'importeur du relevé de SMT. En attendant, le **CSV générique** `generic-csv` : UTF-8 (BOM accepté), une ligne d'en-tête, séparateur `;` ou `,` (celui de l'en-tête), champs entre guillemets au besoin. Colonnes par nom (casse et accents ignorés, dans n'importe quel ordre) :

  | Colonne | Alias | Contenu |
  |---|---|---|
  | `date` (obligatoire) | `transaction_date`, `date_transaction` | `yyyy-MM-dd` ou `dd/MM/yyyy` |
  | `reference` (obligatoire) | `order`, `order_number`, `orderid`, `order_id`, `commande` | numéro de commande ou identifiant Clic to Pay |
  | `type` (obligatoire) | `kind`, `sens` | `payment`, `paiement`, `P`, `debit` ou `refund`, `remboursement`, `R`, `credit` |
  | `amount` (obligatoire) | `montant` | positif, point ou virgule, millimes au plus |
  | `fee` | `commission`, `frais` | commission de la banque sur la ligne |
  | `settlement_date` | `settled_on`, `date_remise`, `remise` | jour où la banque a réglé la ligne |
  | `note` | `libelle`, `label` | texte libre |

  Tout le fichier est lu d'abord (2 Mo, 5 000 lignes au plus) : une seule ligne illisible et rien n'est enregistré, l'écran donne les numéros des lignes en cause ;
- **saisie d'une ligne** relevée sur le portail du commerçant.

Un relevé importé par erreur se supprime tant qu'aucun de ses jours n'est dans un mois clos. Les lignes réglées (`settlement_date`) font les **remises Clic to Pay** du journal de banque ([ADR-0065](0065-plan-de-comptes-journaux-exports-et-cloture.md)).

### Booking.com

Pas de second écran : les règlements mensuels calculés par le rapprochement des canaux (lot 5A, `ChannelReconciliation`) arrivent par le contrat `IChannelPayouts` — réservations du mois de prise en charge, brut, commission, net, prépayé, commissions des réservations payées sur place, **attendu** (net prépayé − commissions payées sur place, `TODO(spec)` : le contrat du canal) et versement saisi. Écarts : **versement différent de l'attendu** (`channel:payout:{canal}`) et **versement manquant** pour un mois où le canal doit payer (`channel:missing:{canal}`). Le versement se saisit toujours dans l'écran des canaux. `TODO(spec)` : l'import du relevé des versements de Booking.com.

### Justification des écarts

- Un écart est identifié par sa **clé** (ci-dessus) et son **montant actuel**. Le justifier enregistre une **justification** (motif de 5 à 500 caractères, montant justifié, utilisateur, instant) dans `accounting.Justifications` — on n'en modifie ni n'en supprime jamais.
- Un écart est justifié quand une justification porte **son montant actuel** : s'il change (un nouveau comptage, une ligne de relevé corrigée), il redevient à justifier et les anciennes justifications restent visibles sous la nouvelle.
- **Un écart non justifié bloque la clôture du mois** ([ADR-0065](0065-plan-de-comptes-journaux-exports-et-cloture.md)).

## Conséquences

- Le comptable voit en un écran ce qui empêche de clore : sessions non verrouillées (dans la clôture), écarts de caisse, paiements en ligne sans règlement, versements Booking.com.
- Tant que le format de SMT n'est pas connu, le relevé se convertit au CSV générique (tableur) ou se saisit ligne à ligne ; l'importeur de SMT s'ajoutera sans toucher au rapprochement.
- Les écarts justifiés ne corrigent rien en comptabilité : un écart compté est déjà passé en différence de caisse, un versement différent reste au compte d'attente `471` jusqu'à sa régularisation par l'expert-comptable.

## Alternatives écartées

- **Deviner le format du relevé de SMT** : interdit (« ne jamais inventer un format de partenaire ») ; un import faux serait pire que la saisie.
- **Rapprocher les versements Booking.com dans un écran de plus** : il existe déjà (lot 5A) ; le dupliquer ferait deux vérités.
- **Valider un écart par une case à cocher** : sans motif ni montant, on ne saurait ni pourquoi, ni s'il a changé depuis.
