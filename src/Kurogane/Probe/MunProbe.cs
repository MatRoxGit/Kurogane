using System.Diagnostics;
using System.Text;
using Kurogane.Core;
using Kurogane.Native;

namespace Kurogane.Probe;

/// <summary>
/// Sonde de niveau 4 — catalogue des icônes stockées dans les fichiers
/// système, en LECTURE SEULE.
///
/// Pourquoi cette sonde existe : les six dossiers du profil affichés dans
/// l'Accès rapide gardent leurs icônes Windows, et quatre voies de
/// substitution par le registre ont été testées sans succès (voir le relevé
/// dans claude/mecanismes-icones-verifies.md). Les outils qui y parviennent
/// ne passent pas par le registre : ils remplacent les ressources à
/// l'intérieur des binaires du shell.
///
/// Avant d'envisager la moindre écriture, il faut savoir QUELLE ressource
/// correspond à QUELLE icône. C'est tout ce que fait cette commande : elle
/// énumère, reconstitue un .ico par groupe, et produit une planche de contact
/// pour que l'identification se fasse à l'œil plutôt qu'au pari.
///
/// Elle n'écrit que dans %LOCALAPPDATA%\Kurogane\probe-mun\. Aucun fichier
/// système n'est ouvert en écriture, et aucune élévation n'est demandée.
/// </summary>
internal static class MunProbe
{
    /// <summary>
    /// Les trois binaires qui portent l'essentiel des icônes du shell.
    /// Depuis Windows 10 1903, les ressources ont été déplacées dans un
    /// fichier compagnon .mun sous SystemResources ; le .dll d'origine reste
    /// en place, presque vide de ressources. On tente les deux, dans cet
    /// ordre, et on note lequel a répondu.
    /// </summary>
    private static readonly string[] Binaires = { "imageres", "shell32", "ddores" };

    internal static int Executer(Journal journal, string? filtre)
    {
        string racine = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kurogane", "probe-mun");

        journal.Step("Sonde des ressources d'icônes système");
        journal.Info("Lecture seule : les binaires sont ouverts en données, jamais en écriture.");
        journal.Info($"sortie : {racine}");
        journal.Blank();

        Directory.CreateDirectory(racine);

        var fiches = new List<Fiche>();

        // Sans filtre : les trois bibliothèques du shell. Avec un filtre : ce
        // qu'on demande, même hors de cette liste — un nom de binaire ou un
        // chemin complet. De quoi fouiller explorer.exe ou twinui sans
        // toucher au code.
        var aSonder = filtre is null ? Binaires : new[] { filtre };

        foreach (var nom in aSonder)
        {
            var fiche = Sonder(nom, racine, journal);
            if (fiche is not null) fiches.Add(fiche);
            journal.Blank();
        }

        if (fiches.Count == 0)
        {
            journal.Warn("Aucun binaire n'a pu être lu.");
            return 1;
        }

        string planche = Path.Combine(racine, "planche.html");
        File.WriteAllText(planche, Planche(fiches), new UTF8Encoding(false));

        journal.Step("Planche de contact");
        journal.Ok($"{fiches.Sum(f => f.Icones.Count)} groupes d'icônes extraits");
        journal.Info(planche);
        journal.Blank();
        journal.Info("Ouvre la planche, repère les six icônes de l'Accès rapide");
        journal.Info("(Bureau, Documents, Téléchargements, Images, Musique, Vidéos)");
        journal.Info("et relève leur identifiant. C'est cette correspondance qui");
        journal.Info("manque — le reste n'est que de la mécanique.");

        return 0;
    }

    private sealed record Fiche(
        string Nom,
        string Chemin,
        string Version,
        DateTime Modifie,
        List<(int Id, string Fichier, int Tailles)> Icones);

    private static Fiche? Sonder(string nom, string racine, Journal journal)
    {
        // Le fichier .mun est la source depuis 1903 ; le .dll System32 reste
        // le bon emplacement sur les versions antérieures.
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        string[] candidats = nom.Contains('\\') || nom.Contains('/')
            ? new[] { nom }                     // chemin complet fourni tel quel
            : new[]
            {
                Path.Combine(windows, "SystemResources", $"{nom}.dll.mun"),
                Path.Combine(windows, "SystemResources", $"{nom}.exe.mun"),
                Path.Combine(windows, "System32", $"{nom}.dll"),
                Path.Combine(windows, "System32", $"{nom}.exe"),
                Path.Combine(windows, $"{nom}.exe"),
            };

        foreach (string chemin in candidats)
        {
            using var module = ResourceNative.Module.Open(chemin);
            if (module is null)
            {
                journal.Info($"{Path.GetFileName(chemin)} : absent ou illisible");
                continue;
            }

            var ids = module.GroupIconIds();
            if (ids.Count == 0)
            {
                journal.Info($"{Path.GetFileName(chemin)} : aucun groupe d'icônes");
                continue;
            }

            var info    = FileVersionInfo.GetVersionInfo(chemin);
            var version = info.FileVersion ?? "inconnue";
            var modifie = File.GetLastWriteTime(chemin);

            journal.Step($"{Path.GetFileName(chemin)}");
            journal.Info($"version {version} — modifié le {modifie:dd/MM/yyyy}");
            journal.Ok($"{ids.Count} groupes d'icônes");

            string dossier = Path.Combine(racine,
                Path.GetFileName(nom).Replace(".", "_"));
            Directory.CreateDirectory(dossier);

            var extraites = new List<(int, string, int)>();
            int echecs = 0;

            foreach (int id in ids)
            {
                var (ico, tailles) = Reconstituer(module, id);
                if (ico is null) { echecs++; continue; }

                string fichier = $"{id}.ico";
                File.WriteAllBytes(Path.Combine(dossier, fichier), ico);
                extraites.Add((id, fichier, tailles));
            }

            if (echecs > 0)
                journal.Warn($"{echecs} groupes non reconstituables (ressource incomplète)");

            journal.Ok($"{extraites.Count} fichiers .ico écrits dans {dossier}");

            return new Fiche(nom, chemin, version, modifie, extraites);
        }

        journal.Warn($"{nom} : introuvable");
        return null;
    }

    // ------------------------------------------------------------------
    //  GRPICONDIR -> ICONDIR
    // ------------------------------------------------------------------
    //
    //  Les deux structures sont jumelles, à un champ près. Dans le module,
    //  chaque entrée se termine par un WORD nID qui désigne la ressource
    //  RT_ICON portant l'image. Dans un fichier .ico, ce champ est remplacé
    //  par un DWORD dwImageOffset : la position de l'image dans le fichier.
    //
    //  Reconstituer un .ico consiste donc à recopier l'en-tête, remplacer
    //  chaque nID par l'offset où l'on vient d'écrire l'image correspondante,
    //  et concaténer les images. Les octets des images sont repris tels
    //  quels : aucun décodage, aucune recompression, aucune perte.

    private const int EnteteDir     = 6;
    private const int EntreeGroupe  = 14;
    private const int EntreeFichier = 16;

    private static (byte[]? Ico, int Tailles) Reconstituer(ResourceNative.Module module, int id)
    {
        var groupe = module.Read(ResourceNative.RT_GROUP_ICON, id);
        if (groupe is null || groupe.Length < EnteteDir) return (null, 0);

        int nb = BitConverter.ToUInt16(groupe, 4);
        if (nb == 0 || groupe.Length < EnteteDir + nb * EntreeGroupe) return (null, 0);

        var images = new byte[nb][];
        for (int i = 0; i < nb; i++)
        {
            int o   = EnteteDir + i * EntreeGroupe;
            int nId = BitConverter.ToUInt16(groupe, o + 12);

            var image = module.Read(ResourceNative.RT_ICON, nId);
            if (image is null) return (null, 0);   // groupe incomplet : on l'écarte
            images[i] = image;
        }

        int entete = EnteteDir + nb * EntreeFichier;
        var ico = new byte[entete + images.Sum(b => b.Length)];

        ico[2] = 1;                                        // idType = 1 (.ico)
        BitConverter.GetBytes((ushort)nb).CopyTo(ico, 4);  // idCount

        int offset = entete;
        for (int i = 0; i < nb; i++)
        {
            int src = EnteteDir + i * EntreeGroupe;
            int dst = EnteteDir + i * EntreeFichier;

            // Les 12 premiers octets sont identiques dans les deux formats :
            // largeur, hauteur, couleurs, réservé, plans, bits, taille.
            Array.Copy(groupe, src, ico, dst, 12);
            BitConverter.GetBytes(offset).CopyTo(ico, dst + 12);

            images[i].CopyTo(ico, offset);
            offset += images[i].Length;
        }

        return (ico, nb);
    }

    // ------------------------------------------------------------------
    //  Planche de contact
    // ------------------------------------------------------------------

    private static string Planche(List<Fiche> fiches)
    {
        var sb = new StringBuilder();

        sb.Append("""
            <!DOCTYPE html>
            <html lang="fr">
            <head>
            <meta charset="utf-8">
            <title>Kurogane — catalogue des icônes système</title>
            <style>
              :root { color-scheme: dark; }
              body { margin:0; padding:24px; background:#0B0708; color:#E8E2E3;
                     font:14px/1.5 "Segoe UI",system-ui,sans-serif; }
              h1 { font-size:18px; letter-spacing:.08em; text-transform:uppercase;
                   color:#C8102E; margin:0 0 4px; }
              h2 { font-size:14px; letter-spacing:.06em; text-transform:uppercase;
                   color:#C8102E; margin:32px 0 2px; }
              .meta { color:#8A8083; font-size:12px; margin:0 0 16px;
                      font-family:Consolas,monospace; }
              .grille { display:grid; gap:8px;
                        grid-template-columns:repeat(auto-fill,minmax(96px,1fr)); }
              .case { background:#140D0E; border:1px solid #2A1C1F; border-radius:6px;
                      padding:10px 4px 6px; text-align:center; }
              .case:hover { border-color:#C8102E; }
              .case img { width:48px; height:48px; image-rendering:auto;
                          display:block; margin:0 auto 6px; }
              .id { font-family:Consolas,monospace; font-size:12px; color:#E8E2E3; }
              .n  { font-family:Consolas,monospace; font-size:10px; color:#6E6467; }
              .note { border-left:2px solid #C8102E; padding:8px 14px; margin:16px 0 0;
                      color:#B9AFB2; font-size:13px; max-width:70ch; }
            </style>
            </head>
            <body>
            <h1>Catalogue des icônes système</h1>
            <p class="meta">Extraction en lecture seule. L'identifiant sous chaque
            vignette est celui de la ressource dans le binaire.</p>

            """);

        foreach (var f in fiches)
        {
            sb.Append($"<h2>{Html(Path.GetFileName(f.Chemin))}</h2>\n");
            sb.Append($"<p class=\"meta\">{Html(f.Chemin)}<br>version {Html(f.Version)} — " +
                      $"modifié le {f.Modifie:dd/MM/yyyy HH:mm} — {f.Icones.Count} groupes</p>\n");
            sb.Append("<div class=\"grille\">\n");

            foreach (var (id, fichier, tailles) in f.Icones)
            {
                sb.Append($"""
                      <div class="case">
                        <img src="{Path.GetFileName(f.Nom).Replace(".", "_")}/{fichier}" alt="{id}" loading="lazy">
                        <div class="id">{id}</div>
                        <div class="n">{tailles} tailles</div>
                      </div>

                    """);
            }

            sb.Append("</div>\n");
        }

        sb.Append("""
            <p class="note">Une icône référencée ailleurs sous la forme
            <code>imageres.dll,-112</code> porte ici l'identifiant <code>112</code> :
            le signe moins indique un identifiant de ressource, pas un index de
            position.</p>
            </body>
            </html>
            """);

        return sb.ToString();
    }

    private static string Html(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
