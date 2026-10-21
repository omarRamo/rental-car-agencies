# Recette Clic to Pay — liste de contrôle

Liste des essais à faire **dans l'environnement de test de Clic to Pay** (`https://test.clictopay.com/payment/rest/`) avant la mise en production du paiement en ligne. Elle confirme ou corrige les points marqués `TODO(spec)` dans le code ([ADR-0038](adr/0038-passerelle-clic-to-pay-isolee-et-simulee.md), [ADR-0039](adr/0039-paiement-en-ligne-confirmation-idempotence-rattrapage.md)) et ceux de [points-ouverts.md](points-ouverts.md) (montants, statuts, pré-autorisation, cartes étrangères). Les informations d'origine viennent d'un SDK non officiel et de la documentation BPC ([SPEC.md](SPEC.md) §9) : **rien n'est considéré comme acquis tant que cette liste n'est pas remplie.**

## Préparation

| Élément | Valeur attendue | Fait |
|---|---|---|
| Contrat commerçant e-commerce STB avec **paiement en deux étapes** (pré-autorisation) activé | Confirmation écrite de SMT | ⬜ |
| Identifiants de l'API de test | `ClicToPay--UserName`, `ClicToPay--Password` dans le Key Vault de recette (jamais dans le dépôt) | ⬜ |
| Configuration de recette | `ClicToPay:Enabled=true`, `ClicToPay:UseFake=false`, `ClicToPay:BaseUrl=https://test.clictopay.com/payment/rest/`, `PublicSite:BaseUrl` = adresse HTTPS de la recette | ⬜ |
| Documentation officielle SMT | Liste des codes `orderStatus`, des `errorCode` et des `actionCode` ; champs de `getOrderStatusExtended.do` ; paramètres facultatifs (`currency`, `description`, `sessionTimeoutSecs`) | ⬜ |
| Cartes de test | Carte acceptée, carte refusée (fonds insuffisants), carte 3-D Secure, carte étrangère si disponible | ⬜ |
| Suivi | Écran `/admin/payments` → onglet « Paiements en ligne » → détail : statut, code passerelle, historique des appels | ⬜ |

Pour chaque essai : noter la date, la personne, le numéro de commande (32 caractères hexadécimaux, visible dans le détail du paiement) et l'identifiant de commande Clic to Pay, puis le résultat obtenu. Un écart avec le résultat attendu est reporté dans [points-ouverts.md](points-ouverts.md) et corrigé (configuration ou code) avant la production.

## Essais

### R1 — Unité des montants (paiement de 1,000 TND)

- **Objectif** : confirmer `ClicToPay:AmountMultiplier` (1000 = millimes).
- **Étapes** : démarrer un paiement en une étape de **1,000 TND** (lien de paiement depuis le panneau d'un dossier) ; ouvrir la page de paiement ; payer.
- **Attendu** : la page Clic to Pay affiche **1,000 TND** (ni 0,001 ni 1 000) ; le détail montre « Encaissé 1,000 TND » ; l'appel d'enregistrement du journal porte 1,000 TND ; le montant débité lu (`getOrderStatusExtended.do`) vaut 1,000 TND.
- **Si la page affiche 0,010 ou 100** : corriger `AmountMultiplier` (10 ou 100) et refaire R1 ; un montant non exprimable avec ce multiplicateur est alors refusé avant l'envoi.

| Date | Testeur | Commande | Résultat obtenu | Conforme |
|---|---|---|---|---|
| | | | | ⬜ |

### R2 — Pré-autorisation puis capture d'un montant inférieur

- **Étapes** : démarrer une pré-autorisation de 10,000 TND ; payer ; au retour, vérifier « Pré-autorisé » ; dans le détail, « Capturer » 8,500 TND.
- **Attendu** : statut lu 1 puis 2 ; « Encaissé 8,500 TND » ; aucun signalement ; le reste (1,500 TND) est libéré sur la carte (vérifier le relevé de la carte de test) ; le journal montre `registerPreAuth.do`, la lecture, `deposit.do` (8,500) et la lecture finale. Noter si `paymentAmountInfo.depositedAmount` est présent.

| Date | Testeur | Commande | Résultat obtenu (`depositedAmount` présent ?) | Conforme |
|---|---|---|---|---|
| | | | | ⬜ |

### R3 — Pré-autorisation puis annulation (`reverse.do`)

- **Étapes** : pré-autorisation de 10,000 TND payée ; « Annuler la pré-autorisation ».
- **Attendu** : `reverse.do` sans erreur ; le paiement passe « Annulé » ; **noter le code `orderStatus` lu après l'annulation** (3 selon BPC) et le reporter dans `ClicToPay:ReversedStatusCodes` ; la carte de test n'est pas débitée.

| Date | Testeur | Commande | Code de statut après annulation | Conforme |
|---|---|---|---|---|
| | | | | ⬜ |

### R4 — Remboursement partiel puis total (`refund.do`)

- **Étapes** : paiement en une étape de 300,000 TND ; « Rembourser en ligne » 50,000 TND (sous le seuil) ; puis un remboursement de 250,000 TND (au-delà du seuil : validation par un gérant).
- **Attendu** : remboursement partiel confirmé, statut lu toujours 2 ; **noter si `refundedAmount` est rapporté** ; le second attend la validation, puis, validé par un gérant, est confirmé ; le paiement passe « Remboursé » ; **noter le code `orderStatus` d'une commande remboursée en totalité** (4 selon BPC) → `ClicToPay:RefundedStatusCodes` ; vérifier les deux crédits sur la carte de test.

| Date | Testeur | Commande | `refundedAmount` présent ? Code après remboursement total | Conforme |
|---|---|---|---|---|
| | | | | ⬜ |

### R5 — Carte refusée

- **Étapes** : pré-autorisation avec la carte de test refusée (fonds insuffisants), puis avec une carte au cryptogramme faux.
- **Attendu** : retour par l'adresse d'échec ; **noter `orderStatus` et `actionCode`** de chaque refus (6 selon BPC) → `ClicToPay:DeclinedStatusCodes` ; tant que le code n'est pas configuré, le paiement reste « En attente » et signalé « Statut inconnu » (jamais réussi) ; une fois configuré, il passe « Échoué ». Noter si la page de paiement permet plusieurs essais dans la même commande.

| Date | Testeur | Commande | Codes relevés | Conforme |
|---|---|---|---|---|
| | | | | ⬜ |

### R6 — Onglet fermé avant le retour

- **Étapes** : démarrer une pré-autorisation ; payer ; **fermer l'onglet** sur la page de confirmation de Clic to Pay, avant la redirection.
- **Attendu** : dans les 15 minutes (tâche `payments.online_catch_up`), le paiement passe « Pré-autorisé » sans action ; le journal montre une lecture de statut par la tâche (`system:job:payments.online_catch_up`).

| Date | Testeur | Commande | Délai constaté | Conforme |
|---|---|---|---|---|
| | | | | ⬜ |

### R7 — Adresse de retour falsifiée

- **Étapes** : après un paiement **refusé**, rejouer l'adresse de retour en remplaçant `orderId` par celui d'une autre commande payée, puis en ajoutant `&returnUrl=https://exemple.org`.
- **Attendu** : le statut appliqué est celui de la commande enregistrée (refusée), jamais celui de l'URL ; la redirection se fait vers le chemin local enregistré, jamais vers un site extérieur ; un `orderNumber` inconnu ramène à l'accueil. Noter si Clic to Pay **conserve notre paramètre `order`** dans l'adresse de retour et y **ajoute `orderId`**.

| Date | Testeur | Commande | Paramètres reçus au retour | Conforme |
|---|---|---|---|---|
| | | | | ⬜ |

### R8 — Double clic et double soumission

- **Étapes** : cliquer deux fois de suite sur le bouton qui mène au paiement ; revenir en arrière et soumettre de nouveau dans les dix minutes ; recharger plusieurs fois l'adresse de retour ; capturer deux fois de suite.
- **Attendu** : une seule commande chez Clic to Pay pour la même demande (même page de paiement) ; un rechargement du retour ne fait aucune nouvelle lecture une fois le paiement réglé ; une seule capture (`deposit.do`) dans le journal, la seconde renvoie le paiement déjà encaissé.

| Date | Testeur | Commande | Résultat obtenu | Conforme |
|---|---|---|---|---|
| | | | | ⬜ |

### R9 — Délais dépassés et réponses perdues

- **Étapes** : en recette seulement, régler `ClicToPay:TimeoutSeconds=1` puis capturer et rembourser (la réponse de Clic to Pay arrive en général après plus d'une seconde) ; remettre la valeur normale.
- **Attendu** : l'opération est « Sans réponse » dans le journal, puis la lecture de statut qui suit constate la capture ou le remboursement ; **jamais de second débit ou de second remboursement** ; si rien n'est constaté, le paiement reste avec l'opération en cours et la tâche la signale (capture : à refaire ; remboursement : à vérifier dans le portail marchand après 24 h).

| Date | Testeur | Commande | Résultat obtenu | Conforme |
|---|---|---|---|---|
| | | | | ⬜ |

### R10 — Codes de statut au-delà de 0, 1, 2

- **Étapes** : recenser, avec la documentation SMT et les essais R3, R4, R5, R12, tous les `orderStatus` possibles (annulée, remboursée, refusée, authentification 3-D Secure en cours, expirée…).
- **Attendu** : chaque code a un sens documenté ; les codes « annulée », « remboursée », « refusée » sont renseignés dans `ReversedStatusCodes`, `RefundedStatusCodes`, `DeclinedStatusCodes` ; un code non documenté reste « inconnu » (signalé, jamais pris pour un succès).

| Code | Sens selon SMT | Configuration retenue |
|---|---|---|
| 0 | Enregistrée, non payée | (connu) |
| 1 | Pré-autorisée | (connu) |
| 2 | Débitée | (connu) |
| | | |

### R11 — Cartes étrangères

- **Étapes** : payer avec une carte Visa et une carte Mastercard émises hors de Tunisie (clientèle de la diaspora, environ 80 % de la demande estivale).
- **Attendu** : acceptées (ou refus explicite documenté par la banque) ; 3-D Secure fonctionnel ; montant débité en TND, conversion par la banque du porteur.

| Date | Testeur | Carte | Résultat obtenu | Conforme |
|---|---|---|---|---|
| | | | | ⬜ |

### R12 — Durée de vie d'une pré-autorisation et d'une page de paiement

- **Étapes** : demander à SMT la durée de vie d'une pré-autorisation (fixée par la banque émettrice) ; essayer de capturer une pré-autorisation de plus de 7 jours ; ouvrir la page d'un lien de paiement après 30 minutes, 2 heures et 24 heures.
- **Attendu** : durée de pré-autorisation connue (elle borne le blocage de 24 h d'une demande et la caution, qui se prend au comptoir tant qu'elle n'est pas confirmée) ; si la page d'un lien expire avant 24 h, confirmer avec SMT le paramètre `sessionTimeoutSecs` et activer `ClicToPay:SendSessionTimeout` ; noter s'il existe une opération pour **annuler une commande impayée** (sinon le lien expire seulement chez nous) ; si une page reste payable après l'expiration chez nous, payer quand même : le paiement tardif est enregistré (au retour, ou par la tâche dans les 24 h) et signalé « Payé après l'expiration ».

| Date | Testeur | Constat | Décision | Conforme |
|---|---|---|---|---|
| | | | | ⬜ |

### R13 — Paramètres facultatifs et format des réponses

- **Étapes** : enregistrer une commande avec `language=en` et `language=ar`, avec et sans `currency=788`, avec une `description` de 99 caractères.
- **Attendu** : langues acceptées ; devise nécessaire ou non (`ClicToPay:Currency`) ; longueur maximale de la description ; `errorCode` rendu en texte ou en nombre (les deux sont lus) ; liste des codes d'erreur des opérations.

| Date | Testeur | Constat | Configuration retenue | Conforme |
|---|---|---|---|---|
| | | | | ⬜ |

### R14 — Redirection depuis le tunnel (CSP)

- **Étapes** : dans le tunnel de réservation (lot 2C), soumettre l'étape de paiement avec Chrome, Firefox et Safari mobiles.
- **Attendu** : le navigateur arrive sur la page de Clic to Pay (la directive CSP `form-action 'self'` bloque une redirection 302 vers un autre domaine après un envoi de formulaire : page intermédiaire avec lien, ou origine de Clic to Pay ajoutée à `form-action`) ; retour sur la page de confirmation du site.

| Date | Testeur | Navigateur | Résultat obtenu | Conforme |
|---|---|---|---|---|
| | | | | ⬜ |

### R15 — Rapprochement quotidien

- **Étapes** : laisser tourner la tâche `payments.online_reconciliation` (04 h 30) après les essais ; modifier une commande dans le portail marchand (capture ou remboursement fait hors plateforme).
- **Attendu** : les écarts apparaissent signalés dans `/admin/payments` (« Montant différent », « Statut différent ») ; demander à SMT le **format du relevé marchand** (`TODO(spec)` : import non développé).

| Date | Testeur | Constat | Conforme |
|---|---|---|---|
| | | | ⬜ |

## Passage en production

| Élément | Fait |
|---|---|
| Toutes les lignes ci-dessus conformes, écarts reportés et corrigés | ⬜ |
| URL de production et identifiants de production fournis par SMT, secrets `ClicToPay--UserName` / `ClicToPay--Password` dans le Key Vault de production | ⬜ |
| `ClicToPay:BaseUrl` de production, `ClicToPay:Enabled=true`, `ClicToPay:UseFake=false`, codes de statut configurés, `PublicSite:BaseUrl` correct | ⬜ |
| Seuil de validation des remboursements (`payments.online_refund_approval_threshold`) et validités (`payments.payment_link_validity`, `payments.online_attempt_validity`) fixés avec le gérant | ⬜ |
| Un paiement réel de faible montant, puis son remboursement, faits en production | ⬜ |
