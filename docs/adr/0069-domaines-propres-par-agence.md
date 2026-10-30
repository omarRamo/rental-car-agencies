# ADR-0069 — Vitrine de chaque agence sur son propre domaine

- **Statut** : Acceptée
- **Date** : 2026-10-30
- **Source** : demande du 30/10/2026 (« déployer des sites vitrines différents pour chaque agence, qui aura son site client à part, la solution restant un SaaS ») ; précise l'[ADR-0067](0067-plateforme-multi-agences-cloisonnement-support-modules.md) (vitrine à l'adresse de l'agence) et l'[ADR-0015](0015-localisation-routes-par-culture.md) (routes par culture) ; lève le point ouvert « Domaines » de la plateforme multi-agences

## Contexte

Depuis l'ADR-0067, chaque agence a sa vitrine à l'adresse `https://rental.app/{agence}/{culture}/…` : une seule application, une seule base, un seul hôte. Une agence qui vend sous sa propre marque veut pourtant un site **à elle**, sur **son** nom de domaine (`www.agence-tunis.tn`), sans que l'adresse de la plateforme ni le segment `/agence-tunis/` apparaissent à ses clients, dans ses e-mails ou dans les moteurs de recherche. Le service doit rester un SaaS : même application, même base, même back office, aucune instance par agence.

L'ADR-0067 avait écarté « un sous-domaine par agence » parce que l'adresse par chemin suffisait au lancement ; la demande d'un site à part par agence rend le domaine propre nécessaire.

## Décision

### Des noms d'hôte rattachés à l'agence

- Une nouvelle entité `AgencyDomain` (données d'une agence, `IAgencyOwned`, schéma `referential`, table `AgencyDomains`) porte un **nom d'hôte** (`HostName`) : minuscules, forme ASCII (punycode pour un nom internationalisé), 253 caractères au plus, au moins deux étiquettes de lettres, chiffres et tirets, sans schéma, port ni chemin (`AgencyHostName.Normalize`). Un nom d'hôte est **unique sur toute la plateforme** (index unique, clé de route comme `Agency.Slug`) et jamais un nom de la plateforme elle-même (`IPlatformHosts`, nourri par `HostFiltering:AllowedHosts` et `HostFiltering:CustomDomains`, plus `localhost`).
- Une agence peut tenir plusieurs noms d'hôte ; dès qu'elle en tient un, exactement un est **principal** (`IsPrimary`) : c'est lui que portent les liens de la vitrine et des e-mails ; les autres **redirigent** (301) vers lui, même chemin et même requête. Le premier nom ajouté devient principal ; retirer le principal passe le rôle au plus ancien nom restant.
- Le support gère ces noms depuis la fiche de l'agence (`/admin/platform/agencies/{id}`, section « Domaines du site ») : `AgencyPlatformService.AddDomainAsync`, `RemoveDomainAsync`, `SetPrimaryDomainAsync`, audités (`referential.agency_domain_added`, `_removed`, `agency_primary_domain_changed`). Le gérant ne les voit pas : le DNS et le certificat sont l'affaire de l'exploitant.
- L'annuaire des agences (`IAgencyDirectory`, en mémoire, une copie pour toutes les agences) connaît les noms d'hôte (`FindByHostAsync`, `AgencySummary.PrimaryHost`, `Hosts`) et jette sa copie à tout changement d'une agence **ou d'un de ses domaines**. Aucune requête SQL par requête HTTP.

### La vitrine est servie sur l'hôte de l'agence par réécriture

- Le middleware `AgencyHostMiddleware`, placé juste après le filtrage des hôtes et **avant** la localisation et le routage, reconnaît l'hôte d'une agence et **réécrit le chemin** : `https://www.agence-tunis.tn/fr/reserver` devient en interne `/{agence-tunis}/fr/reserver`. Toutes les pages, formulaires et API publiques (`/api/public/…`, `/api/v1/…`) fonctionnent sans changement : la culture est lue au deuxième segment du chemin réécrit, l'agence de la vitrine est résolue par `PublicSiteAgencyMiddleware` comme sur l'hôte de la plateforme, le visiteur travaille pour cette agence seulement.
- Ne sont **jamais réécrites** les adresses de la plateforme elle-même : les mots réservés (`/admin`, `/account`, `/health`, `/webhooks`, `/payments`, `/media`, `/culture`, `/status`, `/dev`…), les API de la plateforme (`/api/channels`, `/api/openapi`), les fichiers statiques (`/_framework`, `/_content`, `/css`, `/js`, tout premier segment avec un point : `favicon.svg`, `robots.txt`, `sitemap.xml`). Sur l'hôte d'une agence, `/admin` reste le back office de la plateforme.
- Sur l'hôte d'une agence, une adresse qui commence par l'adresse d'une vitrine (`/{autre-agence}/fr`) devient `/{agence}/{autre-agence}/fr`, qui n'est aucune route : 404. Une agence suspendue ou dont le module vitrine est fermé répond 404 sur ses hôtes comme sur l'hôte de la plateforme (page d'erreur rendue).
- Le filtrage des hôtes (`HostFilteringMiddleware`) accepte les noms d'hôte des agences sans qu'il faille les lister dans `HostFiltering:CustomDomains` : l'annuaire les connaît.

### Les liens ne portent pas le segment de l'agence sur l'hôte de l'agence

- Toute fonction qui écrit une adresse publique prend le **premier segment des liens** : l'adresse de la vitrine (`agence-tunis`) sur l'hôte de la plateforme, **rien** (`""`) sur l'hôte de l'agence (`PublicRoutes.Root`, `PublicBookingPaths.Root`). `PublicSiteAgency.SlugOf(context)` et `PublicPageBase.Slug` donnent ce segment ; les pages, les formulaires, les redirections des points d'accès, les cookies (`Path=/` au lieu de `/{agence}`), le lien « contenu » de la page et le sélecteur de langue (`PublicSiteAgency.ForLinks`) suivent.
- Les adresses absolues (canonique, `hreflang`, Open Graph, JSON-LD, plan du site) prennent pour base l'hôte de l'agence (`PublicUrls.BaseUri`). L'adresse **canonique** d'une vitrine qui a un hôte principal pointe vers cet hôte **même lue sur l'hôte de la plateforme** ; le plan du site de la plateforme liste chaque vitrine à son adresse canonique, et le plan du site lu sur l'hôte d'une agence ne liste que cette vitrine ; `robots.txt` y exclut `/fr/reserver` plutôt que `/*/fr/reserver`.
- La couche application écrit les chemins des e-mails avec `IAgencyDirectory.LinkSlugAsync` (vide quand l'agence a un hôte principal) ; le compositeur d'e-mails (`CustomerEmailComposer.SiteOf`) prend pour base `https://{hôte principal}/` — ou l'adresse que le client a utilisée quand c'est un des hôtes de l'agence — au lieu de `PublicSite:BaseUrl`. Les liens du bot WhatsApp font de même (`AgentAgencyContext.SiteUri`).
- **Paiement en ligne** : une seule boutique Clic to Pay et une seule adresse de retour pour la plateforme (ADR-0067) ; le chemin de retour enregistré avec le paiement reste celui de l'hôte de la plateforme (`/{agence}/fr/…`, chemin local exigé par le domaine `Payment`). Au retour de la banque, si l'agence du chemin a un hôte principal, le navigateur est renvoyé **sur cet hôte** sans le segment (`ClicToPayEndpoints.OwnHostAddressAsync`) : il y retrouve ses cookies du tunnel et de « Ma réservation ».

### Ce qui ne change pas

- L'hôte de la plateforme continue de servir `/{agence}/fr` pour toutes les agences, domaine propre ou non : aucune redirection, seule l'adresse canonique change.
- Le back office, les comptes, les webhooks, l'API des canaux, les retours de paiement et les sondes restent à la racine de l'hôte de la plateforme (et répondent aussi, inchangés, sur l'hôte d'une agence).
- Le cloisonnement (ADR-0067) ne dépend pas de l'hôte : l'acteur de la requête est le visiteur de l'agence résolue, par l'adresse réécrite.

## Conséquences

- Exploitation : pour chaque domaine d'agence, l'exploitant crée le CNAME vers la plateforme (via Cloudflare ou non), lie le domaine à l'App Service avec son certificat (certificat géré ou certificat d'origine Cloudflare), déclare le domaine dans le widget Turnstile (le serveur vérifie le nom d'hôte du défi, ADR-0066), puis le support l'ajoute à la fiche de l'agence (`infra/README.md` §8 bis, `docs/runbook.md` « Agences de la plateforme »).
- Une nouvelle adresse publique écrite dans le code prend le premier segment des liens par `PublicSiteAgency.SlugOf(context)` / `PublicPageBase.Slug` et jamais `AgencySummary.Slug` directement ; un nouveau mot de la plateforme à la racine va dans `AgencySlug.Reserved` (test `EndpointInventoryTests`) et n'est donc pas réécrit sur l'hôte d'une agence ; une nouvelle API de la plateforme sous `/api` s'ajoute à `AgencyHostMiddleware.PlatformApiSegments`.
- Les tests : `AgencyDomainTests` (domaine), `AgencyDomainsTests` (intégration : gestion par le support, pages et formulaires sans segment, redirection des hôtes secondaires, adresses de la plateforme intactes, plan du site, retour de paiement), `PublicRoutesTests` et `PublicBookingUnitTests` (adresses et e-mails sans segment).
- Points restés ouverts : certificat et règle Cloudflare par domaine, déclaration des domaines dans le widget Turnstile, redirection éventuelle de `/{agence}/…` sur l'hôte de la plateforme vers le domaine propre (SEO : l'adresse canonique suffit pour l'instant), domaine d'expédition des e-mails par agence (point « E-mails » de l'ADR-0067).

## Alternatives écartées

- **Un sous-domaine de la plateforme par agence** (`agence-tunis.rental.app`) : le même mécanisme le servirait (c'est un nom d'hôte comme un autre), mais il ne répond pas au besoin d'une marque propre ; rien ne l'interdit si une agence le souhaite.
- **Une instance (App Service, base) par agence** : contraire au SaaS, multiplie migrations, sauvegardes et coûts pour des agences d'une trentaine de véhicules.
- **Réécrire les liens dans le HTML produit** (filtre de sortie qui retirerait `/{agence}`) : fragile (JSON, e-mails, en-têtes `Location`), coûteux ; les liens sont écrits justes à la source.
- **Des routes sans segment d'agence en plus des routes actuelles** (`/{culture}/…` et `/{agence}/{culture}/…`) : ambiguës entre elles (`/fr/en`) et doublant chaque page ; la réécriture garde une seule famille de routes.
