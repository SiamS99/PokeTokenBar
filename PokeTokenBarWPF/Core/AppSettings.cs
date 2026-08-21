using System.Text.Json;

namespace PokeTokenBar.Core;

public class AppSettings
{
    public bool ShowTokensInTray { get; set; } = true;
    public bool ShowCostInTray { get; set; } = false;
    public bool ShowLimitInTray { get; set; } = false;
    public int RefreshIntervalMinutes { get; set; } = 5;
    public bool LaunchAtStartup { get; set; } = false;
    public double WarnThreshold { get; set; } = 80.0;
    public double CritThreshold { get; set; } = 95.0;
    public bool EnableLimitNotifications { get; set; } = true;
    public bool EnableCompanionNotifications { get; set; } = true;
    public string Language { get; set; } = "en";
    public bool OhMyPoshExportEnabled { get; set; } = true;

    private static string SettingsPath => Path.Combine(AppDataDir, "settings.json");

    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PokeTokenBar");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }
}
