# ADR-0070 — API des applications clientes : comptes clients, jetons JWT et module par agence

- **Statut** : Acceptée
- **Date** : 2026-10-30
- **Source** : revue globale du 30/10/2026 (« une API qui sera exploitée pour une application mobile … pense à l'authentification par JWT ») ; précise l'[ADR-0049](0049-tunnel-sessions-devis-figes-garantie-de-prix.md) (tunnel public), l'[ADR-0051](0051-ma-reservation-acces-et-liaison-dossier-paiement.md) (« Ma réservation ») et l'[ADR-0067](0067-plateforme-multi-agences-cloisonnement-support-modules.md) (multi-agences)

## Contexte

La vitrine est rendue côté serveur et ses formulaires sont protégés par cookie, antiforgery et Turnstile : rien de cela ne convient à une application mobile, ni à un site vitrine hébergé ailleurs qui voudrait appeler la plateforme. L'API publique existante (`/{agence}/api/public/…`) est réservée au bot WhatsApp et aux partenaires par une clé commune. Les clients n'ont aucun compte (« Ma réservation » ouvre un dossier à la fois par lien magique).

La plateforme reste un SaaS : chaque agence vend sous son nom, par sa vitrine et, demain, par son application ; une agence ne voit jamais les clients d'une autre.

## Décision

### Une API par agence, sur l'adresse de sa vitrine

- L'API vit sous **`/{agence}/api/v1/…`** : le premier segment est l'adresse de la vitrine (ADR-0067), donc une agence inconnue, suspendue ou dont la vitrine est fermée répond 404, et chaque requête travaille pour cette agence seulement (acteur `system:customer-api`, cloisonné par la persistance).
- Lectures anonymes, limitées par adresse comme la vitrine : `GET site` (identité, lieux, catégories, horaires, politiques, adresses des conditions), `GET categories`, `GET availability`, `POST quotes` / `GET quotes/{code}` (mêmes règles que le bouton « Choisir » : le devis est figé 24 h ; **sans clé d'API**, l'API à clé reste pour le bot et les partenaires).
- Un **module d'agence** `CustomerApi` (« Application mobile et API »), activé par le support, ouvert par défaut ; fermé, l'API répond 404 et la vitrine continue.
- Le document OpenAPI est servi à la racine (`/api/openapi/customer-api-v1.json`) : il décrit l'API de toutes les vitrines.
- Les origines web autorisées à appeler l'API depuis un navigateur (sites vitrines hébergés ailleurs) sont listées dans `CustomerApi:AllowedOrigins` (CORS) ; les applications natives n'en ont pas besoin.

### Les comptes clients appartiennent à une agence

- Un **compte client** (`CustomerAccount`, module Customers) : e-mail (unique dans l'agence), mot de passe haché (PBKDF2 d'ASP.NET Core Identity, sans magasin Identity : le client n'est pas un `ApplicationUser` et ne verra jamais le back office), prénom, nom, téléphone, langue. La même personne ouvre un compte par agence, comme elle est une `Person` par agence.
- Règle de mot de passe (ASVS 2.1) : 10 à 128 caractères, aucune règle de composition, refus des mots de passe courants et de l'adresse e-mail.
- **Vérification de l'adresse** par un code à six chiffres envoyé par e-mail (hash salé, 15 min, 5 essais, usage unique) ; par défaut une demande ne part de l'application qu'avec une adresse vérifiée (`customer_accounts.require_verified_email_to_book`). **Mot de passe oublié** par le même mécanisme ; la réponse est la même que l'adresse ait un compte ou non.
- **Verrouillage** après 5 échecs (15 min, réglable par agence), en plus de la limitation par adresse (`customer-auth`, 20 par 10 min).
- **Fermeture** à la demande du client : compte purgé (noms, téléphone), adresse remplacée par une pierre tombale (elle peut rouvrir un compte), identifiants invalidés ; les dossiers et la personne restent à l'agence sous ses règles de conservation.

### Jetons : JWT court + jeton de rafraîchissement rotatif

- **Jeton d'accès** : JWT HS256 signé par la plateforme (`CustomerApi:Jwt:SigningKey`, secret du Key Vault `CustomerApi--Jwt--SigningKey`, au moins 32 octets ; une clé fixe en développement et en test, refusée ailleurs), 15 minutes, porté en `Authorization: Bearer`. Il nomme le compte (`sub`), son **agence** (`agency`), son **empreinte de sécurité** (`stamp`) et `kind=customer`.
- Le schéma `CustomerBearer` est distinct des cookies du personnel : un cookie n'ouvre rien de l'API (aucune surface CSRF), un jeton n'ouvre rien du back office.
- À chaque requête authentifiée, un filtre vérifie que l'agence du jeton est celle de la vitrine (sinon 401, sans rien dire), que le compte est ouvert et que l'empreinte est courante (un changement de mot de passe termine toutes les sessions).
- **Jeton de rafraîchissement** : secret aléatoire de 256 bits, hash en base, 30 jours (réglable), **usage unique** : chaque rafraîchissement le remplace par un nouveau de la même *famille* (un appareil). Un jeton déjà utilisé présenté de nouveau est un rejeu : la famille entière est révoquée. La déconnexion révoque la famille ; « déconnecter partout » et tout changement de mot de passe révoquent tout.
- Une tâche nocturne purge les jetons morts depuis un mois et les codes de plus d'un jour.

### Réserver depuis l'application

- `POST me/bookings` rejoue le **tunnel public** côté serveur avec les services existants (`FunnelService`) : recherche revalidée, catégorie active, devis figé réutilisé ou créé, options, conducteurs (le conducteur principal reprend les données du compte) vérifiés par le module Customers, demande soumise sous le verrou de disponibilité, puis **pré-autorisation** démarrée hors transaction. La réponse donne la page de paiement de la banque à ouvrir dans un navigateur et l'adresse de confirmation de la vitrine, où le retour de la banque aboutit : l'application la surveille, puis relit le dossier.
- Le dossier est **lié au compte** (`CustomerAccountBooking`) et le compte à la personne du conducteur principal : l'application liste les dossiers faits avec le compte et ceux de cette personne (y compris pris au comptoir).
- Les suites passent par les services de « Ma réservation » : dossier, photos des pièces, « informations fournies », solde en ligne (page de la banque), annulation au tarif affiché, documents (contrat, factures).
- Erreurs : *problem details* (RFC 9457) avec le code métier stable dans `code` et son texte dans la langue de la requête ; erreurs de champs des conducteurs comme sur la vitrine (`main.birth_date`…).

## Conséquences

- Nouveau secret à créer avant la mise en production : `CustomerApi--Jwt--SigningKey` (32 octets aléatoires au moins) ; sans lui l'application refuse de démarrer hors développement.
- Les e-mails des comptes (code de vérification, réinitialisation) partent de l'agence du compte, par l'outbox, avec les gabarits des e-mails clients.
- Les réglages `customer_accounts.*` apparaissent dans `/admin/settings` ; le module « Application mobile et API » dans la fiche de l'agence.
- Les sites vitrines séparés (ADR-0069) consomment la même API ; leurs origines se déclarent dans `CustomerApi:AllowedOrigins`.
- Lien profond vers l'application après le paiement : non prévu ; l'application observe l'adresse de confirmation de la vitrine ([points ouverts](../points-ouverts.md)).

## Alternatives écartées

- **ASP.NET Core Identity pour les clients** (un `ApplicationUser` de rôle « Client ») : mélange les clients et le personnel dans la même table et la même unicité d'e-mail sur la plateforme, impose la double authentification, et un cookie de client ouvrirait des pages du back office par erreur.
- **Jetons opaques côté serveur à chaque requête** : une lecture en base par appel pour tout, y compris le catalogue ; le JWT court suffit, la lecture de l'empreinte (une requête indexée) n'a lieu que pour les points d'entrée du compte.
- **Clé d'API par application** : une clé embarquée dans une application mobile est publique en pratique ; la limitation par adresse et les mêmes règles que la vitrine protègent les lectures anonymes.
- **Fournisseur d'identité externe (Entra External ID, Auth0…)** : coût et dépendance supplémentaires pour des agences de trente véhicules ; possible plus tard derrière le même schéma Bearer.
