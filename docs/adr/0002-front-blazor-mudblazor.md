# ADR-0002 — Front en Blazor : vitrine en rendu serveur, back office interactif avec MudBlazor

- **Statut** : Acceptée
- **Date** : 2026-10-20
- **Source** : [spécifications](../specifications.md), section 12

## Contexte

La vitrine doit être mobile d'abord, bien référencée et rapide (LCP < 2,5 s en 4G). Le back office est dense (grilles, formulaires, planning). Le besoin laisse le choix du front mais demande une seule application.

## Décision

- **Vitrine** en Blazor rendu serveur statique : HTML complet dès la première requête, quelques lignes de JavaScript seulement (compression des photos, envoi automatique de formulaire).
- **Back office** en Blazor interactif côté serveur avec MudBlazor (licence MIT), qui appelle directement les services métier.

## Conséquences

- Référencement natif, aucun serveur Node à héberger.
- Pas d'API interne à maintenir pour le back office : les seules API exposées sont celles des partenaires.
- Modèles et validations partagés entre front et back.
- Blazor interactif côté serveur garde une connexion SignalR par agent ; avec une seule instance, Azure SignalR n'est pas nécessaire.

## Alternatives écartées

- Angular pré-rendu servi par la même application : crédible, mais impose une API interne, des DTO en double et deux chaînes de build, pour un gain faible à cette échelle.
