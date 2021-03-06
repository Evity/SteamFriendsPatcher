﻿using Microsoft.Win32;

using Semver;

using SteamFriendsPatcher.Forms;
using SteamFriendsPatcher.Properties;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

using static SteamFriendsPatcher.Utilities;

using File = System.IO.File;

namespace SteamFriendsPatcher
{
    internal class Program
    {
        public enum LogLevel
        {
            Debug,
            Info,
            Warning,
            Error
        }

        // location of steam directory
        public static string steamDir = FindSteamDir();

        // location of Steam's CEF Cache
        private static readonly string SteamCacheDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Steam\\htmlcache\\Cache\\");

        // location of Library UI files
        public static readonly string LibraryUIDir = Path.Combine(steamDir, "steamui");
        private static readonly string LibraryCSS = Path.Combine(LibraryUIDir, "css\\libraryroot.css");

        // friends.css etag
        private static string _etag;

        // original friends.css
        public static byte[] friendscss;

        // patched friends.css
        public static byte[] friendscsspatched;

        public static string friendscssetag;

        // original friends.css age
        public static DateTime friendscssage;

        public static bool updatePending;

        // objects to lock to maintain thread safety
        private static readonly object MessageLock = new object();

        private static readonly object UpdateScannerLock = new object();

        private static readonly object GetFriendsCssLock = new object();
        // private static readonly object LogPrintLock = new object();

        private static readonly MainWindow Main = App.MainWindowRef;

        public static bool UpdateChecker()
        {
            lock (UpdateScannerLock)
            {
                Print("Checking for updates...");
                try
                {
                    using (var wc = new WebClient())
                    {
                        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                        wc.Headers.Add("user-agent", Assembly.GetExecutingAssembly().FullName);
                        var latestver =
                            wc.DownloadString(
                                "https://api.github.com/repos/phantomgamers/steamfriendspatcher/releases/latest");
                        const string verregex = "(?<=\"tag_name\":\")(.*?)(?=\")";
                        var latestvervalue = Regex.Match(latestver, verregex).Value;
                        if (!string.IsNullOrEmpty(latestvervalue))
                        {
                            var assemblyVer = ThisAssembly.AssemblyInformationalVersion;
                            assemblyVer = assemblyVer.Substring(0,
                                assemblyVer.IndexOf('+') > -1 ? assemblyVer.IndexOf('+') : assemblyVer.Length);
                            if (!SemVersion.TryParse(assemblyVer, out var localVer) ||
                                !SemVersion.TryParse(latestvervalue, out var remoteVer))
                            {
                                Print("Update check failed, failed to parse version string.", LogLevel.Error);
                                return false;
                            }

                            if (remoteVer > localVer)
                            {
                                if (MessageBox.Show("Update available. Download now?",
                                        "Steam Friends Patcher - Update Available", MessageBoxButton.YesNo) ==
                                    MessageBoxResult.Yes)
                                    Process.Start("https://github.com/PhantomGamers/SteamFriendsPatcher/releases/latest");

                                return true;
                            }

                            Print("No updates found.");
                            return false;
                        }

                        Print("Failed to check for updates.", LogLevel.Error);
                        return false;
                    }
                }
                catch (WebException we)
                {
                    Print("Failed to check for updates.", LogLevel.Error);
                    Print(we.ToString(), LogLevel.Error);
                }

                return false;
            }
        }

        public static void PatchCacheFile(string friendscachefile, IEnumerable<byte> decompressedcachefile)
        {
            Print($"Successfully found matching friends.css at {friendscachefile}.");
            File.WriteAllBytes(steamDir + "\\clientui\\friends.original.css",
                Encoding.ASCII.GetBytes("/*" + _etag + "*/\n").Concat(decompressedcachefile).ToArray());

            Print("Overwriting with patched version...");
            File.WriteAllBytes(friendscachefile, friendscsspatched);

            if (!File.Exists(steamDir + "\\clientui\\friends.custom.css"))
                File.Create(steamDir + "\\clientui\\friends.custom.css").Dispose();

            if (Process.GetProcessesByName("Steam").FirstOrDefault() != null &&
                File.Exists(steamDir + "\\clientui\\friends.custom.css") && FindFriendsWindow())
            {
                Print("Reloading friends window...");
                Process.Start(steamDir + "\\Steam.exe", @"steam://friends/status/offline");
                Task.Delay(TimeSpan.FromSeconds(1)).Wait();
                Process.Start(steamDir + "\\Steam.exe", @"steam://friends/status/online");
            }

            Print("Done! Put your custom css in " + steamDir + "\\clientui\\friends.custom.css");
            Print("Close and reopen your Steam friends window to see changes.");

            if (Settings.Default.restartSteamOnPatch)
            {
                ShutdownSteam();
                RestartSteam();
            }

            Main.Dispatcher.Invoke(() =>
            {
                if (Main.IsVisible || !Settings.Default.showNotificationsInTray) return;
                Main.NotifyIcon.BalloonTipTitle = @"Steam Friends Patcher";
                Main.NotifyIcon.BalloonTipText = @"Successfully patched friends!";
                Main.NotifyIcon.ShowBalloonTip((int)TimeSpan.FromSeconds(10).TotalMilliseconds);
            });
        }

        public static void FindCacheFile(bool forceUpdate = false)
        {
            Settings.Default.Reload();
            var preScannerStatus = FileWatcher.scannerExists;
            FileWatcher.ToggleCacheScanner(false);
            Main.ToggleButtons(false);

            Print("Force scan started.");

            GetLatestFriendsCss(forceUpdate);

            while (updatePending) Task.Delay(TimeSpan.FromMilliseconds(20)).Wait();

            if (friendscss == null || friendscsspatched == null)
            {
                Print("Friends.css could not be obtained, ending force check...");
                goto ResetButtons;
            }

            Print("Finding list of possible cache files...");
            if (!Directory.Exists(SteamCacheDir))
            {
                Print("Cache folder does not exist.", LogLevel.Error);
                Print("Please confirm that Steam is running and that the friends list is open and try again.",
                    LogLevel.Error);
                goto ResetButtons;
            }

            var validFiles = new DirectoryInfo(SteamCacheDir).EnumerateFiles("f_*", SearchOption.TopDirectoryOnly)
                .Where(f => f.Length == friendscss.Length || f.Length == friendscsspatched.Length)
                .OrderByDescending(f => f.LastWriteTime)
                .Select(f => f.FullName)
                .ToList();
            var count = validFiles.Count;
            if (count == 0)
            {
                Print("No matching cache files found.", LogLevel.Error);
                Print("Please confirm that Steam is running and that the friends list is open and try again.",
                    LogLevel.Error);
                goto ResetButtons;
            }

            Print($"Found {count} possible cache files.");

            string friendscachefile = null;
            var patchedFileFound = false;

            Print("Checking cache files for match...");
            Parallel.ForEach(validFiles, (s, state) =>
            {
                byte[] cachefile;

                try
                {
                    using (var f = new FileStream(s, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        cachefile = new byte[f.Length];
                        f.Read(cachefile, 0, cachefile.Length);
                    }
                }
                catch
                {
                    Print($"Error, {s} could not be opened.", LogLevel.Debug);
                    return;
                }

                if (!IsGZipHeader(cachefile)) return;
                if (cachefile.Length == friendscss.Length && ByteArrayCompare(cachefile, friendscss))
                {
                    state.Stop();
                    friendscachefile = s;
                    PatchCacheFile(s, Decompress(cachefile));
                }
                else if (cachefile.Length == friendscsspatched.Length && ByteArrayCompare(cachefile, friendscsspatched))
                {
                    patchedFileFound = true;
                }

                /*
                    if (useDecompressionMethod)
                    {
                        byte[] decompressedcachefile = Decompress(cachefile);
                        if (decompressedcachefile.Length == friendscss.Length && ByteArrayCompare(decompressedcachefile, friendscss))
                        {
                            state.Stop();
                            friendscachefile = s;
                            PatchCacheFile(s, decompressedcachefile);
                            return;
                        }
                        else if (decompressedcachefile.Length == friendscsspatched.Length && ByteArrayCompare(decompressedcachefile, friendscsspatched))
                        {
                            patchedFileFound = true;
                        }
                    }
                    */
            });

            if (string.IsNullOrEmpty(friendscachefile))
            {
                if (!patchedFileFound)
                    Print("Cache file does not exist or is outdated.", LogLevel.Warning);
                else
                    Print("Cache file is already patched.");
            }

        ResetButtons:
            PatchLibrary();
            Main.ToggleButtons(true);
            if (preScannerStatus) FileWatcher.ToggleCacheScanner(true);
        }

        private static byte[] PrependFile(IEnumerable<byte> file)
        {
            // custom only
            // string appendText = "@import url(\"https://steamloopback.host/friends.custom.css\");\n";

            // custom overrides original (!important tags not needed)
            const string appendText =
                "@import url(\"https://steamloopback.host/friends.original.css\");\n@import url(\"https://steamloopback.host/friends.custom.css\");\n{";

            // original overrides custom (!important tags needed, this is the original behavior)
            // string appendText = "@import url(\"https://steamloopback.host/friends.custom.css\");\n@import url(\"https://steamloopback.host/friends.original.css\");\n{";

            // load original from Steam CDN, not recommended because of infinite matching
            // string appendText = "@import url(\"https://steamcommunity-a.akamaihd.net/public/css/webui/friends.css\");\n@import url(\"https://steamloopback.host/friends.custom.css\");\n";
            var append = Encoding.ASCII.GetBytes(appendText);

            var output = append.Concat(file).Concat(Encoding.ASCII.GetBytes("}")).ToArray();
            return output;
        }

        public static void PatchLibrary()
        {
            if (Settings.Default.patchLibraryBeta)
            {
                if (!Directory.Exists(Path.Combine(LibraryUIDir, "css")))
                {
                    Print("Library UI directory not found.");
                    return;
                }
                if (!File.Exists(LibraryCSS))
                {
                    Print("Library CSS not found.");
                    return;
                }
                Print("Patching Library [BETA]...");
                string librarycss;
                string patchedText = "/*patched*/";
                try
                {
                    librarycss = File.ReadAllText(LibraryCSS);
                }
                catch (Exception e)
                {
                    Print(e.ToString(), LogLevel.Error);
                    Print("File could not be read, aborting...", LogLevel.Error);
                    return;
                }
                if (librarycss.StartsWith(patchedText, StringComparison.InvariantCulture))
                {
                    Print("Library already patched.");
                    return;
                }
                if (File.Exists(Path.Combine(LibraryUIDir, "libraryroot.original.css")))
                {
                    File.Delete(Path.Combine(LibraryUIDir, "libraryroot.original.css"));
                }
                File.Copy(LibraryCSS, Path.Combine(LibraryUIDir, "libraryroot.original.css"));
                int originalLibCSSLength = librarycss.Length;
                librarycss = patchedText + "\n@import url(\"https://steamloopback.host/libraryroot.original.css\");\n@import url(\"https://steamloopback.host/libraryroot.custom.css\");\n";
                var fillerText = new string('\t', originalLibCSSLength - librarycss.Length);
                librarycss += fillerText;
                if (!File.Exists(Path.Combine(LibraryUIDir, "libraryroot.custom.css")))
                {
                    File.Create(Path.Combine(LibraryUIDir, "libraryroot.custom.css")).Dispose();
                }
                File.WriteAllText(LibraryCSS, librarycss);

                string maincss = File.ReadAllText(Path.Combine(LibraryUIDir, "css", "main.css"));
                bool maincsspatched = false;
                if (maincss.StartsWith(patchedText, StringComparison.InvariantCulture))
                {
                    Print("Library main.css already patched.", LogLevel.Debug);
                    maincsspatched = true;
                }
                if (!maincsspatched)
                {
                    if (File.Exists(Path.Combine(LibraryUIDir, "main.original.css")))
                    {
                        File.Delete(Path.Combine(LibraryUIDir, "main.original.css"));
                    }
                    if (!File.Exists(Path.Combine(LibraryUIDir, "main.custom.css")))
                    {
                        File.Create(Path.Combine(LibraryUIDir, "main.custom.css")).Dispose();
                    }
                    File.Copy(Path.Combine(LibraryUIDir, "css", "main.css"), Path.Combine(LibraryUIDir, "main.original.css"));
                    int originalmaincsslength = maincss.Length;
                    maincss = patchedText + "\n@import url(\"https://steamloopback.host/main.original.css\");\n@import url(\"https://steamloopback.host/main.custom.css\");\n";
                    maincss += new string('\t', originalmaincsslength - maincss.Length);
                    File.WriteAllText(Path.Combine(LibraryUIDir, "css", "main.css"), maincss);
                }

                string ofriendscss = File.ReadAllText(steamDir + "\\clientui\\css\\friends.css");
                bool ofriendscsspatched = false;
                if (ofriendscss.StartsWith(patchedText, StringComparison.InvariantCulture))
                {
                    Print("Offline friends.css already patched.", LogLevel.Debug);
                    ofriendscsspatched = true;
                }
                if (!ofriendscsspatched)
                {
                    if (File.Exists(steamDir + "\\clientui\\ofriends.original.css"))
                    {
                        File.Delete(steamDir + "\\clientui\\ofriends.original.css");
                    }
                    if (!File.Exists(steamDir + "\\clientui\\ofriends.custom.css"))
                    {
                        File.Create(steamDir + "\\clientui\\ofriends.custom.css").Dispose();
                    }
                    File.Copy(steamDir + "\\clientui\\css\\friends.css", steamDir + "\\clientui\\ofriends.original.css");
                    int originalofriendscsslength = ofriendscss.Length;
                    ofriendscss = patchedText + "\n@import url(\"https://steamloopback.host/ofriends.original.css\");\n@import url(\"https://steamloopback.host/ofriends.custom.css\");\n";
                    ofriendscss += new string('\t', originalofriendscsslength - ofriendscss.Length);
                    File.WriteAllText(steamDir + "\\clientui\\css\\friends.css", ofriendscss);
                }

                Print("Library patched! [BETA]");
                Print("Put custom library css in " + Path.Combine(LibraryUIDir, "libraryroot.custom.css"));

            }
        }

        public static bool GetLatestFriendsCss(bool force = false)
        {
            if (DateTime.Now.Subtract(friendscssage).TotalMinutes < 1 && !force || updatePending) return true;
            lock (GetFriendsCssLock)
            {
                updatePending = true;
                Print("Checking for latest friends.css...");
                using (var wc = new WebClient())
                {
                    try
                    {
                        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                        wc.Encoding = Encoding.UTF8;
                        wc.Headers.Add("user-agent", "Mozilla/5.0 (Windows; U; Windows NT 10.0; en-US; Valve Steam Client/default/1596241936; ) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/79.0.3945.117 Safari/537.36");

                        Settings.Default.Reload();
                        var steamChat = wc.DownloadString("https://steam-chat.com/chat/clientui/?l=&cc=" + Settings.Default.steamLocale + "&build=");

                        wc.Headers[HttpRequestHeader.AcceptEncoding] = "gzip";
                        Regex r =
                            new Regex(@"(?<=<link href="")(.+friends.css.+)(?="" rel)");
                        var friendscssurl = !String.IsNullOrEmpty(steamChat) ? r.Match(steamChat).Value.Replace("&amp;", "&") : String.Empty;
                        r = new Regex(@"(?<=\?v=)(.*)(?=&)");
                        _etag = !String.IsNullOrEmpty(friendscssurl) ? r.Match(friendscssurl).Value : String.Empty;



                        if (!string.IsNullOrEmpty(_etag) && !string.IsNullOrEmpty(friendscssetag) &&
                            _etag == friendscssetag)
                        {
                            Print("friends.css is already up to date.");
                            updatePending = false;
                            return true;
                        }

                        if (string.IsNullOrEmpty(_etag))
                        {
                            Print("Could not find etag, using latest.", LogLevel.Debug);
                            wc.DownloadData("https://steamcommunity-a.akamaihd.net/public/css/webui/friends.css");
                            var count = wc.ResponseHeaders.Count;
                            for (var i = 0; i < count; i++)
                            {
                                if (wc.ResponseHeaders.GetKey(i) != "ETag") continue;
                                _etag = wc.ResponseHeaders.Get(i);
                                break;
                            }
                        }

                        if (!string.IsNullOrEmpty(_etag))
                        {
                            Print("etag: " + _etag, LogLevel.Debug);
                            var fc = wc.DownloadData(friendscssurl);
                            if (fc.Length > 0)
                            {
                                friendscss = fc;
                                var tmp = PrependFile(Decompress(fc));

                                using(var file = new MemoryStream())
                                {
                                    using (var gzip = new GZipStream(file, CompressionLevel.Optimal, false))
                                    {
                                        gzip.Write(tmp, 0, tmp.Length);
                                    }

                                    friendscsspatched = file.ToArray();
                                }

                                friendscssage = DateTime.Now;
                                friendscssetag = _etag;
                                Print("Successfully downloaded latest friends.css");
                                updatePending = false;
                                return true;
                            }
                        }

                        /*
                        else
                        {
                            fc = wc.DownloadData("https://steamcommunity-a.akamaihd.net/public/css/webui/friends.css");
                            if (fc.Length > 0)
                            {
                                friendscss = Decompress(fc);
                                friendscsspatched = PrependFile(friendscss);
                                friendscssage = DateTime.Now;
                                Print("Successfully downloaded latest friends.css.");
                                return true;
                            }
                        }
                        */
                        Print("Failed to download friends.css", LogLevel.Error);
                        updatePending = false;
                        return false;
                    }
                    catch (WebException we)
                    {
                        Print("Failed to download friends.css.", LogLevel.Error);
                        Print(we.ToString(), LogLevel.Error);
                        updatePending = false;
                        return false;
                    }
                }
            }
        }

        private static string FindSteamDir()
        {
            using (var registryKey = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Valve\\Steam"))
            {
                string filePath = null;
                var regFilePath = registryKey?.GetValue("SteamPath");
                if (regFilePath != null) filePath = regFilePath.ToString().Replace(@"/", @"\");

                return filePath;
            }
        }

        private static bool FindFriendsWindow()
        {
            return (int)NativeMethods.FindWindowByClass("SDL_app") != 0;
        }

        private static void ShutdownSteam()
        {
            var steamp = Process.GetProcessesByName("Steam").FirstOrDefault();
            Print("Shutting down Steam...");
            Process.Start(steamDir + "\\Steam.exe", "-shutdown");
            if (steamp != null && !steamp.WaitForExit((int)TimeSpan.FromSeconds(30).TotalMilliseconds))
            {
                Print("Could not successfully shutdown Steam, please manually shutdown Steam and try again.",
                    LogLevel.Error);
                Main.ToggleButtons(true);
                return;
            }
        }

        private static void RestartSteam()
        {
            Print("Restarting Steam...");
            Process.Start(steamDir + "\\Steam.exe", Settings.Default.steamLaunchArgs);
            for (var i = 0; i < 10; i++)
            {
                if (Process.GetProcessesByName("Steam").FirstOrDefault() != null)
                {
                    Print("Steam started.");
                    break;
                }

                if (i == 9) Print("Failed to start Steam.", LogLevel.Error);
            }
        }

        public static void ClearSteamCache()
        {
            if (!Directory.Exists(SteamCacheDir))
            {
                Print("Cache folder does not exist.", LogLevel.Warning);
                return;
            }

            var preScannerStatus = FileWatcher.scannerExists;
            var preSteamStatus = Process.GetProcessesByName("Steam").FirstOrDefault() != null;
            if (preSteamStatus)
            {
                if (MessageBox.Show("Steam will need to be shutdown to clear cache. Restart automatically?",
                        "Steam Friends Patcher", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;

            }

            ShutdownSteam();

            FileWatcher.ToggleCacheScanner(false);
            Main.ToggleButtons(false);

            Print("Deleting cache files...");
            try
            {
                Directory.Delete(SteamCacheDir, true);
            }
            catch (IOException ioe)
            {
                Print("Some cache files in use, cannot delete.", LogLevel.Error);
                Print(ioe.ToString(), LogLevel.Error);
            }
            finally
            {
                Print("Cache files deleted.");
            }

            if (File.Exists(LibraryCSS))
            {
                Print("Deleting patched library file...");
                File.Delete(LibraryCSS);
            }

            FileWatcher.ToggleCacheScanner(preScannerStatus);
            if (preSteamStatus)
            {
                RestartSteam();
            }

            Main.ToggleButtons(true);
        }



        public static void Print(string message = null, LogLevel logLevel = LogLevel.Info, bool newline = true)
        {
            var dateTime = DateTime.Now.ToString("G", CultureInfo.CurrentCulture);
#if DEBUG
            Debug.Write($"[{dateTime}][{logLevel}] {message}" + (newline ? Environment.NewLine : string.Empty));
#endif
            /*
            if (SteamFriendsPatcher.Properties.Settings.Default.outputToLog)
            {
                lock (LogPrintLock)
                {
                    FileInfo file = new FileInfo(Path.Combine(Environment.GetEnvironmentVariable("LocalAppData"), $"PhantomGamers/logs/SteamFriendsPatcher_"
                                       + $"{DateTime.Now.ToString("yyyyMMdd")}.log"));
                    Directory.CreateDirectory(file.Directory.FullName);
                    File.AppendAllText(file.FullName, fullMessage);
                }
            }
            */

            if (logLevel == LogLevel.Debug && !Settings.Default.showDebugMessages) return;

            lock (MessageLock)
            {
                Main.Output.Dispatcher.Invoke(() =>
                {
                    if (Main.Output.Document == null) Main.Output.Document = new FlowDocument();

                    // Date & Time
                    var tr = new TextRange(Main.Output.Document.ContentEnd, Main.Output.Document.ContentEnd)
                    {
                        Text = $"[{dateTime}] "
                    };
                    tr.ApplyPropertyValue(TextElement.ForegroundProperty,
                        (SolidColorBrush)new BrushConverter().ConvertFromString("#76608a") ??
                        throw new InvalidOperationException());
                    tr.Select(Main.Output.Document.ContentEnd, Main.Output.Document.ContentEnd);
                    tr.Text += $"[{logLevel}] ";

                    // Message Type
                    switch (logLevel)
                    {
                        case LogLevel.Error:
                            tr.ApplyPropertyValue(TextElement.ForegroundProperty,
                                (SolidColorBrush)new BrushConverter().ConvertFromString("#e51400") ??
                                throw new InvalidOperationException());
                            break;

                        case LogLevel.Warning:
                            tr.ApplyPropertyValue(TextElement.ForegroundProperty,
                                (SolidColorBrush)new BrushConverter().ConvertFromString("#f0a30a") ??
                                throw new InvalidOperationException());
                            break;

                        default:
                            tr.ApplyPropertyValue(TextElement.ForegroundProperty,
                                (SolidColorBrush)new BrushConverter().ConvertFromString("#76608a") ??
                                throw new InvalidOperationException());
                            break;
                    }

                    // Message
                    tr.Select(Main.Output.Document.ContentEnd, Main.Output.Document.ContentEnd);
                    tr.Text += message + (newline ? "\n" : string.Empty);
                    tr.ApplyPropertyValue(TextElement.ForegroundProperty,
                        (SolidColorBrush)new BrushConverter().ConvertFromString("White") ??
                        throw new InvalidOperationException());
                });
            }
        }
    }
}