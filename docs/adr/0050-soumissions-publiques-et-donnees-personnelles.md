# ADR-0050 — Soumissions publiques des conducteurs et protection des données personnelles

- **Statut** : Acceptée
- **Date** : 2026-10-21
- **Source** : [spécifications](../SPEC.md) §5 (conducteurs, pièces, éligibilité, liste noire) et §14 (données personnelles, INPDP) ; [prompt](../PROMPT.md) §6 (accès aux pièces autorisé et journalisé) ; [conception phase 2](../conception/phase-2-vitrine-paiement.md) §1 ; [ADR-0049](0049-tunnel-sessions-devis-figes-garantie-de-prix.md) (tunnel), ADR des clients et des images de pièces (déduplication, conteneur privé, accès journalisé)

## Contexte

À l'étape 2 du tunnel, un visiteur anonyme tape l'identité, les coordonnées, le permis et la pièce d'identité d'un ou deux conducteurs et photographie leurs documents ; « Ma réservation » permet d'envoyer des photos plus tard. Le module Clients déduplique les personnes (document, puis téléphone et nom, puis e-mail et nom) et garde des fiches vérifiées par les agents (pièces contrôlées sur l'original, images, consentements, liste noire). N'importe qui peut taper le numéro de document ou le téléphone d'une autre personne : une soumission publique ne peut donc ni modifier une fiche existante, ni révéler qu'elle existe, ni montrer une inscription sur la liste noire.

## Décision

### Une saisie publique ne modifie jamais une personne connue

- `RegisterSubmittedDriverAsync` cherche une correspondance avec les règles de déduplication. **Aucune** : la personne est créée à partir de la saisie (identifiant choisi d'avance pour ranger ses photos), avec ses documents, ses photos et ses consentements ; la soumission est enregistrée « appliquée ». **Une correspondance** : la fiche reste **inchangée** — identité, coordonnées, documents, images, vérifications, consentements — et la saisie attend comme **soumission en attente** (`DriverSubmission` : détails saisis, documents, photos, consentements, type de correspondance, nom différent ou non) rattachée au dossier.
- L'agent accepte la soumission (documents ajoutés ou corrigés, photos rattachées, détails manquants complétés, consentements) ou l'écarte (photos supprimées) depuis la fiche du conducteur (panneau « Dépôts en ligne », permission `customers.manage` ; numéros masqués sans `customers.documents.read`, images vues par l'accès autorisé et journalisé). La validation du dossier est refusée tant qu'une soumission attend (`booking.drivers.submission_pending`) ; la liste de contrôle le signale avec le lien vers la fiche.
- La correspondance est recherchée de nouveau dans l'unité de travail : si elle a changé entre-temps (deux soumissions simultanées du même document), la soumission échoue sans rien enregistrer (conflit) et le visiteur la renvoie.

### Le visiteur n'apprend rien d'une fiche existante

- Mêmes pages, mêmes réponses, mêmes e-mails qu'il y ait correspondance ou non ; le dossier est répondu à **l'adresse saisie avec la demande** (contact de la demande), jamais à celle de la fiche.
- L'**éligibilité** affichée (âge, ancienneté du permis, validité des documents jusqu'au retour, permis international, CIN pour un Tunisien) est évaluée sur une **personne transitoire** construite à partir de la seule saisie, jamais stockée ni comparée à la fiche. La **liste noire n'est jamais montrée** : l'agent la voit à la validation.
- Les erreurs montrées au public sont limitées à une liste de codes connus (format de la saisie, disponibilité, expiration, fichiers) ; tout autre code devient un message générique (jamais « numéro déjà enregistré », jamais une raison de refus).

### Photos des documents

- Module JavaScript léger (`wwwroot/js/document-upload.js`) : photo redimensionnée dans le navigateur (1600 px au plus, JPEG), envoi avec barre de progression et nouvelle tentative ; sans JavaScript, l'étape 2 dit que les photos peuvent être envoyées depuis « Ma réservation », où chaque emplacement est aussi un formulaire multipart ordinaire.
- `POST /{culture}/reserver/{jeton}/pieces` et `/{culture}/ma-reservation/dossier/pieces` : antiforgery, limite `public-upload` (40 par 10 minutes), taille bornée (10 Mo plus l'enveloppe), contenu vérifié par sa signature (JPEG, PNG, PDF), emplacement connu (`main.license.back`…), 40 envois au plus par session. **La réponse est un texte, jamais l'image ni une adresse de l'image** ; l'aperçu affiché est le fichier local.
- Stockage : zone de **transit** (`staged/{id}`) du conteneur privé, puis à la soumission copie sous le dossier du propriétaire (`persons/{id}/…`, contenu revérifié) et suppression du transit ; tâche `customers.purge_staged_images` (toutes les heures) pour les fichiers consommés ou inutilisés depuis plus de 26 h.

### Consentements et minimisation

- Le traitement des pièces est **obligatoire** pour envoyer des documents ; les notifications WhatsApp et les offres sont **facultatives et séparées**. Pour une personne connue, ils attendent avec la soumission.
- Les soumissions en attente d'un dossier refusé, expiré ou annulé sont écartées avec leurs images (outbox) ; l'export des données d'une personne inclut ses dépôts en ligne, l'anonymisation les supprime.

## Conséquences

- Une usurpation (document d'un autre tapé dans le tunnel) ne change rien sur la fiche : elle aboutit à une demande que l'agent rapproche, avec la fiche et la saisie côte à côte.
- L'agent a une étape de plus pour un client connu qui a réellement changé de permis ou de téléphone ; c'est le prix de l'intégrité des fiches vérifiées.

## Alternatives écartées

- **Mettre à jour la fiche quand la saisie est « plus récente »** : n'importe qui pourrait remplacer le téléphone, l'e-mail ou les pièces d'un client.
- **Dire au visiteur « vous êtes déjà client, connectez-vous »** : révèle l'existence d'une fiche à partir d'un numéro de document ou de téléphone.
- **Évaluer l'éligibilité sur la fiche trouvée** : le résultat dépendrait de données que le visiteur n'a pas saisies et pourrait trahir la liste noire.
- **Renvoyer l'adresse de l'image téléversée** : une adresse de pièce d'identité ne doit exister que derrière l'accès autorisé et journalisé.
