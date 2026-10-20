# ADR-0024 — Moteur de prix pur, alimenté par le service de tarification

- **Statut** : Acceptée
- **Date** : 2026-10-20
- **Source** : [conception phase 1](../conception/phase-1-exploitation.md) §1 et §4, [prompt](../PROMPT.md) §7 et §16 (« pas de prix calculé ailleurs que dans le moteur de prix »), [spécifications](../SPEC.md) §7

## Contexte

Le même prix doit sortir partout (vitrine, bot, agent, canaux, avenants) et se vérifier par des tests rapides, dont l'exemple chiffré de la spécification. Le calcul dépend pourtant de données de plusieurs modules : grille publiée et règle du canal (Pricing), cautions et modèle garanti (Fleet), lieux (Referential), paramètres de l'agence.

## Décision

- **Moteur pur** : `PricingEngine.Calculate(grille, règle du canal, demande, paramètres, cautions)` dans `Domain/Pricing`, sans entrée-sortie ni horloge (l'instant de la demande fait partie de la demande), déterministe. Il renvoie un `PriceCalculation` immuable (lignes avec type, code, clé de libellé et arguments invariants, quantité, prix unitaire TTC, HT, TVA, TTC, taux ; totaux ; jours ; prix de chaque jour ; caution hors total ; grille et version ; promotions appliquées ; issue du code promo ; entrées) ou une erreur `pricing.*`.
- **Alimentation** : `QuoteCalculator` (Application) contrôle la demande, choisit les grilles (`RatePlanSelector` : la grille par défaut publiée porte les règles ; parité sans règle, majoration sur sa propre grille, ou grille propre publiée du canal, sinon `pricing.channel_plan_missing`), lit la catégorie et le véhicule garanti par `IFleetQueries`, les lieux par `IReferentialQueries`, les paramètres par `ISettingsReader` (`billing.vat_rate`, `billing.daily_contribution_excl_vat`, `pricing.late_return_tolerance`, et ceux du module : `pricing.round_public_prices_to_dinar`, `drivers.young_driver_age_below`, `pricing.opening_hours`), appelle le moteur et produit le contrat `PriceQuote`.
- **Surface publique** : `IPricingService.QuoteAsync(PriceRequest)` (instant de la demande = `TimeProvider`, sans permission : tunnel public) et `IPricingQueries` (options proposées sur un canal, résumé de la grille publiée). Les contrats n'exposent que des DTO, `Money`, `Period` et `SalesChannel`. Le simulateur du back office passe par le même `QuoteCalculator`, avec un instant de demande choisi et une grille de remplacement.
- **Port interne `IRatePlanStore`** : charge un plan et ses huit collections (une requête par collection, rattachées par EF Core ; la couche Application n'a pas accès aux requêtes fractionnées du fournisseur relationnel) et sert de double en mémoire dans les tests des services. Il reste interne au module (pas de dépôt générique, [ADR-0012](0012-dbcontext-unique-et-frontieres-de-modules.md)).
- **Règles retenues là où le contrat laissait un choix** :
  - jour i = date de Tunis de `départ + i × 24 h` ; parmi les événements qui couvrent ce jour, la priorité la plus haute **qui a un prix pour la catégorie**, sinon la saison, sinon le prix de base ;
  - durée minimale = la plus grande de **toutes** les saisons et de tous les événements qui couvrent un jour loué (le jour du retour n'est pas un jour loué) ; l'erreur porte le minimum requis (`PricingErrorCodes.TryGetRequiredMinimumDays`) ;
  - règle du canal puis arrondi au dinar en une seule opération, appliqué à tous les prix journaliers (parité comprise) ; options et supplément « modèle garanti » ne sont ni majorés ni arrondis ;
  - remise de palier sur la somme des prix journaliers, pourcentage du plan ou de la catégorie ;
  - promotions calculées sur le sous-total location après palier : la meilleure non cumulable (la première du catalogue à égalité), puis chaque cumulable, chacune plafonnée à ce qui reste (jamais sous zéro) ; fenêtres de réservation (date de la demande) et de départ, filtres de catégories et de canaux ; un code inconnu n'empêche pas le devis (issue `Unknown`, `NotApplicable`, `NotRetained`) ;
  - options par jour plafonnées par unité ; livraison facturée au départ et au retour au lieu de l'option ; hors horaires facturé au départ et au retour hors de `[ouverture, fermeture]` (bornes incluses) ; aller simple si le lieu de retour diffère ; jeune conducteur par conducteur plus jeune que le seuil à la date de départ ; rachat de franchise (quantité 1) qui passe la caution au montant réduit ;
  - TVA : lignes TTC regroupées par taux, HT du groupe arrondi puis réparti (reste d'arrondi sur la plus grande ligne) ; contribution journalière saisie HT (jours × montant, TVA en sus) ; droit de timbre hors devis (facture, phase 3).
- **Instantané des entrées** : demande (âges des conducteurs, pas leurs dates de naissance), instant de la demande, règle du canal, grille par défaut et grille utilisée avec leurs versions, paramètres, cautions de la catégorie.

## Conséquences

- Le test de référence `PricingEngineTests.ReferenceExample` et les propriétés FsCheck (somme des lignes, HT + TVA = TTC, répartition du HT, total jamais négatif, monotonie, déterminisme) s'exécutent sans base ; les services se testent avec des doubles ; l'intégration vérifie le même exemple depuis SQL Server.
- Le module Bookings fige `PriceQuote` (lignes et entrées) dans ses versions de devis ; un avenant ou une prolongation rappelle `QuoteAsync` avec la nouvelle demande, au tarif du moment.
- Un devis coûte une vingtaine de requêtes (plans, paramètres, flotte, lieux) : acceptable pour 30 véhicules ; un cache des grilles publiées, invalidé par `RatePlanPublished`, reste possible.

## Alternatives écartées

- **Calcul dans les écrans ou dans chaque module consommateur** : prix divergents, interdit par le prompt.
- **Moteur qui lit lui-même la base et l'horloge** : tests lents, résultats dépendants de l'heure.
- **Erreur sur un code promo inconnu** : le tunnel devrait recalculer sans le code pour afficher un prix.
