# Kurogane

Personnalisation visuelle de Windows 11, entièrement réversible.

Kurogane applique un thème décrit dans un fichier JSON — mode sombre, couleur
d'accent, fond d'écran — en n'utilisant que des mécanismes documentés par
Microsoft. Aucun fichier système n'est modifié, aucune DLL n'est patchée.

> `Kurogane` (黒鉄, « fer noir ») est un nom de travail. Le thème livré,
> *Obsidian*, est une création originale : il ne reprend ni le nom, ni le logo,
> ni les éléments graphiques d'une œuvre existante.

## Le principe

**La simulation est le mode par défaut.** Sans `--commit`, rien n'est jamais
écrit — l'outil lit l'état actuel, affiche exactement ce qu'il changerait, et
s'arrête là.

Trois garanties, dans cet ordre :

1. **Lire avant d'écrire.** Chaque modification capture la valeur d'origine,
   y compris le cas « la valeur n'existait pas » — qui se restaure par une
   suppression, pas par l'écriture d'un zéro.
2. **Point de restauration write-ahead.** L'état d'origine complet est écrit
   sur disque *avant* la première modification. Une coupure en cours de route
   laisse donc un point de restauration exploitable.
3. **Retour arrière automatique.** Si une écriture échoue, les précédentes sont
   défaites dans l'ordre inverse avant que l'outil ne rende la main.

## Compiler

Visual Studio 2022 (17.8+) ou le SDK .NET 8 en ligne de commande :

```
dotnet build src/Kurogane/Kurogane.csproj -c Release
```

Cible : `net8.0-windows`, x64. Aucun paquet NuGet.

## Utiliser

```
# Voir ce qui serait modifié — n'écrit rien
kurogane apply --theme themes\kurogane-obsidian.json

# Appliquer
kurogane apply --theme themes\kurogane-obsidian.json --commit

# Lister les points de restauration
kurogane list

# Revenir en arrière (simulation, puis réel)
kurogane restore --last
kurogane restore --last --commit
```

Points de restauration : `%LOCALAPPDATA%\Kurogane\snapshots\`
Journaux : `%LOCALAPPDATA%\Kurogane\logs\`

Aucun droit administrateur n'est requis : tout se passe dans `HKEY_CURRENT_USER`.

## Le fichier de thème

```json
{
  "name": "Kurogane Obsidian",
  "version": "0.1.0",

  "darkMode":  { "apps": true, "system": true },

  "accent": {
    "color": "#C8102E",
    "titleBars": true,
    "taskbarAndStart": true,
    "transparency": false
  },

  "wallpaper": {
    "path": "../assets/wallpapers/obsidian-4k.png",
    "style": "fill"
  }
}
```

Toute section absente est simplement ignorée : un thème qui ne déclare que
`accent` ne touchera pas au fond d'écran. Les chemins relatifs sont résolus
depuis le dossier du fichier de thème.

Ajustements acceptés pour le fond d'écran : `fill`, `fit`, `stretch`,
`center`, `tile`, `span`.

## Architecture

```
src/Kurogane/
├── Core/
│   ├── RegistryChange.cs   Une écriture + sa capture + son inverse
│   ├── ChangeRecord.cs     Trace sérialisable d'une modification
│   ├── Snapshot.cs         Point de restauration (write-ahead, écriture atomique)
│   ├── Journal.cs          Journal console + fichier
│   └── ColorUtil.cs        Conversions ABGR / ARGB, nuancier d'accent
├── Modules/
│   ├── IThemeModule.cs     Plan() sans effet de bord, PostApply() notifie
│   ├── DarkModeModule.cs
│   ├── AccentModule.cs
│   └── WallpaperModule.cs
├── Native/
│   └── NativeMethods.cs    SystemParametersInfo, WM_SETTINGCHANGE, SHChangeNotify
├── Theme/
│   └── ThemeDefinition.cs  Modèle du fichier JSON
└── Program.cs              CLI
```

Pour ajouter un module : implémenter `IThemeModule` et l'inscrire dans le
tableau `Modules` de `Program.cs`. Le contrat est strict — `Plan()` ne doit
avoir aucun effet de bord. Tout ce qui écrit passe par les `RegistryChange`
retournés, donc par la capture et le point de restauration.

## Portée

Ce que l'outil fait, et ce qu'il ne fera pas :

| | |
|---|---|
| **Niveau 1** — `HKCU`, sans élévation | mode sombre, accent, fond d'écran, curseurs, barre des tâches |
| **Niveau 2** — `HKLM`, administrateur | substitution d'icônes système via `Shell Icons`, écran de verrouillage |
| **Niveau 3** — hors périmètre | `.msstyles` personnalisé, patch d'`uxtheme.dll`, modification d'`imageres.dll` |

Le niveau 3 exige de contourner la vérification de signature des thèmes ou de
prendre possession de fichiers système. Les deux cassent à chaque mise à jour
cumulative et ne se défont pas proprement : c'est incompatible avec la
première garantie du projet.

## État

v0.1.0 — modules mode sombre, accent et fond d'écran. Niveau 2 à venir.
