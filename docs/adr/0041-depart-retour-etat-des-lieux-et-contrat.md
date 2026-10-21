# ADR-0041 — Départ et retour : état des lieux, signatures et contrat PDF

- **Statut** : Acceptée
- **Date** : 2026-10-21
- **Source** : [conception phase 1](../conception/phase-1-exploitation.md) (lot 1F), [spécifications](../SPEC.md) §3 (droits) et §6 (« Départ », « Retour »), [prompt](../PROMPT.md) §6 (invariants 3, 4, 8, 9) et §16, [ADR-0026](0026-pieces-identite-stockage-et-acces-journalise.md), [ADR-0030](0030-dossier-de-location-agregat-et-chronologie.md), [ADR-0031](0031-devis-figes-versions-et-prolongation.md), [ADR-0032](0032-protocole-ecriture-dossiers-et-taches.md), [ADR-0034](0034-paiements-et-cautions.md), [ADR-0040](0040-outbox-messages-sans-gestionnaire.md), [ADR-0042](0042-frais-de-retour-franchise-et-retard.md)

## Contexte

Le départ et le retour se font sur une tablette de 10 pouces (parfois un téléphone), sur le parking, avec le client à côté : contrôle des originaux, véhicule, état des lieux guidé (8 photos au moins, kilométrage, carburant, accessoires, dommages pointés sur un schéma), encaissement et caution, contrat PDF signé à l'écran par le client et l'agent puis envoyé par e-mail ; au retour, comparaison avec le départ, frais, caution restituée ou retenue, clôture une fois tout soldé. L'état des lieux est une pièce de preuve en cas de litige : il doit être complet, horodaté, attribué, et ses fichiers ne doivent jamais être publics. La connexion du parking est incertaine et une remise peut commencer sur une tablette et finir sur une autre. Les transitions du dossier (`Confirmed → InProgress → Returned → Closed`) existent déjà ([ADR-0030](0030-dossier-de-location-agregat-et-chronologie.md)), avec une fonction « à côté » exécutée dans la même unité de travail ([ADR-0032](0032-protocole-ecriture-dossiers-et-taches.md)) ; l'argent passe par le module Paiements ([ADR-0034](0034-paiements-et-cautions.md)).

## Décision

### Modèle

- **`Inspection`** (schéma `bookings`, table temporelle) : un état des lieux de **départ** ou de **retour** d'un dossier, **brouillon** puis **terminé** ; un seul brouillon par dossier et par type (index unique filtré), créé à la première visite de l'écran et repris depuis n'importe quelle tablette. Ses enfants — photos, dommages, signatures et frais de retour ([ADR-0042](0042-frais-de-retour-franchise-et-retard.md)) — ne changent que par l'agrégat, dont le compteur `Revision` fait porter la `rowversion` de la racine sur toute modification : deux tablettes qui écrivent en même temps, l'une est refusée (`handover.inspection.concurrent_update`).
- **Relevés** : kilométrage au moins égal au dernier connu du véhicule (pris à la confirmation du véhicule) et, au retour, à celui du départ ; carburant en huitièmes (0 à 8) ; accessoires (roue de secours, cric, triangle, gilet, nombre de clés jusqu'à 5, sièges enfant jusqu'à 4, GPS, papiers du véhicule) ; au départ, pour chaque conducteur, les originaux vus (pièce d'identité et permis) ; au retour, le nettoyage nécessaire. Le départ terminé fixe aussi le véhicule : un véhicule changé depuis la confirmation fait reprendre la partie véhicule.
- **Photos guidées** : avant, arrière, côté gauche, côté droit, intérieur avant, intérieur arrière, tableau de bord, coffre — **les 8 sont obligatoires au départ**, conseillées au retour (la photo du départ est affichée à côté) ; reprendre une vue la remplace ; s'y ajoutent des photos libres et des gros plans de dommages (40 au plus). Le navigateur réduit chaque photo (`js/admin/handover-photos.js` : environ 1 600 px en JPEG, plus une vignette de 320 px, orientation EXIF appliquée) : moins de données mobiles et d'attente. Le serveur ne fait pas confiance au navigateur : signature binaire JPEG, PNG ou WebP, 10 Mo au plus (1 Mo pour une vignette ou une signature), empreinte SHA-256 conservée.
- **Dommages** : vue du schéma (avant, arrière, gauche, droite, dessus, intérieur), position en pour cent de la vue, type (rayure, bosse, fissure ou éclat, pièce manquante, tache, autre), gravité, note ; **existants** (copiés du dernier état des lieux terminé du véhicule au départ, du départ au retour, supprimables s'ils ont été réparés) ou **nouveaux**. Le schéma SVG s'utilise au doigt (un appui ouvre le dialogue à cet endroit) ; la liste des dommages et son bouton « Ajouter un dommage » (zone en 3 × 3) en sont le chemin clavier et lecteur d'écran.
- **Signatures** : client et agent dessinent au doigt, au stylet ou à la souris (`js/admin/signature-pad.js`), avec le nom du signataire ; l'image PNG, l'horodatage et l'auteur de l'enregistrement sont conservés. Alternative accessible : « signé sur papier », avec un motif obligatoire, tracée et imprimée au contrat. **Toute modification du contenu de l'état des lieux après une signature annule les signatures** (leurs fichiers sont libérés).

### Fichiers

- Conteneurs **privés** : `inspections` (photos, vignettes, signatures, `bookings/{dossier}/{état des lieux}/{fichier}`) et `contracts` (PDF, `bookings/{dossier}/{numéro}-v{version}-{tentative}.pdf`). Un fichier est écrit avant la transaction qui le référence et supprimé si elle échoue ; un fichier libéré (vue reprise, signature annulée, brouillon recréé) est supprimé après la validation.
- Lecture par des **points d'accès autorisés** sous `/admin/handovers/media/…` : photos et contrats pour `bookings.read`, signatures pour `handovers.manage`, toujours avec une session MFA ; `ETag`, `Cache-Control: private, no-cache`, affichage en ligne. Ni URL publique ni SAS : le serveur relit le fichier et contrôle le droit à chaque fois.

### Écrans

- `/admin/handovers` : départs et retours d'un jour de Tunis (`?day=`), retours en retard, état de préparation de chaque dossier (documents, véhicule et ses documents, solde, caution — un texte et une icône, jamais la couleur seule), bouton vers la remise ; un encart du tableau de bord et des boutons sur la page du dossier y mènent.
- `/admin/handovers/{dossier}/departure` et `/return` : **une étape par écran**, l'étape dans l'adresse (`?step=`), indicateur de progression, focus sur le titre de l'étape à chaque changement, cibles d'au moins 48 px, une réussite annoncée dans une région polie (un toast couvrirait les boutons). Départ : originaux, véhicule, photos, relevés, dommages, paiement et caution, contrat et signatures, remise des clés. Retour : relevés, photos, comparaison des dommages, frais, paiement et caution, clôture.
- **Étapes d'inspection et étapes de comptoir.** `handovers.manage` donne les étapes d'inspection (véhicule, photos, relevés, dommages) : c'est le travail du préparateur, qui ne voit qu'elles. Les étapes de comptoir — originaux, contrat et signatures, remise des clés, fin du retour — exigent en plus `bookings.manage` (agent, gérant) ; l'argent exige `payments.manage` et la clôture `bookings.manage`. Les services contrôlent les mêmes droits que les écrans. Une petite agence dont le préparateur remet aussi les clés lui donne `bookings.manage` en permission individuelle.

### Argent au départ

- L'étape « Paiement et caution » intègre le panneau des paiements du lot 1F-a (encaisser, prendre une caution). Le départ exige **le solde payé** (total du devis courant − net payé − cautions retenues) et **la caution du devis détenue** (empreinte, espèces ou chèque).
- **Dérogation** : permission `handovers.payment.defer` (gérant) — reporter le solde et/ou laisser partir sans caution, avec un motif. La décision est tracée sur l'état des lieux (qui, quand, motif) et imprimée au contrat ; le report fige le montant dû à cet instant : un solde devenu plus élevé bloque de nouveau. Tout est recontrôlé dans la transaction du départ.
- Booking.com : le prépaiement au canal s'enregistre comme un paiement « prépayé au canal » avec la référence de la réservation, comme tout autre paiement ; les règles du contrat de canal (commission, caution exigée ou non) restent `TODO(spec)`.

### Contrat

- **`RentalContract`** (schéma `bookings`, temporel) : numéro du contrat = numéro du dossier, version (1, 2… unique par dossier), devis et version de devis figés, langue (français ou anglais), PDF (chemin, empreinte, taille), établi par et le, envoyé à et le (adresse marquée sensible pour l'audit).
- **PDF** produit par **QuestPDF** (A4, police embarquée) : agence ; location (dates et lieux, départ effectif, canal) ; conducteurs et pièces (numéros **masqués** sauf les 4 derniers caractères, jamais d'image de pièce, [ADR-0026](0026-pieces-identite-stockage-et-acces-journalise.md)) ; véhicule ; état au départ (kilométrage, carburant, accessoires, les six vues du schéma avec les dommages numérotés et leur tableau) ; prix (lignes du devis figé, totaux HT, TVA, TTC, déjà payé, reste dû ou reporté) ; caution et franchise ; référence des conditions générales (`CG-LOC-2026-1`, texte définitif `TODO(spec)`) ; les deux signatures ou la mention papier. Avant signature, l'écran montre le même document en HTML (la CSP `frame-ancestors 'none'` interdit un aperçu PDF en cadre) ; le PDF signé s'ouvre par son lien.
- **Licence** : QuestPDF Community, fixée une fois au démarrage (`QuestPdfLicense`). Elle est gratuite pour une entreprise sous le seuil de chiffre d'affaires annuel de la licence ; si l'agence le dépasse, une licence payante sera nécessaire (à revoir chaque année).

### Départ, en une unité de travail

1. Contrôles : véhicule confirmé égal au véhicule affecté (et couvert par ses documents, invariant 3), originaux, relevés, 8 photos, signatures, argent.
2. Le PDF est rendu puis stocké (nouvelle version).
3. `RentalLifecycleService.StartRentalAsync(dossier, départ effectif, à côté)` : l'argent est recontrôlé, l'état des lieux est terminé, le contrat ajouté, le kilométrage enregistré dans la Flotte (`IFleetMileage`, jamais en baisse, inscrit à l'historique du véhicule avec le numéro du dossier), l'e-mail mis dans l'outbox et le contrat marqué envoyé.
4. En cas d'échec, rien n'est enregistré et le PDF est supprimé.

L'**e-mail** part au conducteur principal s'il a une adresse (message de service lié au contrat : aucun consentement marketing requis), en deux langues (celle du contrat d'abord), le PDF **en pièce jointe** : l'outbox ne porte que le conteneur et le nom du fichier, relu au moment de l'envoi (expéditeur ACS et dépôt `.eml` de développement) ; un fichier introuvable fait échouer l'envoi, qui sera réessayé. Sans adresse, l'écran dit d'imprimer le contrat.

### Retour, en une unité de travail

- Le brouillon reprend les dommages du départ comme existants (il est recréé si le véhicule a été échangé en cours de location). Relevés, photos à côté de celles du départ, nouveaux dommages, puis frais ([ADR-0042](0042-frais-de-retour-franchise-et-retard.md)) et `RentalLifecycleService.CompleteReturnAsync(dossier, retour effectif, à côté)` : état des lieux terminé avec ses frais, kilométrage enregistré dans la Flotte. L'heure de retour proposée est le début de l'état des lieux de retour (à la seconde, jamais avant le départ effectif), modifiable.
- Règlement par le panneau des paiements (encaissement, remboursement) et décision sur la caution (retenir le solde dû, le reste étant restitué, ou restituer) ; **clôture** seulement quand le solde est nul et qu'aucune caution n'est détenue, contrôlé avant et dans la transaction de clôture.
- Le kilométrage est enregistré **aux deux remises** : le dernier relevé connu de la Flotte reste à jour pour la location suivante.

## Conséquences

- La phase 3 (facture finale) lit `IHandoverQueries` : frais de retour avec leurs clés de libellé, contrats, solde ; les libellés sont dans `HandoversResources` (`ReturnCharge.Label.*`, `ReturnCharge.Detail.*`).
- L'envoi du contrat par WhatsApp (SPEC §6) viendra avec le canal WhatsApp (phase 4) ; seul l'e-mail existe.
- Volume : environ 10 photos réduites (200 à 400 Ko) par remise, deux remises par location ; la durée de conservation des états des lieux et du contrat (preuve, prescription) reste à fixer avec l'avocat (`TODO(spec)`).
- La signature dessinée est une image horodatée et attribuée, pas une signature électronique qualifiée ; sa valeur probante (et celle du contrat papier) est à confirmer par l'avocat (`TODO(spec)`).
- Pas de mode hors ligne (Blazor Server) : chaque étape est enregistrée à mesure, une coupure se reprend sur l'étape en cours.
- Un préparateur ne peut ni signer ni remettre les clés : il prépare, l'agent conclut au comptoir.
- QuestPDF apporte son moteur de rendu natif (SkiaSharp) au déploiement ; aucun navigateur sans tête sur le serveur.

## Alternatives écartées

- **PDF par un navigateur sans tête** (Chromium, wkhtmltopdf) : lourd pour un App Service B1, dépendances natives à maintenir ; QuestPDF est une bibliothèque .NET au rendu déterministe.
- **Photos en base** : volume et sauvegardes alourdis ; le stockage privé est fait pour cela.
- **Liens SAS vers le navigateur** : chaque affichage contournerait le contrôle des droits et la maîtrise du cache ; à ce volume, relire par le serveur est simple et sûr.
- **Photos d'origine envoyées puis réduites sur le serveur** : 5 à 12 Mo par photo sur réseau mobile et une bibliothèque d'images côté serveur ; la réduction se fait dans le navigateur, le serveur contrôle le type et la taille.
- **Signature en tracé vectoriel** : une image PNG suffit au PDF et à l'affichage.
- **Départ sans paiement sur simple note** : aucune traçabilité ; une permission dédiée, un motif, un montant figé et une mention au contrat.
- **Un seul agrégat « remise » par dossier portant départ et retour** : deux états des lieux distincts gardent chacun leur brouillon, leurs signatures et leur historique.
- **Contrat régénéré à chaque consultation** : le PDF stocké est la version signée, une preuve qui ne change plus ; une nouvelle version s'ajoute, l'ancienne reste.
