# ADR-0048 — Boîte WhatsApp partagée et notifications : reprise en main, modèles approuvés, consentement, idempotence

- **Statut** : Acceptée (noms et langues des modèles de l'agence à confirmer)
- **Date** : 2026-10-21
- **Source** : [conception phase 4](../conception/phase-4-whatsapp.md) §3, [prompt](../PROMPT.md) §9.2 et §12, [spécifications](../SPEC.md) §11, §14 et §16, [ADR-0027](0027-personnes-dedoublonnage-et-anonymisation.md), [ADR-0030](0030-dossier-de-location-agregat-et-chronologie.md), [ADR-0047](0047-whatsapp-webhook-signe-traitement-asynchrone-et-fenetre-24-h.md)

## Contexte

Le bot ne doit jamais bloquer un client : un agent reprend la conversation depuis le back office, répond dans la fenêtre de 24 heures et, au-delà, par un modèle approuvé, puis rend la main au bot. Les conversations se rattachent à un client ou à un dossier. L'agence envoie aussi des notifications « utilité » (confirmation, rappel la veille avec l'adresse, rappel de retour, lien de paiement, demande d'avis), uniquement avec le consentement WhatsApp recueilli dans le tunnel, et jamais deux fois : l'outbox livre au moins une fois et les tâches de rappel repassent toutes les heures. Les noms des modèles approuvés par Meta ne sont pas encore connus. Les échanges sont conservés 12 mois par défaut.

## Décision

### La boîte partagée (`/admin/whatsapp`, permission `whatsapp.inbox`)

- **Liste** paginée (`AdminTablePager`) : nom (personne rattachée, sinon nom de profil WhatsApp, sinon numéro), mode (bot, agent nommé, « en attente d'un agent » après un transfert du bot), dossier rattaché, non-lus, dernier message, compte à rebours de la fenêtre de 24 h ; vues ouvertes, non lues, gérées par un agent, par le bot, closes, toutes ; recherche par numéro ou nom.
- **Conversation** : fil des messages (auteur en toutes lettres, heure de Tunis, statut de livraison et motif d'un échec, choix offerts par un message interactif ; un média reçu est annoncé, jamais affiché), ce que le bot a compris et ce qu'il a coûté, rattachements avec liens vers la fiche de la personne (`customers.read`) et le dossier (`bookings.read`) : personnes ayant ce numéro en un clic, recherche, dossiers de la personne ou numéro de dossier.
- **Prendre la main** (le bot se tait, y compris un tour en cours, et ses réponses encore en file sont annulées ; reprendre la main d'un collègue la réattribue), **rendre au bot**, **clore** (un message du client rouvre, le mode est gardé), **rouvrir**. Prises en main, remises, rattachements et messages d'agent écrivent un événement du journal d'audit (`messaging.conversation_taken_over`, `messaging.conversation_handed_back`, `messaging.conversation_linked`, `messaging.message_sent_by_agent`), sans contenu de message ; la ligne de la conversation est auditée en plus, données personnelles masquées.
- **Répondre**, par l'agent qui a la main seulement (un collègue la reprend d'abord) : texte libre dans la fenêtre (4 096 caractères), modèle approuvé avec ses paramètres à tout moment, seule possibilité une fois la fenêtre fermée. Le service revérifie la permission, l'agent, le mode et la fenêtre ; une action croisant un message du client au même instant est rejouée une fois sur des données fraîches. Le brouillon (texte, modèle, paramètres, numéro de dossier saisi) appartient à la conversation affichée : il ne suit jamais l'agent vers un autre client.
- **Quasi temps réel** : chaque validation qui touche une conversation, un message ou un statut prévient les écrans ouverts (écouteur des modifications validées, en processus) ; l'écran se recharge aussitôt (les notifications d'une rafale se fondent en un rechargement de plus), et toutes les 30 secondes en secours. Ouvrir une conversation marque ses messages lus. Les agents sont affichés par leur nom (module Identité).
- Une conversation s'ouvre directement par `/admin/whatsapp?conversation={id}`.

### Les notifications

| Notification | Déclencheur | Paramètres du modèle (ordre `{{1}}`, `{{2}}`…) |
|---|---|---|
| `booking_confirmed` | `BookingStatusChanged` vers `Confirmed` (demande validée, option confirmée, dossier payé, réservation créée confirmée) | prénom, numéro du dossier, date et heure de départ, lieu de départ |
| `payment_link` | `BookingStatusChanged` vers `AwaitingPayment` | prénom, numéro du dossier, montant dû, échéance |
| `pickup_reminder` | tâche `messaging.pickup_reminders`, chaque heure de 9 h à 19 h : départs de demain confirmés | prénom, numéro, date et heure de départ, lieu, adresse |
| `return_reminder` | tâche `messaging.return_reminders`, chaque heure de 9 h 15 à 19 h 15 : retours de demain des locations en cours | prénom, numéro, date et heure de retour, lieu, adresse |
| `review_request` | tâche `messaging.review_requests`, chaque heure de 10 h 30 à 19 h 30 : retours d'hier (restitués ou clôturés) | prénom, lien de l'avis |
| `conversation_follow_up` | envoyé par un agent, hors fenêtre | nom du client |

- **Destinataire** : le conducteur principal du dossier, à son téléphone. **Consentement** `WhatsAppNotifications` vérifié dans le module Clients (contrat `ICustomersQueries.HasConsentAsync`) quand la notification est préparée, puis de nouveau au moment de l'envoi : sans lui, ou retiré entre-temps, rien ne part (le message est annulé, motif affiché) ; une personne anonymisée ou sans téléphone n'est pas notifiée.
- **Modèles configurés** dans `/admin/settings` (groupe WhatsApp, valeur unique de la plateforme) : `messaging.templates.*` (nom approuvé, vide = notification coupée) et `messaging.template_language` (`fr` par défaut, aussi la langue des dates et montants envoyés) ; `messaging.review_link` (par agence, vide = pas de demande d'avis). `TODO(spec)` : noms et langues réels des modèles approuvés de l'agence ; les valeurs par défaut sont les noms du tableau, à créer chez Meta avec ces paramètres.
- **Adresse** : celle du lieu de départ ou de retour, sinon celle de l'agence, sinon sa ville.
- **Idempotence** : une ligne `BookingNotification` par dossier et par sorte (index unique), écrite dans la même transaction que le message du modèle ; un message d'outbox rejoué ou une tâche repassée ne renvoie rien. Une notification qui ne s'applique plus (statut changé entre-temps) ou impossible (pas de consentement, pas de téléphone) n'écrit rien : la tâche suivante réessaie, le consentement peut arriver plus tard.
- La notification part dans la conversation du numéro (créée au besoin), rattachée à la personne et au dossier s'ils ne l'étaient pas ; elle part quel que soit le mode, n'ouvre pas la fenêtre de 24 h et suit les mêmes règles d'envoi que tout message ([ADR-0047](0047-whatsapp-webhook-signe-traitement-asynchrone-et-fenetre-24-h.md)).
- `TODO(spec)` **lien de paiement** : l'adresse Clic to Pay du lot 2B n'est exposée par aucun contrat ; le modèle porte le montant et l'échéance, le lien part par e-mail en attendant un paramètre de bouton URL.

### La rétention

- Tâche `messaging.purge_conversations` (chaque nuit à 3 h 15, heure de Tunis) : supprime par lots les messages plus anciens que `messaging.retention_days` (365 jours par défaut, de 30 à 3 650, valeur de la plateforme), puis les conversations sans activité depuis et les preuves de notification du même âge ; écrit le décompte au journal d'audit.
- **Personnes anonymisées** : la même tâche détache des conversations toute personne que le module Clients a anonymisée (ou ne connaît plus), avec son payeur.

## Conséquences

- Un agent reprend une conversation en un clic et le bot ne peut plus y répondre, même un tour en cours ; chaque prise en main et chaque message d'agent sont tracés.
- La liste et le fil restent à jour sans recharger la page sur l'instance qui traite l'outbox ; sur plusieurs instances, le rechargement périodique (30 s) prend le relais.
- Les notifications ne dépendent d'aucun format inconnu : seuls les noms des modèles restent à saisir quand l'agence les aura fait approuver ; un modèle mal nommé échoue chez Meta (code 132001), visible sur le message.
- Les rappels sont envoyés par jour prévu du dossier (départ ou retour planifié), pas par l'heure réelle d'un retour anticipé.
- Les données de démonstration déclenchent des notifications vers leurs personnes consentantes (faux en développement).

## Alternatives écartées

- **Répondre sans prendre la main** : le bot et l'agent pourraient répondre au même message, et le client recevrait deux réponses facturées.
- **Lire le consentement dans le module Messagerie** (copie) : il deviendrait faux au premier retrait ; il est lu à la source à chaque envoi.
- **Noms de modèles dans le code ou dans des options** : à redéployer à chaque modèle approuvé ; un paramètre se change dans l'écran des paramètres, audité.
- **Rappels par tâche Hangfire programmée par dossier** : à reprogrammer à chaque avenant ; un balayage horaire idempotent est plus simple.
- **Rattacher automatiquement une personne parmi plusieurs ayant le même numéro** : risque de fausse fusion ([ADR-0027](0027-personnes-dedoublonnage-et-anonymisation.md)) ; l'agent choisit.
- **Rafraîchir par sondage seul** : chaque écran ouvert interrogerait la base toutes les quelques secondes pour rien.
