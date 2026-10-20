# Points ouverts

Suivi vivant des décisions, démarches et vérifications qui conditionnent le projet. La référence figée reste [specifications.md](specifications.md) (v1 du 20/10/2026) : quand un point est tranché, on le coche ici avec la date et la décision, et on met à jour l'ADR ou la spec concernée.

## Démarches de l'agence à lancer dès la semaine 1

Leurs délais ne dépendent pas du développement ; un retard décale le point de passage qu'elles débloquent.

| Démarche | Débloque | Statut |
|---|---|---|
| Dossier INPDP : déclaration du traitement et autorisation de transfert hors de Tunisie (avec un avocat) | Ouverture au public (fin du lot 2) | ⬜ À lancer |
| Contrat Clic to Pay avec pré-autorisation (STB / SMT) | Ouverture au public (fin du lot 2) | ⬜ À lancer |
| Adhésion à la facturation électronique TTN et certificat de signature | Factures électroniques (fin du lot 3) | ⬜ À lancer |
| Vérification de l'entreprise chez Meta et numéro WhatsApp dédié | Bot WhatsApp en ligne (fin du lot 4) | ⬜ À lancer |
| Candidature fournisseur Booking.com, puis contrat et cahier des charges technique | Vente sur Booking.com (fin du lot 5) | ⬜ À lancer |

## Décisions à obtenir de l'agence avant le lot 1

- [ ] Agences, lieux de remise (aéroports, hôtels, livraison) et horaires d'ouverture
- [ ] Vente par catégorie ou par modèle ; liste des 30 véhicules et de leurs catégories
- [ ] Grilles initiales : saisons, paliers, options, cautions, kilométrage, carburant
- [ ] Politique de garantie (acompte ou totalité, par saison) et politique d'annulation
- [ ] Âge minimum, ancienneté du permis, cas du permis international
- [ ] Langues au lancement (arabe dès le départ ?), nom de domaine, logo et charte
- [ ] Données à reprendre : clients, réservations futures, fichiers existants
- [ ] Outil actuel remplacé et date de bascule visée
- [ ] Numéro WhatsApp dédié et vérification de l'entreprise chez Meta
- [ ] Clic to Pay : contrat commerçant, pré-autorisation activable, durée de validité, commission
- [ ] Booking.com : candidature fournisseur, connecteur maison ou intermédiaire
- [ ] Expert-comptable : logiciel et format d'export, TVA, timbre, contrat tenant lieu de facture, adhésion TTN
- [ ] INPDP : déclaration du traitement et autorisation de transfert, avec un avocat
- [ ] Cibles des objectifs mesurables (section 2 de la spec), proposées mais à valider

## Hypothèses à confirmer pendant les lots

| Sujet | À confirmer | Avec qui | Lot |
|---|---|---|---|
| TVA | Taux normal de 19 % sur la location | Expert-comptable | 1 |
| Droit de timbre | Barème en vigueur sur facture | Expert-comptable | 3 |
| Contrat tenant lieu de facture | Le contrat de location peut-il remplacer la facture (note générale n°2/2026) ? | Expert-comptable | 3 |
| Clic to Pay — montants | Montant envoyé en millimes (× 1 000) : faire un paiement test de 1,000 TND dès la recette | Recette Clic to Pay | 2 |
| Clic to Pay — statuts | Codes de statut annulée, remboursée, refusée (seuls 0, 1, 2 sont connus) | Documentation SMT | 2 |
| Clic to Pay — pré-autorisation | Durée de vie d'une pré-autorisation ; activation du paiement en deux étapes | SMT / banque | 2 |
| Clic to Pay — cartes étrangères | Acceptation des cartes étrangères (diaspora ≈ 80 % de la demande estivale) | Banque | 2 |
| QuestPDF | Seuil de la licence gratuite | — | 1 |
| Coûts Azure | Prix réels en France Central (la source du prix Azure SQL date de 2016) ; offre gratuite Azure SQL toujours proposée pour la recette | Calculatrice Azure | 0 |
| Purge des pièces d'identité | Procédure de destruction (art. 45 : PV d'huissier, expert INPDP) | Avocat | 2 |
| Contrat et conditions générales | Franchise, caution, carburant, retard, sortie du territoire, amendes | Avocat | 1 |

## Incohérences relevées dans la spec v1

À trancher puis corriger dans une v2 de la spec.

- [ ] **Devise du coût d'infrastructure** : « 20 à 35 USD » (résumé, section 12) contre « ≈ 20 à 35 € » (tableau des objectifs, section 2).
- [ ] **Prix du marché** : le texte dit « 120 à 150 TND par jour » (section 7), la source Tuniscope citée s'intitule « des prix entre 120 et 180 dinars par jour ».
- [ ] **Statut manquant** : le plan de repli Clic to Pay (section 9) introduit « Validée, en attente de paiement », absent du cycle de vie (section 4). À ajouter au cycle, ou à modéliser comme « Confirmée » avec paiement en attente.
- [ ] **Fin d'une option datée** : elle « expire seule », mais le cycle de vie ne dit pas vers quel statut (« Expirée » ?) ni si la capacité est libérée de la même façon.
- [ ] **Décompte des améliorations** (section 16) : « Quatorze ajouts […] ; les cinq derniers points sont des évolutions possibles », alors que le tableau compte déjà 14 lignes et que les 5 évolutions sont listées à part.
