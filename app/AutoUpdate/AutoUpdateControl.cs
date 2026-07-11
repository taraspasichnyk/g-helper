using GHelper.Helpers;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GHelper.AutoUpdate
{
    public class AutoUpdateControl
    {
        const string ReleasesUrl = "https://github.com/seerge/g-helper/releases";

        SettingsForm settings;

        public string versionUrl = ReleasesUrl;
        public bool update = false;

        static long lastUpdate;

        public AutoUpdateControl(SettingsForm settingsForm)
        {
            settings = settingsForm;
            var appVersion = new Version(Assembly.GetExecutingAssembly().GetName().Version.ToString());
            settings.SetVersionLabel(Properties.Strings.VersionLabel + $": {appVersion.Major}.{appVersion.Minor}.{appVersion.Build}");
        }

        public void CheckForUpdates()
        {
            // Run update once per 12 hours
            if (Math.Abs(DateTimeOffset.Now.ToUnixTimeSeconds() - lastUpdate) < 43200) return;
            lastUpdate = DateTimeOffset.Now.ToUnixTimeSeconds();

            Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                CheckForUpdatesAsync();
            });
        }

        public void Update()
        {
            if (update)
            {
                Task.Run(() =>
                {
                    CheckForUpdatesAsync();
                });
            } else
            {
                LoadReleases();
            }
        }

        public void LoadReleases()
        {
            try
            {
                Process.Start(new ProcessStartInfo(versionUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Failed to open releases page:" + ex.Message);
            }
        }

        async void CheckForUpdatesAsync()
        {
            if (AppConfig.Is("skip_updates")) return;

            try
            {
                using (var httpClient = new HttpClient())
                {
                    httpClient.DefaultRequestHeaders.Add("User-Agent", "G-Helper App");
                    var json = await httpClient.GetStringAsync("https://api.github.com/repos/seerge/g-helper/releases");
                    var releases = JsonSerializer.Deserialize<JsonElement>(json);

                    JsonElement config = default;
                    bool allowPrerelease = AppConfig.Is("allow_prerelease");
                    bool found = false;
                    bool isPrerelease = false;

                    for (int r = 0; r < releases.GetArrayLength(); r++)
                    {
                        var candidate = releases[r];
                        if (candidate.GetProperty("draft").GetBoolean()) continue;
                        isPrerelease = candidate.GetProperty("prerelease").GetBoolean();
                        if (!allowPrerelease && isPrerelease) continue;

                        config = candidate;
                        found = true;
                        break;
                    }

                    if (!found)
                    {
                        Logger.WriteLine("No suitable releases found");
                        update = false;
                        versionUrl = ReleasesUrl;
                        return;
                    }

                    var tag = config.GetProperty("tag_name").ToString().TrimStart('v');
                    string skipVersion = AppConfig.GetString("skip_version");
                    var assets = config.GetProperty("assets");

                    string url = null;

                    for (int i = 0; i < assets.GetArrayLength(); i++)
                    {
                        if (assets[i].GetProperty("browser_download_url").ToString().Contains(".zip"))
                            url = assets[i].GetProperty("browser_download_url").ToString();
                    }

                    if (url is null && assets.GetArrayLength() > 0)
                        url = assets[0].GetProperty("browser_download_url").ToString();

                    var gitVersion = new Version(tag);
                    var appVersion = new Version(Assembly.GetExecutingAssembly().GetName().Version.ToString());
                    bool forceChannelSwitch = AppConfig.Is("check_updates_force_channel_switch");
                    bool isSameVersion = IsSameReleaseNumber(gitVersion, appVersion);
                    if (gitVersion.CompareTo(appVersion) > 0 || (forceChannelSwitch && !isSameVersion))
                    {
                        versionUrl = url ?? ReleasesUrl;
                        update = url != null;
                        string channelSuffix = isPrerelease ? " [pre-release]" : "";
                        settings.SetVersionLabel(Properties.Strings.DownloadUpdate + $": {appVersion.Major}.{appVersion.Minor}.{appVersion.Build} → {tag}{channelSuffix}", true);

                        string[] args = Environment.GetCommandLineArgs();
                        if (args.Length > 1 && args[1] == "autoupdate")
                        {
                            if (url != null) AutoUpdate(url);
                            return;
                        }

                        if (skipVersion != tag)
                        {
                            DialogResult dialogResult = DialogResult.No;

                            settings.Invoke((System.Windows.Forms.MethodInvoker)delegate
                            {
                                dialogResult = MessageBox.Show(settings, Properties.Strings.DownloadUpdate + ": G-Helper " + tag + channelSuffix + "?", "Update", MessageBoxButtons.YesNo);
                            });

                            if (dialogResult == DialogResult.Yes && url != null)
                                AutoUpdate(url);
                            else
                                AppConfig.Set("skip_version", tag);
                        }
                    }
                    else
                    {
                        update = false;
                        versionUrl = ReleasesUrl;
                        Logger.WriteLine($"Latest version {appVersion}");
                    }

                    if (forceChannelSwitch)
                        AppConfig.Set("check_updates_force_channel_switch", 0);
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Failed to check for updates:" + ex.Message);
            }
        }

        static bool IsSameReleaseNumber(Version left, Version right)
        {
            static int Part(int value) => value < 0 ? 0 : value;
            return left.Major == right.Major
                && left.Minor == right.Minor
                && Part(left.Build) == Part(right.Build)
                && Part(left.Revision) == Part(right.Revision);
        }

        public static string EscapeString(string input)
        {
            return Regex.Replace(Regex.Replace(input, @"\[|\]", "`$0"), @"\'", "''");
        }

        async void AutoUpdate(string requestUri)
        {

            Uri uri = new Uri(requestUri);
            string zipName = Path.GetFileName(uri.LocalPath);

            string exeLocation = Application.ExecutablePath;
            string exeDir = Path.GetDirectoryName(exeLocation);
            //exeDir = "C:\\Program Files\\GHelper";
            string exeName = Path.GetFileName(exeLocation);
            string zipLocation = exeDir + "\\" + zipName;

            using (HttpClient client = new HttpClient())
            {

                client.DefaultRequestHeaders.Add("User-Agent", "G-Helper App");
                Logger.WriteLine(requestUri);
                Logger.WriteLine(exeDir);
                Logger.WriteLine(zipName);
                Logger.WriteLine(exeName);

                try
                {
                    var bytes = await client.GetByteArrayAsync(uri);
                    File.WriteAllBytes(zipLocation, bytes);
                    Logger.WriteLine($"Downloaded {bytes.Length}b: {zipLocation} (exists={File.Exists(zipLocation)}, size={new FileInfo(zipLocation).Length})");
                }
                catch (Exception ex)
                {
                    Logger.WriteLine(ex.Message);
                    if (!ProcessHelper.IsUserAdministrator())
                    {
                        ProcessHelper.RunAsAdmin("autoupdate");
                        Application.Exit();
                    } else
                    {
                        LoadReleases();
                    }
                    return;
                }

                string command = $"$ErrorActionPreference = \"Stop\"; Set-Location -Path '{EscapeString(exeDir)}'; Wait-Process -Name \"GHelper\"; Expand-Archive \"{zipName}\" -DestinationPath . -Force; Remove-Item \"{zipName}\" -Force; \".\\{exeName}\"; ";
                Logger.WriteLine(command);

                try
                {
                    var cmd = new Process();
                    cmd.StartInfo.WorkingDirectory = exeDir;
                    cmd.StartInfo.UseShellExecute = false;
                    cmd.StartInfo.CreateNoWindow = true;
                    cmd.StartInfo.FileName = "powershell";
                    cmd.StartInfo.Arguments = command;
                    if (ProcessHelper.IsUserAdministrator()) cmd.StartInfo.Verb = "runas";
                    cmd.Start();
                }
                catch (Exception ex)
                {
                    Logger.WriteLine(ex.Message);
                }

                Application.Exit();
            }

        }

    }
}
