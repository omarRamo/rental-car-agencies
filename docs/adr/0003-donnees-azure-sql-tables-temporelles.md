# ADR-0003 — Azure SQL avec tables temporelles, Blob Storage privé

- **Statut** : Acceptée
- **Date** : 2026-10-20
- **Source** : [spécifications](../SPEC.md), sections 12, 13 et 14

## Contexte

Tout doit être tracé : qui a fait quoi, quand, et l'état de n'importe quel tarif, dossier ou véhicule à n'importe quelle date. Le coût doit rester bas.

## Décision

- **Azure SQL Database** (Basic 5 DTU au départ, S0 10 DTU si besoin) via EF Core 10.
- **Tables temporelles** pour l'historique de chaque ligne, sans code applicatif.
- **Azure Blob Storage** en conteneur privé pour les pièces d'identité, les photos d'états des lieux et les PDF, servis par l'application ou par lien signé de quelques minutes.
- Conventions : GUID version 7, montants en `decimal(18,3)` dans un objet valeur `Money` en TND, horodatages en UTC affichés en `Africa/Tunis`, suppression logique, concurrence optimiste par `rowversion` sur les agrégats.

## Conséquences

- La traçabilité repose sur trois niveaux : chronologie du dossier, journal d'audit en ajout seul, tables temporelles.
- Restauration à l'instant près (7 jours en Basic), plus une rétention longue hebdomadaire et mensuelle.
- Hangfire stocke aussi ses tâches dans cette base.
- Les prix Azure SQL de la spec viennent d'une source de 2016 : à revérifier (voir [points ouverts](../points-ouverts.md)).
