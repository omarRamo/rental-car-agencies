# ADR-0014 — Identité : double authentification obligatoire, rôles en préréglages de permissions

- **Statut** : Acceptée
- **Date** : 2026-10-20
- **Source** : [spécifications](../SPEC.md) section 3 (rôles et droits), [prompt](../PROMPT.md) section 12

## Contexte

Quatre profils (gérant, agent, comptable, préparateur) se partagent le back office, avec une matrice de droits fine (section 3 de la spécification). Le back office manipule des pièces d'identité et de l'argent : la double authentification est obligatoire et chaque action est tracée. Il y a moins de dix comptes.

## Décision

- **ASP.NET Core Identity** (comptes individuels) dans la même base, schéma `identity`, avec `ApplicationUser` / `ApplicationRole` à clé `Guid` v7. Pas d'inscription libre : le gérant crée les comptes dans `/admin/users`, l'invité reçoit un lien de définition du mot de passe. En production, le premier gérant est créé au démarrage à partir de `Identity:InitialManagerEmail` (il passe ensuite par « mot de passe oublié »).
- **Politique** : mot de passe de 12 caractères au moins avec chiffre, minuscule et majuscule ; verrouillage 15 minutes après 5 échecs ; adresse e-mail unique. Cookie de session de 8 heures glissantes, sans « se souvenir de moi » ni « se souvenir de cet appareil » ; tampon de sécurité revérifié chaque minute (requêtes) et toutes les 2 minutes (circuits Blazor) : un compte verrouillé ou un mot de passe changé coupe les sessions.
- **TOTP obligatoire pour `/admin`** : la stratégie `AdminArea` exige une session authentifiée portant la revendication `amr=mfa`, posée par la connexion avec code TOTP ou code de récupération, et par l'activation de l'authentificateur. Sans double authentification, l'utilisateur est renvoyé vers `/account/manage/enable-authenticator` (code QR et clé) avant tout accès. La désactivation n'est pas proposée : seule la réinitialisation, suivie d'une nouvelle activation.
- **Permissions** : catalogue `Permissions` (`CarRental.Application.Identity.Contracts`), noms `domaine.action` (`bookings.manage`, `audit.read`…). Les **rôles sont des préréglages** (`RolePresets`) : leurs permissions sont des revendications de rôle `permission`, réalignées au démarrage (`RolePresetSynchronizer`). Des permissions supplémentaires par utilisateur restent possibles (revendications utilisateur, sans écran pour l'instant). Au-delà de la matrice de la spécification : `audit.read`, `jobs.dashboard`, `users.manage` et `settings.manage` pour le gérant ; `customers.*`, `whatsapp.inbox`, `channels.manage` selon les rôles.
- **Application** : stratégies `Permission:<nom>` créées à la demande par `PermissionPolicyProvider` (une permission inconnue lève une exception) ; les pages déclarent `[HasPermission(Permissions.X)]` en plus de `AdminArea` (vérifié par test d'architecture) ; le menu est filtré par les mêmes permissions ; les services revérifient avec `IUserContext.HasPermission`.
- **Traçabilité** : connexion réussie ou échouée, code faux, code de récupération, verrouillage, déconnexion, changement et réinitialisation du mot de passe, activation de l'authentificateur, création, verrouillage et déverrouillage d'un compte sont écrits dans le journal d'audit avec l'adresse IP (`CF-Connecting-IP` derrière Cloudflare) et l'identifiant de corrélation ([ADR-0018](0018-journal-audit-ajout-seul.md)).
- **Pages de compte** (`/account/...`) en rendu statique, issues du modèle Blazor, traduites, protégées par jeton anti-falsification.

## Conséquences

- Les tests E2E et d'intégration se connectent avec une clé TOTP connue, uniquement en développement et en test (utilisateurs de démonstration, [points ouverts](../points-ouverts.md) écart n° 8). En recette, mot de passe et clé viennent du Key Vault (`Seed--DemoUserPassword`, `Seed--DemoAuthenticatorKey`).
- Ajouter une permission : une constante dans `Permissions` et son entrée dans le catalogue, son ajout aux préréglages concernés, puis l'attribut sur la page et l'entrée du menu ; le réalignement au démarrage met les rôles à jour.
- Les tables des clés d'accès (passkeys) d'Identity 10 sont ignorées pour l'instant.

## Alternatives écartées

- **Entra ID ou fournisseur externe** pour le personnel : coût et administration disproportionnés pour moins de dix comptes ; la stratégie fondée sur `amr` resterait compatible.
- **`[Authorize(Roles = ...)]`** : trop grossier pour la matrice de la spécification.
- **Code par SMS** : coût et sécurité moindre que TOTP.
