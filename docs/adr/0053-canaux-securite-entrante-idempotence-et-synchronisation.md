# ADR-0053 — Canaux : sécurité du point d'entrée, journal, idempotence par référence et synchronisation sortante

- **Statut** : Acceptée
- **Date** : 2026-10-21
- **Source** : [spécifications](../SPEC.md) §10 ; [prompt](../PROMPT.md) §9.3 ; [conception phase 5](../conception/phase-5-canaux.md) §3 ; [ADR-0004](0004-hebergement-app-service-b1-cloudflare.md) (App Service derrière Cloudflare), [ADR-0017](0017-execution-cas-usage-portee-transactions.md) (unités de travail rejouées), [ADR-0025](0025-moteur-de-disponibilite-cache-et-ecriture-sous-verrou.md) (cache de disponibilité, notification des changements), [ADR-0037](0037-vitrine-anti-abus-et-mesure-entonnoir.md) (adresse du client, limitation de débit), [ADR-0043](0043-outbox-pompe-en-processus.md) (pompe en processus), [ADR-0044](0044-caches-parametres-et-plans-publies.md) ; modèle et adaptateurs : [ADR-0052](0052-canaux-de-vente-modele-restrictions-et-adaptateurs.md)

## Contexte

Un canal connecté appelle la plateforme depuis Internet (disponibilité, création, modification, annulation, consultation) et attend des réponses rapides : la disponibilité en moins d'une seconde, une création confirmée ou refusée sans ambiguïté. Il renvoie volontiers un message dont il n'a pas reçu la réponse. Dans l'autre sens, chaque changement de disponibilité, de prix, de quota ou d'arrêt de vente doit lui parvenir vite, sans le submerger, malgré ses pannes. Le cahier des charges de Booking.com fixera l'authentification exacte (`TODO(spec)`) ; la spécification promet une clé d'API ou un certificat client, plus une liste d'adresses autorisées.

## Décision

### Point d'entrée

- `/api/channels/{code}/{**chemin}` (GET, POST, PUT, PATCH, DELETE) : anonyme pour l'authentification ASP.NET (le canal s'authentifie lui-même), sans jeton anti-falsification, réponses `Cache-Control: no-store`, corps lu jusqu'à `Channels:Inbound:MaxBodyBytes` (256 Kio ; au-delà `413 channels.inbound.payload_too_large`). Le chemin et le corps sont confiés à l'adaptateur du canal.
- **Limitation de débit dédiée** `channels-inbound` : fenêtre glissante **par adresse du client** (`Channels:RateLimiting` : 300 requêtes par minute, 6 segments), adresse résolue comme ailleurs (`CF-Connecting-IP` quand Cloudflare est de confiance, sinon l'adresse de connexion corrigée par App Service). Jamais par le code de l'adresse, qu'un attaquant choisit.
- **Contrôles, dans cet ordre** : code inconnu → `404 channels.inbound.unknown_channel` (la clé présentée est quand même hachée : même durée qu'une mauvaise clé) ; canal non connecté → `403 channels.inbound.not_connected` ; adresse hors de la liste → `403 channels.inbound.address_not_allowed` ; identifiants → `401 channels.inbound.unauthorized` ; adaptateur absent → `501 channels.adapter_not_configured`. Chaque rejet est journalisé (avertissement avec le code, la raison et l'adresse) et écrit au journal des messages.
- **Liste d'adresses** : plages CIDR IPv4 et IPv6 (adresses IPv4 encapsulées ramenées à l'IPv4, réseau normalisé, notations abrégées refusées), 50 au plus ; **une liste vide n'autorise personne** (échec fermé).
- **Clé d'API** (en-tête `X-Channel-Key`) : générée par la plateforme (32 octets aléatoires en base64url, préfixe `crk_`), **montrée une seule fois**, seuls son empreinte SHA-256 et un indice de 8 caractères sont gardés ; comparaison en temps constant (`CryptographicOperations.FixedTimeEquals`) ; rotation (l'ancienne cesse aussitôt) et révocation depuis l'écran du canal.
- **Certificat client** : empreinte SHA-1 ou SHA-256 enregistrée, comparée en temps constant, au certificat de la connexion TLS (Kestrel) ou, seulement si `Channels:Inbound:TrustArrClientCertHeader` est vrai, à l'en-tête `X-ARR-ClientCert` d'App Service (qui termine le TLS). Derrière Cloudflare le certificat n'atteint pas l'application sans configuration du proxy (`TODO(spec)` selon ce qu'exige Booking.com) : la clé et la liste d'adresses restent alors les protections.
- Au moins un identifiant doit être configuré et **chacun de ceux configurés doit correspondre**.
- Le message est traité dans une portée agissant comme `system:channel:{code}` (chronologie des dossiers, journal d'audit) ; une panne technique répond `500 channels.inbound.failed` : le canal renverra le message, que l'idempotence rend sans danger.

### Journal des messages

- `ChannelMessageLog` : chaque message entrant (accepté, refusé, rejeté, en erreur) et sortant : sens, opération, référence du canal, statut, statut HTTP, code d'erreur, durée, adresse, corrélation, dossier, et un **extrait de 1 000 caractères au plus sans donnée personnelle** (codes, dates, montants ; jamais nom, téléphone, e-mail ni remarque du conducteur ; caractères de contrôle retirés).
- Écrit dans sa propre portée, jamais annulé avec la transaction métier et sans jamais lever d'exception ; exclu du journal d'audit (volume) dont il tient lieu pour les échanges. Conservation : paramètre `channels.message_log_retention_days` (90 jours, plateforme), tâche `channels.purge_message_log` (03 h 50) tracée par l'action d'audit `channels.message_log_purged`.

### Idempotence et refus

- **Clé** : (canal, référence du canal), unique sur `ChannelBookings`, plus l'**empreinte SHA-256 de la demande de création** (catégorie, lieux, instants, montants, mode de paiement, options ; aucune donnée personnelle). Même référence et même empreinte → rejeu : la réponse décrit le dossier existant, marquée comme rejouée (le faux canal répond `200` au lieu de `201`), rien n'est créé, même après une modification ; même référence, autre contenu → `channels.booking.reference_conflict` (une modification passe par l'opération de modification). Deux créations identiques simultanées : la seconde bute sur l'index unique et devient un rejeu.
- **Modification** : sans changement de dates, lieux, catégorie ou options → « inchangé » ; sinon avenant contrôlé sous le verrou. **Annulation** : répétable (déjà annulé → même réponse).
- **Refus explicites, codes stables**, que chaque adaptateur traduit dans le format de son canal (le faux canal : un corps JSON portant le code, 409 pour un conflit, 422 pour une donnée refusée, 404 pour une référence inconnue, 400 pour un message illisible) : plus de véhicule (`availability.not_available`), arrêt de vente (`channels.booking.stop_sell`), code non mappé (`channels.mapping.*_unmapped`), période, montants ou conducteur invalides, monnaie autre que le dinar (`channels.booking.currency_not_supported`, `TODO(spec)` : devises du canal), référence inconnue (`channels.booking.not_found`), dossier annulé (`channels.booking.cancelled`), message illisible (`channels.inbound.malformed`).
- **Disponibilité en moins d'une seconde** : servie par le cache du moteur (ADR-0025) pour le canal, catégories mappées seulement, prix du moteur ; le banc de contrat mesure le temps de réponse sur SQL Server.

### Synchronisation sortante

- **Déclencheurs** : `ChannelAwareAvailabilityNotifier` décore `IAvailabilityChangeNotifier` (chaque dossier, véhicule ou entretien qui change la disponibilité le signale déjà) et alimente `ChannelSyncSignal` (catégories changées, en mémoire) ; un quota, un arrêt de vente ou une correspondance modifié met directement son envoi en file ; un changement de prix est détecté à chaque passage par l'empreinte de la grille publiée et de la règle du canal (le module Tarifs ne publie pas d'événement).
- **Pompe** (`ChannelSyncPump`, service d'arrière-plan quand `BackgroundJobs:RunServer`) : attend le signal, puis le **délai de regroupement** (`Channels:Sync:Debounce`, 10 s), et passe sous le verrou distribué `carrental:job:channels.sync` ; la tâche `channels.sync` (chaque minute) rattrape un signal perdu, une autre instance et les nouvelles tentatives échues.
- **File durable** (`ChannelSyncItems`) : un seul envoi en attente par (canal, opération, catégorie ou tout) — les changements se fondent ; réservation d'un envoi par la `rowversion` (bail de 5 minutes) : deux instances n'envoient jamais le même ; **contenu calculé au moment de l'envoi** depuis l'état courant : disponibilités jour par jour (le plus petit reste vendable du canal, zéro à un lieu arrêté) sur `channels.push_horizon_days` (365 jours), regroupées en plages ; prix journaliers du moteur sur 30 jours. Un canal qui n'est plus connecté voit ses envois abandonnés (`Skipped`) ; un canal manuel n'envoie rien (liste de l'extranet, ADR-0052).
- **Échecs** : nouvelle tentative après 30 s, 2 min, 5 min, 15 min, 1 h, 3 h, puis toutes les 6 h ; après `Channels:Sync:MaxAttempts` (8) tentatives, l'envoi est **en échec définitif**. L'entrée de santé `channels` (étiquette « ready ») passe alors à **Degraded**, de même quand le plus ancien envoi dû attend depuis plus de `Channels:Sync:StaleAfter` (30 min) ; l'écran `/admin/channels/sync` les montre avec la dernière erreur et le message envoyé, **« Renvoyer »** remet l'envoi en file immédiatement (compteur de tentatives remis à zéro) et « Synchroniser maintenant » lance un passage.
- **Resynchronisation complète** : tâche `channels.full_resync` chaque nuit (02 h 40) pour chaque canal connecté (disponibilités et prix de toutes les catégories mappées), et dès qu'un canal est connecté ou qu'une correspondance change.
- Chaque envoi, réussi ou non, est écrit au journal des messages.

### Configuration (`appsettings`, section `Channels`)

`Inbound:MaxBodyBytes` (262144), `Inbound:TrustArrClientCertHeader` (faux), `RateLimiting:PermitLimit` (300), `RateLimiting:Window` (00:01:00), `RateLimiting:SegmentsPerWindow` (6), `Sync:Debounce` (00:00:10), `Sync:MaxAttempts` (8), `Sync:StaleAfter` (00:30:00), `Sync:BatchSize` (20), `Sync:ClaimLease` (00:05:00), `Sync:PumpIdleInterval` (00:00:30) ; validées au démarrage.

## Conséquences

- Un message rejoué, perdu ou concurrent ne crée jamais deux dossiers ni deux annulations ; une indisponibilité est toujours refusée au canal, jamais surréservée.
- Une clé volée ne sert à rien hors des adresses autorisées ; une base volée ne livre aucune clé ; une liste oubliée ferme le canal au lieu de l'ouvrir.
- Les pannes du canal se voient (santé, écrans, journal) et se rattrapent seules ou d'un clic ; le canal reçoit l'état courant, jamais une suite de changements périmés.
- `TODO(spec)` : authentification et adresses de Booking.com, débit d'envoi autorisé, accusés de réception, devises.

## Alternatives écartées

- **Clé d'API chiffrée ou en clair** : inutile, la clé n'est montrée qu'une fois ; une empreinte suffit à la vérifier.
- **Limitation par code de canal** : partition choisie par l'appelant.
- **Liste d'adresses facultative (vide = tout le monde)** : un oubli ouvrirait le canal à Internet.
- **Envois par l'outbox** : l'outbox livre des événements un par un ; les envois doivent être fusionnés, recalculés depuis l'état courant, réessayés par canal avec des délais croissants et visibles dans un écran — d'où une file dédiée.
- **Envoi dans la transaction métier** : appel réseau sous le verrou de capacité, rejoué avec la transaction.
- **Idempotence par la seule référence** : une référence réutilisée pour une autre réservation passerait pour un rejeu.
