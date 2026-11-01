using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kurogane.Core;
using Kurogane.Theme;

namespace Kurogane.Barre;

public partial class FenetreBarre : Window
{
    private readonly BarreSection _config;
    private readonly ThemeDefinition _theme;
    private readonly Journal _journal;
    private DispatcherTimer? _horloge;

    public FenetreBarre(ThemeDefinition theme, BarreSection config, Journal journal)
    {
        _theme   = theme;
        _config  = config;
        _journal = journal;

        InitializeComponent();

        if (config.Noeud is { } n) Noeud.Text = n;
        if (config.Etat  is { } e) Etat.Text  = e;

        BoutonDemarrer.Click  += (_, _) => AppBarNative.Combinaison(AppBarNative.VK_LWIN);
        BoutonRecherche.Click += (_, _) => AppBarNative.Combinaison(AppBarNative.VK_LWIN,
                                                                    AppBarNative.VK_S);

        RemplirEpingles();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

            // Hors Alt+Tab, et surtout sans vol de focus : cliquer sur une
            // barre des tâches ne désactive jamais l'application en cours.
            AppBarNative.AjouterStyles(hwnd,
                AppBarNative.WS_EX_TOOLWINDOW | AppBarNative.WS_EX_NOACTIVATE);
        };

        Loaded += (_, _) => DemarrerHorloge();
        Closed += (_, _) => _horloge?.Stop();
    }

    private void DemarrerHorloge()
    {
        void Battre()
        {
            var maintenant = DateTime.Now;
            Heure.Text = maintenant.ToString("HH:mm:ss");

            string suffixe = _config.SuffixeDate is { } s ? $" // {s}" : "";
            DateDuJour.Text = maintenant.ToString("yyyy.MM.dd") + suffixe;
        }

        Battre();

        _horloge = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _horloge.Tick += (_, _) => Battre();
        _horloge.Start();
    }

    private void RemplirEpingles()
    {
        if (_config.Epingles is not { Count: > 0 } liste) return;

        foreach (var e in liste)
        {
            if (e.Commande is not { Length: > 0 } commande) continue;

            var bouton = new Button
            {
                Style   = (Style)FindResource("Cellule"),
                Width   = 42,
                Height  = 38,
                Padding = new Thickness(0),
                ToolTip = e.Libelle ?? commande,
                Content = Contenu(e),
            };

            bouton.Click += (_, _) => Lancer(commande, e.Arguments);
            Epingles.Children.Add(bouton);
        }
    }

    /// <summary>
    /// Une icône si le thème en désigne une et qu'elle se charge, sinon le
    /// libellé. Un bouton sans contenu serait pire qu'un bouton textuel.
    /// </summary>
    private object Contenu(EpingleBarre e)
    {
        if (e.Icone is { Length: > 0 } fichier)
        {
            try
            {
                string chemin = _theme.ResolveIcon(fichier);

                if (File.Exists(chemin))
                    return new Image
                    {
                        Source  = new BitmapImage(new Uri(chemin)),
                        Width   = 22,
                        Height  = 22,
                        Stretch = Stretch.Uniform,
                    };

                _journal.Warn($"épingle « {e.Libelle} » : {chemin} est introuvable");
            }
            catch (Exception ex)
            {
                _journal.Warn($"épingle « {e.Libelle} » : {ex.Message}");
            }
        }

        return new TextBlock
        {
            Text       = (e.Libelle ?? "?")[..Math.Min(3, (e.Libelle ?? "?").Length)].ToUpperInvariant(),
            Foreground = (Brush)FindResource("Rouge"),
            FontFamily = (FontFamily)FindResource("Mono"),
            FontSize   = 10,
        };
    }

    private void Lancer(string commande, string? arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = commande,
                Arguments       = arguments ?? "",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _journal.Error($"lancement de « {commande} » : {ex.Message}");
        }
    }
}
