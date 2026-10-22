# Revue de sécurité — OWASP Top 10 2021 et ASVS 4.0.3 niveau 2 (octobre 2026, passes 1 et 2)

- **Date** : 21/10/2026 (passe 1) ; 22/10/2026 (passe 2, [section 7](#7-passe-2--après-les-fusions-de-2c-4b-et-3a))
- **Périmètre** : l'application fusionnée sur `main` au commit `a4f396c` — socle (phase 0), exploitation (1A à 1F-b, dont départs et retours), vitrine 2A, paiement en ligne 2B, finance et pilotage 3B et 3C, canal WhatsApp 4A, canaux de vente 5A — et l'infrastructure (`infra/`, `.github/workflows/`).
- **Hors périmètre de la passe 1** (lots en cours dans d'autres branches, à revoir après leur fusion, [section 4](#4-à-revoir-après-les-fusions)) : tunnel de réservation et « Ma réservation » (2C), agent IA WhatsApp (4B), facturation (3A). Revus par la passe 2 ([section 7](#7-passe-2--après-les-fusions-de-2c-4b-et-3a)).
- **Méthode** : lecture du code, de la configuration et des ADR ; vérification des contrôles annoncés par les tests existants ; correction des constats de gravité moyenne dans le périmètre de la passe, avec tests ; décisions dans l'[ADR-0060](../adr/0060-alertes-et-telemetrie-de-sante.md), l'[ADR-0061](../adr/0061-sauvegardes-retention-et-immuabilite.md) et l'[ADR-0062](../adr/0062-origine-cloudflare-et-revue-de-securite.md).
- **Gravité** : *élevée* (exploitable à distance, données ou argent exposés), *moyenne* (contrôle de défense contourné, déni de service à faible coût, exigence ASVS niveau 2 manquante), *faible* (fuite d'information mineure, durcissement), *info* (constat sans risque, recommandation).

## 1. Synthèse

Aucun constat de gravité élevée. Les contrôles de fond sont solides : double authentification obligatoire et permissions vérifiées deux fois (page et service), pièces d'identité jamais publiques et chaque accès journalisé avant la réponse, journal d'audit en ajout seul jusque dans la base, webhook WhatsApp signé et comparé en temps constant, clés des canaux hachées, paiement confirmé uniquement de serveur à serveur, aucun secret dans le dépôt, identité managée partout.

| | Constats | Corrigés dans cette passe | À revoir après fusion | Acceptés |
|---|---|---|---|---|
| Gravité moyenne | 6 | 6 | — | — |
| Gravité faible | 4 | 4 | — | 5 |
| Info | — | — | — | 11 |
| Lots 2C, 4B, 3A | — | — | 9 | — |

Les corrections : adresse du client fiable seulement derrière Cloudflare, limitation de débit des formulaires de compte, des sondes de santé et des lectures anonymes, durée de vie absolue des sessions, journal d'audit sans identifiant tapé, temps de réponse identique pour un compte inconnu, alertes, conservation longue et immuabilité des pièces comptables.

## 2. Constats et corrections

| ID | Gravité | Référence | Emplacement | Constat | Correction ou décision | Test |
|---|---|---|---|---|---|---|
| S-01 | Moyenne | A04, A09 ; ASVS 11.1.4 | `src/CarRental.Web/appsettings.json`, `Hosting/WebOptions.cs`, `Hosting/ClientIpResolver.cs` | `ReverseProxy:TrustCloudflareHeader` valait `true` alors que l'App Service accepte toute adresse : un client appelant l'origine directement choisissait son adresse avec `CF-Connecting-IP` — contournement des limites de débit (vitrine, webhook, canaux), contournement de la liste d'adresses d'un canal (la clé d'API reste exigée) et adresse fausse dans le journal d'audit | Défaut `false` ; le paramètre Bicep `restrictToCloudflare` réserve l'origine aux plages de Cloudflare et règle `ReverseProxy__TrustCloudflareHeader` en même temps (ADR-0062) | `RateLimitingConventionTests` (défaut, `appsettings.json`, couplage dans `main.bicep`) ; les hôtes de tests déclarent l'en-tête |
| S-02 | Moyenne | A07 ; ASVS 2.2.1 | `Components/Account/Pages/*` | Aucune limite de débit sur les formulaires de compte : essais d'un mot de passe sur tous les comptes (le verrouillage ne protège qu'un compte à la fois), codes de double authentification et de récupération, e-mails de réinitialisation envoyés en boucle vers le personnel (quota d'Azure Communication Services, e-mails de confirmation retardés) | Politique `account` sur toutes les pages de compte (`_Imports.razor`) : 20 envois par 10 minutes et par adresse, l'affichage n'est pas compté | `RateLimitingConventionTests` (toutes les pages, partition GET/POST) ; `Security/AnonymousSurfaceRateLimitTests` (connexion, mot de passe oublié, budget commun) |
| S-03 | Moyenne | A04, A05 ; ASVS 11.1.4 | `Hosting/WebApplicationExtensions.cs` | `/health` et `/health/ready` anonymes, hors limitation de débit et placés avant tout le pipeline ; chaque sonde de `/health/ready` exécute une quinzaine de requêtes (Hangfire, outbox, canaux…) sur une base Basic à 5 DTU : déni de service par amplification à très faible coût | Seuil par adresse avant les contrôles (`UseHealthProbeThrottling`, 60 par minute, `RateLimiting:Health`), réponse 429 en texte brut ; App Service, le déploiement et une surveillance externe restent très en dessous | `RateLimitingConventionTests.Health_probes_beyond_the_limit…` (pipeline réel, sans conteneur) ; `AnonymousSurfaceRateLimitTests.Health_probes…` |
| S-04 | Moyenne | A07 ; ASVS 3.3.2 | `Hosting/WebServiceCollectionExtensions.cs`, `Components/Account/IdentityRevalidatingAuthenticationStateProvider.cs` | Cookie de 8 heures glissantes sans durée maximale, et un circuit interactif ouvert n'était revalidé que pour le tampon de sécurité : une tablette restée connectée gardait une session du back office indéfiniment | Durée de vie absolue de 12 heures (`Identity:Session:AbsoluteLifetime`) : revendication `auth_time` posée à la connexion, conservée au rafraîchissement, vérifiée à chaque requête et toutes les deux minutes dans les circuits | `Security/SessionLifetimeTests` (unitaire) ; `Security/SessionLifetimeTests` (intégration, session de 8 s maintenue active jusqu'à sa fin) |
| S-05 | Faible | A04 ; ASVS 11.1.4 | `Endpoints/FleetEndpoints.cs` | `/media/categories/{id}` anonyme et sans limite : une requête SQL et une lecture de blob à chaque absence de cache (Cloudflare ne garde pas une adresse sans extension) | Politique `public-media` (300 par minute et par adresse) | `AnonymousSurfaceRateLimitTests.Category_photos…` ; inventaire des points d'accès |
| S-06 | Faible | A09 ; ASVS 7.1.1 | `Infrastructure/Identity/SignInRecorder.cs` | Une connexion à un compte inconnu écrivait l'identifiant tapé dans le journal d'audit, en ajout seul et jamais purgé : un mot de passe tapé dans le champ de l'adresse y restait pour toujours | Seul le domaine d'une adresse e-mail est gardé (`***@exemple.tn`), sinon une mention neutre | `Security/LoginNameMaskingTests` |
| S-07 | Faible | A07 ; WSTG-IDNT-04 (énumération) | `Components/Account/Pages/Login.razor` | La réponse à un compte inconnu ne hachait aucun mot de passe : quelques dizaines de millisecondes de moins, qui disaient si le compte existe | Vérification contre le hachage d'un mot de passe aléatoire calculé une fois avec le hacheur configuré | Revue de code (une mesure de temps serait instable en CI) ; le comportement fonctionnel reste couvert par `AuthenticationTests` |
| S-08 | Faible | A04 ; ASVS 11.1.4 | `Components/Public/Pages/PaymentResultPage.razor` | Page de résultat d'un paiement en ligne anonyme, lue en base à chaque requête, sans limite de débit | Politique `public-search` | `Security/EndpointInventoryTests` |
| S-09 | Moyenne | A09 | `infra/`, `src/CarRental.Infrastructure/Observability` | Aucune alerte : erreurs, paiements en échec, canal en échec, file WhatsApp bloquée, tâches et échéances de flotte n'étaient visibles qu'en ouvrant les écrans | Jauge `carrental.health.status` publiée par l'application pour chaque entrée de santé, entrées `jobs` et `payments`, module `alerts.bicep` (ADR-0060) | `Observability/*` (unitaires et intégration) ; build Bicep des deux fichiers de paramètres |
| S-10 | Moyenne | A08 (intégrité et disponibilité des données) | `infra/modules/sql.bicep`, `storage.bicep` | Pas de conservation au-delà de 12 mois (pièces comptables : 10 ans) ; factures effaçables ou remplaçables par toute identité ayant un rôle de données sur le stockage | Sauvegarde annuelle de la base gardée 10 ans ; conteneur `invoices` sous rétention temporelle de 10 ans, non verrouillée (ADR-0061) | Build et lint Bicep de `main.bicep` avec les deux fichiers de paramètres (job `bicep` de la CI) |

L'inventaire des points d'accès (`tests/CarRental.Integration.Tests/Security/EndpointInventoryTests`) protège la suite : chaque point d'accès exige une stratégie d'autorisation (MFA comprise pour `/admin`), ou porte une politique de limitation de débit, ou figure dans une liste courte et justifiée ; un nouveau point d'accès anonyme non limité fait échouer la CI.

## 3. Contrôles vérifiés, par catégorie

### A01 — Contrôle d'accès

- Pages du back office : `[Authorize(Policy = AdminArea)]` (session à double authentification) par `Admin/Pages/_Imports.razor` et `[HasPermission]` sur chaque page sauf le tableau du jour (tests d'architecture `Every_back_office_page_*`) ; menu filtré par les mêmes permissions (test).
- API minimales du back office : `RequireAuthorization(PermissionPolicy.For(...))` (pièces, export de données personnelles, exports Excel, photos et contrats des départs, photos des véhicules) ; tableau de bord Hangfire `MapHangfireDashboardWithAuthorizationPolicy` avec `jobs.dashboard` ; les services revérifient la permission (`IUserContext.HasPermission`).
- Pièces d'identité : lues seulement par `DocumentImageAccess` (permission `customers.documents.read`), journal `audit.DocumentAccessLogs` écrit **avant** la réponse, `no-store`, nom neutre, lien temporaire de 5 minutes par SAS de délégation d'utilisateur, aucune URL publique ni devinable ([ADR-0026](../adr/0026-pieces-identite-stockage-et-acces-journalise.md)) ; numéros masqués sans la permission.
- Identifiants dans les adresses : GUID v7 (74 bits aléatoires) ; la page de résultat de paiement n'affiche que montant et statut.
- Redirections : `LocalUrls.IsLocal`, `IdentityRedirectManager`, chemins de retour de paiement enregistrés côté serveur (jamais lus dans la requête) ; aucune CORS configurée.
- Point d'attention (A-15) : le personnel n'est pas cloisonné par agence (une seule agence aujourd'hui).

### A02 — Défaillances cryptographiques

- HTTPS seul, HSTS d'un an avec sous-domaines, TLS 1.2 minimum (App Service, SQL, stockage).
- Cookies : session `carrental.auth` `HttpOnly`, `SameSite=Lax`, `Secure` toujours hors développement et tests ; cookies de double authentification et anti-falsification `Secure` ; cookies de langue et de visite `HttpOnly`, `Lax`, `Secure` en HTTPS.
- Clés Data Protection dans un blob versionné, enveloppées par la clé RSA 3072 du Key Vault (rotation annuelle), exigées hors développement (`DataProtectionSettingsValidator`).
- Comparaisons en temps constant : signature et jeton de vérification WhatsApp, clés d'API et empreintes de certificat des canaux (`CryptographicOperations.FixedTimeEquals`) ; clés des canaux stockées sous forme d'empreinte SHA-256, montrées une seule fois.
- Mots de passe : hachage PBKDF2 d'Identity ; aucun mot de passe d'infrastructure (Entra partout, clés de compte de stockage désactivées) ; aucune donnée de carte (page de paiement de Clic to Pay).

### A03 — Injection

- Accès aux données par EF Core (requêtes paramétrées) ; deux instructions SQL écrites à la main (`sp_getapplock`, compteurs `MERGE`), paramétrées ; ni `FromSql*` ni `ExecuteSqlRaw` ni motif `LIKE` construit à partir d'une saisie.
- Rendu Razor encodé ; un seul `MarkupString`, le bloc JSON-LD, dont l'encodeur échappe `<`, `>` et `&` ; page d'erreur 429 encodée par `HtmlEncoder`.
- Exports Excel (3C) : ClosedXML 0.105 écrit les textes comme textes, jamais comme formules ; la seule donnée venue du public (source de l'entonnoir) est limitée à `a-z0-9._-` (A-08). Les canaux (5A) n'ont pas d'export.

### A04 — Conception non sécurisée

- Limitation de débit par adresse sur toute la surface anonyme après S-02, S-03, S-05, S-08 : vitrine, API publique, retours de paiement, webhook WhatsApp, point d'entrée des canaux, formulaires de compte, sondes, photos.
- Turnstile en échec fermé (secret absent, réponse inattendue, action différente : refus) ; verrouillage après 5 échecs pendant 15 minutes, échecs de code compris.
- Idempotence : paiements par numéro de commande, canaux par référence et empreinte, livraisons WhatsApp par identifiant Meta ; disponibilité écrite sous verrou applicatif.
- Mot de passe oublié et réinitialisation répondent pareil que le compte existe ou non ; les invitations ne sont envoyées que par le gérant.

### A05 — Mauvaise configuration

- En-têtes sur toute réponse : CSP par zone (vitrine et comptes sans `unsafe-inline` ; back office `style-src 'unsafe-inline'` exigé par MudBlazor ; scripts et cadre de Turnstile ajoutés à la seule réponse qui les affiche ; les liens WhatsApp `https://wa.me/…` sont des navigations, que la CSP ne restreint pas ; la redirection vers la page de paiement de Clic to Pay est à revoir avec le tunnel, R-04), `frame-ancestors 'none'`, `X-Frame-Options: DENY`, `nosniff`, `Referrer-Policy: strict-origin-when-cross-origin`, `Cross-Origin-Opener-Policy`, `Permissions-Policy` restrictive, `no-store` sur `/admin` et `/account`, en-tête `Server` retiré (test d'intégration `Security_headers_protect_every_area` ; politique de chaque zone figée sans conteneur par `Security/ContentSecurityPolicyTests`).
- Erreurs : gestionnaire d'exceptions vers `/error` (identifiant de trace seulement), erreurs détaillées des circuits en développement seulement, détails des entrées de santé masqués hors développement et tests.
- Filtrage des hôtes (400 sinon, sondes exemptées) ; en-têtes transférés par App Service (`ASPNETCORE_FORWARDEDHEADERS_ENABLED`, dernier saut seulement).
- Pages et passerelles de développement (`/dev/clictopay`, faux WhatsApp, faux canal, galerie d'interface) absentes ou refusées en production ; données de démonstration refusées en production.

### A06 — Composants vulnérables

- Vérifié dans `.github/workflows/ci.yml` : échec sur tout paquet NuGet vulnérable, direct ou transitif (`dotnet list package --vulnerable --include-transitive`), audit NuGet à la restauration avec épinglages documentés, gitleaks sur tout l'historique, lint Bicep (règles de sécurité en erreur) ; `codeql.yml` (requêtes `security-extended`, chaque lundi et à chaque PR) ; Dependabot hebdomadaire (NuGet, Actions, SDK) ; actions épinglées par empreinte ; images de conteneurs épinglées. Point d'attention A-16 : CodeQL exige un dépôt public ou GitHub Advanced Security.

### A07 — Identification et authentification

- Comptes individuels, double authentification TOTP obligatoire pour `/admin` (`amr=mfa`), codes de récupération, pas de « se souvenir de moi » ; mot de passe de 12 caractères ; tampon de sécurité revérifié chaque minute (requêtes) et toutes les deux minutes (circuits) : un compte verrouillé ou un mot de passe changé coupe les sessions ; permissions retirées appliquées à la requête suivante.
- Après la passe : durée de vie absolue de 12 heures (S-04), limitation des formulaires (S-02), temps de réponse identique pour un compte inconnu (S-07).

### A08 — Intégrité des logiciels et des données

- Webhook WhatsApp : HMAC-SHA256 du corps brut avec le secret de l'application, comparaison en temps constant, 401 sans rien enregistrer ; Clic to Pay : les adresses de retour ne font qu'identifier la commande, le statut vient toujours de `getOrderStatusExtended.do` ; canaux : clé, certificat et liste d'adresses, chacun exigé s'il est configuré, liste vide fermée.
- Désérialisation : l'outbox ne désérialise que les types qui ont un gestionnaire (registre), avec System.Text.Json ; Hangfire garde ses noms de types (A-02).
- Journal d'audit en ajout seul (intercepteur et déclencheurs SQL) ; tables temporelles.
- Chaîne de livraison : OIDC sans secret, production depuis `main` avec une CI verte, migrations par bundle.
- Après la passe : sauvegarde annuelle de 10 ans et factures immuables (S-10).

### A09 — Journalisation et surveillance

- Journal d'audit : connexions réussies et échouées, codes faux, verrouillages, mots de passe, double authentification, comptes, permissions, paramètres, exports de données personnelles, relances de l'outbox, purges ; accès aux pièces d'identité dans leur propre journal.
- Pas de secret ni de donnée personnelle dans les journaux applicatifs : appels Clic to Pay sans identifiants ni réponse brute, WhatsApp sans jeton ni contenu, extraits de messages des canaux sans donnée personnelle, contenu des messages de l'outbox jamais affiché ; seul l'e-mail du premier gérant est journalisé au démarrage (compte du personnel).
- Après la passe : identifiant tapé masqué (S-06), alertes (S-09).

### A10 — Falsification de requêtes côté serveur

- Aucune adresse fournie par un utilisateur n'est appelée par le serveur : Turnstile, Clic to Pay, l'API Graph de Meta et les adaptateurs des canaux ont des adresses de configuration ; l'adresse de la page de paiement renvoyée par la passerelle est seulement validée (HTTPS) et donnée au navigateur ; les médias WhatsApp ne sont jamais téléchargés ; les pièces jointes des e-mails sont lues dans le stockage privé.

## 4. À revoir après les fusions

Ces points touchent des fichiers des lots en cours (`Components/Public/**` du tunnel, `Endpoints/PublicSite/**`, `**/Messaging/**`, `**/Billing/**`) ; ils sont à vérifier sur `main` après chaque fusion, avec l'inventaire des points d'accès et les tests de `Security/`. **Revérifiés par la passe 2** : issue de chacun dans la [section 7.2](#72-points-r-01-à-r-09).

| ID | Lot | À vérifier |
|---|---|---|
| R-01 | 2C | **Liens magiques « Ma réservation »** : jeton d'au moins 128 bits (`RandomNumberGenerator`), durée courte, usage limité, comparaison en temps constant ou empreinte stockée, jamais journalisé ni conservé en clair ; demande de lien sous `public-submit` et Turnstile, même réponse que le dossier existe ou non |
| R-02 | 2C | **Dépôt des pièces par le client** : type réel par les octets, 10 Mo, `public-upload`, chemin fait d'identifiants, lectures ultérieures par `DocumentImageAccess` seulement, fichiers orphelins nettoyés |
| R-03 | 2C | **Turnstile** : comparer le `hostname` renvoyé par `siteverify` aux domaines de l'agence (`TurnstileVerifier` ne le fait pas aujourd'hui), une action attendue par formulaire |
| R-04 | 2C | **CSP et Clic to Pay** : `form-action 'self'` bloque une redirection d'envoi de formulaire vers la page de paiement ([ADR-0038](../adr/0038-passerelle-clic-to-pay-isolee-et-simulee.md)) : page intermédiaire avec un lien, ou origine de la page de paiement ajoutée à la seule réponse concernée (`ContentSecurityPolicyAdditions` ne gère pas `form-action`) ; jamais `unsafe-inline` |
| R-05 | 2C | **Pages du tunnel et de « Ma réservation »** : `no-store`, `noindex`, politiques `public-submit` / `public-search`, inventaire des points d'accès vert, aucune donnée personnelle dans les journaux |
| R-06 | 4B | **Agent IA** : `statut_reservation` seulement si le numéro WhatsApp correspond au dossier (sinon fuite d'un dossier par sa référence), outils en lecture seule sauf le devis et le transfert, résistance aux injections d'instructions (le message du client n'est jamais une consigne), plafonds de tours, de jetons et de coût, aucune pièce acceptée, données envoyées au modèle minimisées, clé du fournisseur dans le Key Vault, aucun contenu de message dans les journaux |
| R-07 | 4B | **Entrée de santé `whatsapp`** (tours du bot ou envois en attente trop anciens), couverte d'office par l'alerte `health-degraded` ; webhook, limite et déduplication inchangés |
| R-08 | 3A | **Factures** : écriture unique dans le conteneur immuable `invoices` (jamais d'écrasement ni de suppression, même de compensation : 409 sinon), chemin par numéro et version ; permissions `invoices.*` revérifiées dans les services ; émission, avoir et envoi audités |
| R-09 | 3A | **Exports comptables** : en CSV, neutraliser les cellules texte qui commencent par `=`, `+`, `-`, `@`, tabulation ou retour chariot (préfixe `'`) ; en Excel, cellules texte (ClosedXML) |

## 5. Risques acceptés et recommandations

| ID | Gravité | Constat | Décision |
|---|---|---|---|
| A-01 | Info | Le tableau de bord Hangfire exige `script-src` et `style-src 'unsafe-inline'` (CSP propre à `/admin/jobs`) | Accepté : accès MFA et permission `jobs.dashboard` (gérant) ; jamais d'entrée utilisateur affichée |
| A-02 | Info | Hangfire sérialise les arguments avec les noms de types (Newtonsoft) | Accepté : exploiter cela demande déjà d'écrire dans la base ; les arguments sont des identifiants de tâche |
| A-03 | Faible | Mot de passe : règles de composition (ASVS 2.1.9 les déconseille) et pas de contrôle contre les mots de passe divulgués (ASVS 2.1.7) | Recommandation : longueur seule et contrôle par k-anonymat (Have I Been Pwned, appel externe) si l'agence l'accepte |
| A-04 | Faible | Pas de réauthentification avant une action sensible (approbation d'un remboursement, anonymisation, permissions) | Accepté pour l'instant : session à double authentification, 12 heures au plus, chaque action auditée |
| A-05 | Faible | La page de verrouillage, après 5 échecs, montre qu'un compte existe | Accepté : limite `account`, comptes du personnel peu nombreux |
| A-06 | Faible | Pas de réinitialisation de la double authentification d'un membre du personnel par le gérant (téléphone perdu sans codes de récupération) | Procédure SQL du [runbook](../runbook.md#double-authentification-perdue) ; fonction recommandée dans `/admin/users` (point ouvert) |
| A-07 | Info | Cookies sans préfixe `__Host-` | Recommandation (le changement de nom déconnecte tout le monde une fois) |
| A-08 | Info | Injection de formule dans les exports Excel | Sans objet : cellules texte, aucune saisie libre du public exportée |
| A-09 | Info | `Channels:Inbound:TrustArrClientCertHeader` ferait croire un en-tête forgé sans le mode certificat client d'App Service | Désactivé par défaut, documenté ([ADR-0053](../adr/0053-canaux-securite-entrante-idempotence-et-synchronisation.md)) |
| A-10 | Info | Application Insights accepte la chaîne de connexion (authentification locale) | Durcissement possible (ingestion Entra), README de l'infrastructure, section 14 |
| A-11 | Info | Points de terminaison publics du Key Vault et du stockage, règle « Autoriser les services Azure » sur SQL | Accepté à ce budget : Entra uniquement, rôles minimaux |
| A-12 | Info | Clés TOTP de l'authentificateur en clair dans `identity.UserTokens` (comportement d'Identity) | Accepté : base chiffrée au repos, accès Entra uniquement ; `ProtectPersonalData` possible plus tard |
| A-13 | Info | Pas d'analyse antivirus des fichiers déposés (option de la spécification) | Accepté : types limités (JPEG, PNG, PDF) vérifiés par les octets ; Defender for Storage est payant |
| A-14 | Faible | La déconnexion n'invalide pas un cookie volé (cookie sans état) | Accepté : 8 heures sans activité, 12 heures au plus ; le verrouillage par le gérant ou un changement de mot de passe coupe les sessions en une minute |
| A-15 | Info | Personnel non cloisonné par agence | Accepté (une agence) ; à revoir avant d'en ouvrir une seconde |
| A-16 | Info | CodeQL n'analyse qu'un dépôt public ou un dépôt avec GitHub Advanced Security | Vérifier la visibilité du dépôt ; sinon activer GHAS ou accepter l'absence d'analyse statique |

## 6. Vérifier soi-même

```bash
dotnet test tests/CarRental.Architecture.Tests --filter "FullyQualifiedName~Security|FullyQualifiedName~Observability"   # sans Docker
dotnet test tests/CarRental.Integration.Tests --filter "FullyQualifiedName~CarRental.Integration.Tests.Security"          # Docker
curl -s -o /dev/null -w '%{http_code}\n' https://www.<domaine>/health                                                     # 200
curl -sI https://<webApp>.azurewebsites.net/ | head -1                                                                    # 403 avec restrictToCloudflare
```

## 7. Passe 2 — après les fusions de 2C, 4B et 3A

- **Date** : 22/10/2026
- **Périmètre** : `main` au commit `0ad5170` — tunnel de réservation et « Ma réservation » (2C), agent IA WhatsApp (4B), facturation, amendes et sinistres (3A), journée de haute saison (1G) — hors lot 3B (dépenses, rapprochements, comptabilité, en cours dans une autre branche : ses exports comptables neutralisent eux-mêmes les formules, R-09).
- **Méthode** : celle de la passe 1 ; chaque point R-01 à R-09 relu dans le code fusionné ; régressions des corrections de la passe 1 et nouveaux points d'accès anonymes ; chaque correction a son test de non-régression, écrit d'abord et vu en échec. Décisions : [ADR-0066](../adr/0066-revue-de-securite-passe-2.md).

### 7.1 Synthèse

Aucun constat de gravité élevée. Les contrôles annoncés par 2C, 4B et 3A tiennent : jetons de 256 bits dont seule l'empreinte est stockée, codes salés comparés en temps constant et limités, liens à usage unique consommés par un clic, mêmes réponses qu'un dossier existe ou non, dépôts vérifiés par leur contenu sous un chemin fait d'identifiants, outils du bot en lecture seule (sauf le devis verrouillé et le transfert), statut d'un dossier réservé à ses numéros, factures écrites une seule fois et jamais supprimées, exports sans formule.

| | Constats | Corrigés dans cette passe | Acceptés |
|---|---|---|---|
| Gravité moyenne | 2 | 2 | — |
| Gravité faible | 3 | 3 | 1 |
| Info | — | — | 6 |
| Points R-01 à R-09 | 9 revérifiés | 6 conformes ; 3 corrigés (R-03, R-07 et le cache de R-08) ; R-05 conforme pour les pages, corrigé pour les journaux | — |

Les corrections : plus aucun jeton d'adresse dans les journaux, une entrée de santé `whatsapp` pour les tours du bot, Turnstile lié au site et jamais en clé de test en production, documents à données personnelles jamais conservés par le navigateur, adresses de retour sans caractère de contrôle.

### 7.2 Points R-01 à R-09

| ID | Gravité | Emplacement | Issue | Test |
|---|---|---|---|---|
| R-01 | — | `Domain/Bookings/Access/AccessSecrets.cs`, `BookingAccessGrant.cs`, `Application/Bookings/PublicAccess/MyBookingService.cs`, `Endpoints/PublicSite/MyBookingEndpoints.cs` | **Conforme.** Jeton de 256 bits (`RandomNumberGenerator`) en base64url, seul son SHA-256 stocké (index unique), forme vérifiée avant la base ; code à 6 chiffres uniforme, haché avec un sel de 16 octets ; comparaisons `FixedTimeEquals` ; lien à usage unique consommé par le POST du bouton (jamais par le GET des scanners), valable 30 minutes (`bookings.my_booking_link_validity`) ; 5 codes faux verrouillent la demande et son lien, une demande sans correspondance se comporte de même ; au plus 5 e-mails par dossier et par heure, au-delà traités comme sans correspondance : 25 essais de code par heure et par dossier au plus (ASVS 2.2.1 : 100) ; même réponse et mêmes requêtes que l'adresse corresponde ou non (reste l'ajout d'un message d'outbox dans le même enregistrement, cinq fois par heure au plus : non mesurable à travers le réseau) ; session limitée au dossier, revérifiée à chaque requête, 2 heures glissantes, 24 heures au plus. Écart corrigé : jetons dans les journaux (P2-01). Acceptés : A-17, A-18 | `MyBookingHttpTests`, `PublicBookingUnitTests`, `Security/AccessTokenLoggingTests` |
| R-02 | — | `Customers/Documents/DocumentImageStore.cs`, `StagedDocumentImagePurgeJob.cs`, `Bookings/PublicAccess/FunnelService.cs`, `MyBookingService.SendDocumentAsync` | **Conforme.** Type réel (JPEG, PNG, PDF) par la signature, nom et type annoncés ignorés ; 10 Mo pendant la lecture, requête bornée à 10 Mo plus 256 Kio (`RequestSizeLimit`) ; `public-upload` (40 par 10 minutes), 40 envois par session ; emplacement pris dans une liste connue ; chemins faits d'identifiants (`staged/{id}`, puis `persons/{id}/{id}` avec le contenu revérifié à la copie) ; la réponse est un texte, jamais l'image ni son adresse ; transit purgé toutes les heures (consommé, ou inutilisé depuis 26 heures), image remplacée supprimée par l'outbox ; lectures ultérieures par `DocumentImageAccess` seulement. Info : A-19 | `FunnelFlowTests`, `PublicSubmissionTests`, `MyBookingHttpTests` |
| R-03 | Faible | `Endpoints/PublicSite/Turnstile/TurnstileVerifier.cs`, `PublicForms.IsHumanAsync` | **Corrigé** (P2-03, commit `170d19d`) : nom d'hôte de la réponse comparé à celui de la requête, action déjà vérifiée, échec fermé ; réponses des clés de test refusées en production | `Public/TurnstileVerifierTests`, `Security/TurnstileSettingsTests`, `FunnelFlowTests` |
| R-04 | — | `Components/Public/Pages/Booking/PaymentRedirectPage.razor`, `Endpoints/PublicSite/BookingFunnelEndpoints.cs` | **Conforme.** Le formulaire répond 303 vers `/{culture}/reserver/redirection/{paiement}` (même origine, permis par `form-action 'self'`) ; cette page GET mène à la banque par `meta refresh` à 0 seconde et un lien : une navigation, que la CSP ne restreint pas ; aucune directive relâchée, jamais `unsafe-inline`. Pas de redirection ouverte : la cible est l'adresse `formUrl` enregistrée côté serveur (https, ou page locale du faux en développement), jamais un paramètre ; page réservée au navigateur de la demande (cookie `carrental.request`) ou de la session « Ma réservation » du dossier, pour un paiement en attente et non expiré. Info : A-21 | `FunnelFlowTests`, `MyBookingHttpTests`, `Security/PrivatePagesTests` |
| R-05 | Moyenne | Pages `Components/Public/Pages/Booking/*`, `MyBooking/*`, `Hosting/ObservabilitySetup.cs`, `PublicRateLimiting.cs` | **Conforme pour les pages, corrigé pour les journaux** (P2-01, commit `d6222dc`). Chaque page répond `no-store` et `noindex, nofollow` ; limites : `public-search` (pages, options, conducteurs, informations, déconnexion, documents), `public-submit` (choix, soumission, nouvelle tentative de garantie, demande d'accès, code, lien, annulation, paiement du solde), `public-upload` (photos) ; inventaire des points d'accès vert ; aucune donnée personnelle dans les messages de journal (numéros de dossier, identifiants et codes d'erreur seulement) | `Security/PrivatePagesTests`, `Security/EndpointInventoryTests`, `Security/AccessTokenLoggingTests` |
| R-06 | — | `Application/Messaging/Agent/**`, `Infrastructure/Messaging/Agent/**` | **Conforme.** `statut_reservation` ne répond que si le numéro WhatsApp (signé par Meta) est le téléphone du conducteur principal, du second conducteur ou du payeur — fiches qu'une saisie publique ne modifie jamais (ADR-0050) —, sinon la même réponse « introuvable » qu'une référence inconnue ; 3 recherches par conversation ; ni montant ni donnée personnelle dans le résultat. Outils en lecture seule sauf `creer_lien_devis` (devis verrouillé recalculé par le moteur, jamais un montant du modèle) et `transferer_a_un_agent` ; arguments validés contre le schéma ; résultats rendus comme données ; balise du contexte retirée des textes du client ; garde-fous du code sur le message qui part (montants, confirmation, demande de pièce, divulgation) ; plafonds de tours, de jetons, de coût et d'appels par tour ; pièces jointes jamais téléchargées ; modèle sans numéro, nom ni pièce ; clé `Ai:ApiKey` lue dans la configuration (Key Vault `Ai--ApiKey`), jamais journalisée, traces du SDK en métadonnées seules ; journaux de l'agent sans texte (test d'architecture). Info : A-22 | `WhatsAppAgentIntegrationTests`, `Messaging/Agent/WhatsAppAgentConventionTests`, jeu d'évaluation |
| R-07 | Moyenne | `Infrastructure/Messaging/Agent/WhatsAppHealthCheck.cs` | **Corrigé** (P2-02, commit `24e49e3`) : l'entrée `whatsapp` annoncée par l'ADR-0060 n'existait pas | `Observability/HealthTelemetryTests` |
| R-08 | Faible | `Billing/Documents/BillingDocumentPipeline.cs`, `Components/Admin/Billing/BillingWeb.cs`, `Components/Admin/Handovers/HandoversWeb.cs` | **Écriture unique conforme** (correction `8e44add` de l'orchestrateur vérifiée) : le pipeline lit d'abord le fichier déjà stocké et ne réécrit jamais, chemin `{année}/{numéro}-{id}.pdf`, aucune suppression dans le conteneur `invoices`, traduction de courtoisie jamais stockée. Permissions revérifiées dans les services (`invoices.read` ; `fines.manage` et `customers.documents.read` pour la désignation, qui journalise chaque pièce imprimée et un événement d'audit ; `claims.manage`), stratégies à double authentification, émission, avoir et envoi audités par l'intercepteur (acheteur masqué) ; la lecture d'une facture ou d'un fichier de sinistre n'est pas journalisée, comme les autres lectures du back office (seules les pièces d'identité et la désignation le sont : invariant 9). **Mise en cache corrigée** (P2-04, commit `fad78c3`) | `BillingIntegrationTests`, `FinesAndClaimsIntegrationTests`, `HandoverIntegrationTests`, `BillingDocumentPipelineTests` |
| R-09 | — | `Components/Admin/Reports/ReportWorkbook.cs` | **Conforme.** Hors 3B, le seul export est le classeur Excel des rapports (aucun CSV dans le dépôt ; Billing et Channels n'exportent rien) : ClosedXML 0.105 écrit chaque texte comme une cellule texte, le classeur n'écrit aucune formule ; la seule donnée du public (source de l'entonnoir) est limitée à `a-z0-9._-`. Le comportement est désormais figé par un test (les versions anciennes de ClosedXML lisaient `=…` comme une formule) | `Reporting/ReportExportTests.Texts_that_look_like_formulas_stay_texts_in_the_workbook` |

### 7.3 Nouveaux constats

| ID | Gravité | Référence | Emplacement | Constat | Correction | Test |
|---|---|---|---|---|---|---|
| P2-01 | Moyenne | A09 ; ASVS 7.1.1 | `Hosting/ObservabilitySetup.cs`, `Endpoints/PublicSite/PublicRateLimiting.cs` | Les traces écrivaient les jetons des adresses `{token}`, pas les journaux : la portée de requête d'ASP.NET Core (`IncludeScopes`, `RequestPath`) portait le chemin brut dans **chaque** journal écrit pendant une étape du tunnel ou l'ouverture d'un lien, et le refus d'une limite publique journalisait le chemin brut. Un lecteur d'Application Insights pouvait ouvrir une session du tunnel (conducteurs saisis : identité, téléphone, e-mail, numéros de pièces) ou un lien pas encore utilisé | Portées des journaux non exportées (le contexte de trace relie le journal à sa requête), chemin du refus 429 caviardé par `SecretPaths` (ADR-0066) | `Security/AccessTokenLoggingTests` (Kestrel réel et journaux OpenTelemetry exportés ; refus 429) |
| P2-02 | Moyenne | A09 ; SPEC §14 | `Infrastructure/Messaging/Agent/WhatsAppAgentSetup.cs`, `WhatsAppHealthCheck.cs` | Depuis l'ADR-0054, les tours du bot ne passent plus par l'outbox : exécuteur arrêté (instance sans serveur de tâches), verrou d'une conversation jamais libéré ou tour sans transfert laissaient des clients sans réponse ni alerte (« file WhatsApp bloquée ») ; l'entrée `whatsapp` prévue pour le lot 4B (R-07) n'avait pas été ajoutée | Entrée `whatsapp` (Degraded, jamais Unhealthy) : un message client attend le bot plus longtemps qu'un tour ne peut durer (silence, délais de nouvelle tentative, appels au modèle ; 20 minutes au moins), lue sur l'index filtré de la file ; publiée et alertée par `health-degraded` sans changer l'infrastructure | `Observability/HealthTelemetryTests` (entrée listée, conversation bloquée → Degraded, seuil) |
| P2-03 | Faible | A07 ; ASVS 2.2.1 | `Endpoints/PublicSite/Turnstile/TurnstileVerifier.cs`, `PublicSiteSetup.cs`, `PublicForms.cs` | Le nom d'hôte de `siteverify` n'était pas comparé (R-03) : un jeton obtenu sur un autre site portant la même clé de site était accepté. Les réponses des clés de test (sans action, hôte `example.com`, `result_with_testing_key`) faisaient échouer tout formulaire protégé en développement et auraient été acceptées en production avec un secret de test | Nom d'hôte de la requête exigé (sans casse, échec fermé s'il manque) ; réponses de clé de test acceptées hors production seulement, quelle que soit la configuration | `Public/TurnstileVerifierTests` (autre site, hôte absent, casse, clé de test), `Security/TurnstileSettingsTests`, `FunnelFlowTests` (hôte transmis) |
| P2-04 | Faible | A04 ; ASVS 8.2.1 | `Components/Admin/Billing/BillingWeb.cs`, `Components/Admin/Handovers/HandoversWeb.cs` | Les PDF des factures et des avoirs, les fichiers des sinistres, les contrats et les signatures répondaient `private, no-cache` avec un `ETag` : le navigateur d'une tablette partagée les gardait sur disque (acheteur, conducteur, papiers d'un sinistre, signature manuscrite) au-delà de la session | `no-store, private`, comme les pièces d'identité et les exports ; les photos des véhicules gardent leur cache privé revalidé (ADR-0066) | `BillingIntegrationTests`, `FinesAndClaimsIntegrationTests` (téléchargement d'un fichier de sinistre), `HandoverIntegrationTests` |
| P2-05 | Faible | A01 ; ASVS 5.1.5 | `Localization/CultureCatalog.cs` (`LocalUrls`) | `LocalUrls.IsLocal` acceptait les caractères de contrôle, qu'un navigateur retire : `returnUrl=/%09/exemple.com` devenait `//exemple.com` — redirection ouverte par le lien « Continuer » de l'activation de la double authentification ; et le sélecteur de langue anonyme sans limite (`/culture/set`) comme la déconnexion levaient une exception (`LocalRedirect`) : une erreur 500, un journal d'erreur et un pas vers l'alerte `exceptions` par requête, pour n'importe quel visiteur | Caractères de contrôle refusés comme dans `IsLocalUrl` d'ASP.NET Core : retour à l'accueil | `Security/LocalUrlTests`, `Security/ReturnAddressTests` (500 avant, redirection vers `/` après) |

### 7.4 Régressions de la passe 1 et nouveaux points d'accès

- **Corrections de la passe 1 après les fusions** : aucune régression. `CF-Connecting-IP` non cru par défaut et couplé à `restrictToCloudflare` (S-01), politique `account` sur toutes les pages de compte (S-02), seuil des sondes avant tout le pipeline (S-03 ; l'entrée `whatsapp` ajoute deux requêtes sur un index filtré), durée de vie absolue des sessions (S-04), politiques `public-media` et `public-search` (S-05, S-08), identifiant tapé masqué (S-06) : tests verts. CSP par zone inchangée : vitrine et tunnel sans `unsafe-inline`, script et cadre de Turnstile ajoutés aux seules réponses des deux formulaires protégés, module d'envoi des photos servi par le site, aperçus en `blob:`.
- **Nouveaux points d'accès anonymes (2C)**, tous limités et inscrits à l'inventaire : choix d'une catégorie, soumission, nouvelle tentative de garantie, demande d'accès, code, lien, annulation et paiement du solde (`public-submit`) ; étapes options et conducteurs, informations, déconnexion, documents et pages (`public-search`) ; photos (`public-upload`). `POST /api/public/quotes` et `GET /api/public/quotes/{code}` : `X-Api-Key` (256 caractères au plus) comparée en temps constant sur les empreintes SHA-256, toutes les clés comparées sans sortie anticipée, aucune clé configurée = tout est refusé, jamais journalisée ni renvoyée, `public-search`, `no-store`.
- **4B et 3A** n'ajoutent aucun point d'accès anonyme (écrans et flux du back office sous stratégie à double authentification). Webhook WhatsApp et point d'entrée des canaux : inchangés (signature ou clé, politiques propres).

### 7.5 Risques acceptés et recommandations (passe 2)

| ID | Gravité | Constat | Décision |
|---|---|---|---|
| A-17 | Faible | Lien et code « Ma réservation » valables 30 minutes, au-delà des 10 minutes de l'ASVS 2.7.2 ; les bornes des réglages permettent 50 demandes par heure et 10 essais par code (500 essais par heure et par dossier) | Accepté : usage unique, 5 essais, 5 demandes par heure, session limitée au dossier ; e-mail souvent lu sur un autre appareil, à l'étranger. Recommandation : 15 minutes, et le produit `bookings.my_booking_requests_per_hour × bookings.my_booking_code_attempts` jamais au-delà de 100 ([points ouverts](../points-ouverts.md)) |
| A-18 | Info | Le jeton et le code d'un lien sont dans les messages d'outbox de l'e-mail (`AccessEmailRequested`, `EmailRequested`) jusqu'à la purge des messages traités (30 jours) | Accepté : valables 30 minutes (24 heures pour « Compléter mon dossier »), à usage unique ; lire la base donne déjà le dossier ; l'écran `/admin/outbox` n'affiche jamais un contenu |
| A-19 | Info | Un dépôt est lu en entier (10 Mo, deux copies en mémoire) avant le contrôle de sa signature | Accepté : session du tunnel ou de « Ma réservation » exigée, 40 dépôts par 10 minutes et par adresse. Recommandation : refuser dès le premier bloc lu, sans copie finale |
| A-20 | Info | Les formulaires et l'API anonymes acceptent le corps par défaut de Kestrel (30 Mo) | Accepté : limites de débit par adresse. Recommandation : abaisser `MaxRequestBodySize` (1 Mo), les dépôts gardant leur limite explicite |
| A-21 | Info | La page vers la banque croit le cookie de session « Ma réservation » sans revérifier la demande côté serveur | Accepté : elle ne montre que le montant et mène à la page de paiement du dossier du cookie (protégé, 24 heures au plus, effacé à la déconnexion) |
| A-22 | Info | Coût de l'agent plafonné par conversation (0,50 USD), sans plafond global | Recommandation : limite de dépense chez le fournisseur (console Anthropic, budget Azure pour Foundry) et ouverture par numéros pilotes |
| A-23 | Info | Un niveau `Information` pour `Microsoft.AspNetCore` journaliserait les adresses complètes (« Request starting… »), jetons compris ; les journaux HTTP d'App Service (`AppServiceHTTPLogs`, non activés par l'infrastructure) aussi | Garder `Microsoft.AspNetCore: Warning` (valeur de `appsettings.json`) et ne pas activer les journaux HTTP d'App Service ; n'abaisser le niveau que le temps d'un diagnostic |

### 7.6 Avant la mise en production

En plus de la [liste du runbook](../runbook.md#avant-la-mise-en-production) et des points acceptés de la passe 1 :

- **Turnstile** : dans le widget de Cloudflare, déclarer exactement les noms d'hôte du site (domaine, `www`, recette) — une réponse venue d'un autre hôte est désormais refusée — et ranger les vraies clés (`Turnstile__SiteKey`, `Turnstile--SecretKey`) : une clé de test est refusée en production.
- **Alertes** : l'entrée `whatsapp` est couverte par `health-degraded` dès `ALERT_EMAILS` ; compléter la section « Entrée whatsapp » du runbook (prendre la main dans `/admin/whatsapp`, vérifier `BackgroundJobs:RunServer`, la configuration `Ai` et le journal de l'exécuteur).
- **Agent IA** : secret `Ai--ApiKey` (ou `Ai--Foundry--ApiKey`) dans le Key Vault, limite de dépense chez le fournisseur, numéros pilotes au début, évaluation contre le vrai modèle.
- **Journaux** : garder `Microsoft.AspNetCore` au niveau `Warning` et ne pas activer les journaux HTTP d'App Service (A-23) : les adresses du tunnel et de « Ma réservation » portent des jetons.
- **« Ma réservation »** : décider de la durée du lien et du code (A-17).

### 7.7 Vérifier soi-même

```bash
dotnet test tests/CarRental.Architecture.Tests --filter "FullyQualifiedName~Security"                     # sans Docker
dotnet test tests/CarRental.Integration.Tests --filter "FullyQualifiedName~Security|FullyQualifiedName~HealthTelemetryTests|FullyQualifiedName~TurnstileVerifierTests"
curl -s https://www.<domaine>/health/ready                                                               # entrée « whatsapp » listée
```
