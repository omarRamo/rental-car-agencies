# Conception — Phase 4 (agent IA WhatsApp)

Contrat commun aux agents de la phase 4. Il précise [SPEC.md](../SPEC.md) §11 (« Agent IA WhatsApp ») et [PROMPT.md](../PROMPT.md) §6 (invariant 10), §9.2 et l'annexe B (prompt système). En cas de doute, ces documents priment et l'écart va dans [points-ouverts.md](../points-ouverts.md).

**Fini quand** (PROMPT §15) : les 30 conversations d'évaluation passent et un agent reprend une conversation depuis le back office.

## 1. WhatsApp Cloud API (Meta)

- Webhook dans l'application : `GET /webhooks/whatsapp` (vérification `hub.mode`, `hub.verify_token`, `hub.challenge`) et `POST /webhooks/whatsapp` (signature `X-Hub-Signature-256` = HMAC-SHA256 du corps brut avec le secret de l'application, comparée en temps constant ; réponse 200 immédiate ; dépôt dans une file ; déduplication par identifiant de message Meta). Les statuts de livraison (envoyé, distribué, lu, échec) et la catégorie de tarification renvoyée par Meta sont enregistrés.
- Envoi par l'API Graph (version configurable, jeton d'accès et identifiant du numéro dans le Key Vault) : texte, messages interactifs (boutons, listes) et modèles approuvés. Formats tirés de la documentation publique de Meta ; tout ce qui n'y figure pas est `TODO(spec)`. Un faux (`FakeWhatsApp`) sert en développement, en test et en E2E.
- Fenêtre de 24 h : réponse libre seulement dans les 24 h qui suivent le dernier message du client ; au-delà, modèles approuvés uniquement.
- Tarification Meta en vigueur depuis le 1er octobre 2026 : chaque réponse libre est facturée au-delà de 1 000 messages de service gratuits par mois et par numéro. D'où **un seul message par tour** et des messages interactifs pour limiter les allers-retours.

## 2. Agent IA

- Modèle par défaut `claude-haiku-5-5` (configurable), SDK officiel `Anthropic` (C#). Fournisseur configurable : API Anthropic (défaut) ou Microsoft Foundry (`Anthropic.Foundry`, déploiements « Global » ou « US Data Zone », sans zone européenne).
- PROMPT.md demande `IChatClient` (Microsoft.Extensions.AI) avec invocation de fonctions. L'agent vérifie dans la documentation ou le dépôt du SDK C# comment l'obtenir, et s'il permet la mise en cache du prompt et la lecture des jetons de cache. Sinon, l'orchestrateur appelle l'API Messages avec une boucle d'outils explicite derrière le même port, et l'écart est consigné dans une ADR.
- Réglages : effort `low` pour un dialogue ; pas de pré-remplissage de la réponse (refusé par le modèle) ; un refus (`stop_reason` = `refusal`) n'a pas de modèle de repli sur Haiku 5.5 et entraîne un transfert à un agent humain avec un message poli.
- **Mise en cache** : le prompt système (annexe B, sans la date) et les définitions d'outils forment le préfixe stable, marqué en cache. Les éléments variables (date et heure de Tunis, état du besoin, consignes ponctuelles) passent après ce préfixe, par un message système en cours de conversation, pour ne pas invalider le cache. Vérification par les jetons `cache_read_input_tokens` ; un préfixe sous le minimum du modèle n'est pas mis en cache, ce qui est accepté.
- **Coûts** : jetons d'entrée, de sortie, d'écriture et de lecture de cache relevés à chaque appel, valorisés selon une grille configurable (défauts : 0,10 USD en entrée et 0,50 USD en sortie par million de jetons, lecture de cache au dixième du prix d'entrée, à vérifier sur la page des tarifs), cumulés par conversation et par dossier attribué ; coût Meta estimé par message facturable.
- **Plafonds** par conversation : nombre de tours de l'IA, jetons cumulés, nombre d'appels d'outils par tour. Au-delà : transfert à un agent.
- **Outils** (côté serveur, en lecture seule sauf le devis et le transfert) : `rechercher_disponibilites` (disponibilités en cache et prix totaux du vrai moteur), `creer_lien_devis` (devis verrouillé 24 h via `IBookingRequests.CreateLockedQuoteAsync`, lien profond `/fr/reserver?...&devis=CODE&src=whatsapp`), `infos_agence` (base de connaissances validée par le gérant), `statut_reservation` (seulement si le numéro WhatsApp correspond à un conducteur ou au payeur du dossier), `transferer_a_un_agent` (conversation marquée « humain », le bot se tait).
- **Garde-fous vérifiés par le code**, pas seulement par le prompt : une réponse qui cite un montant absent des résultats d'outils du tour est bloquée et remplacée par un transfert ; aucune réponse ne promet une confirmation ; une pièce jointe (image, document) reçue n'est jamais traitée et le client est renvoyé au lien sécurisé ; hors sujet refusé poliment.

## 3. Boîte partagée et notifications

- `/admin/whatsapp` : liste des conversations (état bot ou humain, client ou dossier rattaché, fenêtre de 24 h restante), fil des messages, reprise en main (le bot se tait), réponse libre dans la fenêtre, modèles approuvés au-delà, rattachement à un client ou à un dossier, remise au bot. Rétention limitée (12 mois par défaut, paramètre).
- Notifications sortantes par l'outbox, uniquement avec le consentement WhatsApp du client : confirmation, rappel la veille avec l'adresse, rappel de retour, lien de paiement, demande d'avis, via des modèles « utilité » approuvés (noms et langues configurés).

## 4. Évaluation

- 30 conversations scénarisées (français, arabe, darija, anglais) décrites en fichiers de données : messages du client, résultats attendus des outils, attentes (outils appelés, transfert, absence de prix inventé, absence de promesse de confirmation).
- En CI, rejouées contre un **faux modèle scripté** et de faux moteurs : on vérifie l'orchestration, les outils et les vérificateurs de garde-fous.
- Contre le **vrai modèle**, uniquement à la demande (tâche manuelle, clé API de l'agence, coût réel) : même jeu, même vérificateurs, rapport des écarts. Rejoué à chaque modification du prompt.

## 5. Découpage de la phase 4

| Lot | Contenu | Dépend de |
|---|---|---|
| 4A WhatsApp | Webhook, signature, file, déduplication, conversations et messages, envoi (faux et réel), fenêtre de 24 h, boîte partagée, reprise humaine, modèles et notifications par l'outbox | phase 1 |
| 4B Agent IA | Orchestrateur, outils, garde-fous, mise en cache, coûts, plafonds, jeu d'évaluation | 4A (contrats de conversation) |
