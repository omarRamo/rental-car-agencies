# ADR-0043 — Outbox : déclenchement immédiat par une pompe en processus

- **Statut** : Acceptée
- **Date** : 2026-10-21
- **Remplace en partie** : [ADR-0013](0013-outbox-traitee-par-hangfire.md) (déclenchement immédiat par une tâche Hangfire mise en file à chaque validation)

## Contexte

Chaque validation qui écrivait des messages d'outbox mettait en file une tâche Hangfire. Cette tâche prenait le verrou distribué de l'outbox avec une attente d'une seconde et abandonnait si un traitement était déjà en cours. Deux défauts en résultaient, visibles sur les machines d'intégration continue (plus lentes) :

- **file saturée** : sous charge, chaque validation ajoutait une tâche ; les deux travailleurs Hangfire passaient leur temps à attendre le verrou au lieu de traiter les messages ;
- **déclenchement perdu** : un message validé juste après la dernière lecture d'un traitement en cours voyait sa tâche abandonner ; il attendait la tâche planifiée suivante (jusqu'à une minute), puis davantage si elle tombait à son tour sur un traitement en cours.

## Décision

- Les validations réveillent une **pompe en processus** (`OutboxPump`, service d'arrière-plan) par un signal à un seul jeton (`OutboxWakeup`) : les signaux se fondent, et un signal reçu pendant un traitement provoque exactement un passage de plus. Aucun message validé n'attend donc la tâche planifiée.
- La pompe prend le **même verrou distribué** que la tâche planifiée (`carrental:job:outbox.process`), avec une attente de 30 secondes : un seul traitement à la fois, toutes instances confondues. Si une autre instance tient le verrou, la pompe garde son signal et réessaie juste après.
- Sans signal, la pompe passe toutes les 15 secondes (`Outbox:PumpIdleInterval`) pour les nouvelles tentatives arrivées à échéance.
- La tâche Hangfire de chaque minute reste en **filet de sécurité** (redémarrage, autre instance). Une instance qui ne fait pas tourner le serveur de tâches (`BackgroundJobs:RunServer=false`) garde l'ancien déclenchement : elle met une tâche en file pour l'instance qui traite.

## Conséquences

- Les messages sont traités dans la seconde qui suit la validation, même sous charge, sans occuper les travailleurs Hangfire.
- Les tests d'intégration de l'outbox ne dépendent plus de la vitesse de la machine : ils échouaient sur la CI et passaient en local.
- Le traitement reste séquentiel et sous verrou : l'ordre des effets de bord et l'unicité du traitement sont inchangés (ADR-0040).
