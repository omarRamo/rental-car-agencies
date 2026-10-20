# ADR-0026 — Pièces d'identité : stockage privé, accès autorisé et journalisé, rétention désactivée

- **Statut** : Acceptée (procédure de destruction à valider avec l'avocat)
- **Date** : 2026-10-20
- **Source** : [spécifications](../SPEC.md) sections 3, 5 et 14, [prompt](../PROMPT.md) section 9.5 et invariant 9, [conception phase 1](../conception/phase-1-exploitation.md) §6, [ADR-0018](0018-journal-audit-ajout-seul.md)

## Contexte

Les images des CIN, passeports et permis sont les données les plus sensibles de la plateforme : elles ne doivent jamais être publiques, chaque consultation doit être journalisée (qui, quand, quelle pièce, pourquoi), et la loi 2004-63 (article 45) encadre leur destruction (procès-verbal d'huissier en présence d'un expert de l'INPDP). Le back office s'utilise sur tablette (photo au comptoir). La politique de sécurité du back office n'autorise les images que depuis l'application (`img-src 'self'`), interdit les objets (`object-src 'none'`) et l'affichage en cadre (`X-Frame-Options: DENY`).

## Décision

- **Dépôt** : conteneur privé `documents` ; chemin fait d'identifiants seulement (`persons/{personId}/{uploadId}.{ext}`), jamais le nom du fichier envoyé, jamais réécrit. Le type réel est détecté par les premiers octets (JPEG `FF D8 FF`, PNG `89 50 4E 47 0D 0A 1A 0A`, PDF `%PDF-`) et la taille bornée à 10 Mo pendant la lecture ; le nom et le type annoncés par le navigateur sont ignorés. Sur tablette, l'agent photographie le recto et le verso (`accept="image/*" capture="environment"`) ; le navigateur réduit la photo à environ 1 600 px (`RequestImageFileAsync`, sans JavaScript maison) avant l'envoi.
- **Écriture** : le fichier est envoyé avant la transaction (nouveau chemin), sa référence enregistrée dans l'unité de travail, et le fichier supprimé si l'opération échoue. Un fichier qui n'est plus référencé (image remplacée, pièce supprimée, purge, anonymisation) est supprimé par l'outbox (`DocumentImagesDiscarded`) après la validation : jamais de référence vers un fichier absent, jamais de suppression annulée.
- **Lecture** : uniquement par le service `DocumentImageAccess` (permission `customers.documents.read`, revérifiée dans le service, session à double authentification) derrière deux points d'accès :
  - `GET /admin/customers/documents/{id}/images/{front|back}` : flux de l'image, `Cache-Control: no-store`, `inline`, nom neutre (`identity_card-front.png`) ;
  - `GET …/link` : lien de lecture seule valable 5 minutes (SAS de délégation d'utilisateur sur Azure ; SAS de service seulement avec Azurite), en JSON.
  Chaque accès écrit une ligne dans `audit.DocumentAccessLogs` (`IDocumentAccessLog` : utilisateur, IP, corrélation, pièce et face, mode `view` ou `temporary_link`, motif) **avant** la réponse ; si l'écriture échoue, rien n'est servi. Les motifs sont des codes : `customer_file` (défaut), `verification`, `booking_check`, `dispute`, `data_subject_request`.
- **Visionneuse** : rien n'est chargé à l'ouverture de la fiche. L'agent ouvre la visionneuse, choisit le motif, puis affiche : une ligne de journal par affichage. Un PDF s'ouvre par le lien de 5 minutes (le PDF ne peut pas être affiché dans la page avec la politique de sécurité actuelle).
- **Numéros** : affichés en entier aux seuls titulaires de `customers.documents.read` ; masqués sinon (`••••3456`, comptable), y compris dans `ICustomersQueries`.
- **Rétention** : tâche `customers.purge_document_images` (chaque nuit à 03:30, heure de Tunis) qui retire les images conservées plus de `customers.document_image_retention_days` jours (400 par défaut) après le plus tardif de leur dépôt et de la dernière location clôturée de la personne (signalée par Dossiers via `ICustomerRegistration.RecordRentalClosedAsync`). Les données de la pièce (type, numéro, dates) restent. **Désactivée par défaut** (`customers.document_image_retention_enabled = false`) : `TODO(spec)` procédure de destruction (article 45) à valider avec l'avocat avant activation.

## Conséquences

- Invariant 9 tenu par construction : aucune URL publique, aucun autre chemin de lecture que `DocumentImageAccess` ; les tests d'intégration vérifient l'écriture du journal à chaque lecture, le refus du comptable et des visiteurs anonymes, et la durée du lien.
- Les images ne passent jamais dans les journaux applicatifs ni dans l'outbox (seuls des chemins faits d'identifiants y figurent).
- Remplacer l'image d'une face supprime l'ancienne (correction) ; une pièce remplacée par une plus récente garde ses images jusqu'à la purge.
- `TODO(1E)` : la purge devra ignorer les personnes ayant un dossier en cours dès que le contrat Dossiers l'exposera.

## Alternatives écartées

- **Lien SAS dans la balise `<img>`** : bloqué par `img-src 'self'`, et un lien réutilisable pendant 5 minutes ne journalise pas chaque affichage.
- **Conteneur public ou URL devinable** : contraire à l'invariant 9.
- **Images en base de données** : volume, sauvegardes et coût du tier Basic.
- **Miniatures affichées automatiquement sur la fiche** : chaque visite de fiche deviendrait une consultation de pièce, sans motif.
- **Clé de compte de stockage** : interdite (identité managée uniquement).
