# Revue de sécurité — OWASP Top 10 2021 et ASVS 4.0.3 niveau 2 (octobre 2026, passe 1)

- **Date** : 21/10/2026
- **Périmètre** : l'application fusionnée sur `main` au commit `a4f396c` — socle (phase 0), exploitation (1A à 1F-b, dont départs et retours), vitrine 2A, paiement en ligne 2B, finance et pilotage 3B et 3C, canal WhatsApp 4A, canaux de vente 5A — et l'infrastructure (`infra/`, `.github/workflows/`).
- **Hors périmètre** (lots en cours dans d'autres branches, à revoir après leur fusion, [section 4](#4-à-revoir-après-les-fusions)) : tunnel de réservation et « Ma réservation » (2C), agent IA WhatsApp (4B), facturation (3A).
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

Ces points touchent des fichiers des lots en cours (`Components/Public/**` du tunnel, `Endpoints/PublicSite/**`, `**/Messaging/**`, `**/Billing/**`) ; ils sont à vérifier sur `main` après chaque fusion, avec l'inventaire des points d'accès et les tests de `Security/`.

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
