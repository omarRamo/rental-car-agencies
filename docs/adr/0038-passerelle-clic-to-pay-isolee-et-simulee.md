# ADR-0038 — Passerelle Clic to Pay : port isolé, client REST défensif, journal des appels et passerelle simulée

- **Statut** : Acceptée
- **Date** : 2026-10-21
- **Source** : [spécifications](../SPEC.md) §9 (opérations, environnement de test, millimes, statuts 0/1/2, flux web, rattrapage, rapprochement, repli, prérequis) ; [prompt](../PROMPT.md) §6 invariant 7, §9.1 et §13 (`FakeClicToPay`) ; [conception phase 2](../conception/phase-2-vitrine-paiement.md) §2 ; [ADR-0007](0007-paiement-clic-to-pay-pre-autorisation.md), [ADR-0034](0034-paiements-et-cautions.md) ; [points ouverts](../points-ouverts.md) (Clic to Pay : montants, statuts, pré-autorisation, cartes étrangères)

## Contexte

Les seules sources sur l'API de Clic to Pay (STB / SMT, plateforme BPC) sont un SDK public non officiel et la documentation de BPC, résumés par SPEC §9 : six opérations REST sous `https://test.clictopay.com/payment/rest/`, identifiants `userName` / `password`, montant dans l'unité minimale, numéro de commande, adresses de retour et d'échec, langue, `orderId` et `formUrl` en réponse d'un enregistrement, statuts 0, 1 et 2. Le reste (codes de statut au-delà de 2, champs détaillés, durée de session, devise, format des erreurs) n'est pas confirmé ; aucun accès réseau à Clic to Pay n'est disponible pendant le développement, et le prompt interdit d'inventer un format. Le paiement doit pourtant être développé, testé de bout en bout et prêt pour la recette.

## Décision

### Un port, trois implémentations

- **`IClicToPayGateway`** (module Paiements, `Application/Payments/Online`) : `RegisterAsync` (`register.do`), `RegisterPreAuthAsync` (`registerPreAuth.do`), `DepositAsync` (`deposit.do`), `ReverseAsync` (`reverse.do`), `RefundAsync` (`refund.do`), `GetOrderStatusExtendedAsync` (`getOrderStatusExtended.do`). Montants en `Money` (TND), résultats `Result` avec des erreurs stables : `payments.gateway.unavailable` (aucune réponse exploitable : la demande a pu être traitée ou non), `payments.gateway.rejected` (code d'erreur de la passerelle, gardé dans le journal), `payments.gateway.invalid_response`, `payments.gateway.amount_not_representable`, `payments.gateway.disabled`. La lecture de statut renvoie un état classé (`Registered`, `PreAuthorized`, `Deposited`, `Reversed`, `Refunded`, `Declined`, `Unknown`) et les champs utiles bruts (code, montants, `actionCode`), jamais de donnée de carte.
- **`ClicToPayClient`** (Infrastructure) : client HTTP typé sur `ClicToPay:BaseUrl`.
- **`FakeClicToPay`** (Application, simulation) : passerelle en mémoire pour le développement, les tests et l'E2E (ci-dessous).
- **`DisabledClicToPayGateway`** : `ClicToPay:Enabled` faux (valeur par défaut) — tout appel est refusé, rien n'est envoyé.
- Le choix se fait par configuration à la résolution (`Enabled`, `UseFake`) ; **`UseFake` est refusé au démarrage hors Développement et Test** (la simulation accepte n'importe quel paiement).

### Options `ClicToPay` (validées au démarrage)

| Clé | Défaut | Rôle |
|---|---|---|
| `Enabled` | `false` | Paiement en ligne disponible |
| `UseFake` | `false` | Passerelle simulée (Développement, Test seulement) |
| `BaseUrl` | `https://test.clictopay.com/payment/rest/` | Racine de l'API (HTTPS, se termine par `/`) ; URL de production fournie par SMT |
| `UserName`, `Password` | — | Identifiants de l'API ; sur Azure, secrets Key Vault `ClicToPay--UserName` et `ClicToPay--Password` ; jamais journalisés ni stockés |
| `AmountMultiplier` | `1000` | Unités minimales par dinar (millimes) — `TODO(spec)` : paiement test de 1,000 TND ; 1, 10, 100 ou 1000 |
| `Currency` | vide | Code ISO 4217 numérique (`788`) envoyé seulement si SMT le demande — `TODO(spec)` |
| `Language` | `fr` | Langue par défaut de la page de paiement |
| `ReturnUrl`, `FailUrl` | `/payments/clictopay/return`, `/payments/clictopay/fail` | Adresses de retour ; avec la vraie passerelle, adresses HTTPS absolues (complétées par `PublicSite:BaseUrl` quand elles sont relatives) ; notre numéro de commande y est ajouté (`order`) |
| `TimeoutSeconds` | `30` | Délai d'un appel |
| `StatusReadAttempts`, `StatusRetryDelayMilliseconds` | `3`, `500` | Nouvelles tentatives des seules lectures de statut |
| `SendSessionTimeout` | `false` | Envoie la validité d'un lien en `sessionTimeoutSecs` (paramètre BPC) — `TODO(spec)` : à activer une fois confirmé |
| `ReversedStatusCodes`, `RefundedStatusCodes`, `DeclinedStatusCodes` | vides | Codes de statut au-delà de 0, 1, 2 — `TODO(spec)` : documentation SMT ; vides, un code inconnu n'est jamais un succès |

### Format d'échange (ce que décrit SPEC §9, `TODO(spec)` pour le reste)

- **POST `application/x-www-form-urlencoded`** sur `{BaseUrl}{opération}.do`, identifiants dans le corps (jamais dans une adresse, donc jamais dans un journal de proxy) ; enregistrement : `orderNumber`, `amount`, `returnUrl`, `failUrl`, `language` (+ `currency`, `description`, `sessionTimeoutSecs` seulement s'ils sont configurés ou fournis) ; capture et remboursement : `orderId`, `amount` ; annulation : `orderId` ; statut : `orderId` (ou `orderNumber` tant que l'identifiant de la passerelle est inconnu).
- **Montant** : `Money` (déjà arrondi au millime, `AwayFromZero`) × multiplicateur, **jamais arrondi à nouveau** : un montant que le multiplicateur ne peut pas exprimer exactement est refusé avant tout appel (`payments.gateway.amount_not_representable`), plutôt que de prélever un montant différent.
- **Réponses lues défensivement** : JSON objet seulement ; noms de champs insensibles à la casse ; nombres en nombre ou en texte ; champs inconnus ignorés, champs manquants à `null` ; `errorCode` absent ou `0` = succès, autre valeur = refus ; un enregistrement sans `orderId` ou sans `formUrl` HTTPS est une réponse invalide ; une lecture de statut sans `orderStatus` aussi. Les montants détaillés (`paymentAmountInfo.approvedAmount`, `depositedAmount`, `refundedAmount`) sont lus quand ils existent (`TODO(spec)`) ; `cardAuthInfo`, `bankInfo` et le reste ne sont jamais lus.
- **Statuts** : 0 enregistrée, 1 pré-autorisée, 2 débitée (SPEC §9) ; les autres codes ne sont reconnus qu'une fois configurés (`ClicToPayStatusMap`, codes entiers, jamais 0/1/2, chacun dans un seul état) ; sinon `Unknown`, traité de façon conservatrice ([ADR-0039](0039-paiement-en-ligne-confirmation-idempotence-rattrapage.md)).

### Résilience

- Délai par appel (`TimeoutSeconds`) ; réponse lue avec une limite de 256 Kio.
- **Seule la lecture de statut est rejouée** (idempotente) : sur une panne réseau, un délai dépassé ou un HTTP 5xx / 408 / 429, jusqu'à `StatusReadAttempts` essais. Un enregistrement, une capture, une annulation ou un remboursement n'est **jamais renvoyé à l'aveugle** : l'appelant lit d'abord le statut (ADR-0039).
- Une erreur HTTP 4xx (adresse fausse, configuration) est une réponse invalide.

### Journal des appels

- Table `payments.GatewayCallLogs`, ajout seul, exclue du journal d'audit ligne à ligne (c'est elle-même un journal) : opération, numéro de commande, paiement et remboursement concernés, identifiant de commande de la passerelle, montant, résultat (`Succeeded`, `GatewayError`, `Unavailable`, `InvalidResponse`), statut HTTP, code et message d'erreur de la passerelle (tronqué à 200 caractères), code de statut et `actionCode` d'une lecture, durée, horodatage, corrélation. **Ni identifiants, ni donnée de carte, ni réponse brute.**
- Écrit **dans sa propre portée DI et son propre `SaveChanges`** (`EfGatewayCallLog`), hors de la transaction de l'appelant : l'appel reste tracé même si le changement métier qui suit échoue ; un échec d'écriture du journal est lui-même journalisé et ne casse jamais le paiement.
- Journaux applicatifs, traces OpenTelemetry (activité `clictopay {opération}`) et compteur `carrental.clictopay.calls` (opération, résultat) portent les mêmes champs non sensibles.

### Passerelle simulée

- `FakeClicToPay` (singleton en mémoire) : enregistrement en une ou deux étapes, capture au plus du montant pré-autorisé, annulation d'une pré-autorisation, remboursements au plus du débité, lecture de statut ; elle utilise pour la simulation les codes BPC 3 (annulée), 4 (remboursée) et 6 (refusée), configurés en Développement et en Test.
- Sa page de paiement est une page de l'application, **`/dev/clictopay/pay/{orderId}`, cartographiée seulement en Développement et en Test avec `UseFake`** : montant, numéro de commande, boutons « Payer », « Refuser », « Abandonner », puis redirection vers l'adresse de retour ou d'échec de la commande avec `orderId`, comme la vraie page. `/dev/clictopay/start` démarre un paiement comme le fera le tunnel. Aucune carte n'est demandée.
- Réglages de test : défauts par opération et par condition (pas de réponse, réponse perdue après traitement, code d'erreur, réponse invalide), lectures de statut en retard (`HoldStatus`), réponses sans montants détaillés, changement fait hors plateforme (`OverrideStatus`). Elle sert aux tests applicatifs, d'intégration et E2E, et à la démonstration en développement.
- La page publique `/{culture}/paiement/{id}` (`/en/payment/{id}`) montre le résultat confirmé par le serveur (montant et statut seulement) : arrivée des liens de paiement et des pages de développement.

## Conséquences

- La recette dans l'environnement de test de Clic to Pay ([liste de contrôle](../recette-clictopay.md)) confirme ou corrige chaque `TODO(spec)` par configuration, sans changement de code : multiplicateur, codes de statut, devise, durée de session, champs détaillés.
- En production : `ClicToPay:Enabled=true`, `UseFake=false`, `BaseUrl` de production, secrets Key Vault, `PublicSite:BaseUrl` (adresses de retour absolues).
- La page de paiement réelle est sur un autre domaine que le site : un formulaire du site qui répondrait par une redirection 302 vers `formUrl` serait bloqué par la directive CSP `form-action 'self'` (Chromium l'applique aux redirections d'un envoi de formulaire). Le tunnel affiche donc une page intermédiaire avec un lien vers `RedirectUrl`, ou l'origine de la page de paiement est ajoutée à `form-action` (à vérifier en recette).
- Les pages `/dev/clictopay/*` n'existent dans aucun autre environnement ; leur présence dépend aussi de `UseFake`.

## Alternatives écartées

- **Formats complets repris du SDK ou de BPC par défaut** (codes 3, 4, 6, `sessionTimeoutSecs`, `currency`) : contraire au prompt ; ils restent configurables et désactivés jusqu'à confirmation.
- **Politique de nouvelles tentatives générique** (Polly, `Microsoft.Extensions.Http.Resilience`) sur le client : un renvoi automatique d'une capture ou d'un remboursement peut débiter ou rembourser deux fois ; la boucle explicite ne vise que les lectures.
- **WireMock.Net** pour les tests HTTP : un serveur Kestrel minimal dans le projet de tests suffit, sans dépendance de plus.
- **Passerelle simulée dans l'Infrastructure** : les tests applicatifs ne pourraient pas l'utiliser ; c'est une simulation pure, sans dépendance technique.
- **Journal des appels dans la transaction métier** : un appel suivi d'un échec de sauvegarde disparaîtrait du journal.
- **Réponse brute stockée pour le diagnostic** : elle contient des données de carte (`cardAuthInfo`) ; seuls des champs structurés et non sensibles sont gardés.
