# ADR-0040 — Outbox : messages sans gestionnaire terminés en bloc

- **Statut** : Acceptée
- **Date** : 2026-10-21
- **Complète** : [ADR-0013](0013-outbox-traitee-par-hangfire.md) et [ADR-0028](0028-exploitation-outbox-et-requetes-fractionnees.md)

## Contexte

Chaque événement de domaine est écrit dans l'outbox, dans la même transaction que la modification. Beaucoup d'événements n'ont pourtant aucun gestionnaire (véhicule enregistré, document ajouté, plan tarifaire modifié…) : ils servent de trace et de point d'accroche pour de futurs modules (canaux, rapports).

Le processeur parcourait les messages un par un, dans l'ordre d'arrivée, chacun dans sa propre portée et avec deux allers-retours vers la base. Sous charge, ces messages sans travail formaient une file devant les messages utiles : avec la suite d'intégration complète, un message ayant un gestionnaire n'était plus traité en 45 secondes. En production, un import ou une resynchronisation aurait retardé de la même façon les e-mails de confirmation.

## Décision

- Tous les événements restent écrits dans l'outbox : la trace est conservée, puis purgée comme les autres messages (`outbox.processed_retention`).
- Au début de chaque passage, le processeur marque traités, **en une seule instruction SQL** (`ExecuteUpdate`), les messages en attente, jamais tentés, dont le type n'a aucun gestionnaire déclaré (`AddOutboxHandler`). Un message déjà en échec (gestionnaire retiré depuis) reste visible sur l'écran de l'outbox jusqu'à ce qu'un gérant le relance ; la relance remet ses tentatives à zéro et le passage suivant le termine.
- Les messages restants, ceux qui ont un travail à faire, sont ensuite lus et traités un par un comme avant (ordre d'arrivée, tentatives, délais croissants, écran des échecs).

## Conséquences

- Les messages utiles ne sont plus retardés par les traces : la suite d'intégration passe d'environ 77 secondes à moins de 10.
- Un module qui ajoute plus tard un gestionnaire ne traite que les événements écrits après son déploiement (même comportement qu'avant : un message sans gestionnaire était déjà marqué traité).
- Le traitement reste séquentiel : l'ordre des effets de bord (confirmation puis annulation, par exemple) est préservé.

## Alternatives écartées

- Ne pas écrire les événements sans gestionnaire : plus simple, mais perd la trace attendue par les modules (tests de la flotte, des paiements et des paramètres) et oblige à redéployer pour la retrouver.
- Traiter les messages en parallèle : plus rapide, mais l'ordre des effets de bord ne serait plus garanti.
