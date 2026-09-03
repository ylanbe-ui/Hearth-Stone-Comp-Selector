using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace HDTShopWishlistInstaller
{
    internal static class Program
    {
        private static int Main()
        {
            Console.Title = "HDT Shop Wishlist Overlay - Installation";
            Console.WriteLine("============================================");
            Console.WriteLine("  HDT Shop Wishlist Overlay - Installation");
            Console.WriteLine("============================================");
            Console.WriteLine();

            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string dllSource = Path.Combine(exeDir, "HDT-Shop-Wishlist-Overlay.dll");
            if (!File.Exists(dllSource))
            {
                Fail("HDT-Shop-Wishlist-Overlay.dll est introuvable a cote de Install.exe.\n"
                    + "Assure-toi d'avoir garde tous les fichiers ensemble dans le meme dossier.");
                return 1;
            }

            string pluginDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "HearthstoneDeckTracker", "Plugins");
            Directory.CreateDirectory(pluginDir);

            Console.WriteLine("Fermeture de Hearthstone Deck Tracker s'il est ouvert...");
            CloseHdtIfRunning();

            Console.WriteLine("Copie du plugin vers : " + pluginDir);
            try
            {
                CopyFileOverwrite(dllSource, Path.Combine(pluginDir, "HDT-Shop-Wishlist-Overlay.dll"));
                string scrySource = Path.Combine(exeDir, "untapped-scry-dotnet.dll");
                if (File.Exists(scrySource))
                    CopyFileOverwrite(scrySource, Path.Combine(pluginDir, "untapped-scry-dotnet.dll"));

                string assetsSource = Path.Combine(exeDir, "Assets");
                if (Directory.Exists(assetsSource))
                    CopyDirectoryOverwrite(assetsSource, Path.Combine(pluginDir, "Assets"));
                else
                    Console.WriteLine("AVERTISSEMENT: dossier Assets introuvable, icones/badges ignores (cosmetique seulement).");
            }
            catch (Exception ex)
            {
                Fail("La copie du plugin a echoue : " + ex.Message);
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine("Plugin installe avec succes !");
            Console.WriteLine();
            Console.WriteLine("Le rail (paliers de tavern), le surlignage de boutique et le builder de");
            Console.WriteLine("comp fonctionnent tout de suite, sans rien de plus.");
            Console.WriteLine();
            Console.WriteLine("Hearthstone Deck Tracker se relance toujours en administrateur : ca garde");
            Console.WriteLine("le bouton \"Skip Combat\" du rail toujours disponible.");
            Console.WriteLine();

            string hdtExe = FindInstalledHdtExe();
            Console.Write("Relancer Hearthstone Deck Tracker maintenant ? (o/n) : ");
            string answer = (Console.ReadLine() ?? string.Empty).Trim();
            if (string.Equals(answer, "o", StringComparison.OrdinalIgnoreCase))
            {
                if (hdtExe == null)
                {
                    Console.WriteLine("Impossible de trouver Hearthstone Deck Tracker automatiquement. Relance-le toi-meme.");
                }
                else
                {
                    try
                    {
                        // This process is already elevated (see app.manifest), so the child
                        // inherits that elevation automatically - no ShellExecute "runas" dance
                        // needed, unlike the old Installer.bat.
                        Process.Start(new ProcessStartInfo(hdtExe) { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Impossible de relancer Hearthstone Deck Tracker : " + ex.Message);
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine("Appuie sur une touche pour fermer...");
            Console.ReadKey(true);
            return 0;
        }

        private static void Fail(string message)
        {
            Console.WriteLine();
            Console.WriteLine("ERREUR: " + message);
            Console.WriteLine();
            Console.WriteLine("Appuie sur une touche pour fermer...");
            Console.ReadKey(true);
        }

        private static void CloseHdtIfRunning()
        {
            foreach (Process p in Process.GetProcessesByName("HearthstoneDeckTracker"))
            {
                try
                {
                    p.Kill();
                    p.WaitForExit(5000);
                }
                catch { /* best-effort - a previously-elevated instance closing on its own is fine too */ }
            }
        }

        private static string FindInstalledHdtExe()
        {
            try
            {
                string hdtRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HearthstoneDeckTracker");
                if (!Directory.Exists(hdtRoot)) return null;
                return Directory.GetDirectories(hdtRoot, "app-*")
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .Select(d => Path.Combine(d, "HearthstoneDeckTracker.exe"))
                    .FirstOrDefault(File.Exists);
            }
            catch { return null; }
        }

        private static void CopyFileOverwrite(string source, string destination)
        {
            File.Copy(source, destination, true);
        }

        // Clears the destination first: a shrinking asset set between versions must not leave
        // stale files behind (the plugin loads every frame_*.png it finds - see AutoUpdater.cs
        // for the same concern on the auto-update path).
        private static void CopyDirectoryOverwrite(string sourceDir, string destDir)
        {
            if (Directory.Exists(destDir)) Directory.Delete(destDir, true);
            Directory.CreateDirectory(destDir);
            foreach (string dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dir.Replace(sourceDir, destDir));
            foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
                File.Copy(file, file.Replace(sourceDir, destDir), true);
        }
    }
}
