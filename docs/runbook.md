# Runbook d'exploitation

Pour le prestataire informatique de l'agence : quoi faire quand une alerte arrive, comment déployer, revenir en arrière, faire tourner un secret, restaurer, traiter une demande d'une personne ou un compte du personnel bloqué. L'infrastructure elle-même (création, coûts, paramètres) est décrite dans le [README de l'infrastructure](../infra/README.md) ; les décisions dans les [ADR](adr/README.md).

**Conventions.** Commandes pour bash (Git Bash sous Windows) avec Azure CLI 2.60 ou plus récent, connecté (`az login`) avec un compte du groupe Entra « CarRental SQL Admins » ; `<…>` est à remplacer (noms : sorties du déploiement Bicep, README de l'infrastructure, section 5.3). Production : groupe de ressources `rg-carrental-prod`, recette : `rg-carrental-recette`. Heures en heure de Tunis sauf mention UTC.

## Sommaire

- [L'architecture en bref](#larchitecture-en-bref)
- [Environnements](#environnements)
- [Déployer et revenir en arrière](#déployer-et-revenir-en-arrière)
- [Secrets et rotation](#secrets-et-rotation)
- [Alertes : signification et premières actions](#alertes--signification-et-premières-actions)
- [Messages de fond en échec](#messages-de-fond-en-échec)
- [Canaux : synchronisation et rejeu](#canaux--synchronisation-et-rejeu)
- [WhatsApp : reprendre la main quand le bot est en panne](#whatsapp--reprendre-la-main-quand-le-bot-est-en-panne)
- [Clic to Pay : rapprochement et paiements bloqués](#clic-to-pay--rapprochement-et-paiements-bloqués)
- [Clôture mensuelle](#clôture-mensuelle)
- [Restaurations](#restaurations)
- [Demandes des personnes](#demandes-des-personnes)
- [Agences de la plateforme](#agences-de-la-plateforme)
- [Comptes du personnel](#comptes-du-personnel)
- [Cloudflare et DNS](#cloudflare-et-dns)
- [Avant la mise en production](#avant-la-mise-en-production)
- [Contacts et escalade](#contacts-et-escalade)
- [Annexe : requêtes et commandes utiles](#annexe--requêtes-et-commandes-utiles)

## L'architecture en bref

- **Une seule application** .NET 10 (`CarRental.Web`) sur un App Service Linux **B1** (une instance, France Central) : site public, back office `/admin` (Blazor interactif, WebSocket), API des canaux (`/api/channels/…`), webhook WhatsApp (`/webhooks/whatsapp`), retours de paiement (`/payments/clictopay/…`) et tâches de fond (Hangfire, dans le même processus).
- **Cloudflare** (offre gratuite) devant : DNS, CDN, anti-DDoS, Turnstile. Une fois `restrictToCloudflare` activé, l'App Service n'accepte que les adresses de Cloudflare.
- **Azure SQL Basic** (5 DTU, 2 Go, authentification Entra uniquement) : données, journal d'audit, tâches Hangfire, outbox. **Blob Storage** privé (pièces d'identité, photos, contrats, factures, trousseau Data Protection). **Key Vault** (secrets des partenaires, clé `dataprotection`). **Application Insights** (traces, journaux, métriques, plafond journalier). **Communication Services** (e-mails).
- **Effets de bord par l'outbox** : e-mails, messages WhatsApp, tours du bot, synchronisations ; un message qui échoue est retenté avec des délais croissants, puis marqué en échec après 10 tentatives.
- **Santé** : `/health` (vivant, sans base) et `/health/ready` (base, stockage, Hangfire, tâches, outbox, canaux, paiements ; une entrée dégradée ne rend jamais l'application indisponible). L'application publie chaque minute l'état de chaque entrée dans Application Insights (`carrental.health.status`) : c'est ce que surveillent les alertes.

## Environnements

| | Production | Recette |
|---|---|---|
| Groupe de ressources | `rg-carrental-prod` | `rg-carrental-recette` |
| App Service | B1, Always On, contrôle de santé | F1 gratuit : s'endort sans visite, 60 min de processeur par jour |
| Adresse | `https://www.<domaine>` (Cloudflare) ; `https://<webApp>.azurewebsites.net` répond 403 une fois l'origine réservée | `https://<webApp>.azurewebsites.net` |
| Base | Basic, sauvegardes géo-redondantes, rétention longue (5 semaines, 12 mois, 10 ans) | offre gratuite serverless (se met en pause) |
| Données | réelles | démonstration (`gerant@demo.local`, `agent@demo.local`, `comptable@demo.local`, mot de passe et clé TOTP dans le Key Vault de la recette) |
| Alertes | si `ALERT_EMAILS` est défini au déploiement | aucune par défaut |
| Déploiement | chaque nuit à 02:00 si `main` a changé et que la CI est verte, ou à la demande | à la demande |

## Déployer et revenir en arrière

**Déploiement** (GitHub > Actions > **Deploy**) : publication, bundle de migrations EF Core, ouverture temporaire du pare-feu SQL à l'adresse du runner, **migrations appliquées avant le nouveau code**, déploiement zip, redémarrage, attente de `/health/ready` (200) pendant une dizaine de minutes au plus ; en production, l'étiquette `deployed-prod` marque le commit déployé. La production ne se déploie que depuis `main`, avec une CI verte sur ce commit. Un déploiement redémarre l'application (B1 n'a pas d'emplacement de déploiement) : quelques secondes d'indisponibilité, d'où la nuit.

**Après un déploiement** : `https://www.<domaine>/health/ready` répond 200 ; dans Application Insights, pas de pic d'exceptions ; `/admin/jobs` montre les tâches récurrentes planifiées ; aucune alerte dans l'heure. En cas de doute, le résumé du travail GitHub affiche la réponse de `/health/ready`.

**Revenir en arrière** :

1. **Code** : `git revert` du ou des commits fautifs sur `main`, attendre la CI verte, puis Deploy > `prod` à la main. C'est la seule voie normale : on ne redéploie jamais un ancien paquet à la main.
2. **Schéma** : les migrations sont écrites pour que l'ancien code tourne sur le nouveau schéma (on ajoute, puis on retire en deux temps) : un retour de code ne demande pas de retour de schéma. Un retour de schéma (`./efbundle <MigrationPrécédente> --connection "<chaîne>"`) peut perdre des données : seulement si la migration elle-même est fautive, après une sauvegarde à l'instant (la restauration à un instant suffit).
3. **Données** : restaurer la base ([Restaurations](#restaurations)) est le dernier recours (perte des écritures depuis l'instant choisi).

**Mise hors service d'urgence** (fuite de données en cours, attaque) : `az webapp stop --resource-group rg-carrental-prod --name <webApp>` (App Service répond alors par sa page « site arrêté »), puis `az webapp start` ; prévenir l'agence et, si des données personnelles sont touchées, l'avocat (notification à l'INPDP).

## Secrets et rotation

Les secrets sont dans le Key Vault de l'environnement (`kv-carrental-prd-…`), lus au démarrage par l'application (identité managée). Les membres du groupe « CarRental SQL Admins » ont le rôle Key Vault Secrets Officer. Aucun secret n'est dans le dépôt ni dans les paramètres d'application.

| Secret (Key Vault) | Usage | Où le renouveler | Après le changement |
|---|---|---|---|
| `ClicToPay--UserName`, `ClicToPay--Password` | API Clic to Pay (paiement en ligne) | Espace marchand SMT / la banque | Redémarrer, puis un paiement test de 1,000 TND en recette ([recette Clic to Pay](recette-clictopay.md)) |
| `WhatsApp--AccessToken` | Envoi des messages (API Graph) | Meta Business > Utilisateurs système > générer un jeton (permanent) | Redémarrer ; les messages en attente repartent (l'outbox retente) |
| `WhatsApp--AppSecret` | Vérification de la signature du webhook | Meta for Developers > application > Paramètres > Général | Redémarrer **aussitôt** : entre le changement chez Meta et le redémarrage, les livraisons sont refusées (401) et Meta les relivre plus tard |
| `WhatsApp--VerifyToken` | Abonnement du webhook | Valeur choisie par vous (longue, aléatoire) | Redémarrer, puis la saisir dans la configuration du webhook chez Meta |
| `Turnstile--SecretKey` | Vérification anti-robot du tunnel | Cloudflare > Turnstile > widget > « Rotate secret key » | Enregistrer la nouvelle clé et redémarrer aussitôt (les soumissions sont refusées tant que l'application garde l'ancienne) ; la clé de site (`Turnstile__SiteKey`, paramètre d'application, non secrète) ne change pas |
| `CustomerApi--Jwt--SigningKey` | Signature des jetons d'accès de l'API des applications clientes (ADR-0070) | `openssl rand -base64 48`, nouvelle valeur dans le Key Vault | Redémarrer : les jetons d'accès en cours (15 min au plus) deviennent invalides, les applications se rafraîchissent avec leur jeton de rafraîchissement sans reconnexion |
| Clé du fournisseur d'IA : `Ai--ApiKey` (API Claude) ou `Ai--Foundry--ApiKey` (Microsoft Foundry, facultative : sans clé, identité managée) | Agent WhatsApp | Console du fournisseur | Redémarrer ; révoquer l'ancienne clé |
| `Seed--DemoUserPassword`, `Seed--DemoAuthenticatorKey` | Comptes de démonstration (recette seulement) | Valeurs choisies par vous | Redémarrer la recette |
| Clé `dataprotection` (clé RSA, pas un secret) | Protège les cookies de session et les jetons | Rotation automatique annuelle | Ne **jamais** désactiver ni supprimer une ancienne version (déconnexion de tous, jetons invalides) |

Pas de secret pour Azure SQL, le stockage, Application Insights ni Communication Services : identité managée et rôles.

**Procédure** :

```bash
az keyvault secret set --vault-name <coffre> --name "WhatsApp--AccessToken" --file ./jeton.txt   # puis supprimer le fichier
az webapp restart --resource-group rg-carrental-prod --name <webApp>
curl -s https://www.<domaine>/health/ready                                                       # 200
```

**Revenir à l'ancienne valeur** : les versions précédentes restent dans le Key Vault ; relire l'ancienne (`az keyvault secret list-versions …`, `az keyvault secret show --vault-name <coffre> --name <secret> --version <id> --query value -o tsv > ./ancien.txt`), la réenregistrer comme nouvelle version, redémarrer.

**Fuite présumée d'un secret** : le renouveler chez le partenaire d'abord (l'ancien doit cesser de fonctionner), puis dans le Key Vault, redémarrer, et lire le journal d'audit du Key Vault dans Log Analytics (`AzureDiagnostics | where ResourceProvider == "MICROSOFT.KEYVAULT"`) pour voir qui l'a lu.

## Alertes : signification et premières actions

Les alertes arrivent par e-mail (groupe d'actions `ag-carrental-prod`, schéma d'alerte commun) ; un second e-mail annonce leur résolution. Le détail se lit dans le portail (Monitor > Alertes) et dans Application Insights (Journaux) ; les descriptions des entrées de santé ne sont pas publiées sur `/health/ready` en production, mais chaque changement d'état est journalisé :

```kusto
traces
| where message startswith "Health entry"
| project timestamp, severityLevel, message
| order by timestamp desc
```

### Alerte site-down

Le contrôle de santé d'App Service échoue depuis au moins dix minutes : le site ne répond pas, ou `/health/ready` répond 503 (base ou stockage injoignable).

1. `curl -s -w '\n%{http_code}\n' https://www.<domaine>/health/ready` ; portail > App Service > « Diagnostiquer et résoudre les problèmes » > disponibilité ; flux de journaux (`az webapp log tail --resource-group rg-carrental-prod --name <webApp>`).
2. Base : portail > base SQL > Vue d'ensemble (pause, quota, DTU) ; « Resource Health » de la base et de l'App Service (incident Azure ?).
3. Un déploiement en cours ou raté (GitHub > Actions > Deploy) : laisser finir, ou revenir en arrière.
4. Redémarrer : `az webapp restart …`. App Service remplace d'elle-même une instance en échec depuis une heure.

### Alerte http-5xx

Plus de 10 réponses 5xx en 15 minutes.

1. Application Insights > Échecs : quelle page, quelle exception ; `requests | where toint(resultCode) >= 500 | summarize count() by name, resultCode`.
2. Après un déploiement : revenir en arrière ; sinon, base saturée (alerte `sql-dtu`) ou dépendance en panne (Clic to Pay, Meta, ACS) : voir les dépendances en échec.

### Alerte exceptions

Plus de 20 exceptions en 15 minutes, même sans erreur visible (tâches de fond, outbox).

1. `exceptions | summarize count() by type, outerMessage | top 20 by count_` ; cliquer une exception pour sa trace complète.
2. Une seule cause qui se répète (secret expiré, partenaire injoignable) : voir la section de ce partenaire ; un bogue : ticket au développement avec l'identifiant de trace.

### Alerte health-degraded

Une entrée de santé n'a pas été saine pendant 15 minutes ; la dimension `entry` de l'alerte la nomme. Chaque entrée redevient saine d'elle-même quand la cause est traitée, et l'alerte se résout.

#### Entrée sql

Base injoignable ou trop lente : comme [site-down](#alerte-site-down), point 2.

#### Entrée storage

Le compte de stockage ne répond pas avec l'identité de l'application : « Resource Health » du compte ; rôle « Storage Blob Data Contributor » de l'identité managée (README de l'infrastructure, section 2) ; après une recréation de la Web App, l'identité a changé : redéployer l'infrastructure.

#### Entrée hangfire

Aucun serveur de tâches vivant depuis deux minutes : redémarrer l'application ; si cela se répète, chercher l'exception au démarrage du serveur Hangfire dans les journaux.

#### Entrée jobs

Une tâche récurrente a échoué à sa dernière exécution, n'est plus planifiée ou ne se déclenche plus — notamment **`fleet.deadline_alerts`, l'e-mail quotidien des échéances de flotte** (07:00), le rattrapage des paiements (`payments.online_catch_up`, toutes les 15 minutes), les purges et l'instantané quotidien.

1. `/admin/jobs` (gérant) > Tâches récurrentes : la tâche en cause, sa dernière exécution ; > Échouées : l'exception.
2. Corriger la cause, puis « Déclencher maintenant » sur la tâche récurrente (ou « Relancer » la tâche échouée). Pour les échéances de flotte, vérifier aussi `/admin/fleet/deadlines` : l'écran montre les échéances même sans e-mail.
3. L'entrée redevient saine à la prochaine exécution réussie (l'évaluation est gardée 5 minutes).

#### Entrée outbox

Un message de fond est en échec définitif (10 tentatives) ou le plus ancien attend depuis plus de 30 minutes : e-mails, notifications, **file WhatsApp** (réception des messages, envois, tours du bot), synchronisations. Voir [Messages de fond en échec](#messages-de-fond-en-échec).

#### Entrée channels

Un envoi vers un canal de vente est en échec définitif, ou le plus ancien envoi dû attend depuis plus de 30 minutes. Voir [Canaux](#canaux--synchronisation-et-rejeu).

#### Entrée payments

Un paiement en ligne est signalé à vérifier, ou une capture, une annulation ou un remboursement envoyé à la passerelle n'est toujours pas confirmé après une heure. Voir [Clic to Pay](#clic-to-pay--rapprochement-et-paiements-bloqués).

#### Entrée whatsapp

Un message client attend le bot depuis plus de 20 minutes : l'exécuteur des tours ne tourne plus, ou une conversation reste bloquée.

1. Prendre la main sur ces conversations dans `/admin/whatsapp` (le bot se tait) et répondre aux clients.
2. Vérifier `BackgroundJobs__RunServer=true` et l'entrée `jobs`, puis chercher dans les journaux « WhatsApp bot turn executor: … ».
3. Vérifier la configuration de l'IA : `Ai__Mode`, le secret `Ai--ApiKey`, le réglage `ai.enabled` et la limite de dépense chez le fournisseur.

L'alerte se résout d'elle-même quand plus aucun message n'attend. Voir aussi [WhatsApp](#whatsapp--reprendre-la-main-quand-le-bot-est-en-panne).

### Alerte payments-gateway

Au moins 3 appels à Clic to Pay en échec technique en 15 minutes (pas de réponse, réponse illisible, requête refusée) : ce ne sont pas des cartes refusées.

1. Le détail de chaque appel (opération, code d'erreur de la passerelle, durée) est dans `/admin/payments` > Paiements en ligne > un paiement > historique des appels, et dans `customMetrics | where name == "carrental.clictopay.calls"`.
2. Code d'erreur d'accès refusé ou d'identifiants : identifiants changés ou expirés : [Secrets](#secrets-et-rotation). Délais dépassés, pas de réponse : panne chez SMT (support de la banque) ; les paiements déjà commencés seront confirmés par le rattrapage dès le retour de la passerelle.
3. Panne longue en pleine saison : informer l'agence (les clients paient au comptoir, le bouton WhatsApp reste disponible). En dernier recours seulement, désactiver le paiement en ligne (`ClicToPay__Enabled=false` en paramètre d'application, redémarrage) : le tunnel ne propose plus le paiement.

### Alerte telemetry-missing

Aucune mesure de santé reçue depuis 30 minutes.

1. Le site répond-il (`/health`) ? Non : [site-down](#alerte-site-down).
2. Oui : la télémétrie n'arrive plus. Cause la plus probable : **plafond journalier de Log Analytics atteint** (`logDailyCapGb`, 0,15 Go) — portail > Log Analytics > Utilisation et coûts estimés ; l'ingestion reprend à la remise à zéro quotidienne et les autres alertes de télémétrie se taisent jusque-là. Chercher ce qui a gonflé le volume (`union * | summarize sum(_BilledSize) by Type`) ; relever le plafond seulement si le volume est normal. Sinon, paramètre `APPLICATIONINSIGHTS_CONNECTION_STRING` de l'App Service.

### Alerte sql-capacity

`sql-dtu` : la base Basic travaille au-dessus de 90 % depuis 30 minutes (lenteurs). `sql-storage` : elle occupe plus de 80 % de ses 2 Go (au-delà, les écritures échouent).

1. DTU : portail > base > « Performance Insight » (requêtes coûteuses) ; une rafale passagère (purge nocturne, resynchronisation des canaux) se calme seule. Durable : passer en S0 (README de l'infrastructure, section 9).
2. Stockage : portail > base > « Stockage » par table ; les tables qui grandissent sans limite sont le journal d'audit et les journaux d'appels ; passer en S0 (250 Go inclus) avant 2 Go.

### Alerte availability

Seulement si l'agence a activé le test standard (`availabilityTestUrl`) : `/health` injoignable depuis Internet alors que `site-down` peut ne rien voir (DNS, Cloudflare, certificat). Voir [Cloudflare et DNS](#cloudflare-et-dns). Même conduite pour une alerte d'une surveillance externe (UptimeRobot ou équivalent).

### Alertes de budget

E-mail du budget Azure (80 % du budget réel, 100 % de la prévision) : portail > Gestion des coûts > Analyse des coûts par ressource ; cause habituelle : une ressource passée par erreur à une offre supérieure, ou le plafond de Log Analytics relevé.

## Messages de fond en échec

`/admin/outbox` (gérant, permission `outbox.manage`) : état de la file (en échec, en attente, dont en nouvelle tentative, plus ancien en attente) et liste des messages non traités, avec leur type, leur nombre d'échecs et la dernière erreur ; le contenu n'est jamais affiché (données personnelles).

1. Lire la dernière erreur : destinataire refusé par ACS, partenaire injoignable, jeton WhatsApp expiré (`WhatsApp--AccessToken`), bogue.
2. Corriger la cause, puis **« Relancer »** : le message repart aussitôt avec dix nouvelles tentatives ; la relance est auditée. Les gestionnaires sont idempotents : relancer ne duplique pas un effet déjà produit (un message WhatsApp resté « envoi incertain » n'est jamais renvoyé : l'agent vérifie avec le client).
3. Un message sans objet (dossier supprimé depuis) : le relancer après correction le termine ; il est purgé 30 jours après son traitement.
4. **File bloquée** (le plus ancien attend, rien n'est en échec) : `/admin/jobs` > tâche `outbox.process` et serveur Hangfire ; redémarrer l'application si le serveur est absent.

## Canaux : synchronisation et rejeu

- **Envois vers un canal** (disponibilités, prix) : `/admin/channels/sync` liste les envois en attente ou en échec avec la dernière erreur et le message envoyé. **« Renvoyer »** remet un envoi en file aussitôt (tentatives remises à zéro) ; **« Synchroniser maintenant »** lance un passage. Une resynchronisation complète part chaque nuit à 02:40 et à chaque connexion d'un canal. Le canal reçoit toujours l'état courant, jamais une suite de changements périmés.
- **Messages reçus d'un canal** : `/admin/channels/log` (refusés, rejetés, en erreur, avec le code et l'adresse). Adresse refusée : vérifier la liste d'adresses du canal (`/admin/channels/<canal>`) ; identifiants refusés : la clé a peut-être été renouvelée ou révoquée — en générer une nouvelle (montrée une seule fois) et la transmettre au partenaire par un canal sûr.
- **Canal manuel (Booking.com tant que le connecteur n'existe pas)** : `/admin/channels/extranet` liste les arrêts de vente et les quotas à reporter dans l'extranet du canal ; `/admin/channels/manual-entry` pour saisir une réservation du canal.
- Un canal en panne longue : le mettre en pause ou le déconnecter depuis son écran ; ses envois sont abandonnés, la resynchronisation complète repart à la reconnexion.

## WhatsApp : reprendre la main quand le bot est en panne

La boîte partagée `/admin/whatsapp` (permission `whatsapp.inbox`) montre toutes les conversations. Un agent **« Prendre la main »** sur une conversation : le bot se tait, les réponses du bot encore en file sont annulées ; l'agent répond librement dans les 24 heures qui suivent le dernier message du client, avec un modèle approuvé au-delà ; **« Rendre au bot »** quand c'est réglé.

- **Bot en panne ou qui répond mal** (lot 4B) : prendre la main sur les conversations ouvertes, prévenir le développement ; l'interrupteur général du bot sera décrit ici à la fusion du lot 4B (à compléter). Les clients peuvent toujours appeler l'agence.
- **Plus aucun message reçu** : configuration du webhook chez Meta (adresse `https://www.<domaine>/webhooks/whatsapp`, jeton `WhatsApp--VerifyToken`, abonnement au champ `messages`) ; journaux « WhatsApp webhook delivery … refused: missing or invalid signature » (secret `WhatsApp--AppSecret` différent de celui de l'application Meta) ; une règle Cloudflare (WAF, « Bot Fight Mode ») qui bloquerait Meta : exclure `/webhooks/*` de ces protections ; réponses 429 (limite `whatsapp-webhook`, 600 par minute).
- **Plus aucun message envoyé** : journal critique « WhatsApp Graph API refused the access token » : renouveler `WhatsApp--AccessToken` ; les messages sont retentés par l'outbox (5 tentatives) puis à relancer dans `/admin/outbox`.
- Rappel : aucune pièce d'identité ni carte bancaire ne s'échange par WhatsApp ; le client dépose ses pièces par le lien sécurisé.

## Clic to Pay : rapprochement et paiements bloqués

Le statut d'un paiement ne vient que de la passerelle, de serveur à serveur. Un rattrapage relit toutes les 15 minutes les paiements en attente et les opérations non confirmées ; un rapprochement compare chaque nuit à 04:30 les paiements des 7 derniers jours avec la passerelle et signale les écarts.

`/admin/payments` > onglet **Paiements en ligne** > filtre « À vérifier » : chaque paiement signalé, son motif et l'historique des appels. **« Vérifier le statut »** relit la passerelle ; **« Marquer comme vérifié »** retire le signalement une fois traité (l'entrée de santé `payments` redevient saine quand plus rien n'est signalé).

| Signalement | Que faire |
|---|---|
| Statut inconnu | La passerelle renvoie un code que la plateforme ne connaît pas encore : relever le code dans l'historique, le faire confirmer par SMT et le configurer (`ClicToPay__ReversedStatusCodes`, `RefundedStatusCodes`, `DeclinedStatusCodes`) ; d'ici là, vérifier dans l'espace marchand |
| Statut ou montant différent | Comparer avec l'espace marchand de Clic to Pay ; corriger le dossier (montant, caution) ; marquer comme vérifié |
| Paiement tardif | Le client a payé après l'expiration de sa commande : l'argent est enregistré ; revoir le dossier (confirmer, ou rembourser) |
| Capture non confirmée | La validation a demandé la capture sans la voir confirmée : vérifier dans l'espace marchand **avant** de valider à nouveau (jamais deux captures) |
| Annulation non confirmée | La pré-autorisation semble encore active : vérifier dans l'espace marchand ; le rattrapage renvoie l'annulation |
| Remboursement non confirmé | Après 24 heures sans confirmation : vérifier dans l'espace marchand ; ne jamais le renvoyer à l'aveugle |
| Opération refusée | La passerelle a refusé une capture, une annulation ou un remboursement : code d'erreur dans l'historique ; le traiter avec SMT |

Un remboursement au-delà du seuil (`payments.online_refund_approval_threshold`, 100,000 TND) attend la validation d'un gérant (permission `payments.refunds.approve`). Le relevé marchand de SMT n'est pas encore importé (format inconnu) : rapprochement manuel mensuel avec l'espace marchand.

## Clôture mensuelle

À compléter avec les lots de finance (factures du lot 3A, exports comptables) : procédure de clôture du mois, exports pour l'expert-comptable, contrôles d'équilibre. Ce qui existe déjà : sessions de caisse verrouillées chaque jour (`/admin/cash`), rapprochement des paiements en ligne (ci-dessus) et des canaux (`/admin/channels/reconciliation`), indicateurs et exports Excel (`/admin/reports`).

## Restaurations

Ce qui est sauvegardé et combien de temps : README de l'infrastructure, [section 16](../infra/README.md#16-sauvegardes-rétention-et-immuabilité). Avant toute restauration de production : noter l'heure de l'incident, prévenir l'agence (le site peut être indisponible quelques minutes pendant l'échange), et faire une restauration d'essai sur un nom temporaire.

### Base de données : restauration à un instant (7 derniers jours)

```bash
RG=rg-carrental-prod; SERVER=<sqlServerName>
# 1. Nouvelle base à l'instant choisi (UTC), à côté de la base courante (20 à 60 minutes).
az sql db restore --resource-group $RG --server $SERVER --name carrental \
  --dest-name carrental-restore --time "2026-11-02T08:00:00Z"
# 2. Vérifier la base restaurée (éditeur de requête du portail, authentification Entra) : les dernières données attendues.
# 3. Arrêter le traitement des messages de fond pour la bascule : paramètre d'application BackgroundJobs__RunServer=false.
az webapp config appsettings set --resource-group $RG --name <webApp> --settings BackgroundJobs__RunServer=false
# 4. Échanger les noms (retirer d'abord le verrou CanNotDelete de la base s'il existe).
az sql db rename --resource-group $RG --server $SERVER --name carrental --new-name carrental-old
az sql db rename --resource-group $RG --server $SERVER --name carrental-restore --new-name carrental
```

5. **Messages déjà partis** : la base restaurée contient les messages de l'outbox qui étaient en attente à l'instant choisi, dont certains sont partis depuis (e-mails, WhatsApp). Les lister (`SELECT Id, Type, OccurredAtUtc FROM infra.OutboxMessages WHERE ProcessedAtUtc IS NULL ORDER BY OccurredAtUtc`), marquer comme traités ceux dont l'effet a déjà eu lieu (`UPDATE infra.OutboxMessages SET ProcessedAtUtc = SYSUTCDATETIME() WHERE Id IN (…)`), puis remettre `BackgroundJobs__RunServer=true`.
6. **Anonymisations** faites après l'instant choisi : la base restaurée a « oublié » ces anonymisations ; avant de supprimer `carrental-old`, y relever les personnes anonymisées depuis (`SELECT Id FROM customers.Persons WHERE AnonymizedAtUtc >= '<instant>'`) et les anonymiser à nouveau dans `/admin/customers`.
7. Redéployer l'infrastructure (les politiques de sauvegarde s'appliquent par nom de base), remettre le verrou, puis supprimer `carrental-old` après quelques jours (`az sql db delete`).
8. **Paiements** : le rattrapage et le rapprochement de la nuit relisent la passerelle et signalent les écarts ; vérifier `/admin/payments` le lendemain.

### Base de données : sauvegarde longue (hebdomadaire, mensuelle, annuelle)

```bash
az sql db ltr-backup list --location francecentral --server <sqlServerName> --database carrental -o table
az sql db ltr-backup restore --backup-id "<id>" --dest-database carrental-ltr \
  --dest-server <sqlServerName> --dest-resource-group rg-carrental-prod
```

Pour consulter une année close (contrôle fiscal) : restaurer sous un autre nom, consulter, exporter ce qui est demandé, supprimer la base restaurée. Base supprimée : `az sql db list-deleted …` puis `az sql db restore --deleted-time …` ; région indisponible : `az sql db geo-backup restore` vers un serveur en France South.

**Exercice annuel** (recommandé) : restaurer la dernière sauvegarde mensuelle sous un nom temporaire, vérifier qu'elle s'ouvre et contient le mois attendu, la supprimer ; noter la date de l'exercice.

### Fichiers (blobs)

Il faut un rôle de données sur le compte, attribué temporairement à la personne qui restaure (il donne accès aux pièces d'identité hors du journal de l'application : le retirer ensuite), et `--auth-mode login` :

```bash
ACCOUNT=<stockage>
# Blob supprimé (30 jours)
az storage blob undelete --account-name $ACCOUNT --container-name inspections --name "<chemin>" --auth-mode login
# Version précédente d'un blob (90 jours) : trouver la version puis la recopier sur le blob courant
az storage blob list --account-name $ACCOUNT --container-name contracts --prefix "<préfixe>" --include v \
  --query "[].{name:name, version:versionId, current:isCurrentVersion}" -o table --auth-mode login
az storage blob copy start --account-name $ACCOUNT --destination-container contracts --destination-blob "<chemin>" \
  --source-uri "https://$ACCOUNT.blob.core.windows.net/contracts/<chemin>?versionid=<version>" --auth-mode login
# Conteneur supprimé (30 jours)
az storage container list --account-name $ACCOUNT --include-deleted --auth-mode login -o table
az storage container restore --account-name $ACCOUNT --name <conteneur> --deleted-version <version> --auth-mode login
```

Restaurer **beaucoup** de blobs (pas de restauration à un instant en production, incompatible avec l'immuabilité des factures) : boucle sur la liste des blobs supprimés d'un préfixe —

```bash
az storage blob list --account-name $ACCOUNT --container-name inspections --prefix "<préfixe>" --include d \
  --query "[?deleted].name" -o tsv --auth-mode login |
while read -r name; do
  az storage blob undelete --account-name $ACCOUNT --container-name inspections --name "$name" --auth-mode login
done
```

Le conteneur `invoices` est immuable 10 ans : une facture ne peut être ni supprimée ni écrasée, il n'y a rien à restaurer.

### Key Vault

```bash
az keyvault secret list-deleted --vault-name <coffre> -o table
az keyvault secret recover --vault-name <coffre> --name "<secret>"
az keyvault key recover --vault-name <coffre> --name dataprotection
az keyvault recover --name <coffre>            # coffre supprimé (90 jours, protection contre la purge)
```

Une ancienne version de secret se relit et se réenregistre ([Secrets](#secrets-et-rotation)). Ne jamais purger la clé `dataprotection` ni l'une de ses versions.

## Demandes des personnes

Clients et conducteurs ont un droit d'accès, de rectification et d'opposition (loi 2004-63 ; conception compatible RGPD). Toute demande : vérifier l'identité du demandeur, noter la date de la demande et de la réponse (délai à confirmer avec l'avocat), répondre par écrit.

- **Accès** : `/admin/customers` > la personne > **« Exporter les données (JSON) »** (gérant, permission `customers.privacy`) : identité, coordonnées, pièces sans leurs images, consentements, comptes payeurs ; l'export est audité. L'inscription sur liste noire n'y figure que si l'option est cochée (point ouvert avec l'avocat).
- **Rectification** : modifier la fiche (agent, `customers.manage`) ; la pièce se corrige ou se remplace (l'ancienne reste comme historique).
- **Opposition, consentements** : fiche de la personne > consentements (notifications WhatsApp, communications commerciales).
- **Effacement (anonymisation)** : fiche > **« Anonymiser »** (gérant), avec un motif sans donnée personnelle ; irréversible ; une personne sur liste noire doit d'abord en être retirée. Les dossiers et la comptabilité gardent l'identifiant ; les factures gardent leur copie (obligation comptable). L'historique des tables temporelles garde les anciennes valeurs : sa purge est une opération d'administrateur décrite dans l'[ADR-0027](adr/0027-personnes-dedoublonnage-et-anonymisation.md), à faire après validation de l'avocat. Les sauvegardes (jusqu'à 10 ans) ne sont pas réécrites : après une restauration, refaire les anonymisations postérieures ([Restaurations](#base-de-données--restauration-à-un-instant-7-derniers-jours), point 6).
- **Purge des images des pièces d'identité** : tâche nocturne `customers.purge_document_images`, **désactivée par défaut** (`customers.document_image_retention_enabled` dans `/admin/settings`, durée `customers.document_image_retention_days`, 400 jours après la dernière location clôturée). Ne l'activer qu'après validation de la procédure de destruction par l'avocat (article 45 : procès-verbal d'huissier en présence d'un expert de l'INPDP). Pour une seule personne, l'anonymisation supprime ses images.
- **Consultations des pièces** : chaque affichage est dans `audit.DocumentAccessLogs` (qui, quand, quelle pièce, motif) : `/admin/audit` pour répondre à « qui a vu ma pièce ? ».

## Agences de la plateforme

Réservé au support (rôle `Support`, [ADR-0067](adr/0067-plateforme-multi-agences-cloisonnement-support-modules.md)) : pages `/admin/platform`. Une agence ne voit jamais les données d'une autre ; le support travaille sur la plateforme ou dans **une** agence à la fois.

- **Ouvrir une agence** : **« Nouvelle agence »** : nom, ville, adresse de vitrine (proposée depuis le nom, minuscules, chiffres et tirets, unique, jamais un mot réservé comme `admin` ou `api`), modules (WhatsApp et canaux de vente décochés : ils demandent les comptes partenaires de l'agence), e-mail du gérant, qui reçoit une invitation. Le plan de comptes et la base de connaissances de l'agence sont créés avec elle. Le gérant complète ensuite l'identité de vendeur (`/admin/agency`, requise pour facturer), les lieux, la flotte, les tarifs et ses utilisateurs.
- **Modules** : fiche de l'agence > **« Modules »**. Un module fermé disparaît du menu, ses pages et ses points d'entrée répondent 404 pour l'agence, ses tâches de fond la passent ; ses données sont gardées et reviennent à la réouverture (les mises à jour des canaux en attente partent alors). WhatsApp : le numéro de la plateforme ne sert qu'une agence (un numéro par agence est un point ouvert), et seule l'agence servie envoie et reçoit (notifications, boîte partagée, assistant) ; une autre agence au module ouvert garde son historique mais n'envoie ni ne reçoit rien (alerte sur ses écrans WhatsApp, avertissement journalisé). La fiche de l'agence indique l'agence servie. Désigner l'agence servie par `WhatsApp__AgencyId` (son identifiant ne change jamais ; `WhatsApp__AgencySlug` suit l'adresse de vitrine, qui peut changer ou être reprise, et ne sert plus qu'en dépannage), puis redémarrer ; sans désignation, l'ouverture de WhatsApp à une deuxième agence est refusée. Pour passer le numéro à une autre agence : régler `WhatsApp__AgencyId` sur elle, redémarrer, puis ouvrir son module.
- **Suspendre une agence** (impayé, fin de contrat) : fiche > **« Suspendre l'agence »**. Sa vitrine répond « introuvable », son personnel ne peut plus se connecter et ses sessions s'arrêtent en une à deux minutes ; ses données et ses tâches de fond continuent. **« Réactiver l'agence »** rend tout.
- **Aider une agence** : **« Ouvrir »** sur la ligne de l'agence : le back office s'affiche comme pour son gérant, avec un bandeau qui rappelle l'agence ouverte ; **« Quitter l'agence »** pour revenir à la plateforme. Ce que fait le support dans l'agence figure dans le journal d'audit de l'agence. Ne pas garder deux onglets du back office ouverts sur deux agences différentes : recharger un onglet après avoir changé d'agence.
- **Comptes support** : `/admin/platform/support` ; au moins deux comptes (secours), double authentification enrôlée, codes de récupération en lieu sûr. Paramètres et journal de la plateforme : `/admin/platform/settings` et `/admin/platform/audit`.
- **Gérant parti ou injoignable** : le support invite un nouveau gérant depuis la fiche de l'agence, puis le gérant verrouille l'ancien compte (voir ci-dessous).
- **Domaine propre d'une agence** ([ADR-0069](adr/0069-domaines-propres-par-agence.md)) : fiche de l'agence > **« Domaines du site »**. Préparer d'abord le DNS, la liaison du domaine à l'App Service avec son certificat et le widget Turnstile ([README de l'infrastructure, section 8 bis](../infra/README.md#8-bis-domaine-propre-dune-agence)), puis ajouter le domaine : la vitrine répond aussitôt sur `https://www.agence.tn/fr`, sans le segment de l'agence, et les e-mails de ses clients portent ce domaine. Le premier domaine est **principal** ; les autres redirigent vers lui (« Rendre principal » pour changer). **Retirer** un domaine coupe les liens qui le portent (e-mails envoyés, favoris) ; retirer le principal passe le rôle au plus ancien domaine restant. `rental.app/{agence}/fr` continue de répondre pour toutes les agences. Un domaine « déjà utilisé » est celui d'une autre agence ; un nom de la plateforme (`localhost`, `*.azurewebsites.net`, ses domaines) est refusé.

## Comptes du personnel

Le gérant gère les comptes de son agence dans `/admin/users` : **« Nouvel utilisateur »** (invitation par e-mail, le collègue choisit son mot de passe puis enrôle son application d'authentification), rôles, permissions supplémentaires, **« Verrouiller »** / **« Déverrouiller »**. Tout est audité.

- **Verrouillé après 5 échecs** : déverrouillage automatique après 15 minutes, ou « Déverrouiller » par le gérant (remet le compteur à zéro) après avoir vérifié que c'est bien le collègue qui s'est trompé (sinon : tentative d'intrusion, voir le journal d'audit `identity.login_failed` et l'adresse).
- **Mot de passe oublié** : le collègue utilise « Mot de passe oublié ? » sur la page de connexion (lien valable une durée limitée) ; le gérant ne connaît jamais les mots de passe.
- **Trop de demandes (429)** sur les pages de compte : plus de 20 envois de formulaires en 10 minutes depuis la même adresse (bureau partagé ?) ; attendre quelques minutes. Une rafale venue d'une adresse inconnue est une attaque : rien à faire de plus, la limite la contient.
- **Fin de session** : une session s'arrête 12 heures après la connexion, même en activité, et après 8 heures sans activité : se reconnecter (mot de passe et code).
- **Départ d'un collègue** : « Verrouiller » aussitôt (ses sessions s'arrêtent en une à deux minutes), retirer ses permissions supplémentaires ; ne jamais supprimer le compte (le journal d'audit y fait référence) ; s'il avait des accès Azure (groupe « CarRental SQL Admins ») ou GitHub, les retirer et faire tourner les secrets qu'il a pu voir.

### Double authentification perdue

1. Le collègue a ses **codes de récupération** : « Utiliser un code de récupération » sur la page du code, puis Mon compte > Réinitialiser l'authentificateur et enrôler le nouveau téléphone ; générer de nouveaux codes.
2. Sans code de récupération, l'application n'offre pas encore de réinitialisation par le gérant (point ouvert). Un membre du groupe « CarRental SQL Admins », **après avoir vérifié l'identité du collègue en personne**, désactive sa double authentification dans la base (éditeur de requête du portail, authentification Entra) :

   ```sql
   DECLARE @Email nvarchar(256) = N'<adresse du collègue>';
   DECLARE @UserId uniqueidentifier = (SELECT Id FROM identity.Users WHERE NormalizedEmail = UPPER(@Email));
   BEGIN TRANSACTION;
   DELETE FROM identity.UserTokens
    WHERE UserId = @UserId AND LoginProvider = N'[AspNetUserStore]' AND Name IN (N'AuthenticatorKey', N'RecoveryCodes');
   -- Nouveau tampon de sécurité : toutes les sessions du compte s'arrêtent dans la minute.
   UPDATE identity.Users
      SET TwoFactorEnabled = 0, SecurityStamp = CONVERT(nvarchar(36), NEWID()), ConcurrencyStamp = CONVERT(nvarchar(36), NEWID())
    WHERE Id = @UserId;
   COMMIT;
   ```

   À sa connexion suivante (mot de passe), le collègue est envoyé vers l'enrôlement d'une nouvelle application d'authentification avant tout accès au back office. Cette modification directe n'entre pas dans le journal d'audit de l'application : la consigner (date, auteur, collègue, vérification d'identité faite) dans le registre de l'agence.

## Cloudflare et DNS

| Symptôme | Cause probable et action |
|---|---|
| 403 pour une partie des visiteurs, depuis l'activation de `restrictToCloudflare` ou un changement chez Cloudflare | Une plage d'adresses de Cloudflare manque : comparer `cloudflareIpRanges` (`infra/main.bicep`) avec <https://www.cloudflare.com/ips-v4> et `ips-v6`, mettre à jour, redéployer. En urgence : `restrictToCloudflare = false` et redéployer (l'en-tête `CF-Connecting-IP` n'est alors plus cru, ce qui reste sûr) |
| Erreur Cloudflare 521, 522 ou 523 | L'origine ne répond pas : [site-down](#alerte-site-down) ; 522 récurrent : vérifier que le domaine personnalisé est lié à l'App Service |
| Erreur 525 ou 526 | Certificat d'origine absent, expiré ou non lié : README de l'infrastructure, section 8, étape 3 (certificat d'origine Cloudflare, mode « Full (strict) ») |
| Erreur 524 | Une requête dure plus de 100 secondes : trace dans Application Insights (export lourd, requête lente) |
| Back office qui se reconnecte sans cesse | WebSockets désactivés chez Cloudflare (Réseau > WebSockets) ; Rocket Loader activé (à désactiver) |
| Webhook WhatsApp ou canal bloqués (403 de Cloudflare) | WAF ou « Bot Fight Mode » : règle d'exception pour `/webhooks/*`, `/api/channels/*` et `/payments/clictopay/*` |
| Panne générale de Cloudflare | Attendre (statut sur <https://www.cloudflarestatus.com>). Si elle dure : (1) `restrictToCloudflare = false` et redéployer ; (2) créer et lier un certificat géré App Service pour `www.<domaine>` (`az webapp config ssl create --resource-group rg-carrental-prod --name <webApp> --hostname www.<domaine>`, puis `az webapp config ssl bind`) ; (3) passer l'enregistrement `www` en « DNS seulement » (nuage gris). Revenir en arrière dans l'ordre inverse, Cloudflare rétabli |
| Le domaine ne répond plus du tout | Serveurs de noms chez le registraire, renouvellement du domaine, DNSSEC ; les enregistrements e-mail (SPF, DKIM, DMARC) restent en « DNS seulement » |
| E-mails non reçus après un changement DNS | Vérification du domaine dans Communication Services (README de l'infrastructure, section 7) |

**Chaque trimestre** : comparer les plages de Cloudflare et le paramètre `cloudflareIpRanges`.

## Avant la mise en production

À cocher avec l'agence avant d'ouvrir au public :

- [ ] Autorisation de l'INPDP pour l'hébergement en France ; textes juridiques relus (conditions générales, confidentialité) ; procédure de destruction des pièces validée ou purge laissée désactivée.
- [ ] Durées de conservation confirmées : **10 ans pour les pièces comptables** avec l'expert-comptable (sauvegarde annuelle, immuabilité des factures, décision de verrouiller) ; contrats, états des lieux et photos avec l'avocat.
- [ ] Domaine derrière Cloudflare (« Full (strict) », WebSockets, pas de « Cache Everything », Rocket Loader désactivé), certificat d'origine lié, `HEALTHCHECK_BASE_URL` renseigné, puis **`restrictToCloudflare = true`** et redéploiement ; `https://<webApp>.azurewebsites.net` répond 403.
- [ ] `ALERT_EMAILS` défini au déploiement de la production, groupe d'actions testé (un e-mail reçu par chaque destinataire) ; surveillance externe gratuite de `https://www.<domaine>/health` ou test de disponibilité (`availabilityTestUrl`) ; `BUDGET_ALERT_EMAIL` défini.
- [ ] Secrets dans le Key Vault : Clic to Pay (recette Clic to Pay faite), WhatsApp, Turnstile (et `Turnstile__SiteKey`), `CustomerApi--Jwt--SigningKey` (API des applications clientes, ADR-0070 : sans lui l'application refuse de démarrer) ; e-mail sur le domaine de l'agence vérifié (SPF, DKIM, DMARC).
- [ ] Premier compte support créé (`initialSupportEmail`), double authentification enrôlée et **codes de récupération rangés en lieu sûr** ; un second compte support recommandé (secours) ; chaque agence créée depuis `/admin/platform` avec son gérant invité ; aucune donnée de démonstration.
- [ ] Verrou « CanNotDelete » sur la base et le compte de stockage de production (README de l'infrastructure, section 5.1).
- [ ] Une restauration d'essai de la base faite et notée ; ce runbook relu, contacts remplis.
- [ ] GitHub : environnement `prod` limité à `main`, alertes de sécurité de Dependabot activées, analyse CodeQL disponible (dépôt public ou GitHub Advanced Security).

## Contacts et escalade

À remplir avec l'agence.

| Rôle | Nom | Téléphone | E-mail | Quand l'appeler |
|---|---|---|---|---|
| Gérant de l'agence | `<à compléter>` | | | Toute indisponibilité du site ou du back office, décision d'arrêt |
| Prestataire informatique | `<à compléter>` | | | Destinataire des alertes |
| Développement (application) | `<à compléter>` | | | Bogue, exception récurrente, retour arrière |
| Support Clic to Pay (SMT / banque) | `<à compléter>` | | | Passerelle en panne, identifiants, codes de statut |
| Meta (WhatsApp Business) | via le gestionnaire WhatsApp | | | Numéro bloqué, modèles refusés |
| Expert-comptable | `<à compléter>` | | | Clôture, conservation, factures |
| Avocat | `<à compléter>` | | | Fuite de données, demande d'une personne, INPDP |
| Support Azure | portail > Aide + support | | | Panne d'une ressource Azure |

**Escalade** : une alerte de gravité 1 (`site-down`, `payments-gateway`, `availability`) se traite dans l'heure en journée ; les autres dans la journée ouvrée. Fuite de données personnelle avérée ou suspectée : gérant et avocat le jour même.

## Annexe : requêtes et commandes utiles

Application Insights > Journaux :

```kusto
// État le plus récent de chaque entrée de santé (2 saine, 1 dégradée, 0 en échec)
customMetrics
| where name == "carrental.health.status"
| summarize arg_max(timestamp, value) by entry = tostring(customDimensions["entry"])

// Exécutions des tâches récurrentes par résultat et par jour
customMetrics
| where name == "carrental.jobs.runs"
| summarize runs = sum(value) by job = tostring(customDimensions["job"]), outcome = tostring(customDimensions["outcome"]), bin(timestamp, 1d)

// Appels Clic to Pay par opération et résultat
customMetrics
| where name == "carrental.clictopay.calls"
| summarize calls = sum(value) by operation = tostring(customDimensions["operation"]), outcome = tostring(customDimensions["outcome"]), bin(timestamp, 1h)

// Refus des limites de débit (429)
traces
| where message startswith "Rate limit reached" or message startswith "Health probe rate limit"
| summarize count() by message, bin(timestamp, 1h)
```

Azure CLI :

```bash
az webapp log tail --resource-group rg-carrental-prod --name <webApp>                        # journaux en direct
az webapp restart --resource-group rg-carrental-prod --name <webApp>
az webapp config appsettings list --resource-group rg-carrental-prod --name <webApp> -o table
az webapp config access-restriction show --resource-group rg-carrental-prod --name <webApp>  # règles Cloudflare
az monitor metrics list --resource "$(az webapp show -g rg-carrental-prod -n <webApp> --query id -o tsv)" \
  --metric HealthCheckStatus Http5xx --interval PT5M -o table
```
