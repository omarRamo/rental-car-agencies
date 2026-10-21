# ADR-0055 — Agent WhatsApp : port du modèle, SDK Anthropic, verdict sur `IChatClient`, mise en cache du prompt et coûts

- **Statut** : Acceptée
- **Date** : 2026-10-21
- **Source** : [conception phase 4](../conception/phase-4-whatsapp.md) §2 et §4, [prompt](../PROMPT.md) §9.2 et annexe B, [ADR-0009](0009-whatsapp-cloud-api-claude-haiku.md), [ADR-0054](0054-executeur-des-tours-du-bot-whatsapp.md)

## Contexte

PROMPT.md demande que l'agent passe par `IChatClient` (Microsoft.Extensions.AI) avec invocation de fonctions, et la
conception demande de **vérifier** dans le SDK C# officiel comment l'obtenir, s'il permet la mise en cache du prompt et
la lecture des jetons de cache ; sinon, une boucle d'outils explicite derrière le même port. Le modèle par défaut est
`claude-haiku-5-5`, effort `low`, sans pré-remplissage ; un refus n'a pas de modèle de repli et entraîne un transfert.
Le fournisseur est l'API Anthropic ou Microsoft Foundry. Les coûts doivent être relevés à chaque appel.

## Décision

### SDK et verdict sur `IChatClient` (vérifié)

- Paquets : `Anthropic` 12.60.0 et `Anthropic.Foundry` 0.8.0 (SDK officiels), `Microsoft.Extensions.AI.Abstractions`
  10.5.1 dans l'application (le port ne dépend d'aucun fournisseur). Le SDK fournit `AsIChatClient(modèle, maxTokens)`.
- Vérifié hors ligne sur le vrai SDK, en enregistrant le JSON qu'il envoie à l'API Messages (tests
  `ClaudeChatClientTests`) :
  - `cache_control` sur un contenu (extension `WithCacheControl`) est envoyé sur le bloc `system` ; un `cache_control` de
    niveau requête passe par `ChatOptions.RawRepresentationFactory` → `MessageCreateParams.CacheControl` ;
  - l'usage revient complet : `InputTokenCount` = entrée non mise en cache + lectures + écritures de cache,
    `CachedInputTokenCount` = `cache_read_input_tokens`, `AdditionalCounts["CacheCreationInputTokens"]` =
    `cache_creation_input_tokens` ;
  - `stop_reason` `refusal` → `ChatFinishReason.ContentFilter`, `max_tokens` → `Length`, `tool_use` →
    `FunctionCallContent` ; un `FunctionResultContent` portant une exception part en `tool_result` avec `is_error: true` ;
  - `ReasoningOptions.Effort = Low` → `output_config.effort: low` (avec `thinking: adaptive`) ; un message
    `ChatRole.System` après la transcription part en message `role: system` au milieu de la conversation.
- **Verdict** : `IChatClient` est le port du modèle (`IAgentModel.Client`). En revanche, l'orchestrateur **n'utilise pas**
  l'invocation automatique de fonctions (`UseFunctionInvocation`) : il mène une **boucle d'outils explicite et bornée**
  (appels au modèle et appels d'outils plafonnés par tour, arguments validés contre le schéma avant exécution, résultats
  renvoyés comme données, usage relevé à chaque appel, garde-fous appliqués au texte final). Écart assumé avec la lettre
  de PROMPT.md (« invocation de fonctions ») : la sûreté exige que le code garde la main sur chaque appel.

### Fournisseurs et réglages (section `Ai`, validée au démarrage)

- `Ai:Mode` : `Anthropic` (API Claude, clé `Ai:ApiKey`, secret Key Vault `Ai--ApiKey`), `Foundry` (`Anthropic.Foundry`,
  ressource `Ai:Foundry:Resource`, clé `Ai--Foundry--ApiKey` ou, sans clé, l'identité managée ; déploiements « Global »
  ou « US Data Zone », sans zone européenne : voir les points ouverts), `Fake` (modèle scripté, développement et test
  seulement, refusé en production), `Disabled` (le bot se tait). `Auto` (défaut) : `Fake` en développement et en test,
  `Disabled` ailleurs tant que rien n'est configuré.
- `Ai:Model` (`claude-haiku-5-5`), `Ai:MaxOutputTokens` (2 048, réflexion comprise), `Ai:Timeout` (30 s par appel),
  `Ai:MaxRetries` (2 nouvelles tentatives du SDK sur 429, surcharge ou 5xx, avant celles de l'exécuteur),
  `Ai:PromptCacheTtl` (`5m` ou `1h`), `Ai:ContextPlacement` (`Auto`, `SystemMessage`, `UserTurn`), `Ai:PilotNumbers`
  (déploiement progressif : le bot ne répond qu'à ces numéros).
- Réponse refusée par le modèle (`refusal`) : transfert poli, sans modèle de repli. Réponse coupée (`max_tokens`) :
  jamais envoyée, transfert. Refus définitif du service (4xx sauf 429 : clé, requête) : transfert immédiat et journal
  critique. Panne passagère (429, 5xx, délai) : l'exception remonte et l'exécuteur retente le tour
  ([ADR-0054](0054-executeur-des-tours-du-bot-whatsapp.md)).
- La clé n'est jamais journalisée ; la télémétrie du SDK est réglée sur les **métadonnées seules** (aucun contenu de
  message dans les traces), quelles que soient les variables d'environnement.

### Disposition de la requête pour le cache

1. **Préfixe stable**, identique à l'octet près d'un tour, d'une conversation et d'un jour à l'autre (vérifié par les
   tests) : les cinq outils (ordre et schémas fixes), puis le prompt système — l'annexe B **mot pour mot** (fichier
   `Messaging/Prompts/whatsapp-agent.md`, comparé à PROMPT.md par un test d'architecture) avec le nom, la ville, les
   horaires et l'adresse de l'agence, puis l'addendum technique (`whatsapp-agent-addendum.md` : résultats d'outils =
   données, un seul message, boutons, montants recopiés, bloc de contexte, pièces jointes jamais ouvertes). Point de
   cache explicite sur le dernier bloc système.
2. La transcription (50 messages au plus, rendue toujours de la même façon).
3. Le **contexte variable** du tour, en message système au milieu de la conversation, balisé
   `<contexte_plateforme>` : la phrase de la date de l'annexe B, la fin de la fenêtre de 24 h, la langue probable, le
   besoin connu, les options en cours (ids), le dernier lien, le dossier rattaché, les pièces jointes non ouvertes, les
   tours restants avant le plafond.
4. Point de cache automatique de niveau requête : la queue qui grandit pendant la boucle d'outils d'un tour est relue
   depuis le cache.

- **La date** : l'annexe B place « Nous sommes le {{TODAY}} (heure de Tunis). » dans le prompt système ; la laisser là
  invaliderait le cache à chaque minute. Le fichier garde l'annexe telle quelle ; au rendu, cette phrase est retirée du
  préfixe et rendue dans le contexte variable du tour.
- Repli : si l'API refuse les messages système au milieu de la conversation (400 qui les mentionne), le contexte passe
  dans le dernier message du client, pour la durée du processus ; sur Foundry, d'emblée (`Ai:ContextPlacement=Auto`).
- Un préfixe sous le minimum de mise en cache du modèle n'est pas mis en cache : c'est accepté, et journalisé une fois.

### Coûts et plafonds

- Chaque réponse donne ses jetons : entrée non mise en cache, sortie, lectures et écritures de cache. Le coût IA en USD
  vient d'une grille de réglages de la plateforme (sans valeur par agence) : `ai.price_input_usd_per_mtok` (0,10),
  `ai.price_output_usd_per_mtok` (0,50), `ai.price_cache_read_ratio` (0,1 × l'entrée), `ai.price_cache_write_ratio`
  (1,25 × l'entrée) — à vérifier sur la page des tarifs. Le coût Meta estimé d'une réponse vient de
  `ai.meta_usd_per_message` (0 par défaut, `TODO(spec)`).
- Le tour rend son usage (`ConversationTurnUsageInfo`) à la conversation (cumul affiché dans la boîte partagée) et
  laisse un relevé (`AgentTurnRecords` : modèle, issue, langue, appels, jetons, coûts — sans aucun texte), y compris pour
  une tentative retentée. Écran `/admin/whatsapp/costs` (`reports.read`) : par mois (heure de Tunis) avec la part servie
  par le cache, et par dossier rattaché.
- Plafonds par conversation (réglages de la plateforme), transfert au-delà : `ai.max_bot_turns` (30), `ai.max_tokens`
  (300 000), `ai.max_cost_usd` (0,50), `ai.max_turns_without_progress` (6) ; par tour : `ai.max_model_calls_per_turn` (4),
  `ai.max_tool_calls_per_turn` (6) ; `ai.max_status_lookups` (3 recherches de dossier par conversation). `ai.enabled`
  coupe le bot sans toucher à la configuration.

## Conséquences

- L'application ne dépend que de l'abstraction `IChatClient` ; changer de fournisseur ou de modèle est une affaire de
  configuration.
- Le comportement du SDK (JSON envoyé, usage, raisons d'arrêt, erreurs) est couvert sans clé ni réseau ; le vrai modèle
  n'est évalué qu'à la demande ([ADR-0056](0056-garde-fous-outils-minimisation-et-evaluation.md)).
- Une mise à jour du SDK qui changerait la sérialisation (cache, message système, `is_error`) casse ces tests avant la
  production.
