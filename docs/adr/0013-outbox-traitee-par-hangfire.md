# ADR-0013 — Outbox transactionnelle traitée par Hangfire

- **Statut** : Acceptée
- **Date** : 2026-10-20
- **Source** : [prompt](../PROMPT.md) sections 5 et 16 (pas de bus de messages)

## Contexte

Les effets de bord d'un cas d'usage (e-mail, WhatsApp, synchronisation des canaux, projections) ne doivent ni être perdus si l'application s'arrête après la validation, ni partir si la transaction est annulée. Le prompt exclut un bus de messages : Hangfire (stockage SQL Server) est déjà là pour les tâches de fond.

## Décision

- **Écriture** : les agrégats lèvent des événements de domaine (`AggregateRoot.Raise`, `sealed record` implémentant `IDomainEvent`). L'intercepteur `SaveChangesPipelineInterceptor` les retire des agrégats et écrit une ligne `infra.OutboxMessages` par événement **dans le même `SaveChanges`**, donc dans la même transaction. Un message sans agrégat passe par `IOutbox.Enqueue` (exemple : `IEmailQueue` pour les e-mails).
- **Format** : type = nom complet de la classe, contenu JSON (convertisseurs du domaine : `Money` en `{"amount":"681.400","currency":"TND"}`, `Period`, `AcrissCode`, `DocumentNumber`), date d'occurrence, identifiant de corrélation.
- **Gestionnaires** : `IOutboxHandler<TMessage>`, déclarés par `services.AddOutboxHandler<TMessage, THandler>()` dans le module. Seuls les types déclarés sont désérialisés (registre des types) ; un événement sans gestionnaire est journalisé puis marqué traité.
- **Déclenchement** : (1) aussitôt après la validation de la transaction (`OutboxTransactionInterceptor`, ou après `SaveChanges` hors transaction), le traitement est mis en file Hangfire ; (2) la tâche récurrente `outbox.process` (chaque minute) sert de filet de sécurité. Un verrou distribué Hangfire garantit un seul traitement à la fois.
- **Traitement** (`OutboxProcessor`) : lots de 50 messages, 20 lots au plus par passage ; chaque message dans sa propre portée DI avec l'acteur système `system:outbox` et l'identifiant de corrélation d'origine. Les écritures en base du gestionnaire et la marque « traité » sont enregistrées ensemble. En cas d'échec : nombre de tentatives, dernière erreur et prochaine tentative après min(2^(n−1), 360) minutes ; au-delà de 10 tentatives, le message reste en table, ignoré, pour analyse.
- **Livraison au moins une fois** : un gestionnaire doit être idempotent ; `OutboxMessageContext.MessageId` sert de clé d'idempotence.
- **Hangfire** : stockage SQL Server dans le schéma `hangfire` (créé au démarrage, d'où le rôle `db_ddladmin`), tableau de bord `/admin/jobs` réservé à la permission `jobs.dashboard`. Les tâches récurrentes implémentent `IRecurringJob` (`JobId`, expression cron, fuseau `Africa/Tunis`) et se déclarent par `services.AddRecurringJob<T>()` ; elles ne se relancent pas d'elles-mêmes (`AutomaticRetry` à 0) et sont ignorées si l'exécution précédente tient encore le verrou.

## Conséquences

- Aucun effet de bord perdu ni fantôme ; délai typique de quelques secondes, d'une minute au pire.
- Tests d'intégration : événement enregistré avec l'entité puis traité, aucun message après une annulation, échec puis nouvelle tentative avec l'erreur consignée, e-mails envoyés par l'outbox.
- `TODO` : purge des messages traités anciens (tâche récurrente à ajouter avec le durcissement) et écran de suivi des messages en échec.

## Alternatives écartées

- **Envoi direct après `SaveChanges`** : perdu si le processus s'arrête entre la validation et l'envoi.
- **Mise en file Hangfire dans la transaction** : Hangfire utilise sa propre connexion ; l'atomicité dépendrait d'une transaction distribuée.
- **Bus de messages (Azure Service Bus, MassTransit)** : exclu par le prompt, coût et complexité injustifiés pour une seule application.
