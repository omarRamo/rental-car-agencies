# ADR-0037 — Vitrine : limitation de débit, Turnstile en échec fermé, mesure anonyme de l'entonnoir

- **Statut** : Acceptée
- **Date** : 2026-10-21
- **Source** : [conception phase 2](../conception/phase-2-vitrine-paiement.md) §1 (mesure) et §4, [prompt](../PROMPT.md) §9.5, §10 et §12, [spécifications](../SPEC.md) §5 (anti-abus et mesure) et §14, [ADR-0004](0004-hebergement-app-service-b1-cloudflare.md), [ADR-0010](0010-conformite-ttn-inpdp.md)

## Contexte

La vitrine est la première surface publique : chaque recherche sollicite les moteurs de disponibilité et de prix, et le lot 2C y ajoute la soumission de demandes, les liens magiques et l'envoi de pièces d'identité. Il faut la protéger des robots et des rafales sans gêner la clientèle, souvent derrière une adresse partagée (réseau mobile, wifi d'hôtel), et mesurer la conversion par étape et par canal (SPEC §5, indicateurs de la phase 3) sans donnée personnelle, sans outil tiers et, si possible, sans bandeau de consentement.

## Décision

### Limitation de débit

Trois politiques nommées (`PublicRateLimits`), comptées par adresse client et réglables par configuration (`RateLimiting:Search`, `RateLimiting:Submit`, `RateLimiting:Upload` : `PermitLimit`, `Window`, `SegmentsPerWindow`) :

| Politique | Limite par défaut | Appliquée à |
|---|---|---|
| `public-search` | 60 requêtes par minute, fenêtre glissante de 6 segments | accueil, résultats, choix d'une catégorie, `GET /api/public/availability` |
| `public-submit` | 10 requêtes par 10 minutes, fenêtre fixe | soumissions du tunnel et demandes de lien magique (lot 2C) |
| `public-upload` | 40 requêtes par 10 minutes, fenêtre glissante de 5 segments | envois de pièces (lot 2C) |

- **Pas de limiteur global** : une page s'inscrit par `[EnableRateLimiting(...)]`, un endpoint par `.RequireRateLimiting(...)` ; `/health*`, les fichiers statiques et le back office ne sont jamais limités. Pas de file d'attente : une requête au-delà de la limite est refusée tout de suite.
- **Partition** : l'adresse rendue par `ClientIpResolver` (`CF-Connecting-IP` derrière Cloudflare, sinon l'adresse de connexion), une adresse IPv6 étant ramenée à son préfixe /64 (un abonné dispose du préfixe entier) ; `unknown` si aucune adresse n'est lisible.
- **Refus** : 429 avec `Retry-After` et `no-store` ; un `ProblemDetails` localisé pour `/api`, sinon une courte page localisée (titre, explication, lien vers l'accueil). Le refus est journalisé avec le chemin, sans l'adresse.

### Turnstile

- **`ITurnstileVerifier.VerifyAsync(jeton, adresse, action attendue)`** : client HTTP typé vers `siteverify` de Cloudflare (délai de 10 s), en **échec fermé** : secret absent, jeton absent ou trop long, erreur réseau, réponse inattendue ou action différente de celle attendue donnent « non vérifié », avec les codes d'erreur de Cloudflare ou les nôtres. Le lot 2C l'appelle avant une soumission et avant l'envoi d'un lien magique ; un jeton ne sert qu'une fois et reste valable 5 minutes.
- **`<TurnstileWidget Action="…" />`**, à placer dans le formulaire : il remplit le champ `cf-turnstile-response` et ajoute à la CSP **de cette seule réponse** le script et le cadre de `https://challenges.cloudflare.com` (`ContentSecurityPolicyAdditions`, qui n'accepte que des origines https) ; sans JavaScript, un message explique que la vérification en a besoin ; sans clé configurée, une alerte remplace le widget.
- **Clés** : `Turnstile:SiteKey` et `Turnstile:SecretKey`, le secret dans le Key Vault (`Turnstile--SecretKey`). En développement et en test, à défaut de clés, les clés de test officielles de Cloudflare (toujours valides). En test, le widget est rendu hors ligne avec le jeton factice de ces clés (`Turnstile:OfflineTestWidget`, sans effet en production) et `FakeTurnstileVerifier` (`tests/Shared`) remplace le vérificateur.
- La recherche n'a pas de Turnstile : la limitation de débit suffit, et une recherche doit rester un lien partageable.

### Mesure de l'entonnoir

- **Événement** : `IFunnelEvents.RecordAsync` (module Reporting) ajoute un `FunnelEvent` à `reporting.FunnelEvents` : étape (`search`, `results`, `options`, `documents`, `payment`, `confirmation`), instant, identifiant de visite facultatif, culture, canal (`Web`, ou `WhatsApp` pour `src=whatsapp`), source (`src`) et paramètres `utm_*` nettoyés et bornés, code ACRISS, dossier. La table est exclue du journal d'audit (volume élevé, aucune commande métier) ; index sur (instant, étape) et sur la visite.
- **Au mieux, jamais bloquant** : l'enregistreur travaille dans sa propre portée DI (son `DbContext`, acteur `system:funnel`), borné à 3 secondes, et n'échoue jamais (base indisponible, délai dépassé, requête abandonnée) ; la page n'attend pas plus longtemps et un appelant peut ne pas attendre la fin.
- **Visite anonyme** : cookie de première partie `carrental.visit` (identifiant aléatoire, `HttpOnly`, `SameSite=Lax`, `Secure` en HTTPS), renouvelé à chaque étape mesurée et oublié après 24 heures sans activité ; il n'est relié ni à une personne, ni à un contact, ni à une adresse IP. Un navigateur qui envoie `Sec-GPC: 1` (Global Privacy Control) ou `DNT: 1` ne reçoit pas de cookie : ses étapes sont comptées sans identifiant. Robots, aperçus de liens (WhatsApp), outils d'audit et scripts ne sont pas comptés (agent utilisateur).
- **Sens des étapes du lot 2A** : `search` = une recherche valide arrive sur les résultats (formulaire de recherche ou lien profond) ; `results` = au moins une offre est affichée. L'accueil n'est pas mesuré : page cachable, il ne pose aucun cookie. Le lot 2C enregistre les étapes suivantes avec la catégorie et le dossier, par `FunnelTracker`.
- **Rétention** : la tâche nocturne `reporting.purge_funnel_events` (03:45, heure de Tunis) supprime par lots de 5 000 les événements plus anciens que `reporting.funnel_event_retention_days` (400 jours par défaut, de 30 à 3 650 : une saison se compare à la précédente) et inscrit le nombre supprimé au journal d'audit.
- **Transparence** : la page de confidentialité décrit la finalité, le cookie, sa durée, le signal d'opposition et la durée de conservation lue dans le paramètre.

## Conséquences

- Les limites tiennent en mémoire, par instance ([ADR-0004](0004-hebergement-app-service-b1-cloudflare.md) : une seule) : un redémarrage les remet à zéro ; plusieurs instances exigeraient un compteur partagé.
- **`CF-Connecting-IP` n'est fiable que si l'origine n'accepte que Cloudflare.** Aujourd'hui, l'App Service accepte toute adresse : un client qui l'appelle directement peut choisir son en-tête et changer de partition. Restreindre l'accès aux plages de Cloudflare (ou exiger une authentification de l'origine) est à faire dans `infra/`, hors du lot 2A, avant l'ouverture au public.
- Une adresse partagée peut atteindre une limite : les seuils sont larges, réglables sans déploiement de code, et la page de refus dit combien de temps patienter.
- Sans `Turnstile:SecretKey` en recette et en production, les soumissions protégées sont refusées (échec fermé, erreur journalisée) : le secret doit être dans le Key Vault avant l'ouverture du tunnel (lot 2C). Pendant une panne de Turnstile, les soumissions sont refusées ; le bouton WhatsApp reste disponible.
- La mesure compte des visites, pas des personnes. Les visites sans identifiant (GPC, DNT) comptent dans chaque étape sans être reliées d'une étape à l'autre, et un navigateur qui refuse les cookies apparaît comme une nouvelle visite à chaque étape : un taux de conversion par visite se calcule sur les visites dont l'identifiant revient. Les indicateurs de la phase 3 lisent `reporting.FunnelEvents`.
- La dispense de consentement du cookie de mesure (mesure strictement anonyme, durée courte, opposition respectée) reste à confirmer par l'avocat, avec les autres textes de la page de confidentialité marqués `TODO(spec)` ; s'il fallait un consentement, le cookie ne serait posé qu'après accord, sans autre changement de la mesure.

## Alternatives écartées

- **Limiteur global** : il aurait compté les sondes `/health*`, les fichiers statiques et le back office ; l'inscription explicite rend chaque limite visible là où elle s'applique.
- **Partition par cookie ou par session** : contournée en effaçant ses cookies.
- **reCAPTCHA ou hCaptcha** : traceurs tiers et coût, alors que Turnstile est gratuit, sans pistage publicitaire, et que Cloudflare est déjà devant le site.
- **Turnstile en échec ouvert** (accepter quand Cloudflare ne répond pas) : une panne ou un blocage du domaine ouvrirait la porte aux soumissions automatisées.
- **Outil d'analyse tiers** (Google Analytics ou équivalent) : cookies tiers, bandeau de consentement et transfert de données hors de Tunisie à déclarer à l'INPDP ([ADR-0010](0010-conformite-ttn-inpdp.md)).
- **Adresse IP hachée ou empreinte du navigateur comme identifiant de visite** : donnée personnelle au sens du RGPD et de la loi tunisienne, que le visiteur ne peut ni voir ni effacer.
- **Événements dans l'outbox ou dans le journal d'audit** : volume sans rapport avec les commandes métier ; une insertion directe, au mieux, suffit à une statistique.
