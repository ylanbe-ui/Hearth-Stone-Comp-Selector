using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace HDTShopWishlist
{
    // Checks GitHub Releases for a newer build, downloads + stages it in the background, and
    // (once the caller confirms it's safe, i.e. not mid-match) swaps the plugin files and
    // restarts HDT. The DLL can't replace itself while HDT has it loaded, so the actual file
    // swap + relaunch happens in a small detached .cmd that outlives this process - same trick
    // install-only.bat already uses for the elevated restart.
    internal static class AutoUpdater
    {
        private const string RepoOwner = "ylanbe-ui";
        private const string RepoName = "Hearth-Stone-Comp-Selector";
        private const string PayPalUrl = "https://www.paypal.com/donate/?business=ylan.be%40gmail.com&currency_code=EUR&no_recurring=1";
        private const string UserAgent = "HDT-Shop-Wishlist-Overlay-Updater";

        internal static void OpenPayPalSupport()
        {
            try { Process.Start(new ProcessStartInfo(PayPalUrl) { UseShellExecute = true }); }
            catch (Exception ex) { Debug.WriteLine("AutoUpdater PayPal: " + ex); }
        }

        // Fire-and-forget: checks the latest release, and if newer than currentVersion,
        // downloads + extracts it to a staging folder and hands the folder path to onReady.
        // Never throws back into the caller.
        internal static void CheckAndPrepareAsync(Version currentVersion, Action<string> onReady)
        {
            Task.Run(delegate
            {
                try
                {
                    ReleaseInfo release = FetchLatestRelease();
                    if (release == null) return;
                    Version remote = ParseVersion(release.TagName);
                    if (remote == null || remote.CompareTo(currentVersion) <= 0) return;

                    string zipPath = DownloadAsset(release);
                    if (zipPath == null) return;

                    string payloadDir = ExtractPayload(zipPath);
                    if (payloadDir == null) return;

                    if (onReady != null) onReady(payloadDir);
                }
                catch (Exception ex) { Debug.WriteLine("AutoUpdater check: " + ex); }
            });
        }

        // Swaps the staged payload into the live Plugins folder and restarts HDT. Call only
        // when it's safe to yank HDT away (not mid-match). Shuts down the current process.
        internal static void ApplyAndRestart(string payloadDir)
        {
            try
            {
                string pluginDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "HearthstoneDeckTracker", "Plugins");
                string hdtExe = FindInstalledHdtExe();
                if (hdtExe == null) { Debug.WriteLine("AutoUpdater apply: HDT install not found."); return; }

                string scriptPath = Path.Combine(Path.GetTempPath(), "hdt_shopwishlist_update_" + Guid.NewGuid().ToString("N") + ".cmd");
                File.WriteAllText(scriptPath, BuildUpdateScript(payloadDir, pluginDir, hdtExe), Encoding.ASCII);

                var psi = new ProcessStartInfo("cmd.exe", "/c \"" + scriptPath + "\"")
                {
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true
                };
                Process.Start(psi);

                var app = System.Windows.Application.Current;
                if (app != null) app.Dispatcher.BeginInvoke((Action)delegate { try { app.Shutdown(); } catch { } });
            }
            catch (Exception ex) { Debug.WriteLine("AutoUpdater apply: " + ex); }
        }

        private sealed class ReleaseInfo
        {
            public string TagName;
            public string AssetUrl;
            public string AssetName;
        }

        private static ReleaseInfo FetchLatestRelease()
        {
            try
            {
                string url = "https://api.github.com/repos/" + RepoOwner + "/" + RepoName + "/releases/latest";
                string json;
                using (var wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = UserAgent;
                    wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                    json = wc.DownloadString(url);
                }
                var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                object tagObj;
                if (data == null || !data.TryGetValue("tag_name", out tagObj)) return null;
                string tag = Convert.ToString(tagObj);
                if (string.IsNullOrWhiteSpace(tag)) return null;

                object assetsObj;
                if (!data.TryGetValue("assets", out assetsObj)) return null;
                var assets = assetsObj as object[];
                if (assets == null) return null;

                foreach (object a in assets)
                {
                    var asset = a as Dictionary<string, object>;
                    if (asset == null) continue;
                    object nameObj;
                    if (!asset.TryGetValue("name", out nameObj)) continue;
                    string name = Convert.ToString(nameObj);
                    if (string.IsNullOrEmpty(name) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    object urlObj;
                    asset.TryGetValue("browser_download_url", out urlObj);
                    string assetUrl = Convert.ToString(urlObj);
                    if (string.IsNullOrEmpty(assetUrl)) continue;
                    return new ReleaseInfo { TagName = tag, AssetUrl = assetUrl, AssetName = name };
                }
            }
            catch (Exception ex) { Debug.WriteLine("AutoUpdater fetch: " + ex); }
            return null;
        }

        private static Version ParseVersion(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            string s = tag.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
            Version v;
            return Version.TryParse(s, out v) ? v : null;
        }

        private static string DownloadAsset(ReleaseInfo release)
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "HDTShopWishlistUpdate");
                Directory.CreateDirectory(dir);
                string zipPath = Path.Combine(dir, release.AssetName);
                using (var wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = UserAgent;
                    wc.DownloadFile(release.AssetUrl, zipPath);
                }
                return zipPath;
            }
            catch (Exception ex) { Debug.WriteLine("AutoUpdater download: " + ex); return null; }
        }

        private static string ExtractPayload(string zipPath)
        {
            try
            {
                string extractDir = Path.Combine(Path.GetTempPath(), "HDTShopWishlistUpdate", "extracted");
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                Directory.CreateDirectory(extractDir);
                ZipFile.ExtractToDirectory(zipPath, extractDir);

                // Release zip has one top-level "HDT-Shop-Wishlist-Overlay-vX.Y.Z" folder; if the
                // DLL isn't directly under extractDir, step into that folder.
                if (File.Exists(Path.Combine(extractDir, "HDT-Shop-Wishlist-Overlay.dll"))) return extractDir;
                string sub = Directory.GetDirectories(extractDir).FirstOrDefault();
                return sub != null && File.Exists(Path.Combine(sub, "HDT-Shop-Wishlist-Overlay.dll")) ? sub : null;
            }
            catch (Exception ex) { Debug.WriteLine("AutoUpdater extract: " + ex); return null; }
        }

        private static string FindInstalledHdtExe()
        {
            try
            {
                string hdtRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HearthstoneDeckTracker");
                if (!Directory.Exists(hdtRoot)) return null;
                string best = Directory.GetDirectories(hdtRoot, "app-*")
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(d => File.Exists(Path.Combine(d, "HearthstoneDeckTracker.exe")));
                return best != null ? Path.Combine(best, "HearthstoneDeckTracker.exe") : null;
            }
            catch (Exception ex) { Debug.WriteLine("AutoUpdater find HDT: " + ex); return null; }
        }

        // Mirrors install-only.bat: wait for HDT to exit, copy the known plugin files over,
        // then relaunch HDT elevated via the same ShellExecute "runas" dance (a single UAC
        // prompt) so the firewall-based Skip Combat feature keeps working after the restart.
        private static string BuildUpdateScript(string payloadDir, string pluginDir, string hdtExe)
        {
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("setlocal EnableExtensions EnableDelayedExpansion");
            sb.AppendLine(":waitexit");
            sb.AppendLine("tasklist /FI \"IMAGENAME eq HearthstoneDeckTracker.exe\" | find /I \"HearthstoneDeckTracker.exe\" >nul");
            sb.AppendLine("if not errorlevel 1 (");
            sb.AppendLine("  timeout /t 1 /nobreak >nul");
            sb.AppendLine("  goto waitexit");
            sb.AppendLine(")");
            sb.AppendLine("if not exist \"" + pluginDir + "\" mkdir \"" + pluginDir + "\" >nul 2>&1");
            sb.AppendLine("copy /y \"" + Path.Combine(payloadDir, "HDT-Shop-Wishlist-Overlay.dll") + "\" \"" + Path.Combine(pluginDir, "HDT-Shop-Wishlist-Overlay.dll") + "\" >nul 2>&1");
            sb.AppendLine("if exist \"" + Path.Combine(payloadDir, "untapped-scry-dotnet.dll") + "\" copy /y \"" + Path.Combine(payloadDir, "untapped-scry-dotnet.dll") + "\" \"" + Path.Combine(pluginDir, "untapped-scry-dotnet.dll") + "\" >nul 2>&1");
            sb.AppendLine("if exist \"" + Path.Combine(payloadDir, "Assets") + "\" xcopy /e /i /y \"" + Path.Combine(payloadDir, "Assets") + "\" \"" + Path.Combine(pluginDir, "Assets") + "\\\" >nul 2>&1");

            string elevCmd = Path.Combine(Path.GetTempPath(), "hdt_elevate_" + Guid.NewGuid().ToString("N") + ".cmd");
            string elevVbs = Path.Combine(Path.GetTempPath(), "hdt_elevate_" + Guid.NewGuid().ToString("N") + ".vbs");
            sb.AppendLine("> \"" + elevCmd + "\" echo @echo off");
            sb.AppendLine(">> \"" + elevCmd + "\" echo start \"\" \"" + hdtExe + "\"");
            sb.AppendLine("> \"" + elevVbs + "\" echo Set UAC = CreateObject^(\"Shell.Application\"^)");
            sb.AppendLine(">> \"" + elevVbs + "\" echo UAC.ShellExecute \"" + elevCmd + "\", \"\", \"\", \"runas\", 1");
            sb.AppendLine("cscript //nologo \"" + elevVbs + "\" >nul 2>&1");
            sb.AppendLine("del \"" + elevVbs + "\" >nul 2>&1");
            sb.AppendLine("del \"%~f0\" >nul 2>&1");
            return sb.ToString();
        }
    }
}
