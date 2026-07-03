using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApiTester
{
    // 单条连接预设
    public sealed class Preset
    {
        public string Name { get; set; } = "";
        public ProtocolKind Kind { get; set; }
        public string BaseUrl { get; set; } = "";
        public string? ApiKey { get; set; }   // 仅当用户勾选 "Remember key" 时写入（明文）
    }

    // 预设存储：%APPDATA%\ApiTester\presets.json
    public static class PresetStore
    {
        private static readonly JsonSerializerOptions Opts = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private static string Dir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ApiTester");

        private static string FilePath => Path.Combine(Dir, "presets.json");

        public static List<Preset> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<Preset>();
                string json = File.ReadAllText(FilePath);
                List<Preset>? list = JsonSerializer.Deserialize<List<Preset>>(json, Opts);
                return list ?? new List<Preset>();
            }
            catch
            {
                return new List<Preset>();   // 文件损坏等：当作无预设
            }
        }

        public static void Save(List<Preset> presets)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(presets, Opts));
        }
    }
}
