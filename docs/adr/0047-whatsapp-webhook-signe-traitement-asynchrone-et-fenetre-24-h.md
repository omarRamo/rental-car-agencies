# ADR-0047 — Canal WhatsApp : webhook signé, traitement asynchrone par l'outbox, fenêtre de 24 h et faux

- **Statut** : Acceptée
- **Date** : 2026-10-21
- **Source** : [conception phase 4](../conception/phase-4-whatsapp.md) §1, §3 et §5 (lot 4A), [prompt](../PROMPT.md) §9.2 et §6 (invariants 9 et 10), [spécifications](../SPEC.md) §11 et §14, [ADR-0009](0009-whatsapp-cloud-api-claude-haiku.md), [ADR-0013](0013-outbox-traitee-par-hangfire.md), [ADR-0040](0040-outbox-messages-sans-gestionnaire.md), [ADR-0043](0043-outbox-pompe-en-processus.md)

## Contexte

L'agence reçoit ses clients sur WhatsApp par la Cloud API de Meta, sans intermédiaire ([ADR-0009](0009-whatsapp-cloud-api-claude-haiku.md)). Meta appelle un webhook de l'application (vérification à l'abonnement, puis livraison des messages et des statuts), signe chaque livraison et la relivre tant qu'il n'a pas reçu 200 ; une même livraison peut donc arriver plusieurs fois. Une réponse libre n'est permise que dans les 24 heures qui suivent le dernier message du client ; au-delà, seul un modèle approuvé peut partir. Depuis le 1er octobre 2026, chaque réponse libre est facturée au-delà de 1 000 messages par mois : un seul message par tour. Le lot 4B branchera l'agent IA sur les conversations ; un agent humain doit pouvoir le faire taire. Rien de tout cela ne doit dépendre de Meta dans les tests.

## Décision

### Le webhook : rien de non signé n'est cru

- `GET /webhooks/whatsapp` : `hub.mode=subscribe` et `hub.verify_token` égal au jeton configuré (comparaison en temps constant, sur les empreintes SHA-256) renvoient `hub.challenge` en `text/plain` (lettres, chiffres, `-_.`, 256 caractères au plus) ; tout le reste répond 403.
- `POST /webhooks/whatsapp` : le corps brut est lu (1 Mo au plus, 413 au-delà), puis `X-Hub-Signature-256` est comparé en temps constant au HMAC-SHA256 de ce corps avec le secret de l'application (`sha256=` et hexadécimal) ; une signature absente ou fausse répond 401 et rien n'est enregistré. Une livraison signée est déposée telle quelle dans l'outbox (message `WhatsAppWebhookReceived`) et 200 est répondu aussitôt : aucun travail métier dans la requête. Une panne de la base répond 500, et Meta relivre.
- Point d'accès anonyme, sans jeton anti-falsification, sans page d'erreur HTML, `no-store`, et 404 tant que WhatsApp n'est pas configuré. **Limitation de débit propre** (`whatsapp-webhook`, distincte des politiques publiques) : fenêtre glissante par adresse client de 600 requêtes par minute, réglable (`RateLimiting:WhatsAppWebhook:Limit`), dimensionnée pour les rafales de statuts de Meta ; au-delà, 429, et Meta relivre plus tard.

### Le traitement : l'outbox, un événement à la fois

- Le gestionnaire de `WhatsAppWebhookReceived` lit la livraison (format de Meta, dans l'infrastructure : `object` = `whatsapp_business_account`, `entry[].changes[]` du champ `messages`, `metadata.phone_number_id`, `contacts`, `messages`, `statuses`) ; les événements d'un autre compte WhatsApp Business ou d'un autre numéro sont ignorés.
- Chaque message et chaque statut est enregistré **dans une portée DI à lui** (acteur `system:whatsapp`), jamais dans celle de l'outbox : une unité de travail en échec vide son suivi des modifications, ce qui détacherait le message de l'outbox en cours. Un conflit d'écriture (version de ligne, index unique) rejoue l'étape sur des données fraîches, trois fois au plus ; au-delà, l'outbox retente toute la livraison.
- **Déduplication** : un message par identifiant Meta (`wamid`, index unique filtré), un statut par message et par statut (index unique) ; une relivraison ne crée rien.
- Premier contact d'un numéro : la conversation est créée, et rattachée à la personne qui a ce téléphone **quand une seule personne l'a** (contrat `ICustomersQueries.FindPersonsByPhoneAsync`) ; des proches qui partagent un numéro sont laissés au choix de l'agent ([ADR-0027](0027-personnes-dedoublonnage-et-anonymisation.md)).

### Le modèle

- `Conversation` (agrégat, une par numéro E.164, unique) : mode `Bot` ou `Human` avec l'agent qui a pris la main et depuis quand, motif d'un transfert par le bot, statut ouvert ou clos (un message du client rouvre), dernier message du client (fenêtre de 24 h), non-lus, rattachements facultatifs à une personne, un payeur et un dossier (identifiants), **état structuré du besoin** (lieux, dates, passagers, bagages, boîte, budget) et **compteurs du bot** (tours, jetons d'entrée, de sortie, de lecture et d'écriture de cache, appels d'outils, coût IA et coût Meta estimé en USD), réservés au lot 4B.
- `Message` (agrégat secondaire, chargé à part : un fil compte des centaines de messages) : sens, type (texte, interactif, choix d'un bouton ou d'une liste, modèle, média, non pris en charge), contenu (JSON de la plateforme), identifiant Meta, auteur (client, bot, agent, système), état de livraison et historique des statuts envoyé, distribué, lu, échec avec le code d'erreur de Meta, catégorie de tarification et facturation déclarées par Meta, horodatages. Les statuts peuvent arriver dans le désordre : l'état retenu est le plus avancé (envoyé < échec < distribué < lu).
- **Un média n'est jamais téléchargé** : seuls son type et son identifiant Meta sont gardés (invariant 9 : aucune pièce d'identité ne transite par WhatsApp ; la légende n'est pas gardée non plus).
- Seule la conversation crée des messages : **réponse libre (texte, boutons, liste) seulement dans la fenêtre de 24 h**, modèle approuvé à tout moment ; **le bot ne répond jamais en mode `Human`** ; seul l'agent qui a pris la main répond (un collègue la reprend d'abord ; après un transfert par le bot, un agent la prend). L'envoi revérifie au dernier moment : un message du bot est abandonné si un agent a pris la main depuis, un message libre si la fenêtre s'est fermée entre-temps (nouvelle tentative tardive), une notification si le consentement a été retiré.
- Un contenu enregistré est relu tel qu'il a été écrit, **sans être revalidé** : un texte reçu vide (espaces insécables seulement) ou un contenu valide selon des règles plus anciennes reste lisible ; un type inconnu de cette version s'affiche comme non pris en charge.
- Tables ordinaires du schéma `messaging`, **sans historique temporel** : la purge de rétention doit vraiment effacer les échanges. La conversation est auditée (mode, rattachements, statut) avec le numéro, le nom de profil et le motif de transfert masqués ; les messages, qui sont eux-mêmes le journal des communications (SPEC §14), sont exclus du journal d'audit : aucun contenu de message n'y entre.

### Le point d'extension du bot (lot 4B)

- `IConversationResponder.RespondAsync(ConversationTurn, CancellationToken) → ConversationReply`, dans les contrats du module. Un **tour** regroupe les messages du client pas encore traités : des messages reçus en rafale sont traités une fois, par le tour du dernier ; le responder reçoit le numéro, le nom de profil, les rattachements, la fin de la fenêtre, le besoin, les compteurs, les messages du tour et l'historique (50 messages au plus).
- Il répond **au plus un message** (texte, boutons ou liste), le nouveau besoin, l'usage du tour et, au besoin, un motif de transfert : le message part, puis la conversation passe en mode `Human` et le bot se tait.
- Trois étapes, chacune dans sa portée : préparer le tour (rien si le message est déjà traité, si un message plus récent attend ou si la conversation est en mode `Human`), appeler le responder hors transaction, **appliquer sur des données fraîches** : une prise en main validée pendant l'appel l'emporte (version de ligne), le message est abandonné, seul le coût de l'appel est compté. La prise en main marque elle-même les messages du tour en cours (« réduit au silence ») et annule les réponses du bot encore en file : une réponse périmée ne part pas, même si l'agent a rendu la main avant qu'elle n'arrive. Une exception du responder fait retenter le tour par l'outbox ; un problème métier se traite par un transfert.
- Implémentation par défaut : `NoReplyResponder` (aucune réponse) ; le lot 4B la remplace dans `AddMessagingModule()`.

### L'envoi

- Port `IWhatsAppClient` : le client de l'API Graph (`POST {GraphBaseUrl}/{ApiVersion}/{PhoneNumberId}/messages`, jeton porteur, `HttpClient` typé, délai de 20 s) envoie texte, boutons de réponse, liste et modèle (nom, `language.code`, paramètres du corps), aux formats de la documentation publique de Meta.
- **Nouvelles tentatives seulement sans risque de doublon** : le client ne retente jamais lui-même un envoi. Il classe la réponse : refus (erreur de Meta : définitif, code gardé sur le message) ; temporaire (HTTP 429, codes 4, 80007, 130429, 131056, 2, 131000, 131016, HTTP 503, jeton ou permission refusés — HTTP 401, codes 0, 10, 190, 200 à 299, journalisés en critique : un jeton expiré ne perd pas les messages —, connexion impossible à ouvrir : le message n'est certainement pas parti, l'outbox retente avec des délais croissants, 5 tentatives au plus puis échec) ; **issue inconnue** (délai dépassé ou réponse coupée après l'envoi de la requête : pas de nouvelle tentative, l'agent voit « envoi incertain »).
- **Au plus une fois** : le message passe à l'état « envoi en cours », enregistré avant l'appel (version de ligne : de deux expéditeurs, ou d'un expéditeur et d'une prise en main qui annule le message, un seul continue). Ensuite, ni l'appel ni l'enregistrement de son issue ne sont plus annulés par un arrêt de l'application (le délai du client les borne). Une issue perdue malgré tout (arrêt brutal, panne de la base) laisse le message « envoi en cours » : la tentative suivante le marque « envoi incertain » au lieu de le renvoyer.
- Chaque appel est journalisé (type de message, statut HTTP, code Meta, issue, durée), jamais le jeton ni le contenu.
- Options `WhatsApp:*` validées au démarrage : `Mode` (`Auto` : le faux en développement et en test, désactivé ailleurs ; `Disabled` ; `Fake` ; `Graph`), `GraphBaseUrl`, `ApiVersion` (`v23.0` par défaut, `TODO(spec)` à confirmer à la mise en service), `PhoneNumberId`, `BusinessAccountId`, `Timeout`, et les secrets `AccessToken`, `AppSecret`, `VerifyToken` (Key Vault : `WhatsApp--AccessToken`, `WhatsApp--AppSecret`, `WhatsApp--VerifyToken`). Le mode `Graph` exige tout ; le faux est **refusé en production** ; sans configuration, WhatsApp est désactivé et l'application démarre.
- **`FakeWhatsApp`** (développement, tests, E2E) : enregistre chaque message « envoyé », répond avec un identifiant Meta factice et, comme Meta, rapporte « envoyé » puis « distribué » **par le vrai webhook interne** (même analyseur, même traitement) ; il simule aussi des clients (texte, choix d'un bouton) et des statuts. Les tests posent des livraisons signées avec la vraie HMAC.

## Conséquences

- Un message du client est enregistré et son tour de bot lancé dans la seconde qui suit sa livraison (pompe de l'outbox, [ADR-0043](0043-outbox-pompe-en-processus.md)) ; une file bloquée se voit dans `/admin/outbox` et dans l'entrée `outbox` de `/health/ready` (SPEC §14 « file WhatsApp bloquée »).
- Le traitement de l'outbox reste séquentiel : un appel lent du modèle (4B) retarde d'autant les messages suivants (e-mails compris). Acceptable pour 30 véhicules ; si le volume grandit, une file dédiée aux tours du bot pourra être ajoutée derrière le même contrat.
- Envoi au plus une fois : un arrêt brutal ou une panne de la base entre l'appel et l'enregistrement de son issue donne un message « envoi incertain » (à vérifier par l'agent), jamais un doublon ; un statut d'un message dont l'identifiant Meta n'a pas pu être enregistré est ignoré.
- Les livraisons brutes, données personnelles comprises, restent dans l'outbox jusqu'à sa purge (`outbox.processed_retention`, 30 jours) ; une livraison en échec définitif (10 tentatives) y reste jusqu'à son traitement dans `/admin/outbox`, au-delà de `messaging.retention_days`.
- En recette avec des données de démonstration, utiliser le faux ou un numéro de test de Meta (destinataires autorisés seulement) : les dossiers de démonstration déclenchent des notifications.
- `CF-Connecting-IP` n'est fiable que si l'origine n'accepte que Cloudflare ([ADR-0037](0037-vitrine-anti-abus-et-mesure-entonnoir.md)) ; la limite du webhook en dépend comme les autres.

## Alternatives écartées

- **Traiter la livraison dans la requête** : Meta attend une réponse rapide, et une panne en cours de traitement perdrait l'événement.
- **Une table de file dédiée et sa propre pompe** : reprises, délais croissants, écran des échecs, purge et santé existent déjà dans l'outbox ; seul l'isolement des appels lents du modèle y gagnerait.
- **Dédupliquer par empreinte du corps** : deux livraisons différentes peuvent porter le même message ; l'identifiant Meta est la vraie clé.
- **Les messages dans une collection de l'agrégat `Conversation`** : chaque message chargerait tout le fil, et chaque statut entrerait en conflit avec les actions des agents.
- **Tables temporelles** : l'historique garderait les messages purgés.
- **Retenter tout envoi en échec** : un délai dépassé après l'envoi de la requête produirait des doublons facturés.
- **Renvoyer un message resté « envoi en cours »** (au moins une fois) : un client recevrait deux fois la même réponse, facturée deux fois, après chaque arrêt brutal ; l'agent préfère un « envoi incertain » visible.
- **Télécharger les médias** : contraire à l'invariant 9 ; le client est renvoyé au lien sécurisé.
