# ADR-0036 — Vitrine : rendu statique sans script Blazor, pages publiques cachables, référencement et budget Lighthouse

- **Statut** : Acceptée
- **Date** : 2026-10-21
- **Source** : [conception phase 2](../conception/phase-2-vitrine-paiement.md) §1 et §4, [prompt](../PROMPT.md) §10, [spécifications](../SPEC.md) §5, [ADR-0002](0002-front-blazor-mudblazor.md), [ADR-0015](0015-localisation-routes-par-culture.md), [ADR-0020](0020-navigation-classique-et-focus.md)

## Contexte

La vitrine du lot 2A (accueil, résultats, pages fixes) doit afficher son contenu principal en moins de 2,5 s (LCP) sur un téléphone en 4G simulée, être indexée dans chaque langue, rester utilisable sans JavaScript et accessible (WCAG 2.2 AA). Trois constats ont orienté les choix :

- l'application configure le rendu interactif serveur pour `/admin` ; Blazor insère alors dans **chaque** page statique un état persisté qui contient un jeton antiforgery, et la génération de ce jeton pose le cookie antiforgery et l'en-tête `Cache-Control: no-cache, no-store` : aucune page publique n'était cachable, même sans formulaire ;
- `blazor.web.js` (plusieurs dizaines de kilo-octets compressés) n'apporte rien à des pages rendues en une passe dont les formulaires sont de simples formulaires HTML, et occupe le processeur d'un téléphone d'entrée de gamme ;
- une page de résultats dépend des paramètres de l'URL (combinaisons sans fin) et de la disponibilité du moment : elle ne doit être ni indexée ni mise en cache.

## Décision

### Rendu

- **Rendu statique en une passe, sans `blazor.web.js`** : `App.razor` ne charge le script que pour le back office et les pages situées hors de `Components/Public`, ou pour une page publique marquée `[UsesBlazorScript]` (`PublicPageScripts`). Pas de rendu en flux : la page part complète. Les formulaires de la vitrine sont des formulaires HTML : recherche en GET vers les résultats (`/fr/reserver`, `/en/book`), filtres et tri en GET, choix d'une catégorie en POST avec jeton antiforgery vers `/{culture}/reserver/selection`.
- Cette décision **précise l'[ADR-0020](0020-navigation-classique-et-focus.md)**, qui gardait le script sur les pages statiques pour le rendu en flux et les formulaires : sur la vitrine, seules les pages qui en ont besoin le chargent, par exemple une étape du tunnel (lot 2C) qui active la soumission améliorée (`Enhance`, PROMPT §10), en vérifiant le focus comme le demande l'ADR-0020.
- **Un seul module JavaScript, facultatif** (`js/search-form.js`, chargé en `type="module"`, donc différé) : il vide le lieu de retour quand la case « retour ailleurs » est décochée et garde le retour après le départ. Sans lui, tout fonctionne : le lieu de retour s'affiche par CSS (`:has()`, avec un repli `@supports`) et le serveur valide la recherche (`SearchValidator`) ; en cas d'erreur, un résumé reçoit le focus, chaque message est relié à son champ (`aria-describedby`) et les valeurs saisies sont conservées.
- **Liens vers une ancre de la page** : avec `<base href="/">`, un lien `#main` pointe vers `/#main` et le script Blazor n'est plus là pour le corriger ; `PublicRoutes.InPage` produit « chemin courant + `#ancre` » (lien d'évitement, liens du résumé d'erreurs).
- **Performance** : polices système, une seule feuille de style compacte (propriétés logiques, prête pour l'arabe), aucune bibliothèque ni police externe ; images dimensionnées (`width`, `height`), différées sous la ligne de flottaison (`loading="lazy"`), la première en `fetchpriority="high"` ; une silhouette SVG en ligne (`<use>`) remplace une photo absente. L'annuaire de l'agence et les prix « à partir de » de l'accueil (week-end suivant, calculés par le moteur de prix) sont gardés en mémoire 5 et 10 minutes.

### Cache HTTP

- Les pages sans formulaire POST (accueil, agence, FAQ, conditions, confidentialité) portent `[CacheablePublicPage]`. Pour le GET de ces pages seulement, un décorateur d'`IAntiforgery` (`CacheablePageAntiforgery`) répond qu'il n'y a pas de jeton : ni cookie, ni `no-store`. La **validation** n'est jamais modifiée : un POST sans jeton valide reste refusé partout.
- La mise en page publique appelle `PublicPageCaching.Apply` : une réponse 200 à un visiteur anonyme, sans cookie posé, reçoit `Cache-Control: public, max-age=300` (5 minutes, durée portée par l'attribut) ; une autre réponse 200 d'une page marquée (membre de l'équipe connecté, cookie posé) sans politique de cache reçoit `no-cache, no-store`, pour qu'un cache partagé ne la garde jamais.
- Les résultats (bouton « Choisir » en POST), le tunnel et Ma réservation gardent leur jeton, le cookie antiforgery et `no-store` ; l'API publique de disponibilité répond `no-store`.
- La culture fait partie du chemin ([ADR-0015](0015-localisation-routes-par-culture.md)) : l'URL suffit comme clé de cache, sans `Vary: Accept-Language` ; seule la racine `/`, qui redirige selon la langue, varie.
- `sitemap.xml` et `robots.txt` : `public, max-age=3600`.

### Référencement

- **En-tête de chaque page** (`PublicHead`) : titre « page | marque », description, `canonical` absolue, alternatives `hreflang` des langues actives et `x-default` (la racine pour l'accueil, la version française ailleurs), balises Open Graph. Les adresses absolues partent de `PublicSite:BaseUrl`, à défaut de l'hôte de la requête.
- **Adresse canonique** : une page demandée avec le segment d'une autre langue (`/en/reserver`) ou une culture en majuscules redirige vers son adresse canonique, requête conservée.
- **Résultats hors index** : `noindex`, et exclus par `robots.txt` comme le tunnel et Ma réservation, dans chaque culture préparée (`ar` compris).
- **`sitemap.xml`** : pages indexables × langues actives, chacune avec ses alternatives `xhtml:link`. **`robots.txt`** : back office, comptes, API, `/culture/`, `/status/`, `/health`, photos de la flotte, tunnel et Ma réservation exclus, puis l'adresse du sitemap ; hors production, ou avec `PublicSite:AllowIndexing=false`, `Disallow: /` : la recette n'est jamais indexée.
- **JSON-LD schema.org `AutoRental`** sur l'accueil et la page agence : nom, adresse, coordonnées géographiques, plan, téléphone, e-mail, horaires (`OpeningHoursSpecification`), fourchette de prix, devise. Les coordonnées sont deux colonnes nouvelles de l'agence (`Latitude`, `Longitude`, six décimales, toutes deux ou aucune, migration `Phase2PublicSite`).

### Budget contrôlé en CI

- **Job `lighthouse`** (`.github/workflows/ci.yml`), après `build` et en parallèle des E2E : SQL Server et Azurite en conteneurs, application publiée puis démarrée dans l'environnement `Ci` avec les données de démonstration, en HTTPS derrière un certificat jetable (hors développement, les cookies `Secure` et l'antiforgery exigent HTTPS), puis Lighthouse CI 0.15.1 (`.github/lighthouse/lighthouserc.json`) : profil mobile, ralentissement simulé (4G lente et processeur ralenti, réglages par défaut de Lighthouse), trois passages sur l'accueil et sur une page de résultats.
- **Assertions** (`.github/lighthouse/budget.json`, médiane des passages) : bloquantes pour LCP ≤ 2 500 ms, CLS ≤ 0,1, TBT ≤ 200 ms, score d'accessibilité ≥ 0,95 et JavaScript ≤ 20 Ko ; simples avertissements pour FCP ≤ 1 800 ms et poids total ≤ 300 Ko. Lighthouse 12 ne lit plus les budgets au format `budget.json` : le fichier porte des assertions de Lighthouse CI. Les rapports et le journal de l'application sont publiés en artefact.
- **Mesures locales** avec la même configuration : LCP d'environ 1 s, CLS 0, TBT 0, accessibilité 1,0 sur l'accueil et sur les résultats.

### Moteur de disponibilité en attendant le lot 1E

- `TODO(1E)` : le moteur ([ADR-0025](0025-moteur-de-disponibilite-cache-et-ecriture-sous-verrou.md)) est enregistré par le module Bookings avec sa lecture des dossiers. Tant que ce module n'existe pas, la vitrine enregistre le même moteur sur une occupation vide (`AvailabilityEngineFallback`), qui ne fait rien dès qu'un `IOccupancyReader` est enregistré ; à supprimer après la fusion du lot 1E.

## Conséquences

- Les pages publiques ne chargent aucun script bloquant ni aucune ressource externe ; seul le widget Turnstile des formulaires du tunnel chargera le script de Cloudflare ([ADR-0037](0037-vitrine-anti-abus-et-mesure-entonnoir.md)). Un test d'intégration vérifie que les pages cachables ne contiennent ni jeton ni `blazor.web.js` et ne posent aucun cookie. L'état persisté de Blazor reste présent sous forme de commentaire HTML (environ 1 Ko).
- Une page qui reçoit un formulaire POST doit perdre `[CacheablePublicPage]` : sinon son formulaire n'a pas de jeton et l'envoi est refusé, ce que le premier test du formulaire montre.
- Cloudflare Free ne garde pas le HTML par défaut : pour servir ces pages depuis sa périphérie, il faudra une règle de cache limitée aux pages publiques et qui respecte l'en-tête de l'origine (`infra/`, hors du lot 2A). D'ici là, seul le navigateur garde une page 5 minutes ; une modification de l'agence (horaires, téléphone) apparaît au plus tard après 10 minutes (cache HTTP et cache mémoire).
- Le HTML dynamique n'est pas compressé par l'application : Cloudflare le compresse en production ; Lighthouse le signale en CI, où le site est servi directement.
- Pas encore d'image Open Graph ni de logo : à ajouter quand l'agence fournira ses visuels.
- Les textes juridiques (conditions générales, confidentialité) sont des brouillons marqués `TODO(spec)`, à faire relire par l'avocat avant l'ouverture au public.

## Alternatives écartées

- **Cache de sortie (`OutputCache`)** : il aurait gardé, et servi à tous les visiteurs, une page contenant le jeton antiforgery d'un seul ; le problème du jeton restait entier, et le cache du navigateur suffit pour une seule instance.
- **Retirer après coup le cookie et l'en-tête `no-store`** : la page aurait contenu un jeton sans cookie correspondant, et le jeton serait toujours généré à chaque requête.
- **Séparer la vitrine du back office ou renoncer au rendu interactif** : deux applications à déployer et à faire évoluer ([ADR-0001](0001-monolithe-modulaire-dotnet.md), [ADR-0002](0002-front-blazor-mudblazor.md)) pour un problème réglé par un décorateur.
- **Charger `blazor.web.js` sur toutes les pages publiques**, comme le prévoyait l'ADR-0020 : du JavaScript à télécharger et exécuter sans fonction utilisée par ces pages, au détriment du LCP et du TBT sur mobile.
- **Résultats indexables** : contenu dupliqué à l'infini et prix périmés dans les moteurs de recherche ; les pages fixes et l'accueil portent le référencement.
- **Mesure Lighthouse en HTTP** : refusée par l'antiforgery hors développement ; le certificat jetable reproduit les conditions de production.
