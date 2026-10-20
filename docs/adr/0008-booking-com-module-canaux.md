# ADR-0008 — Booking.com via candidature fournisseur et module Canaux

- **Statut** : Acceptée (choix « connecteur maison ou intermédiaire » encore ouvert)
- **Date** : 2026-10-20
- **Source** : [spécifications](../specifications.md), section 10

## Contexte

Booking.com n'offre pas d'API publique pour publier une flotte. La Demand API sert aux affiliés qui revendent l'inventaire de Booking, pas aux loueurs. L'agence doit d'abord être acceptée comme fournisseur, puis se connecter en XML/API selon un cahier des charges remis à l'intégration.

## Décision

- Lancer la **candidature fournisseur** dès la semaine 1.
- Construire un **module Canaux** générique : un modèle interne unique de disponibilités, tarifs et quotas, et un adaptateur par canal.
  - Entrant : disponibilité et tarif, création, modification, annulation, consultation, en moins d'une seconde depuis le cache.
  - Sortant : publication à chaque changement, plus une resynchronisation complète chaque nuit.
  - Correspondances ACRISS, lieux (TUN, NBE, MIR, DJE…), options et frais selon les listes OpenTravel Alliance.
  - Fiabilité : référence canal unique (idempotence), journal de chaque message, alerte et file de rejeu.
- Deux voies possibles : **connecteur maison** certifié (pas d'abonnement, délai de certification) ou **fournisseur de connectivité** (plus rapide, abonnement ou commission).

## Conséquences

- Booking.com arrive au dernier lot (S18–S20), car son calendrier dépend de l'acceptation fournisseur.
- En attendant l'API : tarifs gérés sur l'extranet, réservations saisies dans le back office avec la source « Booking.com », arrêts de vente signalés par le planning.
- Les réservations reçues sont « Confirmée » d'office et portent prix brut, commission et net pour le rapprochement mensuel.
