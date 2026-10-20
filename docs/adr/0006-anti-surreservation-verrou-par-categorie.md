# ADR-0006 — Anti-surréservation par verrou applicatif par catégorie

- **Statut** : Acceptée
- **Date** : 2026-10-20
- **Source** : [spécifications](../SPEC.md), section 7

## Contexte

Cinq canaux (site, WhatsApp, téléphone, comptoir, Booking.com) vendent la même flotte. L'objectif est zéro surréservation, garanti par un contrôle bloquant.

## Décision

- **Capacité** d'une catégorie à un instant : véhicules actifs, hors maintenance et hors échéance dépassée.
- **Occupation** : dossiers actifs (demande sous blocage, option, confirmée, en cours) qui chevauchent cet instant, tampon de préparation inclus (2 h après chaque retour par défaut).
- Une période est **disponible** si, à chaque instant, occupation + 1 ≤ capacité. Avec des véhicules interchangeables, cela garantit qu'une affectation existe.
- Toute création ou modification s'exécute dans une **transaction avec `sp_getapplock` par catégorie** : le second demandeur reçoit « plus disponible ».
- **Quotas et arrêts de vente** par canal et par catégorie.
- Booking.com et le bot lisent un **cache mémoire** des disponibilités, invalidé à chaque changement de dossier ou de flotte.

## Conséquences

- Le serveur refuse tout chevauchement, même si l'écran du planning le permet.
- Tests obligatoires : test par propriétés (« jamais plus de dossiers actifs que de véhicules, à aucun instant ») et test d'intégration sur SQL Server réel avec deux demandes simultanées sur la dernière voiture.
- Le cache mémoire suppose une seule instance (voir [ADR-0004](0004-hebergement-app-service-b1-cloudflare.md)).
