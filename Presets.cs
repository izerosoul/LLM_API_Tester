using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;

namespace ApiTester
{
    // 单条连接预设
    public sealed class Preset
    {
        public string Name { get; set; } = "";
        public ProtocolKind Kind { get; set; }
        public string BaseUrl { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public bool AutoFillUrl { get; set; }
        public string Model { get; set; } = "";
        public string ThinkingLevel { get; set; } = "None";
        public string ListModelsTimeoutSeconds { get; set; } = "5";
        public string SendTimeoutSeconds { get; set; } = "30";
        public string MaxTokens { get; set; } = "1024";
        public string Temperature { get; set; } = "";
        public bool Stream { get; set; }
        public string System { get; set; } = "";
        public string ProxyType { get; set; } = "None";
        public string ProxyHost { get; set; } = "";
        public string ProxyPort { get; set; } = "";
        public string ProxyUser { get; set; } = "";
        public string ProxyPassword { get; set; } = "";
    }

    // 程序配置：与 EXE 同目录、同名 JSON，例如 ApiTester.json。
    public sealed class AppConfig
    {
        public string LastPresetName { get; set; } = "";
        public List<Preset> Presets { get; set; } = new List<Preset>();
    }

    public static class PresetStore
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();
        private static AppConfig _config = new AppConfig();

        public static string LastPresetName
        {
            get { return _config.LastPresetName ?? ""; }
            set { _config.LastPresetName = value ?? ""; }
        }

        private static string FilePath
        {
            get
            {
                string exe = Assembly.GetEntryAssembly()?.Location ?? AppDomain.CurrentDomain.BaseDirectory + "ApiTester.exe";
                string dir = Path.GetDirectoryName(exe) ?? AppDomain.CurrentDomain.BaseDirectory;
                string name = Path.GetFileNameWithoutExtension(exe);
                return Path.Combine(dir, name + ".json");
            }
        }

        public static List<Preset> Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    _config = new AppConfig();
                    return _config.Presets;
                }

                string json = File.ReadAllText(FilePath);
                _config = Serializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                if (_config.Presets == null) _config.Presets = new List<Preset>();
                return _config.Presets;
            }
            catch
            {
                _config = new AppConfig();   // 文件损坏等：当作无预设
                return _config.Presets;
            }
        }

        public static void Save(List<Preset> presets)
        {
            _config.Presets = presets ?? new List<Preset>();
            string dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonUtil.Pretty(Serializer.Serialize(_config)));
        }

        public static void MarkLastUsed(string presetName, List<Preset> presets)
        {
            LastPresetName = presetName;
            Save(presets);
        }
    }
}
