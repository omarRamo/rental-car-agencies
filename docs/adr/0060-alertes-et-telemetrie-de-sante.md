# ADR-0060 — Alertes : télémétrie de santé publiée par l'application, règles Azure Monitor à faible coût

- **Statut** : Acceptée
- **Date** : 2026-10-21
- **Source** : [prompt](../PROMPT.md) §15 « Transverse — Durcissement » (alertes : erreurs, paiements en échec, canal en échec, file WhatsApp bloquée, échéances de flotte), [spécifications](../SPEC.md) §14, [ADR-0028](0028-exploitation-outbox-et-requetes-fractionnees.md) (entrée de santé `outbox`), [ADR-0039](0039-paiement-en-ligne-confirmation-idempotence-rattrapage.md) (signalements des paiements), [ADR-0047](0047-whatsapp-webhook-signe-traitement-asynchrone-et-fenetre-24-h.md), [ADR-0053](0053-canaux-securite-entrante-idempotence-et-synchronisation.md) (entrée `channels`)

## Contexte

La spécification exige des alertes sur les erreurs, les paiements en échec, la synchronisation des canaux en échec, la file WhatsApp bloquée et les échéances de flotte. Les états utiles existent déjà sous forme d'entrées de santé de `/health/ready` (`outbox`, `channels`, `hangfire`, `storage`, `sql`), Degraded sans jamais rendre l'application indisponible, mais personne n'est prévenu. Le budget est celui d'une agence de 30 véhicules : pas de produit de sécurité payant, pas de service Azure de plus, et chaque règle d'alerte se paie (par série surveillée pour les métriques, par règle et par fréquence pour les recherches dans les journaux). Les lots parallèles ajouteront des états à surveiller (le lot 4B, une entrée `whatsapp` pour les tours du bot) sans toucher à l'infrastructure.

## Décision

### L'application publie sa santé comme télémétrie

- **`HealthStatusPublisher`** (`Infrastructure/Observability`, `IHealthCheckPublisher`) : le service d'ASP.NET Core évalue **toutes** les entrées enregistrées, quelles que soient leurs étiquettes, chaque minute (premier passage 30 s après le démarrage, 30 s au plus par évaluation), et le publieur écrit l'état de chacune dans la jauge OpenTelemetry **`carrental.health.status`** (2 saine, 1 dégradée, 0 en échec, dimension `entry` = nom de l'entrée). Un changement d'état est journalisé une fois (avertissement avec la description de l'entrée, puis information au retour à la normale), jamais à chaque minute. Indépendant des sondes : la recette en F1, sans contrôle de santé d'App Service, publie aussi.
- **Deux entrées nouvelles**, Degraded jamais Unhealthy :
  - `jobs` : une tâche récurrente enregistrée absente de Hangfire, impossible à planifier, en échec à sa dernière exécution, ou en retard de plus de 15 minutes sur sa prochaine exécution (planificateur arrêté). L'e-mail quotidien des échéances de flotte (`fleet.deadline_alerts`) est ainsi surveillé, comme le rattrapage des paiements, les purges et les instantanés. L'évaluation (quelques requêtes par tâche dans les tables de Hangfire) est gardée 5 minutes : l'entrée est un singleton.
  - `payments` : un paiement en ligne signalé à vérifier (`GatewayAttention` : statut inconnu, écart avec la banque, paiement tardif, capture, annulation ou remboursement non confirmé, opération refusée) ou une opération envoyée à la passerelle et toujours non confirmée après une heure (le rattrapage passe toutes les 15 minutes). Un refus de carte n'est jamais compté. Les chiffres viennent du module Paiements (`OnlinePaymentMonitoring`), l'entrée n'en lit que des comptes.
- **Compteur `carrental.jobs.runs`** (dimensions `job`, `outcome` : `succeeded`, `failed`, `skipped`, `cancelled`) dans l'exécuteur des tâches récurrentes, pour les tableaux de bord et le diagnostic. Le compteur `carrental.clictopay.calls` (opération, résultat) existait déjà.
- **Conventions** : instruments nommés `carrental.*` sur le compteur `CarRental`, dimensions à faible cardinalité (nom d'entrée, identifiant de tâche, opération, résultat), jamais d'identifiant, d'adresse ni de donnée personnelle ; un test vérifie les noms et que les requêtes des règles utilisent les noms des instruments.

### Les règles d'alerte (`infra/modules/alerts.bicep`)

- **Un groupe d'actions** par environnement (`ag-carrental-<env>`) : e-mails de `alertEmails` (variable `ALERT_EMAILS` au déploiement, schéma d'alerte commun), SMS facultatifs (`alertSmsNumbers`, vides par défaut : facturés dès le premier hors des États-Unis). **Sans adresse, rien n'est créé** : la recette n'a aucune règle par défaut.
- **Alertes de métriques** (gratuites dans les 10 premières séries de l'abonnement) : `site-down` (`HealthCheckStatus` d'App Service, moyenne < 100 % sur 15 minutes, hors F1), `http-5xx` (> 10 en 15 minutes), `sql-dtu` (> 90 % pendant 30 minutes) et `sql-storage` (> 80 % des 2 Go du Basic), ces deux dernières pour une base DTU seulement.
- **Recherches dans les journaux toutes les 15 minutes** (la fréquence la moins chère) sur Application Insights :
  - `health-degraded` : une entrée dont la meilleure valeur sur 15 minutes est inférieure à 2, **une alerte par entrée** (dimension `entry`) — la règle générique qui couvre `outbox` (messages en échec, file WhatsApp bloquée), `channels`, `payments`, `jobs`, `hangfire`, `storage`, `sql` et toute entrée future (`whatsapp` du lot 4B) ;
  - `payments-gateway` : au moins 3 appels Clic to Pay au résultat autre que `Succeeded` (pas de réponse, réponse illisible, requête refusée par la passerelle) en 15 minutes ;
  - `exceptions` : plus de 20 exceptions en 15 minutes ;
  - `telemetry-missing` : aucune mesure de santé en 30 minutes (application arrêtée, ou télémétrie coupée par le plafond journalier), hors F1 où l'application s'endort.
- **Test de disponibilité standard facultatif** (`availabilityTestUrl`, `GET /health` toutes les 15 minutes, alerte `availability`) : facturé à l'exécution, désactivé par défaut ; une surveillance externe gratuite le remplace (README de l'infrastructure, section 15).
- Validation des requêtes à l'évaluation seulement (`skipQueryValidation`) : les tables d'un Application Insights neuf n'apparaissent qu'avec les premières données. Chaque alerte se résout d'elle-même.
- Chaque règle renvoie à une section du [runbook](../runbook.md) : signification et premières actions.

### Coût

De l'ordre de 2 à 3 USD par mois en production (quatre règles de recherche, leurs séries de dimension, e-mails gratuits jusqu'à 1 000 par mois), 3 à 4 USD de plus par emplacement si l'agence choisit le test de disponibilité ; à vérifier dans la calculatrice Azure.

### Où vivent les tests

Les tests unitaires du publieur, de l'entrée `jobs` et des conventions de nommage sont dans `tests/CarRental.Architecture.Tests/Observability` : c'est le seul projet de tests sans conteneur qui référence l'infrastructure, et ils tournent ainsi dans le premier travail de la CI. Les entrées sur SQL Server sont vérifiées par `tests/CarRental.Integration.Tests/Observability`.

## Conséquences

- Une entrée de santé ajoutée par un module est publiée et alertée sans changement de l'infrastructure : le lot 4B n'a qu'à enregistrer son entrée `whatsapp` (tours du bot ou envois en attente trop anciens).
- Une entrée Degraded reste alertée tant que la cause n'est pas traitée (message relancé, paiement marqué comme vérifié, envoi rejoué, tâche relancée) : l'alerte se résout alors d'elle-même.
- Le contrôle de santé et la télémétrie évaluent les entrées chacun une fois par minute : une vingtaine de requêtes légères par minute sur la base, l'entrée `jobs` étant gardée 5 minutes.
- Toutes les règles de télémétrie dépendent de l'ingestion de Log Analytics : le plafond journalier atteint, elles se taisent jusqu'à sa remise à zéro et `telemetry-missing` le signale.
- Le contrôle de santé d'App Service reste désactivé en recette : en F1, une sonde chaque minute empêcherait l'application de s'endormir et la base gratuite serverless de se mettre en pause, épuisant son allocation mensuelle.

## Alternatives écartées

- **Une règle par entrée, écrite dans le modèle** : à maintenir à chaque entrée nouvelle, et le lot 4B devrait modifier l'infrastructure.
- **Alertes de métriques sur les métriques personnalisées d'Application Insights** : la création d'alertes sur leurs dimensions demande une option payante et dépend du format de l'exportateur OpenTelemetry ; la recherche dans `customMetrics` est sûre et peu chère à 15 minutes.
- **Sondage de `/health/ready` par un test de disponibilité** : payant à l'exécution, il ne dit pas quelle entrée est dégradée (le détail est masqué hors développement) et chaque sonde interroge la base.
- **Jauge observable relisant un état partagé** : un état statique mélangerait les hôtes des tests d'intégration ; la jauge synchrone écrite à chaque évaluation suffit.
- **Fréquences de 5 minutes** : trois fois plus chères pour des incidents qui se traitent en heures ouvrées.
- **Alertes par SMS par défaut** : facturées dès le premier message en Tunisie ; le gérant peut en ajouter.
