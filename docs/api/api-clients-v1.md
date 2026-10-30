# API clients v1 — application mobile et sites vitrines

Contrat de l'API exploitée par l'application mobile des clients et par les sites vitrines hébergés hors de la plateforme ([ADR-0070](../adr/0070-api-clients-comptes-clients-jwt.md)). Le document OpenAPI complet, généré depuis le code, est servi à `GET /api/openapi/customer-api-v1.json`.

## Principes

- **Une API par agence** : toute adresse commence par l'adresse de la vitrine de l'agence : `https://<plateforme>/{agence}/api/v1/…` (par exemple `/agence-demo-tunis/api/v1/site`). Une agence inconnue, suspendue, dont la vitrine est fermée ou dont le module « Application mobile et API » est fermé répond **404**.
- **JSON** en entrée et en sortie (`camelCase`), montants en **TND** avec trois décimales (millimes), instants au format ISO 8601 avec le décalage de Tunis.
- **Erreurs** : *problem details* (RFC 9457), `application/problem+json`, avec `status`, `title` (texte dans la langue de l'en-tête `Accept-Language`, `fr` par défaut) et `code` (code métier stable, par exemple `customers.account.credentials_invalid`). Une erreur de champs porte `errors` : `{ "main.birth_date": ["funnel.required"] }`.
- **Limitation de débit** par adresse cliente : lectures 60 par minute, authentification 20 par 10 minutes, demandes de réservation 10 par 10 minutes, photos 40 par 10 minutes ; au-delà, **429** avec `Retry-After`.
- **Cache** : `GET site` et `GET categories` sont cachables 5 minutes ; tout le reste est `no-store`.

## Authentification

| Étape | Appel | Réponse |
|---|---|---|
| Inscription | `POST auth/register` `{ email, password, firstName, lastName, phone?, culture? ("fr"/"en"), device? }` | **201** session ; un code de vérification est envoyé par e-mail |
| Connexion | `POST auth/login` `{ email, password, device? }` | **200** session ; **401** `credentials_invalid` ; **409** `locked` |
| Rafraîchir | `POST auth/refresh` `{ refreshToken, device? }` | **200** nouvelle session (l'ancien jeton meurt) ; **401** `refresh_token_invalid` → se reconnecter |
| Déconnexion | `POST auth/logout` `{ refreshToken }` | **204** |
| Vérifier l'adresse | `POST auth/verify-email` `{ code }` (Bearer) | **200** compte ; **422** `code_invalid` |
| Nouveau code | `POST auth/resend-verification` (Bearer) | **202** |
| Mot de passe oublié | `POST auth/forgot-password` `{ email }` | **202** toujours |
| Réinitialiser | `POST auth/reset-password` `{ email, code, newPassword }` | **204** ; **422** `code_invalid` / `password_weak` |
| Changer le mot de passe | `POST auth/change-password` `{ currentPassword, newPassword, device? }` (Bearer) | **200** nouvelle session ; les autres appareils sont déconnectés |
| Déconnecter partout | `POST auth/logout-all` (Bearer) | **204** |

Une **session** :

```json
{
  "tokenType": "Bearer",
  "accessToken": "eyJ…",
  "accessTokenExpiresAtUtc": "2026-10-30T18:15:00+00:00",
  "refreshToken": "k9…(43 caractères)",
  "refreshTokenExpiresAtUtc": "2026-11-29T18:00:00+00:00",
  "account": { "id": "…", "email": "amel@example.com", "emailVerified": false, "firstName": "Amel", "lastName": "Trabelsi", "phone": "+21620111001", "culture": "fr", "createdAtUtc": "…" }
}
```

- Le **jeton d'accès** (JWT, 15 minutes) se présente en `Authorization: Bearer <accessToken>`. Il n'est valable que sur l'API de l'agence qui l'a émis. Un **401** sur un appel authentifié signifie : rafraîchir, et si le rafraîchissement échoue, reconnecter.
- Le **jeton de rafraîchissement** (30 jours) est à usage unique : conserver celui de la dernière réponse (stockage sécurisé de l'appareil). Présenter un jeton déjà utilisé révoque toute la session de l'appareil.
- Mot de passe : 10 à 128 caractères, pas de règle de composition, les mots de passe courants sont refusés (`password_weak`).

## Compte

| Appel | Réponse |
|---|---|
| `GET me` | le compte |
| `PATCH me` `{ firstName, lastName, phone?, culture? }` | le compte mis à jour |
| `DELETE me` `{ password }` | **204** : compte fermé (les dossiers restent à l'agence) |

## Catalogue (anonyme)

| Appel | Contenu |
|---|---|
| `GET site` | nom, ville, adresse, téléphone, e-mail, coordonnées, numéro WhatsApp, horaires (`08:00-19:00`), langues, politiques (âge minimum, ancienneté du permis, jeune conducteur, adresse vérifiée exigée), lieux (`code`, `name`, `kind` : `agency`/`airport`/`delivery`), catégories, adresses des conditions et de la confidentialité par langue |
| `GET categories` | catégories : `code` (ACRISS), `name`, `exampleModel`, places, portes, bagages, `transmission`, `fuel`, climatisation, `deposit`, `reducedDeposit`, `photoUrl` |
| `GET availability?lieu=TUN&retour_lieu=NBE&depart=2027-07-01T10:00&retour=2027-07-05T10:00` | catégories disponibles avec `remaining`, `days`, `totalInclVat`, `pricePerDay`, `deposit`, `eurEquivalent` ; `requiredMinimumDays` si la durée est trop courte |
| `POST quotes` `{ pickupLocation, returnLocation?, pickup, return, category, extras?: [{ code, quantity }] }` | **201** devis figé 24 h : `code`, `expiresAtUtc`, `days`, `totalInclVat`, `deposit`, `extras`, `bookingUrls` |
| `GET quotes/{code}` | le devis |

Les dates sont des heures locales de Tunis sans décalage (`2027-07-01T10:00`), comme sur la vitrine.

## Réserver

`POST me/bookings` (Bearer, adresse vérifiée si l'agence l'exige) :

```json
{
  "pickupLocation": "TUN", "returnLocation": null,
  "pickup": "2027-07-01T10:00", "return": "2027-07-05T10:00",
  "category": "ECMR", "quoteCode": "AB12CD",
  "extras": [{ "code": "CHILD_SEAT", "quantity": 1 }],
  "mainDriver": { "nationality": "FR", "birthDate": "1984-03-14", "licenseNumber": "L123", "licenseIssuedOn": "2004-06-01", "licenseCountry": "FR", "identityNumber": "P456", "identityExpiresOn": "2035-01-31" },
  "secondDriver": null,
  "consents": { "documents": true, "whatsApp": false, "marketing": false },
  "termsAccepted": true,
  "culture": "fr"
}
```

- Le conducteur principal reprend prénom, nom, e-mail et téléphone du compte quand ils ne sont pas donnés. Un Tunisien donne sa CIN (`identityNumber`, `identityCountry` `TN`), un étranger son passeport ; un permis non latin exige un permis international (`permitNumber`, `permitCountry`, `permitExpiresOn`, `licenseLatinScript: false`).
- `quoteCode` réutilise un devis de `POST quotes` s'il est valide et correspond exactement ; sinon un nouveau devis est figé.
- Réponse **201** :

```json
{
  "bookingId": "…", "number": "LOC-2027-000123", "status": "pending_validation",
  "totalInclVat": 389.520, "currency": "TND",
  "guarantee": { "mode": "pre_authorization", "amount": 389.520 },
  "paymentUrl": "https://…/dev/clictopay/…", "paymentExpiresAtUtc": "…",
  "confirmationUrl": "https://…/agence-demo-tunis/fr/reserver/confirmation/LOC-2027-000123"
}
```

- `paymentUrl` est la page de paiement de la banque, à ouvrir dans un navigateur (onglet personnalisé / `SFSafariViewController`). La banque renvoie vers `confirmationUrl` : l'application surveille cette adresse pour refermer le navigateur, puis relit `GET me/bookings/{id}`. En mode `payment_link_after_validation`, il n'y a pas de paiement immédiat : l'agence valide, puis envoie un lien.
- Erreurs : **403** `customers.account.email_not_verified`, **422** erreurs de champs (`errors`), **409** `booking.funnel.category_unavailable`, **422** `pricing.*`.

Ensuite, pour chaque dossier (Bearer) :

| Appel | Contenu |
|---|---|
| `GET me/bookings` | résumés : `number`, `status`, `channel`, `categoryCode`, lieux, dates, `totalInclVat`, `deposit` |
| `GET me/bookings/{id}` | le dossier : montants payés et solde, `paymentState`, chronologie, conducteurs et emplacements de photos (`slots`), `canSendDocuments`, `canProvideInformation`, `canPayBalance`, `cancellation` (frais, remboursement estimé), `documents` (contrat, factures) avec leur adresse |
| `POST me/bookings/{id}/documents/{slot}` | photo d'une pièce (`multipart/form-data`, champ `file`, JPEG/PNG/PDF, 10 Mo) ; `slot` = `main.license.front`, `main.identity.back`, `second.permit.front`… |
| `GET me/bookings/{id}/documents/{documentId}` | le fichier (PDF) |
| `POST me/bookings/{id}/information-provided` | **204** : le client a fourni ce que l'agent demandait |
| `POST me/bookings/{id}/pay-balance` | `{ paymentId, paymentUrl, expiresAtUtc }` : page de la banque pour le solde |
| `POST me/bookings/{id}/cancel` `{ expectedFee? }` | `{ fee, refundAmount, refund }` ; **409** `booking.access.fee_changed` si les frais ont changé |

Statuts : `pending_validation`, `information_requested`, `option_held`, `awaiting_payment`, `confirmed`, `in_progress`, `returned`, `closed`, `refused`, `expired`, `cancelled`, `no_show`.

## Sites vitrines séparés

Un site vitrine hébergé ailleurs (par exemple un site statique sur le domaine de l'agence) appelle la même API depuis le navigateur : son origine (`https://www.agence.tn`) doit figurer dans `CustomerApi:AllowedOrigins` (CORS : méthodes `GET POST PATCH DELETE`, en-têtes `Authorization`, `Content-Type`, `Accept-Language`). Il garde les jetons comme une application (jamais en cookie partagé avec la plateforme).

## Configuration

| Clé | Rôle |
|---|---|
| `CustomerApi:Jwt:SigningKey` (`CustomerApi--Jwt--SigningKey` dans le Key Vault) | Clé HMAC des jetons d'accès, 32 octets au moins ; obligatoire hors développement et test |
| `CustomerApi:Jwt:Issuer`, `CustomerApi:Jwt:Audience` | `carrental`, `carrental-customers` |
| `CustomerApi:AccessTokenLifetime` | 15 minutes (1 min à 1 h) |
| `CustomerApi:AllowedOrigins` | origines web autorisées (CORS) |
| `RateLimiting:CustomerAuth` | 20 par 10 minutes et par adresse |
| Réglages `customer_accounts.*` (`/admin/settings`) | échecs avant blocage (5), durée du blocage (15 min), durée de session (30 j), validité des codes (15 min), adresse vérifiée exigée pour réserver (oui) |
