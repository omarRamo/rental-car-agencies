# Journée type de haute saison

> Le scénario qui clôt la phase 1 : « une journée type de haute saison se joue de bout en bout en E2E sans
> surréservation » ([PROMPT §15](../PROMPT.md)). Deux tests le jouent : un **scénario d'intégration**, complet et
> chiffré, sur les services réels et SQL Server ; un **scénario de navigateur**, plus court, sur les écrans de l'agence
> et du client. Ce document dit ce qu'ils jouent, ce qu'ils prouvent, comment les lancer et ce qu'ils ne couvrent pas
> encore.

| | Scénario d'intégration | Scénario navigateur |
|---|---|---|
| Fichier | `tests/CarRental.Integration.Tests/Scenarios/HighSeasonDayTests.cs` | `tests/CarRental.E2E.Tests/Scenarios/HighSeasonDayE2ETests.cs` |
| Hôte | un hôte à lui (`HighSeasonHost`) : sa propre base, l'horloge du scénario, ni serveur de tâches ni données de démonstration | l'hôte E2E partagé (agence de démonstration), joué seul une fois les autres tests de navigateur terminés |
| Jour joué | le prochain 15 juillet (au moins une semaine après le lancement), précédé de sa semaine | aujourd'hui, et un week-end dans un mois |
| Durée | une dizaine de secondes, dont environ 1 s pour 150 ventes simultanées | une quarantaine de secondes |

## 1. Le scénario d'intégration

### 1.1 Le décor

- **Un hôte à lui.** L'application réelle (même composition que la production : modules, moteur de prix, moteur de
  disponibilité, verrou `sp_getapplock`, outbox, paiements simulés Clic to Pay) sur une base dédiée, sans données de
  démonstration. Son **horloge** (`ScenarioClock`) est l'horloge réelle décalée d'une avance que le test fait
  progresser, jamais reculer : la journée se joue de 08 h 00 à 19 h 15, heure de Tunis, quels que soient le jour et
  l'heure du lancement. Le **serveur de tâches est arrêté** : l'expiration des demandes et l'outbox tournent quand le
  scénario les lance, rien ne change la journée dans son dos (pas de course avec une tâche d'expiration ou
  d'escalade).
- **L'agence** : un bureau (AGC) et l'aéroport (TUN) ; **4 économiques (ECMR), 3 compactes (CDMR), 2 SUV (IFMR)** ;
  une grille publiée dont la **haute saison** (1er juillet – 31 août) relève les prix de base, TTC par jour :

  | Catégorie | Base | Haute saison | Caution |
  |---|---|---|---|
  | ECMR | 90,000 | 140,000 | 500,000 |
  | CDMR | 130,000 | 190,000 | 700,000 |
  | IFMR | 180,000 | 260,000 | 900,000 |

  Paliers de durée : 1–2 jours 0 %, 3–6 jours −5 %, 7 jours et plus −10 % ; contribution journalière de 2,000 HT
  (+ TVA 19 %) ; tolérance de retard 59 min ; **tampon de préparation de 2 h** après chaque retour (paramètres par
  défaut). Booking.com est vendu **à la main sur son extranet** (canal manuel, commission 15 %), avec un quota « un SUV
  gardé hors Booking.com » toute la saison.
- **Les acteurs** : Leila, l'agente du comptoir (sa caisse est la sienne), Karim, le gérant (canaux, saisies
  Booking.com, verrouillage de caisse), Omar, le préparateur (états des lieux), le site public et son tunnel, l'agent
  WhatsApp (son outil `creer_lien_devis`), un Booking.com connecté (le contrat `IChannelBookings` qu'appelle le cœur des
  canaux).

### 1.2 La semaine d'avant (J-6 à J-1)

Les dossiers de la journée naissent par les services, à leur date : téléphone, tunnel (avec sa garantie payée sur la
banque simulée), saisies Booking.com ; les départs se font sur les services de la tablette (originaux, 8 photos,
relevés, signatures, contrat PDF, argent). La veille, l'agente prépare le planning du lendemain.

| Dossier | Canal | Catégorie, période | Ce qui lui arrive |
|---|---|---|---|
| Retour à l'heure | téléphone | ECMR, J-5 10 h → J 10 h | parti J-5 sur E1, rendu à 09 h 40 |
| Retour en retard | Booking.com (extranet, prépayé) | CDMR, J-3 9 h → J 9 h, aéroport | pièces vues au comptoir, parti sur C1, rendu à 12 h 30 |
| Prolongation | site (garantie capturée) | IFMR, J-2 10 h → J 18 h | parti J-2 sur S1, gardé un jour de plus |
| Départ de 9 h | téléphone | ECMR, J 9 h → J+3 | E2 attribuée la veille |
| Départ de 11 h | téléphone | CDMR, J 11 h → J+2 | C1 attribuée la veille… que le retard immobilise |
| Non présenté | téléphone | ECMR, J 10 h → J+2 | E3 attribuée la veille |
| Surclassement | téléphone | ECMR, J+3 → J+5 | passe en CDMR à 13 h |
| Annulation avec frais | téléphone | IFMR, J+2 → J+3 | annulé à 14 h 30 |
| Option annulée | téléphone (option) | CDMR, J+4 → J+6 | annulée à 17 h |
| Demande qui expire | site | CDMR, J+5 → J+8 | envoyée J-1 à 7 h 30, jamais traitée |
| Week-end Booking.com | Booking.com (extranet) | ECMR, J+7 16 h → J+13 | — |
| Week-end téléphone | téléphone | ECMR, J+8 9 h → J+12 18 h | — |
| Demande validée | site (garantie détenue) | ECMR, J+1 → J+4 | envoyée J-1 à 22 h 10 |
| Demande refusée | site | CDMR, J+6 → J+9 | envoyée J à 6 h 50 |
| Demande incomplète | site | IFMR, J+4 → J+7 | envoyée J à 7 h 45 |

### 1.3 La journée heure par heure

| Heure | Ce qui se passe | Ce qui est vérifié |
|---|---|---|
| 08 h 00 | Tâche d'expiration ; ouverture de la caisse (fond de 150,000) ; tableau du jour | la demande de la veille est **expirée** (blocage de 24 h) et libère sa place ; départs, retours et demandes du tableau dans l'ordre |
| 08 h 15 | Validation de la demande garantie | **confirmée**, garantie **capturée** ; prix d'été : 3 × 140,000 − 5 % + contribution = **406,140** |
| 08 h 20 – 08 h 25 | Une demande **refusée** (motif), une autre **renvoyée pour un document** | statuts et motifs |
| 08 h 30 | **Option téléphonique** sur un SUV pour le lendemain | son échéance de 48 h s'arrête au départ |
| 09 h 05 | **Départ** de 9 h (assistant de la tablette, payé par carte, empreinte) | contrat PDF, location en cours |
| 09 h 30 | — | le Booking.com attendu à 9 h figure dans les **retours en retard** |
| 09 h 40 | **Retour à l'heure** | aucun frais ; empreinte libérée, dossier **clôturé** |
| 09 h 40 – 10 h 00 | L'agent WhatsApp **fige un devis** 24 h ; le client ouvre le lien et passe par le tunnel | demande WhatsApp au **prix garanti** du devis (version « devis figé ») |
| 10 h 15 | **Prolongation** du SUV d'un jour ; **saisie Booking.com** d'une arrivée de 14 h | nouvelle version « prolongation » : 3 jours (748,140) → 4 jours, **997,520** ; réservation confirmée au prix du moteur |
| 10 h 30 | Validation de la demande WhatsApp ; **client au comptoir** (payé en espèces, caution en espèces) | sans garantie : **en attente de paiement** par lien ; le client part à 10 h 50 sur C2 |
| 10 h 55 | Propositions d'attribution du jour | le départ de 11 h est **en conflit** sur C1 (le retard garde la voiture, tampon compris) ; l'affecter à C1 est refusé, l'agente **l'échange** pour C3 |
| 11 h 05 | **Départ** de 11 h sur C3 | — |
| 11 h 30 | Le gérant **arrête les compactes sur Booking.com** (J à J+2) et **garde deux compactes** du week-end hors Booking.com | Booking.com 0 compacte demain, le site et le téléphone **1** ; le week-end : Booking.com 1, le site 3 ; le SUV gardé hors Booking.com reste en vente sur le site |
| 12 h 05 | **Non présenté** (2 h après l'heure de départ) | sa voiture revient à la vente immédiatement (1 → 2 restantes) |
| 12 h 30 | **Retour en retard** de 3 h 30, un quart de réservoir en moins | 1 jour de retard tarifé **par le moteur** au plan d'été : 4 jours − 3 jours, palier compris = **180,500** + contribution **2,380** + carburant 2 × 15,000 ; payé en espèces ; dossier clôturé |
| 13 h 00 | **Surclassement** ECMR → CDMR | les deux catégories verrouillées ; nouveau devis : 2 × 190,000 + contribution = **384,760** |
| 13 h 30 – 14 h 05 | Le client Booking.com montre ses pièces, paie sur place, part sur E1 (rendue à 9 h 40) | — |
| 14 h 30 | **Annulation** à moins de 3 jours du départ | frais de 20 % de 262,380 = **52,476** |
| 15 h 00 – 15 h 20 | Le client complète sa demande ; validation | revérifiée sous verrou ; en attente de paiement |
| 16 h 00 | **Confirmation de l'option** | revérifiée sous verrou ; confirmée |
| 16 h 30 | **Le coup de feu** (ci-dessous) | — |
| 17 h 00 | **Annulation de l'option** | sans frais |
| 17 h 30 | Propositions d'attribution des deux semaines, toutes appliquées | aucune réservation impossible à placer, aucun conflit |
| 19 h 00 | Fermeture de la caisse (comptée comme attendue), verrouillée par le gérant ; outbox délivrée | écart nul ; chaque contrat envoyé au conducteur (sauf aux clients Booking.com, sans adresse) |

### 1.4 Le coup de feu de 16 h 30

**150 ventes lancées en même temps**, chacune dans sa propre portée (une requête, une tablette, un message de canal),
toutes libérées par le même signal pour se disputer vraiment le verrou de leur catégorie :

| Cible | Tentatives | Attendu |
|---|---|---|
| Les **2 dernières économiques** du week-end suivant (périodes différentes, qui contiennent toutes la même nuit) | 20 site, 20 tunnel, 20 options téléphoniques, 20 comptoir, 10 saisies extranet Booking.com, 10 Booking.com connecté | exactement **2** vendues |
| Les **3 compactes** du week-end, dont 2 gardées hors Booking.com | 20 Booking.com connecté, 10 site, 10 téléphone | exactement **3** vendues, **au plus 1** par Booking.com |
| Un **SUV demain**, quand les deux sont pris | 4 site, 3 téléphone, 3 comptoir | **aucune** |

Chaque perdant reçoit `availability.not_available` — jamais une exception, un `availability.lock_timeout` ou un
`availability.lock_deadlock`. Le gagnant varie d'un lancement à l'autre ; les assertions portent sur ce qui ne varie
pas.

### 1.5 Les vérifications de fin de journée

- **Les dossiers** : chacun dans son statut attendu, et aucun autre dossier en base (les 145 perdants n'ont rien écrit).
- **Invariant 1** (relu sur les lignes en base, sans passer par le moteur) : aucun véhicule ne porte deux dossiers en
  même temps, **tampon de préparation inclus**, passé et avenir confondus.
- **Invariant 2** : à aucun instant une catégorie ne porte plus de dossiers que de véhicules ; le **profil de capacité**
  de chaque catégorie n'a aucun manque d'ici 60 jours ; chaque dossier à venir a sa voiture.
- **Le planning du jour** : chaque départ et chaque retour du jour est sur la ligne de sa voiture, aucune ligne ne
  porte deux dossiers à la fois, aucun dossier du jour sans voiture.
- **Le tableau du jour et les départs et retours** : 4 départs faits (le non-présenté n'en est pas un), 2 retours faits
  (le SUV prolongé revient demain), aucun retard, aucune voiture manquante sous 48 h, aucun manque ; les demandes en
  attente sont exactement celles vendues par le site et le tunnel pendant le coup de feu.
- **La caisse** : fond + espèces du client du comptoir + frais du retard, caution en espèces ; écart nul.
- **L'outbox** : tous les messages de la journée délivrés au premier essai.
- **Les numéros `LOC`** de l'année forment 1…n sans trou, alors que 145 tentatives ont été annulées.

## 2. Le scénario navigateur

Compact, il joue sur l'**agence de démonstration** (la seule que servent le site public et le back office), avec ses
propres dates (un week-end dans un mois, dans la catégorie MCMR qu'aucun autre test ne vend) et ses propres clients ;
avant de commencer, toutes les MCMR de ces dates sont vendues sauf deux.

1. **Le client, sur un téléphone (390 px)** : résultats, options, conducteurs, paiement, page de la banque simulée,
   confirmation (« garantie détenue »). Aucun défilement horizontal.
2. **L'agente, sur une tablette (1280 × 800)** : la file des demandes, le contrôle, la validation (garantie capturée).
3. **La dernière voiture** : deux onglets de création rapide affichent « 1 restant(s) » ; le premier vend la voiture au
   comptoir, le second est **refusé** (« Plus aucun véhicule de cette catégorie n'est disponible pour ces dates. ») et
   voit la catégorie « Complet ».
4. **Le planning** de la semaine : les propositions d'attribution de la catégorie appliquées (les autres dossiers de la
   semaine, ceux de la démonstration, ne sont pas touchés) ; la demande et la vente du comptoir sur deux voitures, leurs
   barres disjointes.
5. **Un départ** sur l'assistant de la tablette (originaux, 8 photos réduites par le navigateur, relevés, paiement déjà
   encaissé, contrat signé à la souris, clés) pour un client de passage du jour.
6. **Un retour en retard** sur l'assistant : un jour de retard tarifé par le moteur (100,000 + contribution 2,380),
   payé, dossier clôturé. Cette location appartient à **une agence à elle** : l'agence de démonstration garde le seul
   retard que d'autres tests comptent.
7. **Le tableau du jour** liste le départ, « fait » ; **les départs et retours** de l'agence du retard montrent le
   retour clôturé et plus aucun retard.

Chaque écran visité passe axe sans violation sérieuse ou critique (la page de la banque simulée, page de développement,
exceptée), une fois ses transitions terminées : un bouton activé à l'arrivée de ses données passe du gris à sa couleur
en 250 ms, et axe mesurerait le contraste du mélange. Le scénario se joue sur un seul jour de Tunis : lancé dans les dix
dernières minutes d'une journée, il attend la suivante. Il écrit beaucoup en quelques secondes (dossiers, photos,
paiements, une caution et les lignes d'audit de tout cela) et lit les écrans partagés du jour : il tourne **seul, après
les autres tests de navigateur** (collection xUnit sans parallélisme), pour ne pas pousser leurs lignes hors de la
première page du journal d'audit ou de la liste des cautions.

## 3. Ce que cela prouve

| Règle | Comment |
|---|---|
| Pas de chevauchement d'affectation, tampon inclus (invariant 1) | refus d'affecter la voiture d'une location en retard ; audit des lignes en base ; planning du jour |
| Capacité jamais dépassée (invariant 2) | 150 ventes simultanées : exactement la capacité vendue ; audit des lignes ; profils de capacité sans manque |
| Écriture de disponibilité sous `sp_getapplock` | chaque perdant du coup de feu reçoit `availability.not_available`, sans interblocage ni délai dépassé |
| Quotas et arrêts de vente du canal | appliqués à Booking.com, jamais au site, au téléphone ni au comptoir ; la saisie extranet les ignore, jamais la capacité |
| Prix du seul moteur, devis figé | prix d'été, palier, contribution ; devis WhatsApp gardé ; nouvelles versions pour la prolongation et le surclassement |
| Expiration, non-présentation, annulation libèrent la capacité | revente possible juste après |
| Retard tarifé par le moteur | méthode delta (location jusqu'au retour réel − location prévue), au plan en vigueur |
| Caisse | écart nul en fin de journée |
| Numéros `LOC` sans trou | 1…n malgré les annulations |
| Messages de fond | outbox délivrée sans erreur |

**Défauts trouvés et corrigés par le scénario** (chacun dans son commit, avec son test de régression) :

1. Une demande web prenait son numéro `LOC` (ligne de compteur verrouillée jusqu'à la validation) **avant** le verrou de
   disponibilité, quand tous les autres écrivains prennent le verrou d'abord : une demande web et une réservation
   téléphonique sur la même catégorie s'attendaient en cycle. Le perdant recevait `availability.lock_deadlock` au lieu de
   `availability.not_available`, et le débit tombait au rythme du détecteur d'interblocages (60 tentatives : 13,8 s et 3
   interblocages avant, 0,4 s et aucun après). La demande prend désormais le verrou avant son numéro.
2. Les propositions d'attribution lisaient les fenêtres des véhicules sur la seule période demandée (la flotte les
   coupe à cette période), alors que les dossiers gardent toute leur période : tout dossier encore dehors à la fin de
   la semaine affichée apparaissait comme conflit (« véhicule non louable sur toute la période ») ou comme impossible à
   placer. Les dossiers de la période sont désormais planifiés sur toute leur durée.

## 4. Lancer les scénarios

```bash
# avec Docker (conteneurs SQL Server et Azurite démarrés par les tests)
dotnet test tests/CarRental.Integration.Tests --filter "FullyQualifiedName~Scenarios"
dotnet test tests/CarRental.E2E.Tests --filter "FullyQualifiedName~Scenarios"

# sans Docker : un SQL Server et un Azurite déjà démarrés (voir tests/Shared/TestInfrastructure.cs)
export CARRENTAL_TEST_SQL='Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true'
export CARRENTAL_TEST_STORAGE='UseDevelopmentStorage=true'   # npx azurite --inMemoryPersistence --skipApiVersionCheck
```

Le scénario d'intégration écrit le bilan du coup de feu dans la sortie du test (`--logger "console;verbosity=detailed"`) :
nombre de tentatives, durée, et pour chaque canal et catégorie le nombre de ventes et de refus. En cas d'échec du
scénario navigateur, sa trace Playwright et une capture sont dans `E2E_ARTIFACTS_DIR` (sinon `bin/.../e2e-artifacts`).

Pour faire évoluer le scénario : `HighSeasonWorld` porte l'agence et les gestes de chaque acteur (un appel de service
par geste, dans sa portée), `OccupancyAudit` les invariants relus en base, `SalesBurst` le coup de feu. Une nouvelle
étape s'ajoute à son heure, après un `JumpTo` : l'horloge n'avance que vers l'avant.

## 5. Ce qui n'est pas encore couvert

- **Facturation** (lot 3A, en cours) : ni facture ni avoir à la clôture des dossiers, ni droit de timbre ; à ajouter
  au scénario quand le lot sera fusionné (facture de chaque dossier clôturé, avoir de l'annulation avec frais).
- **Comptabilité** (lot 3B) : journaux, rapprochements et clôture mensuelle — c'est le scénario « un mois simulé » de la
  phase 3.
- **Indicateurs** : l'instantané quotidien et les indicateurs du jour joué ne sont pas recalculés ni comparés aux
  dossiers.
- **Booking.com connecté** : le format du canal n'est pas connu (aucun format inventé) ; le scénario passe par la saisie
  extranet et par le contrat `IChannelBookings`, pas par un adaptateur réseau ni par l'envoi des disponibilités.
- **WhatsApp** : seul l'outil de devis figé de l'agent est joué, pas une conversation (couverte par le jeu
  d'évaluation et ses tests).
- **Paiement en ligne réel** : la banque est simulée ; le lien de paiement envoyé après une validation sans garantie
  n'est pas payé dans la journée.
- **Plusieurs instances** de l'application : le cache de disponibilité est local au processus (ADR-0004) ; le verrou
  SQL protège les écritures, mais le scénario ne joue qu'une instance.
- **Azure SQL** (lecture validée par instantané active par défaut) : les tests tournent sur SQL Server ou LocalDB, où
  elle ne l'est pas ; l'ordre des verrous corrigé vaut dans les deux cas.
