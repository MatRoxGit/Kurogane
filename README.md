# Kurogane

Personnalisation visuelle de Windows 11, entièrement réversible.

Kurogane applique un thème décrit dans un fichier JSON — mode sombre, couleur
d'accent, fond d'écran, icônes du shell — en n'utilisant que des mécanismes
documentés par Microsoft : clés de registre et `desktop.ini`. Aucun fichier
système n'est remplacé, aucune DLL n'est patchée.

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

# Forcer le shell à relire les icônes
kurogane refresh-icons           # notification + ie4uinit
kurogane refresh-icons --purge    # purge du cache, redémarre explorer
```

Un `.ico` remplacé sans changer de nom ne produit aucune modification de
registre : le registre stocke un chemin, pas un contenu. C'est le rôle de
`refresh-icons --purge`, le cache d'icônes survivant à un redémarrage
d'explorer.

Points de restauration : `%LOCALAPPDATA%\Kurogane\snapshots\`
Journaux : `%LOCALAPPDATA%\Kurogane\logs\`

Les modules de niveau 1 (mode sombre, accent, fond d'écran, icônes du Bureau,
dossiers du profil) n'exigent aucune élévation. Les icônes génériques — dossier,
lecteur, flèche de raccourci — écrivent dans `HKEY_LOCAL_MACHINE` et demandent
une invite administrateur. Sans élévation, l'outil l'annonce avant de poser le
point de restauration et n'écrit rien : la permission est vérifiée clé par clé,
l'élévation ne la garantissant pas.

## Le fichier de thème

```json
{
  "name": "Kurogane Obsidian",
  "version": "0.3.1",

  "darkMode":  { "apps": true, "system": true },

  "accent": {
    "color": "#C8102E",
    "titleBars": true,
    "taskbarAndStart": true,
    "transparency": false
  },

  "wallpaper": {
    "path": "../assets/wallpapers/Wallpaper_Kurogane.jpg",
    "style": "fill"
  },

  "icons": {
    "directory": "../assets/icons",
    "cePc": "ce-pc.ico",
    "corbeilleVide": "corbeille-vide.ico",
    "corbeillePleine": "corbeille-pleine.ico",
    "dossier": "dossier.ico",
    "lecteur": "lecteur.ico",
    "raccourciFleche": "raccourci-fleche.ico"
  }
}
```

La section `icons` accepte quinze entrées ; l'exemple n'en montre que six.
Chaque entrée est facultative et une icône absente laisse celle de Windows en
place.

Toute section absente est simplement ignorée : un thème qui ne déclare que
`accent` ne touchera pas au fond d'écran. Les chemins relatifs sont résolus
depuis le dossier du fichier de thème.

Ajustements acceptés pour le fond d'écran : `fill`, `fit`, `stretch`,
`center`, `tile`, `span`.

## Architecture

```
src/Kurogane/
├── Core/
│   ├── ChangeRecord.cs      Trace sérialisable d'une modification, et son inverse
│   ├── RegistryChange.cs    Écriture de registre : capture, contrôle d'accès, inverse
│   ├── FileChange.cs        Écriture de fichier : contenu, attributs, fusion desktop.ini
│   ├── OwnershipChange.cs   Prise de possession d'une clé protégée (voir plus bas)
│   ├── Privileges.cs        SeTakeOwnership / SeRestore
│   ├── Snapshot.cs          Point de restauration (write-ahead, écriture atomique)
│   ├── Journal.cs           Journal console + fichier
│   ├── IconCache.cs         Rafraîchissement et purge du cache d'icônes
│   ├── IcoWriter.cs         Écriture de .ico multi-résolution, sans dépendance
│   ├── Elevation.cs         Droit d'écriture effectif dans HKLM
│   └── ColorUtil.cs         Conversions ABGR / ARGB, nuancier d'accent
├── Modules/
│   ├── IThemeModule.cs      Plan() sans effet de bord, PostApply() notifie
│   ├── DarkModeModule.cs
│   ├── AccentModule.cs
│   ├── WallpaperModule.cs
│   ├── DesktopIconsModule.cs    Icônes du Bureau            (HKCU)
│   ├── SpecialFoldersModule.cs  Dossiers du profil          (desktop.ini)
│   └── ShellIconsModule.cs      Dossier, lecteur, raccourci (HKLM)
├── Probe/
│   ├── IconProbe.cs         Diagnostic : quelles cibles répondent
│   └── OwnershipProbe.cs    Diagnostic : clés protégées
├── Native/
│   └── NativeMethods.cs     SystemParametersInfo, WM_SETTINGCHANGE, SHChangeNotify
├── Theme/
│   └── ThemeDefinition.cs   Modèle du fichier JSON
└── Program.cs               CLI
```

Pour ajouter un module : implémenter `IThemeModule` et l'inscrire dans le
tableau `Modules` de `Program.cs`. Le contrat est strict — `Plan()` ne doit
avoir aucun effet de bord. Tout ce qui écrit passe par les objets `IChange`
retournés, donc par la capture et le point de restauration. Deux natures de
modification cohabitent, registre et fichier, sous le même contrat.

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

## Niveau 3 — prise de possession : disponible, non utilisé

Le dépôt contient `OwnershipChange` et `Privileges`, capables de prendre
possession d'une clé de registre protégée et de restituer son descripteur de
sécurité complet. **Aucun module de thème ne les appelle**, et ils ne sont
atteignables que par la commande de diagnostic `probe-ownership`, qui exige
deux autorisations explicites :

```
kurogane probe-ownership --commit --autoriser-prise-de-possession
```

Pourquoi ce code existe et pourquoi il ne sert pas : les six dossiers du
profil affichés dans l'Accès rapide gardent leurs icônes Windows, et les clés
qui semblaient les piloter sont protégées en écriture. L'hypothèse a été
testée le 31/10/2026 en prenant possession des deux familles candidates
(`SOFTWARE\Classes\CLSID\{CLSID}\DefaultIcon` et
`...\Explorer\FolderDescriptions\{FOLDERID}\Icon`) et en y écrivant une icône
témoin. **Aucun effet.** Ces icônes viennent d'ailleurs.

Précision utile : ces clés appartiennent à `NT AUTHORITY\SYSTEM`, pas à
TrustedInstaller. C'est la liste d'accès qui bloque l'écriture, TrustedInstaller
y détenant le contrôle total. La formule courante « clés TrustedInstaller »
confond propriété et droit d'écriture.

Si tu envisages de réutiliser ce code : une mise à jour cumulative de Windows
peut réécrire ces clés et réinitialiser leurs permissions, et un outil qui
modifie la sécurité du système sur la machine d'un tiers est à considérer avec
la plus grande prudence.

## État

v0.3.1 — mode sombre, accent, fond d'écran, et 15 icônes réparties sur trois
mécanismes : registre utilisateur, registre machine, `desktop.ini`.

Limite connue : les six dossiers du profil affichés dans l'Accès rapide
gardent leurs icônes Windows. Quatre voies ont été testées et écartées, le
relevé est dans `claude/mecanismes-icones-verifies.md`.

Dette technique à régler avant tout empaquetage : les chemins d'icônes écrits
dans le registre et dans les `desktop.ini` sont absolus et pointent vers le
dossier de travail. Ils devront viser `%LOCALAPPDATA%\Kurogane\icons\`.
