# ADR-0007 — Clic to Pay : pré-autorisation à la demande, capture à la validation

- **Statut** : Acceptée (dépend de l'activation de la pré-autorisation sur le compte marchand)
- **Date** : 2026-10-20
- **Source** : [spécifications](../specifications.md), section 9

## Contexte

Les demandes web passent par une validation manuelle de l'agent. Il faut que la demande soit garantie sans avoir à rembourser le client en cas de refus.

## Décision

- Intégration par **redirection** vers la page hébergée Clic to Pay : aucun numéro de carte vu ni stocké.
- À la soumission : `registerPreAuth.do` avec un numéro de commande interne unique (idempotence).
- Au retour du client : vérification serveur par `getOrderStatusExtended.do`, seule source de vérité ; les paramètres de l'URL de retour ne prouvent rien.
- Validation par l'agent : `deposit.do`. Refus ou expiration : `reverse.do`. Remboursement : `refund.do`, validé par le gérant au-delà d'un seuil.
- Une tâche relit toutes les 15 minutes les paiements en attente ; rapprochement quotidien avec le relevé marchand.

## Conséquences

- **Plan de repli** si la pré-autorisation n'est pas activée : lien `register.do` valable 24 h après validation, dossier « Validée, en attente de paiement », capacité bloquée jusque-là.
- La caution se prend au comptoir sur TPE tant que la durée de vie d'une pré-autorisation n'est pas confirmée.
- Les informations d'API viennent d'un SDK non officiel : montants en millimes, codes de statut et URL de production sont à confirmer en recette (voir [points ouverts](../points-ouverts.md)).
