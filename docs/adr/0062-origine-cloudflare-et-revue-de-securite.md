# ADR-0062 — Origine réservée à Cloudflare et décisions de la revue de sécurité (passe 1)

- **Statut** : Acceptée
- **Date** : 2026-10-21
- **Source** : [prompt](../PROMPT.md) §12 et §15 « Transverse — Durcissement » (revue OWASP, en-têtes), [spécifications](../SPEC.md) §14, [ADR-0004](0004-hebergement-app-service-b1-cloudflare.md), [ADR-0014](0014-identite-2fa-permissions.md), [ADR-0018](0018-journal-audit-ajout-seul.md), [ADR-0037](0037-vitrine-anti-abus-et-mesure-entonnoir.md), [revue OWASP d'octobre 2026](../securite/revue-owasp-2026-10.md), [points ouverts](../points-ouverts.md) (« Origine réservée à Cloudflare »)

## Contexte

La revue de l'application fusionnée (OWASP Top 10 2021, ASVS 4.0.3 niveau 2) a relevé, outre des contrôles solides, quelques défauts de conception : l'adresse du client était lue dans `CF-Connecting-IP` alors que l'App Service accepte tout le monde (un client peut alors choisir son adresse, donc sa limite de débit, et l'adresse écrite dans le journal d'audit) ; les formulaires de compte, les sondes de santé et quelques lectures anonymes n'avaient aucune limite de débit ; une session du personnel pouvait durer indéfiniment ; le journal d'audit, impossible à nettoyer, gardait l'identifiant tapé lors d'une connexion à un compte inconnu. Le détail de chaque constat est dans la revue ; cet ADR fixe les décisions structurantes.

## Décision

### Origine réservée à Cloudflare

- **Paramètre Bicep `restrictToCloudflare`**, désactivé par défaut (la recette reste sur `*.azurewebsites.net`, la production tant que le domaine n'est pas derrière Cloudflare) : restrictions d'accès du site principal limitées aux plages publiées par Cloudflare (`cloudflareIpRanges`, une règle d'autorisation par plage, refus implicite du reste, 403), relevées le 21/10/2026 sur `cloudflare.com/ips-v4` et `ips-v6`, à comparer chaque trimestre.
- Le **site SCM** garde ses propres règles, ouvertes (`scmIpSecurityRestrictionsUseMain = false`) : le déploiement zip depuis GitHub, aux adresses changeantes, s'authentifie par Entra. Le **contrôle de santé d'App Service** est interne à la plateforme : il n'est pas soumis aux restrictions. La vérification après déploiement passe par le domaine public (`HEALTHCHECK_BASE_URL`).
- **L'application ne croit `CF-Connecting-IP` que si l'origine est réservée** : `ReverseProxy:TrustCloudflareHeader` vaut désormais `false` par défaut, et le paramètre Bicep règle `ReverseProxy__TrustCloudflareHeader` en même temps que les restrictions ; sans lui, l'adresse du client est l'adresse de connexion corrigée par le frontal d'App Service.
- **Alternative documentée, non retenue pour l'instant** : les « Authenticated Origin Pulls » avec un certificat propre à la zone (le certificat partagé de Cloudflare est présenté par tous ses clients), qui demanderaient le mode certificat client d'App Service et une vérification dans l'application. Complément simple après la mise en service : retirer `*.azurewebsites.net` des hôtes acceptés.

### Anti-automatisation des surfaces anonymes

- **Politique `account`** : les envois de formulaires de toutes les pages de compte (connexion, codes de double authentification et de récupération, mot de passe oublié et nouveau mot de passe, gestion du compte), 20 par 10 minutes et par adresse (fenêtre glissante, `RateLimiting:Account`). Le verrouillage protège un compte ; cette limite protège tous les comptes à la fois (essais d'un mot de passe sur chaque compte, codes devinés) et la boîte de réception du personnel (e-mails de réinitialisation envoyés en boucle, quota d'Azure Communication Services). Afficher une page n'est jamais compté.
- **Sondes de santé limitées par adresse** avant les contrôles (60 par minute, `RateLimiting:Health`, réponse 429 en texte brut) : chaque sonde de `/health/ready` exécute une quinzaine de requêtes sur une base Basic à 5 DTU. App Service (une sonde par minute), la vérification du déploiement (quatre par minute) et une surveillance externe restent très en dessous. Le seuil placé avant le pipeline garde le principe des sondes : rien d'autre ne les précède.
- **Politique `public-media`** sur les photos anonymes des catégories (300 par minute et par adresse) et **`public-search`** sur la page de résultat d'un paiement en ligne : deux lectures anonymes de la base.
- **Inventaire des points d'accès** (test d'intégration) : chaque point d'accès exige une stratégie d'autorisation, ou porte une politique de limitation de débit, ou figure dans une courte liste justifiée (fichiers statiques, redirection de langue, pages d'erreur, `sitemap.xml`, pages fixes mises en cache). Un nouveau point d'accès anonyme sans limite fait échouer la CI.

### Sessions du personnel

- **Durée de vie absolue de 12 heures** (`Identity:Session:AbsoluteLifetime`, ASVS 3.3.2 niveau 2), en plus de l'expiration glissante de 8 heures sans activité : l'instant de connexion est une revendication de la session (`auth_time`), posée à l'émission du cookie, conservée par le rafraîchissement des revendications, vérifiée à chaque requête et toutes les deux minutes dans les circuits interactifs (qui survivaient au cookie).
- La réauthentification avant une action sensible (remboursement, anonymisation, permissions) n'est pas ajoutée : session à double authentification, durée bornée et journal d'audit en tiennent lieu à ce stade (risque accepté de la revue).

### Journal d'audit et connexion

- Une connexion à un compte inconnu n'écrit plus l'identifiant tapé dans le journal d'audit, qui ne s'efface jamais : seul le domaine d'une adresse e-mail est gardé (`***@exemple.tn`) ; un mot de passe tapé dans le champ de l'adresse n'y entre plus.
- La réponse à un compte inconnu prend le temps d'une vérification de mot de passe réelle (hachage d'un mot de passe aléatoire) : le temps de réponse ne dit plus si un compte existe. Le verrouillage après cinq échecs, qui ne concerne que les comptes existants, reste un indice accepté.

## Conséquences

- Avant l'ouverture au public : domaine derrière Cloudflare, `HEALTHCHECK_BASE_URL`, puis `restrictToCloudflare = true` (README de l'infrastructure, section 8 ; [runbook](../runbook.md#avant-la-mise-en-production)). Tant que ce n'est pas fait, les limites de débit comptent par adresse de connexion, ce qui reste sûr (pas d'adresse choisie par le client).
- Une plage de Cloudflare oubliée donne des 403 à une partie des visiteurs : la liste se compare chaque trimestre et la procédure de secours (désactiver le paramètre) est dans le runbook.
- Les hôtes de tests déclarent l'en-tête de confiance et lèvent les nouvelles limites ; un hôte dédié (`SecurityScenarioHost`) les vérifie avec des limites basses et une session de 8 secondes.
- Le personnel se reconnecte (mot de passe et code) au plus tard douze heures après sa connexion, même en activité.

## Alternatives écartées

- **Vérifier dans l'application que la connexion vient d'une plage de Cloudflare** avant de croire l'en-tête : une seconde liste à tenir à jour dans la configuration, alors que la restriction d'App Service ferme déjà l'origine.
- **Bloquer `/health*` chez Cloudflare** : la vérification après déploiement passe par le domaine public ; un en-tête secret demanderait un secret de plus dans GitHub.
- **Mettre en cache le rapport de santé quelques secondes** : la limite par adresse suffit et garde des sondes exactes (tests, déploiement).
- **Limiteur global sur toute l'application** : il compterait les fichiers statiques et le back office ([ADR-0037](0037-vitrine-anti-abus-et-mesure-entonnoir.md)).
- **Délai d'inactivité de 30 minutes** (autre branche d'ASVS 3.3.2) : intenable au comptoir, sur tablette, entre deux clients ; la durée absolue de 12 heures borne la session sans gêner une journée de travail.
- **Empreinte (hachage) de l'identifiant tapé dans le journal d'audit** : un hachage court d'un mot de passe reste une indication exploitable hors ligne.
