using System;
using System.IO;
using System.Windows;
using VideoOS.Platform.Login;
using VideoOS.Platform;
using VideoOS.Platform.SDK.UI.LoginDialog;

namespace SpeciesDetector
{
    public partial class App : Application
    {
        private static Guid   _integrationId   = new Guid("24ca4f46-6b8c-435a-84fe-939b075da3c5");
        private const  string _integrationName = "Species Detector";
        private const  string _manufacturerName = "Sami";
        private const  string _version          = "1.0";

        internal static DataModel DataModel { get; private set; }
        internal static AppConfig Config    { get; private set; }

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            // ── Load config.json early so every component can access it ────────
            // The config file lives in the project source dir (SpeciesDetector/),
            // three levels above bin/Debug/net4.7.2/.
            var configPath = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\config.json"));
            Config = AppConfig.Load(configPath);

            // ── Initialize MIP SDK environments ───────────────────────────────
            VideoOS.Platform.SDK.Environment.Initialize();
            VideoOS.Platform.SDK.Media.Environment.Initialize();
            VideoOS.Platform.SDK.UI.Environment.Initialize();

            var connected   = false;
            var loginDialog = new DialogLoginForm(x => connected = x,
                _integrationId, _integrationName, _version, _manufacturerName);
            loginDialog.ShowDialog();

            if (!connected)
            {
                var result = MessageBox.Show(
                    "Failed to connect to Milestone.\nDo you want to continue in Offline Test Mode?\n\n" +
                    "(You can still test local images — no live motion events will fire.)",
                    "Offline Mode",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.No)
                {
                    Shutdown();
                    return;
                }
                DataModel = null;
            }
            else
            {
                var loginSettings = LoginSettingsCache.GetLoginSettings(EnvironmentManager.Instance.MasterSite);
                DataModel = new DataModel(loginSettings);
            }
        }

        private void Application_Exit(object sender, ExitEventArgs e)
        {
            DataModel?.Dispose();
            DetectionServerManager.Shutdown();   // kill the Python server we auto-started
        }
    }
}