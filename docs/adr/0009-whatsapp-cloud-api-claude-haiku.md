# ADR-0009 — Bot WhatsApp en Cloud API directe avec Claude Haiku 5.5 et outils

- **Statut** : Acceptée
- **Date** : 2026-10-20
- **Source** : [spécifications](../SPEC.md), section 11

## Contexte

L'agence veut un agent IA sur WhatsApp qui renvoie vers une recherche pré-remplie du site. Depuis le 15 janvier 2026, Meta interdit les assistants IA généralistes sur l'API WhatsApp Business ; l'IA au service de l'activité reste autorisée. Depuis le 1er octobre 2026, Meta facture les réponses libres au-delà de 1 000 messages gratuits par mois et par numéro.

## Décision

- **WhatsApp Cloud API directe**, sans intermédiaire, numéro dédié, entreprise vérifiée par Meta.
- Webhook dans la même application : vérification de `X-Hub-Signature-256`, réponse 200 immédiate, traitement asynchrone par file.
- Modèle **Claude Haiku 5.5** via le SDK C# officiel `Anthropic`, derrière `IChatClient` (Microsoft.Extensions.AI) pour garder modèle et fournisseur interchangeables. Option : Microsoft Foundry (`Anthropic.Foundry`), sans zone de déploiement européenne.
- Le bot n'agit qu'à travers cinq outils : `rechercher_disponibilites`, `creer_lien_devis`, `infos_agence`, `statut_reservation`, `transferer_a_un_agent`.
- **Le bot ne crée ni ne confirme jamais de réservation** : il envoie un lien vers un devis verrouillé 24 h.

## Conséquences

- Aucun prix ni disponibilité inventés : seuls comptent les résultats des outils, et le prix du site fait foi.
- Aucune pièce d'identité ni donnée de carte sur WhatsApp.
- Une réponse complète par tour et des messages interactifs, pour limiter le coût Meta, qui sera le poste principal (l'IA est estimée à 2 à 5 USD pour 1 000 conversations).
- Boîte partagée avec reprise humaine dans le back office ; rétention des conversations de 12 mois par défaut.
- Jeu de 30 conversations de test (français, arabe, anglais) rejoué à chaque modification du prompt.
