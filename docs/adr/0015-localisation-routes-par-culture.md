# ADR-0015 — Localisation par ressources .resx, routes publiques préfixées par la culture

- **Statut** : Acceptée
- **Date** : 2026-10-20
- **Source** : [spécifications](../SPEC.md) (vitrine en français et en anglais, arabe prévu), [prompt](../PROMPT.md) sections 10 et Annexe A

## Contexte

La vitrine doit être indexée dans chaque langue avec des adresses stables ; le back office suit la langue choisie par l'utilisateur ; l'arabe (écriture de droite à gauche) arrivera plus tard sans refonte.

## Décision

- **Textes d'interface en .resx** : langue neutre = français (`NeutralLanguage=fr`), traduction dans `*.en.resx`. Une ressource par zone dans `CarRental.Web/Resources` (`SharedResources`, `Public/PublicResources`, `Account/AccountResources`, `Admin/AdminResources`, `Admin/Identity/IdentityAdminResources`), injectée par `IStringLocalizer<T>`. Un test d'architecture exige les mêmes clés, non vides, en français et en anglais. Les erreurs métier s'affichent par la clé `Error.<code>` ([ADR-0011](0011-services-applicatifs-et-result.md)).
- **Pages publiques préfixées** : `@page "/{culture:length(2)}/..."` (`/fr`, `/en`). La culture vient du chemin (`PathRequestCultureProvider`) ; une culture inconnue ou désactivée donne une 404 (`PublicCultureMiddleware`). La contrainte se limite à `length(2)` parce que le routeur interactif n'accepte pas de contrainte personnalisée. Chaque page publie ses alternatives `hreflang` (et `x-default`) ; le sélecteur de langue garde le chemin et la requête.
- **Racine** : `/` redirige (302, `Vary: Accept-Language, Cookie`) vers la meilleure culture selon le cookie de culture puis `Accept-Language`, sinon `fr`.
- **Back office et pages de compte** sans préfixe : culture du cookie `.AspNetCore.Culture`, posé par `/culture/set?culture=…&returnUrl=…` (adresse de retour locale uniquement), sinon `Accept-Language`, sinon `fr`.
- **Arabe préparé, désactivé** : culture déclarée dans `CultureCatalog`, attributs `lang` et `dir` calculés depuis la culture, CSS en propriétés logiques (`margin-inline`, `inset-inline-start`…) ; activation par `Localization:EnableArabic` quand les traductions et la relecture de droite à gauche seront faites.
- **Formats** : montants en millimes (`681.400 TND`), dates et heures affichées dans le fuseau `Africa/Tunis`.

## Conséquences

- Toute nouvelle page publique commence par `/{culture:length(2)}` et se trouve sous `Components/Public` (vérifié par test d'architecture) ; ses liens internes reprennent la culture courante.
- Un changement de langue recharge la page ([ADR-0020](0020-navigation-classique-et-focus.md)), ce qui applique `lang` et `dir` au document entier.

## Alternatives écartées

- **Paramètre `?culture=`** ou **cookie seul** pour la vitrine : adresses non indexables par langue.
- **Sous-domaines par langue** : DNS et certificats en plus, sans gain.
- **Traductions en base** : inutile tant que les textes ne sont pas gérés par l'agence.
