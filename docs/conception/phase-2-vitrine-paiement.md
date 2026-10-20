# Conception — Phase 2 (vitrine et paiement)

Contrat commun aux agents de la phase 2. Il précise [SPEC.md](../SPEC.md) §5 et §9 et [PROMPT.md](../PROMPT.md) §9.1, §9.5 et §10 ; en cas de doute, ces documents priment et l'écart va dans [points-ouverts.md](../points-ouverts.md). Il s'appuie sur la phase 1 : dossiers (`IBookingRequests`, `IBookingsQueries`), tarifs (`IPricingService`, `IPricingQueries`), disponibilité (`IAvailabilityQueries`), clients (`ICustomerRegistration`, `ICustomersQueries`), paiements et caisse.

## 1. Parcours public

| Étape | Route (`fr`, `en`) | Contenu | Rendu |
|---|---|---|---|
| Accueil | `/fr`, `/en` | Recherche (lieu de départ, date et heure de départ et de retour, case « retour ailleurs » puis lieu de retour), trois arguments, catégories phares, FAQ courte | statique |
| Résultats | `/fr/reserver`, `/en/book` | Catégories disponibles : photo, places, bagages, boîte, prix total TTC (prix par jour en secondaire, équivalent indicatif en euros) ; filtres boîte automatique et nombre de places ; tri par prix | statique, formulaire GET |
| 1 — Options | `/fr/reserver/{jeton}/options` | Options et rachat de franchise, livraison, récapitulatif recalculé à chaque envoi | statique (formulaire amélioré) |
| 2 — Conducteurs | `/fr/reserver/{jeton}/conducteurs` | Identité, nationalité (CIN ou passeport), naissance, permis, téléphone, e-mail, photos recto-verso, second conducteur, consentements | statique + module JS d'envoi des photos |
| 3 — Paiement | `/fr/reserver/{jeton}/paiement` | Récapitulatif, conditions générales, montant garanti, Turnstile, soumission puis redirection vers Clic to Pay | statique |
| Confirmation | `/fr/reserver/confirmation/{numero}` | « Demande reçue, réponse sous 2 h ouvrées » | statique |
| Ma réservation | `/fr/ma-reservation` | Demande d'accès (e-mail + numéro), puis statut, pièces manquantes, paiement du solde, annulation selon les conditions, contrat et facture PDF | statique |
| Pages fixes | `/fr/agence`, `/fr/faq`, `/fr/conditions`, `/fr/confidentialite` (équivalents `en`) | Contenu en ressources ; textes juridiques `TODO(spec)` (avocat) | statique |

**Lien profond** (SPEC §5) : `/fr/reserver?lieu=TUN&depart=2027-07-01T10:00&retour=2027-07-05T10:00&cat=ECMR&boite=auto&places=5&devis=Q7F3K2&src=whatsapp`, mêmes noms de paramètres dans toutes les langues, plus `utm_*`. `lieu` et `retour_lieu` sont des codes de lieu (Referential), les dates sont en heure de Tunis sans fuseau, `cat` un code ACRISS, `devis` le code d'un devis verrouillé. Le serveur recalcule toujours prix et disponibilité ; un devis verrouillé valable et toujours disponible garantit son prix 24 h.

**Brouillon de parcours** : le choix d'une catégorie crée un dossier `Draft` (aucune capacité consommée) et un devis verrouillé 24 h, identifiés par un **jeton aléatoire** (≥ 128 bits, stocké haché) porté par l'URL des étapes. Le jeton expire avec le devis ; un jeton inconnu ou expiré renvoie à la recherche avec un message. La capacité n'est prise qu'à la soumission (étape 3), sous verrou (`Submit` → `PendingValidation`, blocage 24 h).

**Règles du tunnel** : éligibilité (âge, ancienneté du permis, pièces valides jusqu'au retour, permis international si non latin, CIN pour un Tunisien, passeport sinon) contrôlée à l'étape 2 avec des messages clairs ; liste noire jamais signalée au demandeur (contrôlée par l'agent) ; consentement explicite au traitement des pièces (obligatoire), aux notifications WhatsApp et au marketing (facultatifs, séparés) ; photos JPEG, PNG ou PDF ≤ 10 Mo, compressées dans le navigateur à environ 1 600 px ; tout fonctionne sans JavaScript sauf l'envoi des photos.

**Mesure** : un événement d'entonnoir par étape (`search`, `results`, `options`, `documents`, `payment`, `confirmation`) avec un identifiant de visite anonyme (cookie de première partie, sans donnée personnelle), la source (`src`, `utm_*`), la catégorie et le dossier ; exploité par les indicateurs de la phase 3.

## 2. Paiement Clic to Pay

**Passerelle** : `IClicToPayGateway` (Payments) implémentée par `ClicToPayClient` (client HTTP typé, options `BaseUrl`, `UserName`, `Password`, `AmountMultiplier` = 1000, `Language`, `ReturnUrl`, `FailUrl`) et par `FakeClicToPay` (développement, tests, E2E) qui simule la page hébergée dans l'application (`/dev/clictopay/...`, boutons « Payer » et « Refuser ») puis redirige vers l'URL de retour. Formats issus d'un SDK non officiel : `TODO(spec)` à confirmer en recette ; aucun format inventé au-delà de ce que décrit SPEC §9.

| Moment | Appel | Effet |
|---|---|---|
| Soumission d'une demande web | `registerPreAuth.do` (numéro de commande interne unique, montant garanti) | Paiement `Pending` ; redirection du client |
| Retour du client (URL de retour ou d'échec) | `getOrderStatusExtended.do` | `PreAuthorized`, `Failed` ou toujours `Pending` ; jamais sur la foi des paramètres d'URL |
| Validation par l'agent | `deposit.do` (montant ≤ pré-autorisation) **avant** la transition | `Captured` puis `Confirmed` ; échec ⇒ la demande reste en attente avec l'erreur |
| Refus ou expiration | `reverse.do` (gestionnaire d'outbox idempotent sur l'événement du dossier) | `Cancelled` |
| Mode de repli (`bookings.validation_payment_mode` = lien) | `register.do` valable 24 h après validation | `AwaitingPayment` jusqu'au paiement confirmé, puis `Confirmed` |
| Solde, paiement par lien au téléphone | `register.do` | `Captured` après confirmation serveur |
| Annulation, trop-perçu | `refund.do` (validation du gérant au-delà d'un seuil) | `Refunded` |

Tâche toutes les 15 minutes : relecture des paiements `Pending` de plus de 2 minutes (clients qui ferment l'onglet). Idempotence par numéro de commande ; journal de chaque appel (opération, numéro, référence Clic to Pay, statut HTTP, code d'erreur, durée) sans identifiants ni donnée de carte. Montant garanti par défaut : le total TTC du devis (`TODO(spec)` : politique de garantie de l'agence, acompte ou totalité, par saison et par canal). Liste de contrôle de recette : `docs/recette-clictopay.md`.

## 3. Ma réservation et liens magiques

- Demande d'accès : e-mail + numéro de dossier ⇒ e-mail contenant un lien signé à usage unique (30 min) et un code à 6 chiffres (même durée) ; réponse identique que le dossier existe ou non (pas d'énumération).
- Le lien (ou le code) ouvre une session limitée à ce dossier (cookie chiffré Data Protection, 24 h glissantes au plus, `HttpOnly`, `Secure`, `SameSite=Lax`).
- « Compléter mon dossier » (envoyé par l'agent ou après une demande de complément) : même mécanisme, qui ouvre directement la section des pièces.
- Actions : déposer les pièces manquantes, payer le solde, annuler selon la politique (frais affichés avant confirmation), télécharger contrat et facture quand ils existent.

## 4. Sécurité et performance du site

- Turnstile vérifié côté serveur à la soumission du tunnel et aux demandes de lien magique (clés de test officielles de Cloudflare en développement et en test).
- Limitation de débit (`RateLimiter`, adresse client lue dans `CF-Connecting-IP`) : recherche et devis publics, soumission, liens magiques, envois de photos ; jamais sur `/health*`.
- Les pages publiques sans formulaire sont cachables (pas de jeton antiforgery inutile) ; le tunnel et Ma réservation ne le sont pas.
- SEO : titre et description par page, `canonical`, `hreflang`, `sitemap.xml` (pages publiques × langues), `robots.txt` (exclut le tunnel et Ma réservation), JSON-LD `AutoRental` (agence, adresse, téléphone, horaires).
- Budget : LCP < 2,5 s en 4G simulée, contrôlé par Lighthouse en CI (application démarrée avec les données de démonstration) ; polices système, images dimensionnées et différées sous la ligne de flottaison, aucun framework JS.
- Accessibilité WCAG 2.2 AA (axe dans Playwright), mobile d'abord, largeur 390 px dans les tests E2E.

## 5. Découpage de la phase 2

| Lot | Contenu | Dépend de |
|---|---|---|
| 2A Vitrine | Accueil, recherche et résultats, liens profonds, pages fixes, SEO, limitation de débit, Turnstile (service et faux), événements d'entonnoir, budget Lighthouse en CI | phase 1 |
| 2B Clic to Pay | Passerelle, faux Clic to Pay, flux de paiement du module Payments, tâche de rattrapage, journal des appels, capture à la validation et annulation au refus ou à l'expiration, remboursements, liste de recette | phase 1 |
| 2C Tunnel et Ma réservation | Brouillons et jetons, étapes 1 à 3, envoi des pièces, éligibilité, consentements, soumission et redirection de paiement, confirmation, Ma réservation (liens magiques, pièces, solde, annulation), e-mails, API publiques des devis | 2A, 2B |
| 2D Recette | E2E du tunnel mobile contre le faux Clic to Pay, axe, revue de qualité | 2C |
