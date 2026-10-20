# ADR-0020 — Navigation classique pour les pages statiques, focus géré dans le back office

- **Statut** : Acceptée
- **Date** : 2026-10-20
- **Source** : [ADR-0002](0002-front-blazor-mudblazor.md), [prompt](../PROMPT.md) section 10 (accessibilité)

## Contexte

Par défaut, Blazor intercepte les liens des pages statiques (« navigation améliorée ») et place le focus sur le titre `h1` au chargement (`FocusOnNavigate`). Les tests E2E ont montré deux défauts d'accessibilité :

- au chargement, le focus part sur le `h1` : la première tabulation saute le lien d'évitement et l'en-tête (sélecteur de langue) ;
- la navigation améliorée, comme le routeur interactif de `/admin`, se contente de faire défiler la page vers une ancre de la même page : le lien d'évitement ne déplace pas le focus.

## Décision

- **Pages statiques (vitrine, compte, erreurs) en navigation classique** : `data-enhance-nav="false"` sur `<body>` (`App.razor`). Le navigateur gère nativement les liens d'évitement, l'annonce de la nouvelle page par les lecteurs d'écran et le rechargement complet au changement de langue (`lang` et `dir` appliqués au document entier). `blazor.web.js` reste chargé pour le rendu en flux et les formulaires.
- **Focus après navigation interne seulement** : `FocusOnPageChange` (`Components/Shared`) ne rend le `FocusOnNavigate` du framework qu'après un changement de page dans le routeur interactif. Au chargement, aucun focus n'est déplacé ; après une navigation dans `/admin`, le focus va sur le `h1` de la nouvelle page.
- **Lien d'évitement du back office** : une fois interactif, il place lui-même le focus sur `<main>` (`ElementReference.FocusAsync`) au lieu de laisser le routeur faire défiler la page.
- Chaque page a un seul `h1`, et chaque mise en page commence par son lien d'évitement.

## Conséquences

- Les pages statiques rechargent entièrement à chaque lien (feuilles de style en cache : coût négligeable) ; un formulaire peut toujours activer la soumission améliorée au cas par cas (`Enhance`), en vérifiant le focus.
- Tests E2E : lien d'évitement en première tabulation et focus sur le contenu principal (vitrine et back office), focus sur le titre après une navigation interne.

## Alternatives écartées

- **Garder le comportement par défaut** : la première tabulation contourne l'en-tête et le lien d'évitement est inopérant.
- **Script de gestion du focus après navigation améliorée** : code JavaScript supplémentaire pour un gain limité sur une vitrine de quelques pages.
