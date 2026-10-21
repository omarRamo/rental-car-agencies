# ADR-0054 — Exécuteur des tours du bot WhatsApp : hors de l'outbox, en parallèle entre conversations, un à la fois par conversation

- **Statut** : Acceptée
- **Date** : 2026-10-21
- **Source** : [conception phase 4](../conception/phase-4-whatsapp.md) §2 et §5 (lot 4B), [ADR-0043](0043-outbox-pompe-en-processus.md), [ADR-0047](0047-whatsapp-webhook-signe-traitement-asynchrone-et-fenetre-24-h.md), [ADR-0048](0048-boite-whatsapp-partagee-et-notifications-avec-consentement.md)

## Contexte

Au lot 4A, le tour du bot d'un message client s'exécutait dans le gestionnaire d'outbox de ce message : préparer le tour,
appeler le répondeur, appliquer sa réponse sur des données fraîches (une reprise par un agent gagne toujours). Le
répondeur par défaut ne faisait rien. Avec l'agent IA, un tour dure plusieurs secondes (un à quatre appels au modèle) et
peut échouer pour une raison passagère (surcharge, limite de débit, coupure). Or l'outbox est traitée en séquence et sous
verrou ([ADR-0043](0043-outbox-pompe-en-processus.md)) : un seul tour lent retarderait toutes les notifications, les
paiements et les autres événements, et une panne du modèle ferait retenter tout le message d'outbox.

Il faut donc des tours **durables** (rien en mémoire : un redémarrage ne perd aucun tour), **parallèles** entre
conversations, **jamais deux à la fois pour une même conversation** (sur plusieurs instances), déclenchés **sans
attendre**, retentés avec des délais croissants et, en dernier recours, **transférés à un humain** — jamais abandonnés en
silence — tout en gardant la règle préparer → répondre → appliquer.

## Décision

### Le gestionnaire du message ne fait plus que transmettre

- Le gestionnaire d'outbox d'un message client appelle `IBotTurnDispatcher.DispatchAsync`. Par défaut (tests de
  l'application, hôte sans infrastructure), le tour s'exécute sur place comme au lot 4A (`InlineBotTurnDispatcher`).
  L'infrastructure remplace ce répartiteur par un simple réveil de l'exécuteur.
- La **file** est faite des messages eux-mêmes : une conversation est due quand elle a un message client en attente du
  bot (`BotTurn = Pending`), que le client s'est tu depuis `BotTurns:QuietPeriod` (2 s : une rafale de messages reçoit une
  seule réponse) et que sa nouvelle tentative éventuelle est échue ; la plus ancienne d'abord. Un index filtré
  (`IX_Messages_PendingBotTurns`) sert cette requête.

### La pompe des tours (`BotTurnPump`)

- Service d'arrière-plan, sur l'instance qui fait tourner le serveur de tâches (`BackgroundJobs:RunServer`), comme la
  pompe de l'outbox. Signal à un seul jeton : il est réveillé juste après toute validation qui écrit des messages
  (`ICommittedChangesListener`, signal en processus) et par le gestionnaire de chaque message client ; sans signal, il
  balaie la file toutes les `BotTurns:SweepInterval` (15 s) ou à la prochaine échéance (fin d'un silence, nouvelle
  tentative) — filet de sécurité pour les redémarrages et les autres instances.
- Au plus `BotTurns:MaxParallelism` (4) conversations à la fois sur l'instance. Un tour est marqué « en cours » avant de
  démarrer ; une conversation tenue ailleurs (verrou pris) est laissée deux secondes de côté, une conversation dont
  l'exécution a échoué hors du répondeur (base indisponible, donnée illisible) trente secondes, sans boucle active.

### Un tour à la fois par conversation, sur toutes les instances

- `SqlConversationTurnLock` : `sp_getapplock` exclusif sur `conversation:{id}`, détenu par la **session** d'une connexion
  dédiée, pris sans attente (`@LockTimeout = 0`) et gardé pendant tout le tour, appels au modèle compris ; libéré
  explicitement, et par SQL Server si la connexion ou le processus meurt. Les hôtes sans SQL Server (tests) ont un verrou
  en processus.

### Le tour (`BotTurnRunner`)

1. Prendre le verrou de la conversation (sinon : « occupée », réessayée plus tard).
2. Préparer le tour du **dernier** message en attente : il répond à toute la rafale.
3. Appeler le répondeur hors de toute transaction, dans une portée à lui.
4. Appliquer la réponse sur des données fraîches : une reprise par un agent validée entre-temps gagne (le message ne part
   pas, les messages du tour sont marqués « réduits au silence », le coût des appels compte).
5. En cas d'échec du répondeur : une ligne `BotTurnRetry` compte la tentative et fixe la suivante avec un délai
   croissant (`BotTurns:RetryDelays`, 20 s ; 1 min ; 5 min). Après la dernière tentative, la conversation est **transférée
   à un humain** avec un message poli dans la langue du client et le motif « Assistant IA indisponible après plusieurs
   tentatives ». La ligne de tentative est effacée quand le tour aboutit.

## Conséquences

- L'outbox n'attend plus jamais le modèle ; le bot répond environ deux secondes après le dernier message du client, plus
  le temps du modèle.
- Rien n'est perdu au redémarrage : les messages en attente et les tentatives sont en base. Un tour interrompu par un
  arrêt reste en attente et repart ensuite ; ses appels au modèle déjà faits sont relevés comme « retentés » dans les
  coûts.
- Chaque tour en cours tient une connexion SQL (au plus `MaxParallelism` par instance).
- Les tests de l'application gardent l'exécution sur place ; les tests d'intégration vérifient sur SQL Server le verrou,
  le parallélisme entre conversations, la sérialisation de deux rafales d'une même conversation et la reprise par un
  agent pendant l'appel au modèle.
- Options : `BotTurns:MaxParallelism` (1 à 32), `BotTurns:QuietPeriod` (0 à 1 min), `BotTurns:SweepInterval` (1 s à
  10 min), `BotTurns:RetryDelays` (dix délais au plus), validées au démarrage.
