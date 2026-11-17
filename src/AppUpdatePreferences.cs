using System;
using System.IO;
using System.Web.Script.Serialization;

namespace TokLens
{
    public sealed class AppUpdatePreferences
    {
        public bool Enabled = true;
        public string LastSuccessfulCheckUtc = "";
        private static string FilePath { get { return Path.Combine(SyncService.AppConfigDir, "update-check.json"); } }
        public static AppUpdatePreferences Load()
        {
            try { if (File.Exists(FilePath)) return new JavaScriptSerializer().Deserialize<AppUpdatePreferences>(File.ReadAllText(FilePath)) ?? new AppUpdatePreferences(); }
            catch { }
            return new AppUpdatePreferences();
        }
        public void Save()
        {
            Directory.CreateDirectory(SyncService.AppConfigDir);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, new JavaScriptSerializer().Serialize(this));
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, null); else File.Move(temp, FilePath);
        }
        public bool IsDue(DateTime utcNow)
        {
            DateTime previous;
            return Enabled && (!DateTime.TryParse(LastSuccessfulCheckUtc, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out previous) || previous.ToUniversalTime() > utcNow || utcNow - previous.ToUniversalTime() >= TimeSpan.FromDays(1));
        }
    }
}
