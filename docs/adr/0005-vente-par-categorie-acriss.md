# ADR-0005 — Vente par catégorie ACRISS, véhicule affecté ensuite

- **Statut** : Acceptée (à confirmer avec l'agence : « vente par catégorie ou par modèle » est un point ouvert)
- **Date** : 2026-10-20
- **Source** : [spécifications](../SPEC.md), sections 4 et 7

## Contexte

Les comparateurs comme Booking.com vendent des catégories « ou similaire ». Vendre un véhicule précis rend l'affectation rigide et provoque des annulations dès qu'une voiture est indisponible.

## Décision

- On vend une **catégorie** identifiée par son code ACRISS (par exemple ECMR = économique, 2/4 portes, manuelle, climatisée).
- L'agent **affecte le véhicule** après confirmation ; un algorithme glouton propose une affectation, l'agent peut la modifier.
- Option payante « **modèle garanti** », avec un prix par véhicule.
- La caution est portée par la catégorie.

## Conséquences

- La disponibilité se calcule par catégorie (voir [ADR-0006](0006-anti-surreservation-verrou-par-categorie.md)).
- Les correspondances avec les canaux se font sur les codes ACRISS.
- Le planning montre les réservations non affectées regroupées par catégorie.
