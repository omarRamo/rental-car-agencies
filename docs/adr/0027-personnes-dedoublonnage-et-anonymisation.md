# ADR-0027 — Personnes : dédoublonnage par clés normalisées, export et anonymisation réservés au gérant

- **Statut** : Acceptée (durée de l'historique et portée du droit d'accès à valider avec l'avocat)
- **Date** : 2026-10-20
- **Source** : [spécifications](../SPEC.md) sections 6 et 14, [prompt](../PROMPT.md) section 12, [conception phase 1](../conception/phase-1-exploitation.md) §6, [ADR-0003](0003-donnees-azure-sql-tables-temporelles.md), [ADR-0018](0018-journal-audit-ajout-seul.md)

## Contexte

Au téléphone ou au comptoir, l'agent retrouve le client « par téléphone, CIN, passeport ou e-mail, sans doublon ». Mais en Tunisie un téléphone ou un e-mail est souvent partagé par une famille, un père et son fils peuvent porter le même nom, et un même prénom s'écrit de plusieurs façons (Mohamed, Mohammed). Rattacher un dossier au mauvais conducteur fausserait l'éligibilité et les pièces vérifiées : une fausse fusion est plus grave qu'un doublon. La loi 2004-63 et la conception compatible RGPD imposent par ailleurs l'export et l'effacement des données d'une personne, alors que les dossiers et la comptabilité doivent garder leurs références.

## Décision

**Clés normalisées**, en colonnes indexées : téléphone en E.164 (8 chiffres = Tunisie, sinon indicatif obligatoire ; espaces, points, tirets et « (0) » ignorés), e-mail en minuscules, nom de recherche (minuscules, sans accents ni ponctuation), clé de numéro de pièce (lettres et chiffres ASCII de « 12 ab-34567 »), clé de matricule fiscal.

**Retrouver ou créer** (`ICustomerRegistration.FindOrCreatePersonAsync`) :

1. une pièce courante de même type, même pays et même clé désigne la même personne, quel que soit le nom saisi (le résultat signale `NameDiffers`) ;
2. sinon, le même téléphone puis le même e-mail, **avec le même nom normalisé**, et à condition que la personne trouvée n'ait pas une autre pièce courante du type saisi (père et fils homonymes) ;
3. sinon une nouvelle personne est créée ; les personnes qui partagent le téléphone ou l'e-mail sont renvoyées (`SimilarPersonIds`) pour que l'agent vérifie.

Une personne retrouvée ne voit que ses champs vides complétés, jamais écrasés ; elle reçoit la pièce saisie si elle n'en avait aucune de ce type. La base garantit une pièce courante par (type, pays, clé) (index unique filtré), un payeur particulier par personne et une société par matricule fiscal ; téléphone et e-mail restent non uniques. Une pièce de même numéro corrige la pièce courante ; un autre numéro la remplace, l'ancienne restant comme historique.

**Recherche** : un seul champ ; un prédicat unique combine e-mail (début), téléphone (normalisé ou derniers chiffres), début de la clé de pièce et chaque mot du nom : chaque personne apparaît une fois, indépendamment de la collation SQL. Les personnes anonymisées n'apparaissent jamais.

**Droits des personnes** : permission `customers.privacy`, gérant seul.

- **Export** JSON (identité, coordonnées, pièces sans leurs images, consentements, comptes payeurs), journalisé `customers.person_exported`. La liste noire n'y figure que sur demande explicite : `TODO(spec)` portée du droit d'accès (article 32) pour une donnée jamais signalée au demandeur.
- **Anonymisation** journalisée `customers.person_anonymized` avec le motif saisi (sans donnée personnelle) : noms, date de naissance, nationalité, téléphone, e-mail et adresse effacés ; pièces, consentements et historique de liste noire supprimés, images supprimées par l'outbox ; l'identifiant reste pour les dossiers et la comptabilité (les factures gardent leur propre copie). Une personne sur liste noire doit d'abord en être retirée, avec un motif. L'opération est idempotente et irréversible.

Le journal d'audit ne contient pas de données personnelles : noms, nom de recherche, date de naissance, téléphone, e-mail, adresse, numéros de pièces et motifs de liste noire y sont masqués (`IsAuditSensitive`).

## Conséquences

- Des doublons restent possibles (translittérations, téléphone partagé sans nom identique) : l'agent les voit dans les résultats ; `TODO` écran de fusion de deux personnes.
- Le contrat est stable pour les dossiers (lot 1E) et le tunnel web (phase 2), qui appellent le même service.
- **Limite connue** : l'historique des tables temporelles (`customers.PersonsHistory`, `customers.IdentityDocumentsHistory`) garde les valeurs antérieures à l'anonymisation. L'application ne lit jamais cet historique pour les clients ; sa purge ciblée est une procédure d'exploitation, confiée au DBA (elle suspend la gestion des versions, opération DDL) tant que la durée de conservation de l'historique n'est pas fixée avec l'avocat (`TODO(spec)`) :

```sql
-- Personnes anonymisées : SELECT Id FROM customers.Persons WHERE AnonymizedAtUtc IS NOT NULL;
DECLARE @PersonId uniqueidentifier = '00000000-0000-0000-0000-000000000000';
BEGIN TRANSACTION;
ALTER TABLE customers.IdentityDocuments SET (SYSTEM_VERSIONING = OFF);
DELETE FROM customers.IdentityDocumentsHistory WHERE PersonId = @PersonId;
ALTER TABLE customers.IdentityDocuments SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = customers.IdentityDocumentsHistory, DATA_CONSISTENCY_CHECK = ON));
ALTER TABLE customers.Persons SET (SYSTEM_VERSIONING = OFF);
DELETE FROM customers.PersonsHistory WHERE Id = @PersonId;
ALTER TABLE customers.Persons SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = customers.PersonsHistory, DATA_CONSISTENCY_CHECK = ON));
COMMIT;
```

## Alternatives écartées

- **Fusion automatique sur le seul téléphone ou e-mail** : rattache le fils au dossier du père.
- **Rapprochement phonétique ou par distance d'édition** : « Amine » et « Amina » (frère et sœur) seraient fusionnés.
- **Suppression physique de la personne** : casse les références des dossiers et de la comptabilité.
- **Purge de l'historique temporel par l'application** : DDL à l'exécution, droits élevés et verrous de schéma sur des tables vivantes, pour une opération rare et à encadrer juridiquement.
