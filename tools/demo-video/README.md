# Vidéo de démonstration

Outil qui enregistre une vidéo commentée de la plateforme qui tourne en local :

1. **Vitrine** : recherche à l'aéroport Tunis-Carthage, choix d'une catégorie, options, conducteur, conditions générales et
   garantie sur la page de la banque simulée, confirmation.
2. **Back office** : connexion avec double authentification, tableau du jour, contrôle puis validation de la demande, dossier,
   planning.
3. **Visite** : départs et retours, clients, flotte, tarifs, WhatsApp, canaux, paiements, factures, caisse et exports
   comptables, indicateurs, administration.

Des sous-titres, le chapitre en cours et un curseur visible sont dessinés dans la page ; une voix off française (synthèse vocale
de Windows) est mixée avec ffmpeg. Chaque enregistrement crée un client fictif différent.

## Prérequis

- Windows avec une voix française installée (« Microsoft Hortense » par défaut : Paramètres › Heure et langue › Voix).
- `ffmpeg` dans le `PATH`.
- Chromium de Playwright, installé par les tests E2E (`dotnet test tests/CarRental.E2E.Tests` une fois) ou par
  `pwsh bin/Debug/net10.0-windows/playwright.ps1 install chromium`.
- L'application lancée en local avec les données de démonstration (voir le README racine) sur `https://localhost:7180`.

## Lancer

```bash
dotnet run --project tools/demo-video -- https://localhost:7180
```

Résultat dans `tools/demo-video/out/` (ignoré par git) : `demo-carrental.mp4` (1440 × 900, H.264 et AAC) et
`demo-carrental.srt` (sous-titres). Environ quatre minutes d'enregistrement, puis l'encodage.

Le scénario et les textes sont dans `Program.cs` (une étape = un texte dit et affiché, puis des gestes dans la page). Une étape
qui échoue enregistre une capture `out/failure.png` et le texte de la page.
