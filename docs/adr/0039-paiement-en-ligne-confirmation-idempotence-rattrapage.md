# ADR-0039 — Paiement en ligne : confirmation serveur à serveur, idempotence par commande, rattrapage et remboursements validés

- **Statut** : Acceptée
- **Date** : 2026-10-21
- **Source** : [spécifications](../SPEC.md) §9 (flux web, tâche de 15 minutes, rapprochement quotidien, repli, remboursement validé par le gérant au-delà d'un seuil) ; [prompt](../PROMPT.md) §6 invariant 7 et §9.1 ; [conception phase 2](../conception/phase-2-vitrine-paiement.md) §2 ; [ADR-0017](0017-execution-cas-usage-portee-transactions.md) (transactions rejouées, effets externes), [ADR-0034](0034-paiements-et-cautions.md) (`StartOnline`, `ApplyConfirmedGatewayStatus`, `TODO(spec)` du remboursement en ligne), [ADR-0038](0038-passerelle-clic-to-pay-isolee-et-simulee.md)

## Contexte

Invariant 7 : un paiement n'est « pré-autorisé » ou « encaissé » qu'après confirmation serveur à serveur (`getOrderStatusExtended.do`), et les traitements sont idempotents par numéro de commande. Le navigateur peut revenir plusieurs fois, jamais, ou avec une adresse falsifiée ; un appel à la passerelle peut aboutir sans que sa réponse arrive ; le client attend la page de paiement de façon synchrone (pas d'outbox possible pour l'enregistrement) ; une transaction SQL peut être rejouée en entier après une panne passagère (ADR-0017) ; la passerelle ne documente que trois codes de statut. Les dossiers (module Réservations, phase 2C) appellent ces flux à la soumission, au retour, à la validation, au refus et à l'expiration.

## Décision

### Commande, tentatives, transactions

- **Une tentative = un paiement** (`Payment.StartOnline`, méthode Clic to Pay, `Pending`) ; **son identifiant en 32 caractères hexadécimaux est le numéro de commande** envoyé à la passerelle. Le paiement garde le flux (pré-autorisation ou une étape), les chemins locaux de retour et d'échec (validés : chemin du site, jamais une adresse absolue ni `//hôte` — pas de redirection ouverte ; jeton `{paymentId}` remplacé au retour), la validité, la page de paiement (`formUrl`) et la commande de la passerelle (référence externe).
- **Double soumission** : une nouvelle demande pour le même dossier, le même montant et les mêmes chemins, dans les dix minutes, renvoie la même page sans nouvelle commande ; au-delà (session de la passerelle peut-être expirée), une nouvelle tentative.
- **Jamais d'appel à la passerelle dans une transaction** : enregistrement du paiement (ou de l'opération demandée) dans une unité de travail, appel hors transaction, puis application de la réponse dans une seconde unité de travail. Un rejeu de transaction ne refait donc jamais un appel. `IOnlinePayments` lève `InvalidOperationException` s'il est appelé dans une unité de travail (verrou de capacité tenu pendant un appel réseau sinon).
- **Concurrence** : chaque application est protégée par la `rowversion` du paiement et rejouée (trois fois) après un conflit ; le domaine accepte la répétition du statut courant sans rien changer : retours simultanés, tâche et écran appliquent une transition une seule fois (vérifié sur SQL Server).

### Ce qui change le statut (toujours par `ApplyConfirmedGatewayStatus`)

- **Lecture de statut** interprétée de façon conservatrice : 0 → enregistrée (rien ne change, ou **expirée** une fois la validité passée) ; 1 → pré-autorisée (montant approuvé, sinon montant de la commande, qui doit égaler le montant demandé) ; 2 → débitée (montant débité rapporté, sinon la capture que nous avons demandée — seul débit possible de la commande —, sinon le montant de la commande pour un paiement en une étape) ; codes configurés → annulée, remboursée (traitée comme débitée), refusée ; **tout autre code ne confirme rien** : le paiement reste tel quel, signalé « statut inconnu ».
- Réponses serveur à serveur autres qu'une lecture, admises parce qu'aucun argent n'est pris : **enregistrement refusé** par la passerelle → échoué ; tentative **jamais remise au client** (aucune page enregistrée) et validité passée → expirée même si la passerelle ne la connaît pas (personne n'a pu la payer) ; **annulation acquittée** (`reverse.do` sans erreur) suivie d'un statut ni pré-autorisé ni débité → annulée.
- Une commande **expirée puis payée** (paiement tardif) est quand même enregistrée (pré-autorisée ou encaissée) et signalée : l'argent existe, le dossier est à revoir ; une commande **annulée par `reverse.do` n'est jamais rouverte** (incohérence signalée).

### Retour du navigateur

- `GET /payments/clictopay/return` et `/fail`, anonymes : les seuls paramètres lus (`order`, notre numéro ; à défaut `orderId`, celui de la passerelle) **identifient la commande, rien de plus** ; un `orderId` qui contredit la commande enregistrée est ignoré (journalisé). Le statut est lu serveur à serveur, sauf pour un paiement déjà réglé dans un état confirmé (un rechargement ne coûte aucun appel).
- Redirection vers le chemin enregistré : retour si pré-autorisé ou encaissé, échec si échoué ou annulé, sinon le chemin de l'adresse empruntée ; commande inconnue → accueil. `Cache-Control: no-store`.

### Capture, annulation, remboursement

- **Capture** (validation) : demandée sur le paiement avant l'appel (opération en cours, montant au plus le pré-autorisé), acquittée par `deposit.do`, **confirmée seulement par une lecture montrant le débit** ; sinon `payments.online.capture_unconfirmed` et la tâche confirme plus tard. Réponse perdue : la lecture qui suit montre le débit — jamais de second `deposit.do`. Sans réponse et toujours pré-autorisée : nouvel envoi permis seulement après deux minutes ; la tâche abandonne une capture restée sans effet et la signale (la validation est à refaire). Refus de la passerelle : opération abandonnée, signalée.
- **Annulation** (refus, expiration du dossier) : `reverse.do` puis lecture ; avant que le client ait payé, **l'annulation est enregistrée** et la tâche l'envoie dès qu'une pré-autorisation apparaît (sinon la tentative expire) ; une annulation laissée sans réponse est renvoyée par la tâche (sans danger : annuler deux fois est refusé par la passerelle, signalé). Un paiement encaissé ne s'annule pas : il se rembourse.
- **Remboursement en ligne** (`refund.do`) : un remboursement `Pending` (méthode Clic to Pay) dont le montant est **réservé sur le paiement d'origine** (`PendingRefundAmount`, déduit du remboursable, y compris pour un remboursement au comptoir) jusqu'à confirmation ou libération. **Au-delà de `payments.online_refund_approval_threshold`** (100,000 TND par défaut, remboursements du paiement cumulés : fractionner ne contourne pas le seuil), il attend la **validation d'un gérant** — permission `payments.refunds.approve` (préréglage Gérant) ; un gérant qui rembourse lui-même valide d'office (tracé) ; **l'acteur système ne valide jamais**. Confirmé par le montant remboursé que rapporte la passerelle quand elle le donne (`TODO(spec)`), sinon par l'acquittement de `refund.do`, sauf statut incompatible ; refusé → échoué et montant libéré ; rejeté par le gérant → annulé et libéré ; sans confirmation après 24 h → signalé (vérification dans le portail marchand), jamais renvoyé.

### Rattrapage, expiration, rapprochement

- **Tâche `payments.online_catch_up`** (toutes les 15 minutes, heure de Tunis) : paiements en ligne enregistrés depuis plus de 2 minutes (le retour du navigateur d'abord) et moins de 30 jours, encore en attente ou avec une opération en cours, et remboursements envoyés non confirmés : lecture et application du statut, expiration des commandes impayées et des liens échus (`TODO(spec)` : aucune opération connue pour annuler une commande impayée chez la passerelle), envoi des annulations en attente, abandon ou signalement des opérations non confirmées. Chaque paiement est traité seul.
- **Validité** : `payments.online_attempt_validity` (1 h) pour une tentative du navigateur, `payments.payment_link_validity` (24 h) pour un lien ; l'appelant peut la préciser (au plus 30 jours).
- **Tâche `payments.online_reconciliation`** (chaque jour à 04 h 30) : relit les paiements en ligne réglés des 7 derniers jours, compare statut, montant débité et montant remboursé rapportés, et signale les écarts. `TODO(spec)` : import du relevé marchand de SMT (format inconnu).
- **Signalements** (`GatewayAttention`) : statut inconnu, statut ou montant différent, paiement tardif, capture, annulation ou remboursement non confirmé, opération refusée ; affichés dans `/admin/payments` (onglet « Paiements en ligne », détail avec l'historique des appels) ; un statut inconnu devenu connu et une opération finalement confirmée effacent leur signalement ; les autres sont effacés par « Marquer comme vérifié ».

## Conséquences

- Contrat pour le tunnel (2C) : `StartPreAuthorizationAsync` après la soumission validée (hors transaction), redirection par une page intermédiaire vers `RedirectUrl` ; `HandleReturnAsync` est déjà branché sur les adresses de retour ; `CaptureBookingAsync` à la validation **avant** la transition (un échec laisse la demande en attente avec l'erreur) ; `ReverseBookingAsync` depuis un gestionnaire d'outbox idempotent du refus, de l'expiration ou de l'annulation ; `StartPaymentAsync` (lien ou solde) en mode de repli, `PaymentStatusChanged` et `OnlinePaymentExpired` pour suivre le paiement.
- Les paiements en attente de codes SMT restent visibles et signalés au lieu d'être pris pour des succès ou des échecs.
- Les événements de domaine `PaymentStatusChanged`, `OnlineRefundRequested` (validation à demander) et `OnlinePaymentExpired` vont à l'outbox ; aucun gestionnaire n'est livré ici (e-mails, relances : lots suivants).

## Alternatives écartées

- **Croire les paramètres de l'adresse de retour** : contraire à l'invariant 7.
- **Enregistrement par l'outbox** : le client attend sa page de paiement.
- **Appel à la passerelle dans la transaction** : un rejeu après une panne SQL referait l'appel ; le verrou de capacité resterait tenu pendant l'appel réseau.
- **Renvoyer une capture ou un remboursement après un délai dépassé** : risque de double débit ou de double remboursement ; on lit d'abord le statut.
- **Agrégat séparé pour la commande de la passerelle** : deux agrégats à garder cohérents (opération en cours, montants, statut) ; tout est sur le paiement, protégé par sa `rowversion`.
- **Expirer une tentative au statut inconnu** : un code non documenté pourrait cacher un paiement ; on attend une lecture « enregistrée (0) » ou la configuration des codes.
- **Seuil de validation par remboursement isolé** : contournable en fractionnant.
