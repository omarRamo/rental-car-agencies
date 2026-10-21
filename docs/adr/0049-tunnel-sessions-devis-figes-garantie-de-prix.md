# ADR-0049 — Tunnel de réservation : sessions à jeton, devis figés et garantie de prix

- **Statut** : Acceptée
- **Date** : 2026-10-21
- **Source** : [spécifications](../SPEC.md) §9 (tunnel web, maintien 24 h, pré-autorisation) et §13 (API publique) ; [prompt](../PROMPT.md) §6 (invariants : prix du seul moteur, écriture de la disponibilité sous verrou) ; [conception phase 2](../conception/phase-2-vitrine-paiement.md) §1 ; [ADR-0031](0031-devis-figes-versions-et-prolongation.md) (devis figés), [ADR-0036](0036-vitrine-rendu-statique-cache-et-referencement.md) (pages statiques, sans JavaScript), [ADR-0037](0037-vitrine-anti-abus-et-mesure-entonnoir.md) (limites, Turnstile, mesure), [ADR-0039](0039-paiement-en-ligne-confirmation-idempotence-rattrapage.md) (paiement en ligne)

## Contexte

La vitrine (2A) affiche des résultats et un bouton « Choisir ». Le tunnel (2C) doit mener du choix d'une catégorie à une demande `PendingValidation` garantie, en trois étapes (options, conducteurs, paiement) puis une confirmation, sur téléphone (390 px, 80 % de clients de la diaspora), sans JavaScript sauf pour les photos. Le prix n'est calculé que par le moteur de tarification ; la capacité n'est prise que sous le verrou de disponibilité, à la soumission. Les liens profonds du robot WhatsApp et des agents portent un devis figé (`devis=CODE`). Un visiteur peut quitter le tunnel, revenir, changer de langue, ouvrir deux onglets ; une adresse peut être partagée ou rester dans un historique.

## Décision

### Session du tunnel

- Le choix d'une catégorie (`POST /{culture}/reserver/selection`, antiforgery, limite `public-submit`) **revalide la recherche** (jamais les valeurs postées telles quelles), cherche la catégorie parmi les catégories actives de l'agence et vérifie sa disponibilité dans le cache (sans rien prendre) ; il ouvre une **session de tunnel** (`FunnelSession`) et mène à l'étape 1 de son jeton.
- **Jeton** : 256 bits aléatoires en base64url (43 caractères), porté par l'adresse des étapes (`/fr/reserver/{jeton}/options`, `/en/book/{jeton}/drivers`…). **Seul son SHA-256 est stocké** (index unique) : une copie de la base n'ouvre rien. La forme du jeton est vérifiée avant tout accès à la base. Jeton inconnu → la recherche avec un message (`info=lien`) ; session expirée → la même recherche (`info=expire`) ; demande déjà envoyée → sa confirmation.
- La session garde la culture, la recherche canonique, la catégorie, le devis figé courant, l'attribution (`src`, `utm_*`), les extras choisis, les conducteurs saisis (document JSON opaque, données personnelles masquées dans le journal d'audit), le dernier formulaire en erreur, les consentements, les étapes déjà mesurées et un avis à afficher une fois. Elle **expire avec son devis figé** ; la tâche `bookings.purge_public_access` (toutes les heures) la supprime 24 h après son expiration ou sa soumission, avec les références de ses photos (les fichiers en transit sont purgés par le module Clients, ADR-0050).
- **Aucun dossier `Draft` n'est créé avant la soumission** : le tunnel ne laisse ni dossiers abandonnés, ni capacité réservée, ni nettoyage de dossiers. Le dossier (identifiant choisi d'avance) est créé et soumis dans une seule unité de travail à l'étape 3 (`SubmitRequestAsync` depuis le devis figé, sous le verrou de disponibilité).

### Devis figé et garantie de prix

- Au choix : le devis du lien profond est **réutilisé** s'il est valide, non expiré et **exactement** celui de la recherche (agence, catégorie, canal, lieux et instants de départ et de retour, sans modèle garanti) ; sinon un **nouveau devis figé 24 h** est tarifé par le moteur.
- **Règle de garantie** : le prix affiché est toujours celui du devis figé courant, garanti jusqu'à son expiration (indiquée sur chaque étape). Une modification d'un intrant du prix — extras de l'étape 1, second conducteur (extra « conducteur additionnel » quand la grille le propose), âges des conducteurs (supplément jeune conducteur) — **re-tarife avec la grille en vigueur et fige un nouveau devis** (nouvelle garantie de 24 h) ; l'étape suivante le dit si le prix de la location a changé ou si un supplément jeune conducteur s'applique. Sans modification, le devis reste. La soumission refuse un devis expiré (`booking.quote.expired`) : la recherche est à refaire.
- Chaque étape montre le récapitulatif calculé par le serveur à partir du devis figé : lignes TTC, total, prix par jour, équivalent indicatif en euros, caution (réduite avec le rachat de franchise), contribution journalière incluse.

### Étapes

- Formulaires HTML simples, **POST puis 303** vers une page (post/redirect/get), antiforgery vérifié avant la lecture du formulaire, erreurs résumées en tête et répétées près des champs. Limites : `public-search` pour les options et les conducteurs, `public-submit` pour le choix et la soumission, `public-upload` pour les photos (ADR-0050).
- **Étape 3** : récapitulatif, acceptation des conditions générales (`/fr/conditions`), mode de garantie, Turnstile `booking_submit`. La soumission crée le profil client du dossier (langue, accord WhatsApp, adresse du site, sans donnée personnelle), enregistre les conducteurs (ADR-0050), soumet la demande (canal Web, ou WhatsApp pour `src=whatsapp`, avec son attribution) : `PendingValidation`, maintien de 24 h. Un échec défait ce qui avait été enregistré pour elle.
- **Garantie** : pré-autorisation de `total TTC × bookings.online_guarantee_ratio` (1 par défaut, réglage par agence ; `TODO(spec)` : politique de garantie de l'agence — caution ou totalité, selon la saison ou le canal), **démarrée après la soumission, hors de toute unité de travail**. La CSP `form-action 'self'` interdit de rediriger un formulaire vers la banque : le navigateur va vers une **page GET du site** (`/fr/reserver/redirection/{paiement}`) qui le renvoie aussitôt (`meta refresh` à 0 seconde, avec un lien) vers la page de paiement — page réservée au navigateur de la demande ou de la session « Ma réservation » du dossier. Le retour et l'échec mènent à la confirmation ; le statut est lu serveur à serveur (ADR-0039). **Passerelle indisponible ou mode `PaymentLinkAfterValidation`** : soumission sans pré-autorisation, l'écran le dit, un lien de paiement est envoyé après la validation (ADR-0051).
- **Confirmation** (`/fr/reserver/confirmation/{numéro}`) : « Demande reçue, réponse sous 2 h ouvrées » et l'état de la garantie (bloquée, en vérification, échouée ou expirée avec une nouvelle tentative, lien à venir). Les détails ne s'affichent que pour le navigateur qui a envoyé la demande (cookie Data Protection `carrental.request`, 24 h) ; tout autre visiteur voit une page neutre qui ne dit rien du numéro.
- **Mesure** : les étapes options, documents, paiement et confirmation sont mesurées une seule fois par session (ADR-0037).

### Adresses, cache, télémétrie

- Adresses en français et en anglais ; le sélecteur de langue traduit aussi les étapes ; une adresse non canonique redirige. Pages `noindex, nofollow`, sans adresse canonique ni Open Graph, jamais en cache (`no-store` par le jeton antiforgery), absentes du plan du site et interdites par `robots.txt`.
- Les segments de 43 caractères base64url des chemins sont remplacés par `{token}` dans la télémétrie (`url.path`).

### API publique des devis

- `POST /api/public/quotes` tarife et fige un devis 24 h (mêmes règles que « Choisir ») et rend le lien profond qui le réserve ; `GET /api/public/quotes/{code}` relit un devis figé. Clé `X-Api-Key` parmi `PublicApi:QuoteApiKeys` (secrets, comparaison en temps constant ; aucune clé configurée : tout est refusé), limite `public-search`, jamais en cache.

## Conséquences

- Un tunnel abandonné ne coûte qu'une ligne de session et un devis figé ; la capacité n'est jamais prise par un visiteur qui ne soumet pas, et deux visiteurs peuvent viser le dernier véhicule : le second l'apprend à la soumission (`availability.not_available`).
- Le prix vu est le prix demandé : il ne change qu'à une modification par le visiteur, toujours annoncée.
- Le jeton dans l'adresse rend une étape partageable : la session ne contient rien que son détenteur n'ait saisi, et elle expire en 24 h au plus après la dernière modification du prix.

## Alternatives écartées

- **Dossier `Draft` créé au choix de la catégorie** : des milliers de brouillons abandonnés à purger, un numéro consommé par visite, une écriture sous verrou pour un simple clic.
- **Jeton dans un cookie seulement** : deux onglets se mélangent, une étape ne peut pas être rouverte depuis un autre appareil, et l'adresse ne dit plus quelle session elle montre.
- **Prix recalculé à chaque affichage sans devis figé** : le prix pourrait changer entre deux pages sans action du visiteur, contraire à la garantie annoncée.
- **Redirection 302 vers la banque après le POST** : bloquée par `form-action 'self'` ; relâcher la CSP pour un domaine tiers affaiblirait toutes les pages.
