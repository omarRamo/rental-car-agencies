# ADR-0051 — « Ma réservation » : accès par lien et code, session limitée au dossier, liaison dossier ↔ paiement en ligne

- **Statut** : Acceptée
- **Date** : 2026-10-21
- **Source** : [spécifications](../SPEC.md) §9 (validation, capture, repli par lien, annulation et remboursement) ; [prompt](../PROMPT.md) §6 ; [conception phase 2](../conception/phase-2-vitrine-paiement.md) §1 et §2 ; [ADR-0039](0039-paiement-en-ligne-confirmation-idempotence-rattrapage.md) (contrat `IOnlinePayments`, message `OnlinePaymentStatusChanged`), [ADR-0049](0049-tunnel-sessions-devis-figes-garantie-de-prix.md), [ADR-0050](0050-soumissions-publiques-et-donnees-personnelles.md)

## Contexte

Les clients n'ont pas de compte. Ils doivent suivre leur demande, envoyer les pièces manquantes, payer le solde, annuler et retrouver contrat et factures, depuis un téléphone, souvent à l'étranger. L'accès ne doit rien révéler (un numéro de réservation est séquentiel), résister aux robots et aux scanners de liens, et ne jamais ouvrir plus d'un dossier. Côté agence, la validation d'une demande web doit encaisser la garantie, et la fin d'une demande la libérer, sans jamais appeler la passerelle dans une transaction.

## Décision

### Accès

- **Demande d'accès** (`/fr/ma-reservation`, `/en/my-booking`) : adresse e-mail et numéro de réservation, Turnstile `my_booking_access`, limite `public-submit`. **La même réponse dans tous les cas** (page « si une réservation correspond, un e-mail vient d'être envoyé ») ; quand le numéro désigne un dossier dont un contact a cette adresse (conducteur principal ou second conducteur tels que saisis avec la demande, ou payeur), un e-mail part avec un **lien à usage unique** et un **code à 6 chiffres**, valables `bookings.my_booking_link_validity` (30 min). Au plus `bookings.my_booking_requests_per_hour` (5) e-mails par dossier et par heure : au-delà, la demande est traitée comme sans correspondance (pas de bombardement d'e-mails).
- Secrets : jeton de 256 bits haché (SHA-256), code haché avec un sel aléatoire, comparaisons en temps constant. **`bookings.my_booking_code_attempts` (5) codes erronés verrouillent la demande** — le lien aussi — et **une demande sans correspondance se comporte exactement de même** (codes comparés, tentatives comptées, verrouillage) : les réponses ne disent jamais si une réservation existe.
- Le **lien** mène à une page qui demande un clic (POST) : le GET des scanners de messagerie et des aperçus de liens ne le consomme pas.
- **Session** : cookie Data Protection `carrental.mybooking` (chiffré, signé, daté) qui nomme la demande et son dossier, `HttpOnly`, `Secure` (sauf en développement et en tests sur http), `SameSite=Lax`, chemin `/`. **Vérifiée sur le serveur à chaque requête** (demande révoquée par la déconnexion, autre dossier, fin dépassée) et glissante (`bookings.my_booking_session_idle`, 2 h), **24 h au plus** après l'ouverture. Elle ne donne accès qu'à son dossier ; les écritures s'y font comme acteur système `system:my-booking`, après cette vérification.
- **Lien « Compléter mon dossier »** (`bookings.complete_file_link_validity`, 24 h), sans code : envoyé quand l'agent demande des informations, ou par l'agent depuis le dossier (réservations par téléphone ou au comptoir) ; il ouvre la section des documents.

### Page du dossier

- Statut et explication, chronologie publique (sans les étapes internes : affectations, escalades), informations demandées (message de l'agent et « J'ai envoyé ce qui était demandé » → `ProvideInformationAsync`), **photos par conducteur** (soumissions en attente pour l'agent, ADR-0050), **solde payé en ligne** (`StartPaymentAsync` en une étape, jamais un lien ; une page ouverte du même montant est réutilisée ; retour sur la page avec l'état lu serveur à serveur), **annulation** sur une page qui montre d'abord les frais de la politique de l'agence (gratuite jusqu'à `bookings.cancellation_free_until_days` avant le départ, puis `bookings.cancellation_fee_percent` du total, pour une réservation confirmée) : les frais affichés sont renvoyés avec la confirmation et doivent être encore ceux de la politique (sinon la page les montre de nouveau). Ce qui a été payé en ligne, moins les frais, est remboursé en ligne (`RefundAsync`, paiements les plus récents d'abord) ; **au-delà du seuil d'approbation, le remboursement attend un gérant**, ce que la page et l'e-mail disent ; le reste est réglé par l'agence. Une pré-autorisation encore tenue est libérée par l'outbox.
- **Documents** : contrat et factures fournis par `ICustomerDocumentProvider` (paquet 1F-b, phase 3), liste et téléchargement pour le dossier de la session seulement ; sans fournisseur, « disponibles à partir du départ ».

### Notifications

- Message de contrat `BookingCustomerNotification` (outbox, identifiant d'idempotence) : demande reçue, informations demandées (avec le lien « Compléter mon dossier »), confirmée, refusée (**générique, jamais la raison**), expirée, lien de paiement, annulation (frais et remboursement), lien « Compléter mon dossier ». Envoyé seulement pour un dossier qui a un profil client (tunnel, ou lien envoyé par l'agent), dans sa langue.
- L'hôte web compose les e-mails (`ICustomerEmailComposer`) avec un gabarit Razor rendu par `HtmlRenderer` et une version texte du même contenu, liens absolus (`PublicSite:BaseUrl`, sinon l'adresse du site utilisée par le client) ; l'e-mail est mis dans l'outbox avec la marque de traitement de la notification (exactement une fois). Le canal WhatsApp (phase 4) s'abonnera au même message, avec le consentement du client.

### Liaison dossier ↔ paiement en ligne

- **Validation** (`ValidateAsync`) : la pré-autorisation est **capturée avant la transition, hors unité de travail** (`CaptureBookingAsync`). Capture non confirmée → la demande reste en attente (`booking.validation.capture_unconfirmed`) ; refusée par la banque → l'écran propose « Valider avec un lien de paiement » (`booking.validation.capture_rejected`) ; sans pré-autorisation (garantie jamais payée, passerelle indisponible ou mode `bookings.validation_payment_mode = PaymentLinkAfterValidation`) → `AwaitingPayment`.
- Gestionnaire idempotent de `BookingStatusChanged` : refus, expiration, annulation → `ReverseBookingAsync` (relancé par l'outbox tant qu'un montant reste tenu) et soumissions en attente écartées ; `AwaitingPayment` → **lien de paiement** (réutilisé s'il est ouvert) valable jusqu'à l'échéance du paiement, et notification au client.
- Gestionnaire idempotent de `OnlinePaymentStatusChanged` (jamais les événements du domaine Paiements) : l'**état de paiement** du dossier est recalculé à partir de tous ses paiements (`BookingPaymentTrack` : en attente du client, pré-autorisé, échoué, expiré, encaissé, libéré, remboursé ; un paiement fait après son expiration est signalé) ; un lien ou un solde encaissé pour un dossier `AwaitingPayment` le confirme (`IBookingPaymentStatus.MarkPaidAsync`). La liste de contrôle de l'agent montre cet état (« Paiement en ligne (Clic to Pay) »).

## Conséquences

- 1F-b enregistre un `ICustomerDocumentProvider` pour le contrat signé ; la phase 3 un autre pour les factures et avoirs ; la phase 4 s'abonne à `BookingCustomerNotification` pour WhatsApp et lit l'accord WhatsApp du profil client.
- Un client qui a perdu son e-mail redemande un lien ; un client dont l'adresse a changé passe par l'agence (aucune modification de contact depuis la page).

## Alternatives écartées

- **Comptes clients avec mot de passe** : une inscription pour une location par an, des mots de passe réutilisés à protéger.
- **Lien seul, sans code** : inutilisable quand l'e-mail est lu sur un autre appareil que le navigateur.
- **Lien consommé par le GET** : les scanners de messagerie le grilleraient avant le client.
- **Message « aucune réservation ne correspond »** : permettrait d'énumérer les numéros et les adresses.
- **Session valable pour toutes les réservations de l'adresse** : un lien volé ouvrirait tout l'historique du client.
