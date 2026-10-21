# ADR-0052 — Canaux de vente : modèle, quotas et arrêts de vente dans le moteur, format canonique et adaptateurs sans format inventé

- **Statut** : Acceptée
- **Date** : 2026-10-21
- **Source** : [spécifications](../SPEC.md) §10 (« Booking.com et gestionnaire de canaux ») ; [prompt](../PROMPT.md) §9.3 et §15 ; [conception phase 5](../conception/phase-5-canaux.md) §1, §2 et §4 ; [ADR-0006](0006-anti-surreservation-verrou-par-categorie.md) (verrou par catégorie), [ADR-0017](0017-execution-cas-usage-portee-transactions.md) (unité de travail), [ADR-0021](0021-flotte-fenetres-de-disponibilite-et-echeances.md) (capacité de la flotte), [ADR-0025](0025-moteur-de-disponibilite-cache-et-ecriture-sous-verrou.md) (moteur de disponibilité, port `IChannelAvailabilityPolicy`), [ADR-0030](0030-dossier-de-location-agregat-et-chronologie.md) (dossier), [ADR-0044](0044-caches-parametres-et-plans-publies.md) (caches invalidés après validation) ; suite : [ADR-0053](0053-canaux-securite-entrante-idempotence-et-synchronisation.md)

## Contexte

L'agence (30 véhicules) veut vendre sur Booking.com, puis sur d'autres comparateurs. Booking.com n'offre aucune API publique pour publier une flotte de location : le format XML/API arrive avec le cahier des charges, **remis seulement après l'acceptation de la candidature fournisseur**. D'ici là, l'agence vend par l'extranet de Booking.com : elle y recopie ses arrêts de vente et ses quotas, et saisit dans le back-office les réservations qu'elle y reçoit.

Trois contraintes cadrent le module : aucune surréservation (toute vente, quel que soit le canal, passe par le même verrou et le même moteur de disponibilité) ; les quotas et arrêts de vente d'un canal ne doivent jamais bloquer les autres canaux (site, téléphone, comptoir) ; les modules ne se parlent que par leurs contrats (tests d'architecture), sans type du domaine d'un autre module.

## Décision

### Canal et correspondances

- **Canal** (`Channel`, agrégat temporel avec `rowversion`) : un par agence et par canal de vente externe — aujourd'hui `SalesChannel.BookingCom` seulement (un autre comparateur ajoutera sa valeur) ; **code** unique sur la plateforme (2 à 32 caractères : minuscules, chiffres, tirets intérieurs), qui est aussi le segment de l'adresse d'entrée `/api/channels/{code}/…` ; nom ; **adaptateur** (`Manual`, `Fake`, `BookingCom`) ; **état** `Inactive` (ne vend pas), `Manual` (vend par l'extranet) ou `Connected` (l'adaptateur échange avec le canal ; exige un adaptateur réseau : `channels.state_requires_adapter`) ; mode de paiement par défaut (prépayé au canal ou payé sur place) ; taux de commission (0 à 100 %, quatre décimales) ; sécurité entrante (ADR-0053). Un canal naît inactif.
- **Correspondances** (`ChannelMapping`, enfants du canal) : catégorie ↔ code du canal, avec l'ACRISS sous lequel elle est vendue si le canal en attend un autre ; lieu ↔ code du canal (aéroports TUN, NBE, MIR, DJE…) ; option, assurance ou frais ↔ code OpenTravel Alliance **saisi par l'agence** (aucune liste n'est inventée). Uniques par canal, type et objet interne, et par canal, type et code du canal. **Une catégorie sans correspondance n'est pas vendue sur le canal** ; un code inconnu dans un message est refusé clairement (`channels.mapping.category_unmapped`, `location_unmapped`, `extra_unmapped`).
- Données de démonstration : canal `booking-com` de l'agence de démonstration, manuel, payé sur place, 15 %, chaque catégorie active sous son code ACRISS et chaque lieu sous son code.

### Quotas et arrêts de vente appliqués par le moteur

- **Restriction** (`ChannelQuota`, temporelle, suppression logique) : **quota** (`Reserve` : N véhicules de la catégorie gardés pour les autres canaux, par exemple « garder 2 véhicules hors Booking.com en haute saison ») ou **arrêt de vente** (`StopSell`, éventuellement limité à un lieu de départ), sur des jours de Tunis inclus, 731 jours au plus, jamais entièrement passés.
- **Le moteur de disponibilité les applique** : `ChannelAvailabilityPolicy` remplace (`services.Replace`) l'implémentation neutre du port `IChannelAvailabilityPolicy` du module Réservations. Pour une catégorie et un canal, `ChannelRestrictionRules.ForEngine` rend les restrictions sans lieu (arrêts fusionnés, quotas sommés) ; le moteur en déduit ce que **ce canal** peut vendre — capacité de la flotte moins véhicules gardés, moins l'occupation de tous les canaux ; zéro sous un arrêt de vente. Les autres canaux ne voient jamais les restrictions d'un canal. Démontré à tous les étages : propriété du domaine, moteur réel en test d'application, banc de contrat sur SQL Server (« un arrêt de vente sur Booking.com ne bloque jamais le site ; un quota garde N véhicules hors du canal ») et test de bout en bout.
- **Arrêt limité à un lieu** : le moteur n'a pas de dimension « lieu » ; ces arrêts sont appliqués par le cœur du module Canaux (offre de disponibilité sans ce lieu, création refusée par `channels.booking.stop_sell`) et par les envois (zéro à ce lieu).
- **Un canal inactif ne restreint rien** : ses restrictions restent enregistrées mais dormantes.
- **Cache** : `ChannelRestrictionsCache` (instantané en mémoire, 5 minutes au plus, chargements versionnés) est vidé par un `ICommittedChangesListener` après toute validation touchant une restriction ou un canal, qui prévient aussi le cache de disponibilité (`IAvailabilityChangeNotifier.AllChanged`). Dans une transaction — le contrôle sous verrou — les restrictions sont relues en base, jamais prises dans le cache (règle d'ADR-0044).

### Format canonique et adaptateurs

- **Le cœur parle un format canonique** : des objets C# (`ChannelInboundRequest` et ses variantes disponibilité, création, modification, annulation, consultation ; `ChannelInboundOutcome` réponse ou refus avec son code ; `ChannelAvailabilityUpdate` et `ChannelRatesUpdate` pour les envois). Chaque adaptateur traduit le format de son canal vers ces objets et retour :

  ```csharp
  public interface IChannelAdapter
  {
      ChannelAdapterKind Kind { get; }
      Task<ChannelInboundResult> HandleInboundAsync(ChannelInboundMessage message, IChannelInboundCore core, CancellationToken cancellationToken);
      Task<Result> PushAvailabilityAsync(ChannelPushTarget target, ChannelAvailabilityUpdate update, CancellationToken cancellationToken);
      Task<Result> PushRatesAsync(ChannelPushTarget target, ChannelRatesUpdate update, CancellationToken cancellationToken);
      Result<MappedReservation> MapBooking(ChannelReservation reservation, ChannelMappingSet mappings);
  }
  ```

  L'adaptateur lit le message, appelle `IChannelInboundCore` (qui fait tout le métier : correspondances, contrôles, dossier, idempotence) et écrit la réponse ; il ne touche jamais la base.
- **Adaptateur manuel** (`ManualChannelAdapter`) : aucun échange réseau ; tout message entrant est refusé (opération non prise en charge) et un envoi rend `channels.adapter_not_configured`. Le travail passe par la saisie manuelle et la liste « À reporter sur l'extranet ».
- **Adaptateur Booking.com** (`BookingComChannelAdapter`, Infrastructure) : répond `501 channels.adapter_not_configured` à tout, avec un `TODO(spec)` qui liste ce que le cahier des charges doit définir : transport, authentification dans les deux sens (identifiants, certificat client, plages d'adresses de Booking.com), types et schémas des messages (disponibilité et prix, création, modification, annulation et consultation), codes des catégories (ACRISS ou groupes propres), des lieux et des listes OpenTravel, envois et leur débit autorisé, codes d'erreur, scénarios de certification. Un canal connecté trop tôt échoue donc bruyamment (journal, file d'envoi en échec, santé `channels` dégradée) au lieu d'échanger des suppositions. **Aucun format Booking.com n'est inventé** : ni XML, ni noms de champs, ni codes.
- **Faux canal** (`FakeChannelAdapter`) : un petit format JSON **documenté comme réservé aux tests**, sans rapport avec Booking.com, enregistré seulement dans les environnements `Development` et `Testing` ; il exerce tout le chemin entrant et sortant (`FakeChannelOutbox` garde les envois reçus et sait échouer N fois).
- **Banc de contrat** (`ChannelContractBench`, classe de tests abstraite) : les huit scénarios que tout adaptateur doit passer — création confirmée puis rejouée sans doublon, modification de dates contrôlée, annulation idempotente, consultation, disponibilité sous un arrêt de vente et sous un quota, catégorie ou lieu non mappé, dernière voiture demandée par deux canaux en même temps (une seule réussit), disponibilité en moins d'une seconde. `FakeChannelContractTests` le fait tourner aujourd'hui ; l'adaptateur Booking.com fournira son pilote le jour où son format sera connu.

### Réservations des canaux

- **Contrat `IChannelBookings`** (module Réservations, ajout testé) : `CreateConfirmedAsync` (verrou de la catégorie, contrôle de disponibilité avec les restrictions du canal sauf `IgnoreChannelRestrictions`, conducteur enregistré, devis du moteur de prix, dossier **confirmé**), `AmendAsync` (nouveau devis, motif `booking.reason.channel`), `CancelAsync` (motif « canal », sans frais d'annulation), `FindByChannelReferenceAsync`. Un rappel `alongside` s'exécute dans la même transaction : le côté canal (`ChannelBooking`) est écrit avec le dossier ou pas du tout.
- **Côté canal** (`ChannelBooking`, temporel) : référence du canal unique par canal ; origine (reçue, saisie) ; mode de paiement ; prix brut, commission (montant transmis par le canal, qui prime, sinon brut × taux arrondi au millime) et net = brut − commission ; prix du moteur gardé pour comparaison ; empreinte de la demande (idempotence, ADR-0053) ; état recopié du dossier par un gestionnaire d'outbox de `BookingStatusChanged` (annulé, refusé ou expiré → annulé ; non présenté → non présenté). Une annulation au comptoir ne doit donc aucune commission, sauf commission transmise.
- **Prix** : le prix du dossier vient du moteur de prix (règle du canal du module Tarifs) ; le module Canaux ne calcule aucun prix. Le brut du canal est enregistré à côté et l'écart apparaît au rapprochement. Un échec du moteur refuse la réservation : jamais de dossier sans devis.
- **Saisie manuelle** (`/admin/channels/manual-entry`) : une réservation vendue sur l'extranet devient un dossier confirmé sous le verrou ; les restrictions du canal sont ignorées (le canal l'a déjà vendue) mais **jamais la capacité physique** (`availability.not_available`) ; même idempotence par référence ; une ligne `ManualEntry` au journal des messages. Les contrôles (permis, pièce, caution) se font au comptoir.
- **À reporter sur l'extranet** (`/admin/channels/extranet`) : pour un canal manuel, chaque quota ou arrêt de vente ajouté, modifié ou supprimé, et chaque nouvelle grille publiée pour le canal, crée un rappel (`ChannelReplicationItem`) jusqu'à ce que l'agence le marque comme reporté.

### Commissions et rapprochement

- **Contrat `IChannelCommissions`** (module Canaux) : les commissions des réservations des canaux par période de départ (et agence), et par dossier, pour les indicateurs ; il alimentera le port `IChannelCommissionSource` du module Reporting (lot 3C) dès que celui-ci sera fusionné, par un adaptateur de quelques lignes.
- **Rapprochement mensuel** (`/admin/channels/reconciliation`) : réservations du mois de départ (heure de Tunis), brut, commission et net, net prépayé à recevoir, commissions des réservations payées sur place, **versement attendu = net prépayé − commissions dues sur le payé sur place** (hypothèse affichée, `TODO(spec)` : contrat du canal), versement saisi à la main (`ChannelPayout`, un par canal et par mois, temporel) et écart. Import du relevé du canal : `TODO(spec)` (format inconnu).

### Écrans (permission `channels.manage`, préréglage Gérant)

`/admin/channels` (liste, création, état des envois), `/admin/channels/{id}` (paramètres, sécurité, correspondances, quotas et arrêts de vente avec calendrier mensuel), `/admin/channels/manual-entry`, `/admin/channels/extranet`, `/admin/channels/sync` (file d'envoi, rejeu), `/admin/channels/log` (journal), `/admin/channels/reconciliation`. Textes en français et en anglais, tableaux paginés par `AdminTablePager`, aucune violation axe sérieuse ou critique (tests de bout en bout).

## Conséquences

- Brancher Booking.com revient à écrire un adaptateur et le pilote de son banc de contrat : le modèle canonique, le cœur, la sécurité, l'idempotence, la file d'envoi, le journal et les écrans ne changent pas. Un autre comparateur ajoute une valeur de `SalesChannel` et son adaptateur.
- Tant que le cahier des charges manque, l'agence travaille par l'extranet avec la saisie manuelle et la liste des changements à reporter ; l'état `Connected` exige un adaptateur réseau, et l'adaptateur Booking.com refuse tout tant qu'il n'est pas écrit.
- Les modules Réservations et Reporting ne voient que des contrats (`IChannelAvailabilityPolicy`, `IChannelBookings`, `IChannelsQueries`, `IChannelCommissions`) ; neuf tables du schéma `channels`, dont quatre temporelles (canaux, restrictions, réservations des canaux, versements), créées par la seule migration `Phase5Channels`.

## Alternatives écartées

- **Écrire un format « proche d'OpenTravel » en attendant le cahier des charges** : interdit, et certain d'être à refaire.
- **Filtrer la disponibilité après coup dans le module Canaux** : deux sources de vérité, contrôle hors du verrou, risque de surréservation ; les restrictions passent par le port du moteur.
- **Ajouter la dimension « lieu » au moteur de disponibilité** : changement profond du moteur pour un besoin marginal ; les arrêts limités à un lieu restent l'affaire du cœur des canaux.
- **Calculer les prix des canaux dans le module Canaux** : le module Tarifs porte la règle du canal (parité, majoration, grille propre).
- **Créer les réservations des canaux comme demandes à valider** : les distributeurs exigent une confirmation immédiate ; les contrôles se font au comptoir.
- **Un canal pour toute la plateforme** : chaque agence signe son propre contrat (commission, paiement, extranet).
