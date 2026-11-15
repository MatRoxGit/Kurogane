using System.Windows;
using System.Windows.Controls;
using Kurogane.Native;

namespace Kurogane.Theme;

/// <summary>
/// Transpose un menu lu chez le shell en menu Kurogane.
///
/// Sorti de l'explorateur le 15 novembre, quand la couche bureau a eu
/// besoin du même travail. Le recopier aurait créé deux rendus qui
/// divergent au premier ajustement — le même piège que les règles de
/// filtrage, réglé de la même façon.
///
/// Les gabarits viennent de <c>Theme/Menu.xaml</c>, que la fenêtre hôte
/// doit fusionner.
/// </summary>
internal static class MenuShellVue
{
    /// <summary>
    /// Construit le menu. <paramref name="choisir"/> est appelé au clic,
    /// mais l'exécution revient à l'appelant : un verbe qui ouvre une
    /// boîte de dialogue la verrait sinon s'afficher derrière un menu
    /// encore ouvert.
    /// </summary>
    internal static ContextMenu Construire(FrameworkElement hote,
                                           MenuShellNative shell,
                                           Action<EntreeShell> choisir)
    {
        var menu = new ContextMenu
        {
            Style = (Style)hote.FindResource("MenuKurogane"),
        };

        Garnir(menu.Items, hote, shell, shell.Racine(), choisir);

        return menu;
    }

    /// <summary>
    /// Les sous-menus sont garnis à l'ouverture, pas d'avance : le shell
    /// ne les remplit qu'au moment où il croit qu'on va les déplier, et
    /// tout déplier d'emblée coûterait à chaque clic droit ce que coûte
    /// « Envoyer vers ».
    /// </summary>
    private static void Garnir(ItemCollection cible, FrameworkElement hote,
                               MenuShellNative shell,
                               IReadOnlyList<EntreeShell> entrees,
                               Action<EntreeShell> choisir)
    {
        foreach (var entree in entrees)
        {
            if (entree.Separateur)
            {
                cible.Add(new Separator
                {
                    Style = (Style)hote.FindResource("SeparateurMenu"),
                });

                continue;
            }

            var element = new MenuItem
            {
                Style = (Style)hote.FindResource("EntreeMenu"),

                // Majuscules comme le reste de l'interface ; le souligné
                // est doublé, sinon WPF le prend pour une touche d'accès.
                Header = entree.Libelle.ToUpper().Replace("_", "__"),
                InputGestureText = entree.Raccourci,
                IsEnabled = entree.Actif,
                IsChecked = entree.Coche,
            };

            if (entree.SousMenu != IntPtr.Zero)
            {
                // Marque-place : sans enfant, WPF n'affiche pas la flèche
                // et ne déclenche jamais l'ouverture.
                element.Items.Add(new MenuItem
                {
                    Style = (Style)hote.FindResource("EntreeMenu"),
                    Header = "…",
                    IsEnabled = false,
                });

                element.SubmenuOpened += (expediteur, _) =>
                {
                    if (expediteur is not MenuItem parent) return;
                    if (parent.Tag is true) return;

                    parent.Tag = true;
                    parent.Items.Clear();

                    Garnir(parent.Items, hote, shell, shell.Deplier(entree), choisir);
                };
            }
            else
            {
                element.Click += (_, _) => choisir(entree);
            }

            cible.Add(element);
        }
    }
}
