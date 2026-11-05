using System.Collections.Specialized;
using System.IO;
using System.Windows;

namespace Kurogane.Explorateur;

/// <summary>
/// Couper, copier et coller des fichiers.
///
/// Windows ne distingue pas couper de copier par deux presse-papiers
/// différents : les deux posent la même liste de fichiers, et un marqueur
/// « Preferred DropEffect » dit lequel des deux. Sans ce marqueur, un
/// couper se comporte comme un copier, et l'original reste en place sans
/// que rien ne le signale.
///
/// En passant par ce format, le presse-papiers est partagé avec
/// l'Explorateur de Windows : on peut couper ici et coller là-bas.
/// </summary>
internal static class PressePapiers
{
    private const string Effet = "Preferred DropEffect";

    private const int Copie = 5;   // DROPEFFECT_COPY | DROPEFFECT_LINK
    private const int Deplacement = 2; // DROPEFFECT_MOVE

    internal static void Poser(IEnumerable<string> chemins, bool couper)
    {
        var liste = new StringCollection();
        foreach (var c in chemins) liste.Add(c);

        if (liste.Count == 0) return;

        var donnees = new DataObject();
        donnees.SetFileDropList(liste);

        var marqueur = new MemoryStream(4);
        marqueur.Write(BitConverter.GetBytes(couper ? Deplacement : Copie));
        donnees.SetData(Effet, marqueur);

        // true : le contenu survit à la fermeture de Kurogane, comme on
        // l'attend d'un presse-papiers.
        Clipboard.SetDataObject(donnees, true);
    }

    internal sealed record Contenu(IReadOnlyList<string> Chemins, bool Couper);

    internal static Contenu? Lire()
    {
        try
        {
            if (!Clipboard.ContainsFileDropList()) return null;

            var liste = Clipboard.GetFileDropList();
            if (liste.Count == 0) return null;

            var chemins = new List<string>(liste.Count);
            foreach (string? c in liste) if (c is not null) chemins.Add(c);

            bool couper = false;

            if (Clipboard.GetData(Effet) is MemoryStream flux && flux.Length >= 4)
            {
                var tampon = new byte[4];
                flux.Position = 0;
                _ = flux.Read(tampon, 0, 4);

                // Le drapeau MOVE peut être combiné à d'autres : on teste le
                // bit, on ne compare pas la valeur entière.
                couper = (BitConverter.ToInt32(tampon) & Deplacement) != 0;
            }

            return new Contenu(chemins, couper);
        }
        catch (Exception)
        {
            // Le presse-papiers appartient à tout le monde : une autre
            // application peut le tenir au moment où on le lit. Ce n'est pas
            // une anomalie, c'est une course normale.
            return null;
        }
    }

    internal static bool Disponible()
    {
        try { return Clipboard.ContainsFileDropList(); }
        catch { return false; }
    }
}
