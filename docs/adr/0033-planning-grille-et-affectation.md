# ADR-0033 — Planning : grille CSS sur mesure, affectation au clavier ou par glisser-déposer, propositions revues

- **Statut** : Acceptée
- **Date** : 2026-10-21
- **Source** : [spécifications](../SPEC.md) §6 (« Planning de la flotte »), [conception phase 1](../conception/phase-1-exploitation.md) §3, [prompt](../PROMPT.md) §6 (invariants 1 à 3), §10 (WCAG 2.2 AA) et §11 (« composant maison en grille CSS, glisser-déposer, refus serveur des conflits », tablette de 10 pouces), [ADR-0002](0002-front-blazor-mudblazor.md), [ADR-0020](0020-navigation-classique-et-focus.md), [ADR-0025](0025-moteur-de-disponibilite-cache-et-ecriture-sous-verrou.md)

## Contexte

L'agence travaille sur une vue véhicules × jours : qui part et revient quand, quels dossiers n'ont pas encore de véhicule, quels véhicules sont immobilisés, où la capacité manque. L'agent affecte un véhicule à un dossier, en échange un pendant la location, et demande au système de proposer des affectations. L'écran doit fonctionner sur une tablette de 10 pouces, au clavier et avec un lecteur d'écran, sous la politique de sécurité du contenu du back office (scripts du seul domaine, pas de script en ligne). MudBlazor n'a pas de diagramme de Gantt.

## Décision

### Une grille CSS rendue par le serveur

- `PlanningGrid` dessine une **grille CSS** : une colonne d'étiquettes collante et une piste par ligne, découpée en jours (fond répété, aujourd'hui mis en évidence). Vue **semaine** (7 jours à partir du lundi, 9 rem par jour) ou **mois** (3,25 rem par jour) ; défilement horizontal dans une région focalisable. L'adresse accepte `?from=AAAA-MM-JJ&view=month` (liens du tableau de bord vers un manque de capacité).
- Par catégorie : une ligne d'en-tête qui montre les **manques de capacité** (segments hachurés avec leur texte), une **voie des dossiers non affectés** (sous-voies quand ils se chevauchent, pour qu'aucun ne cache l'autre), puis une ligne par véhicule.
- Chaque dossier est une **barre** placée en pourcentage de la plage : de son départ (réel ou prévu) au retour du véhicule (réel, prévu, ou maintenant s'il est en retard), suivie du **tampon de préparation** dessiné à part. Les **indisponibilités** du véhicule (maintenance ou hors service, documents non valides) sont des zones hachurées avec un nom accessible.
- Le statut se lit par le **texte, une icône et un motif** (hachures pour une demande, rayures pour une option, bordure épaisse en cours, pointillés une fois rendu), jamais par la seule couleur ; le texte garde un contraste d'au moins 4,5:1 ; un liseré signale un retard ; une légende explique tout. Les propriétés CSS logiques préparent la mise en page de droite à gauche.
- Les données viennent de `IBookingsQueries.GetPlanningAsync` : dossiers et fenêtres de la flotte relus en base, profil de capacité du moteur de disponibilité ; elles sont chargées une fois l'écran interactif.

### Affectation

- Chaque barre est un **bouton** (nom accessible : numéro, client, statut, période, retard, modèle garanti, dérogation). Un clic, un toucher ou `Entrée` sélectionne le dossier : un panneau d'actions s'ouvre au-dessus de la grille et **reçoit le focus** (ouvrir le dossier, attribuer ou échanger un véhicule, retirer le véhicule). C'est le chemin principal sur tablette et au clavier.
- Le dialogue d'affectation liste les véhicules de la catégorie avec un **précontrôle** (libre, déjà occupé, documents non valides, indisponible) ; un gérant peut déroger aux documents avec un motif ; un échange pendant la location exige un motif.
- À la souris, une barre d'un dossier pas encore parti se **glisse** (glisser-déposer HTML5) sur la ligne d'un véhicule pour l'affecter, ou sur la voie de sa catégorie pour retirer le véhicule. Une dérogation passe toujours par le dialogue.
- Dans tous les cas, **le serveur décide** : `VehicleAssignmentService` affecte sous le verrou de la catégorie, sur des données fraîches (chevauchement tampon compris, documents, fenêtres) ; l'écran ne fait qu'afficher le résultat ou l'erreur.

### Propositions

- « Proposer des attributions » lance l'**affecteur glouton** ([ADR-0025](0025-moteur-de-disponibilite-cache-et-ecriture-sous-verrou.md)) sur la partie à venir de la plage affichée. Le dialogue liste les propositions (cochées par défaut), les dossiers impossibles à placer et les affectations fixes en conflit, avec leur motif. L'agent décoche ce qu'il refuse ; chaque proposition retenue est appliquée **une par une sous le verrou** (une proposition refusée entre-temps n'empêche pas les autres) et le résultat s'affiche ligne par ligne.

## Conséquences

- Aucune dépendance JavaScript ni licence : la grille est du HTML et du CSS, testable par Playwright et axe (tests de bout en bout : affectation au clavier, aucune violation grave ou critique).
- Le glisser-déposer HTML5 ne fonctionne pas au toucher ni, pour les boutons, sous Firefox : le chemin sélection puis dialogue reste toujours disponible et suffit.
- Une plage d'un mois pour 30 véhicules reste légère (quelques centaines d'éléments) ; au-delà de quelques centaines de véhicules, il faudrait virtualiser les lignes.
- Le planning ne lit pas le cache pour les dossiers : il montre l'état exact au chargement ; il se recharge après chaque action.

## Alternatives écartées

- **Composant de Gantt commercial** : licence, accessibilité au clavier et au lecteur d'écran incertaine, style à réconcilier avec MudBlazor.
- **Bibliothèque JavaScript de calendrier** : dépendance et politique de sécurité du contenu à assouplir, état dupliqué entre le navigateur et le circuit Blazor.
- **Canvas ou SVG** : rendu rapide, mais chaque barre devrait recréer à la main le focus, le nom accessible et le comportement de bouton.
- **Tableau HTML jours × véhicules** : une cellule par jour ne montre ni les heures de départ et de retour, ni le tampon, ni plusieurs dossiers dans la même journée.
