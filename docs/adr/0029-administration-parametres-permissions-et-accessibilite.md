# ADR-0029 — Administration du back office : paramètres typés, permissions par utilisateur, sessions rafraîchies, MudBlazor accessible et traduit

- **Statut** : Acceptée
- **Date** : 2026-10-20
- **Source** : [spécifications](../SPEC.md) section 3 (« la permission Tarification peut être confiée à un agent ») et section 14, [ADR-0014](0014-identite-2fa-permissions.md), [ADR-0015](0015-localisation-routes-par-culture.md), [ADR-0025](0025-moteur-de-disponibilite-cache-et-ecriture-sous-verrou.md), [prompt](../PROMPT.md) sections 10 et 11

## Contexte

Le socle déclarait des paramètres métier typés et des permissions fines, sans écran pour les modifier : `/admin/settings` était un écran « À venir », et les permissions supplémentaires par utilisateur, prévues par [ADR-0014](0014-identite-2fa-permissions.md), n'avaient ni écran ni prise d'effet définie. Les écrans MudBlazor avaient trois défauts d'accessibilité et de langue : couleurs d'état sous 4,5:1 (puces, alertes, boutons colorés), liste des tailles de page de `MudTablePager` sans nom accessible, textes propres à MudBlazor en anglais. Enfin, deux connexions simultanées d'un même utilisateur pouvaient répondre une erreur 500.

## Décision

### Paramètres

- **Définition typée** : chaque `SettingDefinition` porte son éditeur (`SettingEditor` : genre et bornes, au format de stockage) donné par sa fabrique — durée, nombre, entier, montant, booléen, texte, plage horaire (`DailyTimeRange`, `08:00-18:00`) ; une définition écrite à la main reçoit l'éditeur de son type. La clé est validée à la déclaration (même règle que la ligne stockée, vérifiée par un test : les contrats ne peuvent pas appeler le domaine) ; son premier segment est le groupe affiché.
- **Portée** : par défaut, une agence peut remplacer la valeur de la plateforme ; `.PlatformOnly()` déclare une valeur unique pour toute la plateforme (règle technique ou nationale, lue sans agence) : le lecteur ignore alors toute ligne d'agence, le service refuse d'en créer (`referential.setting_platform_only`) et l'écran n'en propose pas. `outbox.processed_retention` est dans ce cas.
- **Libellés dérivés de la clé** dans `Resources/Admin/Settings/SettingLabels(.en).resx` : `Setting.<clé>.Label`, `Setting.<clé>.Help`, `Group.<segment>`. Sans libellé, l'écran affiche la clé. Un test d'architecture lit le catalogue réel (`AddApplication()`) au moment du test et exige libellé et titre de groupe en français et en anglais : un module qui déclare un paramètre ajoute ses textes, le message d'échec dit lesquels.
- **Écran `/admin/settings`** (`settings.manage`) : par groupe, la valeur par défaut, la valeur de la plateforme et celle de l'agence choisie (sélecteur d'agence : le modèle accepte plusieurs agences). Modifier ou rétablir la valeur de la plateforme (rétablir = revenir à la valeur par défaut, la ligne reste) ; définir ou retirer la valeur d'une agence (retirer supprime la ligne, la table temporelle garde l'historique, l'événement `SettingRemoved` part par l'outbox). La définition valide chaque valeur.
- **Audit** : différence de ligne automatique ([ADR-0018](0018-journal-audit-ajout-seul.md)) et événement lisible `referential.setting_changed` ou `referential.setting_reset` (« `fleet.preparation_buffer [Agence Démo Tunis]: — → 03:00:00` »). Une écriture concurrente (version de ligne, ou première valeur créée deux fois pour la même portée : index unique) devient l'erreur `referential.setting_concurrent_update`.
- **Disponibilités** : **chaque** changement de paramètre appelle `IAvailabilityChangeNotifier.AllChanged()` après la validation. Le cache ([ADR-0025](0025-moteur-de-disponibilite-cache-et-ecriture-sous-verrou.md)) ne sait pas quels paramètres son chargeur a lus (tampon de préparation, durée de blocage, quotas à venir) ; les paramètres changent rarement et un rechargement coûte peu, une disponibilité périmée coûte une surréservation.

### Permissions par utilisateur

- **Revendications de l'utilisateur** (`permission`), en plus de celles de ses rôles ; jamais touchées par le réalignement des préréglages. `/admin/users` les affiche à part des rôles et les modifie dans une boîte de dialogue par domaine fonctionnel ; chaque octroi et chaque retrait est audité (`identity.permission_granted`, `identity.permission_revoked`, plus la différence de ligne `UserClaim`). Les permissions déjà données par le rôle ne sont pas stockées ; personne ne modifie ses propres permissions ; le gérant, qui a tout, n'en a pas besoin.
- **Inclusion déclarée** dans le catalogue : gérer inclut consulter (`pricing.manage` inclut `pricing.read`, `invoices.manage` inclut l'émission et la consultation, `cash.manage` inclut sa caisse…). La fabrique de l'identité ajoute les permissions incluses aux revendications de connexion : pages et services ne vérifient que la permission dont ils ont besoin. Les préréglages des rôles sont déjà fermés pour l'inclusion (test).
- **Prise d'effet à la requête suivante, sans nouvelle connexion** : après un changement, `IUserSessions.RefreshClaims(utilisateur)` inscrit l'utilisateur dans un registre en mémoire ; le validateur du tampon de sécurité (`CarRentalSecurityStampValidator`) revalide aussitôt tout cookie émis avant le changement : revendications reconstruites depuis la base et cookie renouvelé (la date d'émission du ticket est conservée : le gestionnaire de cookie ne renouvelle qu'un ticket daté, test). Le registre est en mémoire parce que l'application tourne sur une seule instance ([ADR-0004](0004-hebergement-app-service-b1-cloudflare.md), comme le cache de l'[ADR-0025](0025-moteur-de-disponibilite-cache-et-ecriture-sous-verrou.md)) ; la revalidation périodique (chaque minute) rafraîchit de toute façon les revendications, sur n'importe quelle instance et après un redémarrage. Le tampon de sécurité n'est pas changé : il fermerait les sessions.
- **Compte verrouillé** : la validation du cookie refuse aussi un compte verrouillé, même par des échecs de connexion (le tampon reste alors inchangé) : la session se ferme, comme le circuit.
- **Méthode d'authentification conservée** : le validateur reconstruisait le principal sans la revendication `amr` ; une minute après la connexion, toute requête HTTP du back office redemandait la connexion (stratégie MFA). `OnRefreshingPrincipal` recopie désormais `amr` et la méthode d'authentification.
- **Circuits ouverts** : la revalidation des circuits (toutes les 2 minutes) refuse aussi une session qui détient une permission retirée ; le circuit perd son authentification, inscrit l'utilisateur au registre, et `NotAuthorizedRedirect` recharge la page en HTTP, où le cookie est donc revalidé aussitôt : l'utilisateur continue avec ses permissions actuelles, ou part vers la connexion ou l'accès refusé. Une permission accordée apparaît au prochain chargement de page, sans interrompre l'utilisateur.

### Connexions concurrentes

- La date de dernière connexion s'écrit par une mise à jour ciblée et monotone (sans jeton de concurrence) ; les modifications laissées par une mise à jour d'Identity en échec (compteur d'échecs mis à jour par une connexion parallèle) sont écartées avant d'enregistrer l'événement d'audit. Chaque connexion aboutit et est auditée (`SignInRecorder`, désormais dans `Infrastructure/Identity`).

### Accessibilité et textes de MudBlazor

- **Palette `AdminTheme`** (mode clair ; pas de mode sombre, à vérifier de la même façon avant de l'offrir) : primaire, secondaire, tertiaire, information, succès, avertissement et erreur, avec leurs nuances foncée et claire, à 4,5:1 au moins en texte blanc sur la couleur, en texte coloré sur blanc, sur le fond de page et sur la teinte à 6 % : les variantes pleines, contour et texte des boutons, puces et alertes s'utilisent sans contournement.
- **`AdminTablePager`** remplace `MudTablePager` dans `PagerContent` : liste des tailles nommée (un `MudSelect` nomme sa liste par son `Label`), information de page et boutons de `MudTablePager`. Un test d'architecture refuse un `MudTablePager` sans `HideRowsPerPage="true"` hors de ce composant. `admin.css` masque le bouton de titre vide d'un calendrier sans valeur ; le menu ne crée plus deux repères de navigation.
- **Textes intégrés** : `MudBlazorLocalizer` branche le crochet de localisation de MudBlazor sur `Resources/Admin/MudBlazor/MudBlazorResources(.en).resx`, clés égales aux noms de `LanguageResource` ; un test échoue si une mise à jour de MudBlazor ajoute ou retire un texte.
- **Référence vivante** `/admin/ui-gallery` (absente en production) : chaque couleur en boutons, puces et alertes, champs usuels, calendriers, tableau paginé ; tests de bout en bout axe sur téléphone et tablette, en français et en anglais. L'élément `main` porte `data-interactive` pour que les tests attendent le circuit.

## Conséquences

- Un paramètre ou une permission ajoutés par un module apparaissent sans code d'écran ; leurs libellés sont exigés par les tests.
- Pour l'agent à qui l'on confie la tarification : la page `/admin/pricing` s'ouvre à sa requête suivante, et se referme à sa requête suivante quand la permission est retirée (test d'intégration de bout en bout sur HTTP).
- Un circuit garde une permission retirée deux minutes au plus, comme pour un compte verrouillé ; les services revérifient sur l'instantané du circuit pendant ce délai.
- Les écrans des modules suivants héritent de la palette, du pager, des textes traduits et de la recette documentée dans `CLAUDE.md`.

## Alternatives écartées

- **Libellés dans le code des définitions** : textes d'interface hors des ressources, sans traduction ([ADR-0015](0015-localisation-routes-par-culture.md)).
- **Ne vider le cache que pour les paramètres « de disponibilité »** : un paramètre oublié servirait des disponibilités périmées.
- **Changer le tampon de sécurité après chaque changement de permission** : l'utilisateur devrait se reconnecter (mot de passe et code) pour recevoir une permission.
- **Revérifier les revendications en base à chaque requête** : une requête SQL par requête HTTP, ressources statiques comprises.
- **Inclusion déduite des noms** (`*.manage` inclut `*.read`) : implicite et fausse pour `cash.manage`, `invoices.issue` ou les pièces d'identité.
- **Contournements écran par écran** (bloc `Notice`, pager sans liste des tailles) : chaque nouvel écran referait l'erreur ; ils restent possibles.
- **Composants enveloppes pour chaque composant MudBlazor** : coût de maintenance disproportionné ; la palette et le pager suffisent.
