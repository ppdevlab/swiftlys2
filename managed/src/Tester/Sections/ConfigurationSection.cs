using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Services;
using Tester.Framework;
using Tomlyn.Extensions.Configuration;
using static Tester.Framework.TestContext;

namespace Tester.Sections;

public sealed class ConfigurationSection( ISwiftlyCore core ) : Section(core)
{
    public sealed class TesterModel
    {
        public int Number { get; set; } = 7;
        public string Text { get; set; } = "hello";
        public bool Flag { get; set; } = true;
        public List<string> Items { get; set; } = ["a", "b"];
    }

    public sealed class DescribedInnerModel
    {
        [Description("Inner flag")]
        public bool Enabled { get; set; } = true;
    }

    public sealed class DescribedModel
    {
        [Description("How many times\nSecond line")]
        public int Number { get; set; } = 7;

        public string Text { get; set; } = "hello";

        [Description("Renamed key")]
        [JsonPropertyName("renamed_key")]
        public string Renamed { get; set; } = "value";

        [Description("Nested section")]
        public DescribedInnerModel Inner { get; set; } = new();
    }

    public override string Name => "configuration";

    private IPluginConfigurationService Cfg => Core.Configuration;

    private static string Unique( string extension ) => $"tester-{Guid.NewGuid():N}{extension}";

    private void WithFile( string extension, Action<string, string> body )
    {
        var name = Unique(extension);
        var path = Cfg.GetConfigPath(name);
        var sourcesBefore = Cfg.Manager.Sources.ToList();
        try { body(name, path); }
        finally
        {
            foreach (var s in Cfg.Manager.Sources.Except(sourcesBefore).ToList()) _ = Cfg.Manager.Sources.Remove(s);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    public override Task Test( TestContext t )
    {
        var cfg = Cfg;

        t.Test("BasePath is absolute and ends with the plugin's folder", () =>
        {
            Expect(Path.IsPathRooted(cfg.BasePath), $"not absolute: {cfg.BasePath}");
            Expect(Path.GetFileName(cfg.BasePath).Length > 0, "no folder name");
            Expect(cfg.BasePath.Contains("plugins", StringComparison.OrdinalIgnoreCase), $"not under a 'plugins' folder: {cfg.BasePath}");
        });

        t.Test("GetConfigPath joins the file name onto BasePath and creates the folder", () =>
        {
            var name = Unique(".json");
            var path = cfg.GetConfigPath(name);
            Equal(Path.Combine(cfg.BasePath, name), path, "path");
            Expect(cfg.BasePathExists, "BasePath not created");
            Expect(Directory.Exists(cfg.BasePath), "BasePath is not a directory");
            Expect(!File.Exists(path), "GetConfigPath must not create the file itself");
        });

        t.Test("GetConfigPath keeps sub-folders in the name", () =>
        {
            var name = Path.Combine("tester-sub", Unique(".json"));
            Equal(Path.Combine(cfg.BasePath, name), cfg.GetConfigPath(name), "path");
        });

        t.Test("InitializeJsonWithModel writes the model under its section", () =>
            WithFile(".json", ( name, path ) =>
            {
                var returned = cfg.InitializeJsonWithModel<TesterModel>(name, "Main");
                Expect(ReferenceEquals(cfg, returned), "does not return the service");
                Expect(File.Exists(path), "file not created");
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var main = doc.RootElement.GetProperty("Main");
                Equal(7, main.GetProperty("Number").GetInt32(), "Number");
                Equal("hello", main.GetProperty("Text").GetString(), "Text");
                Equal(true, main.GetProperty("Flag").GetBoolean(), "Flag");
                Equal(2, main.GetProperty("Items").GetArrayLength(), "Items.Length");
            }));

        t.Test("InitializeJsonWithModel keeps an existing file", () =>
            WithFile(".json", ( name, path ) =>
            {
                File.WriteAllText(path, "{ \"Main\": { \"Number\": 99 } }");
                _ = cfg.InitializeJsonWithModel<TesterModel>(name, "Main");
                Equal("{ \"Main\": { \"Number\": 99 } }", File.ReadAllText(path), "file content");
            }));

        t.Test("InitializeJsonWithModel creates missing sub-folders", () =>
        {
            var folder = "tester-" + Guid.NewGuid().ToString("N")[..8];
            var name = Path.Combine(folder, "model.json");
            var path = cfg.GetConfigPath(name);
            try
            {
                _ = cfg.InitializeJsonWithModel<TesterModel>(name, "Main");
                Expect(File.Exists(path), "file not created in sub-folder");
            }
            finally
            {
                var dir = Path.GetDirectoryName(path)!;
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        });

        t.Test("JSON model file binds back to the model through the Manager", () =>
            WithFile(".json", ( name, unused ) =>
            {
                _ = cfg.InitializeJsonWithModel<TesterModel>(name, "Main");
                _ = cfg.Configure(b => b.AddJsonFile(name, optional: false, reloadOnChange: false));
                var model = cfg.Manager.GetSection("Main").Get<TesterModel>();
                NotNull(model, "bound model");
                Equal(7, model!.Number, "Number");
                Equal("hello", model.Text, "Text");
                Expect(model.Flag, "Flag");
                Expect(model.Items.Distinct().SequenceEqual(["a", "b"]), $"Items: [{string.Join(", ", model.Items)}]");
            }));

        t.Test("Manager reflects a file change after Reload", () =>
            WithFile(".json", ( name, path ) =>
            {
                _ = cfg.InitializeJsonWithModel<TesterModel>(name, "Main");
                _ = cfg.Configure(b => b.AddJsonFile(name, optional: false, reloadOnChange: false));
                Equal("7", cfg.Manager["Main:Number"] ?? "", "before");
                File.WriteAllText(path, "{ \"Main\": { \"Number\": 123 } }");
                ((IConfigurationRoot)cfg.Manager).Reload();
                Equal("123", cfg.Manager["Main:Number"] ?? "", "after Reload");
            }));

        t.Test("InitializeTomlWithModel writes the model under its section", () =>
            WithFile(".toml", ( name, path ) =>
            {
                var returned = cfg.InitializeTomlWithModel<TesterModel>(name, "Main");
                Expect(ReferenceEquals(cfg, returned), "does not return the service");
                Expect(File.Exists(path), "file not created");
                var text = File.ReadAllText(path);
                Expect(text.Contains("[Main]"), $"no [Main] table:\n{text}");
                Expect(text.Contains("Number = 7"), $"no Number:\n{text}");
                Expect(text.Contains("Text = \"hello\""), $"no Text:\n{text}");
            }));

        t.Test("InitializeTomlWithModel keeps an existing file", () =>
            WithFile(".toml", ( name, path ) =>
            {
                File.WriteAllText(path, "[Main]\nNumber = 99\n");
                _ = cfg.InitializeTomlWithModel<TesterModel>(name, "Main");
                Equal("[Main]\nNumber = 99\n", File.ReadAllText(path), "file content");
            }));

        t.Test("TOML model file binds back to the model through the Manager", () =>
            WithFile(".toml", ( name, unused ) =>
            {
                _ = cfg.InitializeTomlWithModel<TesterModel>(name, "Main");
                _ = cfg.Configure(b => b.AddTomlFile(name, optional: false, reloadOnChange: false));
                var model = cfg.Manager.GetSection("Main").Get<TesterModel>();
                NotNull(model, "bound model");
                Equal(7, model!.Number, "Number");
                Equal("hello", model.Text, "Text");
                Expect(model.Items.Distinct().SequenceEqual(["a", "b"]), $"Items: [{string.Join(", ", model.Items)}]");
            }));

        t.Test("InitializeJsonWithModel writes [Description] as // comments above the key in a .jsonc file", () =>
            WithFile(".jsonc", ( name, path ) =>
            {
                _ = cfg.InitializeJsonWithModel<DescribedModel>(name, "Main");
                var text = File.ReadAllText(path).Replace("\r\n", "\n");
                Expect(text.Contains("    // How many times\n    // Second line\n    \"Number\": 7"), $"no multi-line comment above Number:\n{text}");
                Expect(text.Contains("    // Renamed key\n    \"renamed_key\""), $"no comment above the JsonPropertyName key:\n{text}");
                Expect(text.Contains("    // Nested section\n    \"Inner\""), $"no comment above the nested object:\n{text}");
                Expect(text.Contains("      // Inner flag\n      \"Enabled\": true"), $"no comment inside the nested object:\n{text}");
                Expect(text.Contains("    \"Number\": 7,\n    \"Text\": \"hello\","), $"comment on an undescribed key:\n{text}");
            }));

        t.Test("JSONC with description comments binds back to the model through the Manager", () =>
            WithFile(".jsonc", ( name, unused ) =>
            {
                _ = cfg.InitializeJsonWithModel<DescribedModel>(name, "Main");
                _ = cfg.Configure(b => b.AddJsonFile(name, optional: false, reloadOnChange: false));
                var model = cfg.Manager.GetSection("Main").Get<DescribedModel>();
                NotNull(model, "bound model");
                Equal(7, model!.Number, "Number");
                Equal("hello", model.Text, "Text");
                Expect(model.Inner.Enabled, "Inner.Enabled");
            }));

        t.Test("InitializeJsonWithModel ignores descriptions in a .json file", () =>
            WithFile(".json", ( name, path ) =>
            {
                _ = cfg.InitializeJsonWithModel<DescribedModel>(name, "Main");
                var text = File.ReadAllText(path);
                Expect(!text.Contains("//"), $"comment written into a .json file:\n{text}");
                using var doc = JsonDocument.Parse(text);
                Equal(7, doc.RootElement.GetProperty("Main").GetProperty("Number").GetInt32(), "Number");
            }));

        t.Test("InitializeJsonWithModel without descriptions stays plain JSON", () =>
            WithFile(".json", ( name, path ) =>
            {
                _ = cfg.InitializeJsonWithModel<TesterModel>(name, "Main");
                var text = File.ReadAllText(path);
                Expect(!text.Contains("//"), $"unexpected comment:\n{text}");
                using var doc = JsonDocument.Parse(text);
                Equal(7, doc.RootElement.GetProperty("Main").GetProperty("Number").GetInt32(), "Number");
            }));

        t.Test("InitializeTomlWithModel writes [Description] as # comments above the key", () =>
            WithFile(".toml", ( name, path ) =>
            {
                _ = cfg.InitializeTomlWithModel<DescribedModel>(name, "Main");
                var text = File.ReadAllText(path).Replace("\r\n", "\n");
                Expect(text.Contains("[Main]\n# How many times\n# Second line\nNumber = 7"), $"no multi-line comment above Number:\n{text}");
                Expect(text.Contains("# Nested section\n"), $"no comment for the nested section:\n{text}");
                Expect(!text.Contains("# Text"), $"comment on an undescribed key:\n{text}");
            }));

        t.Test("TOML with description comments binds back to the model through the Manager", () =>
            WithFile(".toml", ( name, unused ) =>
            {
                _ = cfg.InitializeTomlWithModel<DescribedModel>(name, "Main");
                _ = cfg.Configure(b => b.AddTomlFile(name, optional: false, reloadOnChange: false));
                var model = cfg.Manager.GetSection("Main").Get<DescribedModel>();
                NotNull(model, "bound model");
                Equal(7, model!.Number, "Number");
                Equal("hello", model.Text, "Text");
            }));

        t.Test("InitializeWithTemplate copies the packaged template", () =>
            WithFile(".json", ( name, path ) =>
            {
                var returned = cfg.InitializeWithTemplate(name, "tester.template.json");
                Expect(ReferenceEquals(cfg, returned), "does not return the service");
                var template = Path.Combine(Core.PluginPath, "resources", "templates", "tester.template.json");
                Equal(File.ReadAllText(template), File.ReadAllText(path), "copied content");
            }));

        t.Test("InitializeWithTemplate keeps an existing file", () =>
            WithFile(".json", ( name, path ) =>
            {
                File.WriteAllText(path, "{ \"kept\": true }");
                _ = cfg.InitializeWithTemplate(name, "tester.template.json");
                Equal("{ \"kept\": true }", File.ReadAllText(path), "file content");
            }));

        t.Test("InitializeWithTemplate with a missing template throws FileNotFoundException", () =>
            WithFile(".json", ( name, path ) =>
            {
                Throws<FileNotFoundException>(() => cfg.InitializeWithTemplate(name, "tester.no-such-template.json"));
                Expect(!File.Exists(path), "a file was created for a missing template");
            }));

        t.Test("Manager is the same instance on every read", () =>
        {
            _ = cfg.GetConfigPath(Unique(".json"));
            Expect(ReferenceEquals(cfg.Manager, cfg.Manager), "different instances");
        });

        t.Test("Configure hands the Manager to the callback and returns the service", () =>
        {
            IConfigurationBuilder? seen = null;
            var returned = cfg.Configure(b => seen = b);
            Expect(ReferenceEquals(cfg, returned), "does not return the service");
            Expect(ReferenceEquals(cfg.Manager, seen), "callback did not receive the Manager");
        });

        t.Test("Configure sources add values and removing them takes the values away", () =>
            WithFile(".json", ( _, _ ) =>
            {
                var key = "TesterKey:" + Guid.NewGuid().ToString("N")[..8];
                _ = cfg.Configure(b => b.AddInMemoryCollection(new Dictionary<string, string?> { [key] = "value" }));
                Equal("value", cfg.Manager[key] ?? "", "value present");
                foreach (var s in cfg.Manager.Sources.Where(s => s is Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource).ToList())
                    _ = cfg.Manager.Sources.Remove(s);
                Expect(cfg.Manager[key] is null, "value still present after removing the source");
            }));

        t.Test("Later sources override earlier ones", () =>
            WithFile(".json", ( _, _ ) =>
            {
                var key = "TesterOverride:" + Guid.NewGuid().ToString("N")[..8];
                _ = cfg.Configure(b => b.AddInMemoryCollection(new Dictionary<string, string?> { [key] = "first" }));
                _ = cfg.Configure(b => b.AddInMemoryCollection(new Dictionary<string, string?> { [key] = "second" }));
                Equal("second", cfg.Manager[key] ?? "", "winning value");
            }));

        return Task.CompletedTask;
    }

    public override bool ProfileInBackground => true;

    public override Task Profile( ProfileContext p )
    {
        var cfg = Cfg;
        var name = Unique(".json");
        var tomlName = Unique(".toml");
        var path = cfg.GetConfigPath(name);
        var tomlPath = cfg.GetConfigPath(tomlName);
        var sourcesBefore = cfg.Manager.Sources.ToList();

        try
        {
            _ = cfg.InitializeJsonWithModel<TesterModel>(name, "Main");
            _ = cfg.InitializeTomlWithModel<TesterModel>(tomlName, "Main");
            _ = cfg.Configure(b => b.AddJsonFile(name, optional: false, reloadOnChange: false));
            var manager = cfg.Manager;

            p.Profile("Configuration.BasePath", () => _ = cfg.BasePath, 200_000);
            p.Profile("Configuration.GetConfigPath (creates folder if missing)", () => _ = cfg.GetConfigPath(name), 50_000);
            p.Profile("Configuration.BasePathExists (stat)", () => _ = cfg.BasePathExists, 50_000);
            p.Profile("Configuration.Manager getter", () => _ = cfg.Manager, 200_000);
            p.Profile("Manager[\"Main:Number\"] (read)", () => _ = manager["Main:Number"], 200_000);
            p.Profile("Manager.GetSection(\"Main\").Get<TesterModel>() (bind)", () => _ = manager.GetSection("Main").Get<TesterModel>(), 20_000);
            p.Profile("Manager.GetValue<int>(\"Main:Number\")", () => _ = manager.GetValue<int>("Main:Number"), 100_000);
            p.Profile("Configuration.InitializeJsonWithModel (file exists)", () => _ = cfg.InitializeJsonWithModel<TesterModel>(name, "Main"), 50_000);
            p.Profile("Configuration.InitializeTomlWithModel (file exists)", () => _ = cfg.InitializeTomlWithModel<TesterModel>(tomlName, "Main"), 50_000);
            p.Profile("Configuration.InitializeWithTemplate (file exists)", () => _ = cfg.InitializeWithTemplate(name, "tester.template.json"), 50_000);
            p.ProfileBudget("Configuration.InitializeJsonWithModel (create + delete file)", () =>
            {
                var n = Unique(".json");
                _ = cfg.InitializeJsonWithModel<TesterModel>(n, "Main");
                File.Delete(cfg.GetConfigPath(n));
            }, 200, 200);
            p.ProfileBudget("Configuration.InitializeTomlWithModel (create + delete file)", () =>
            {
                var n = Unique(".toml");
                _ = cfg.InitializeTomlWithModel<TesterModel>(n, "Main");
                File.Delete(cfg.GetConfigPath(n));
            }, 200, 200);
            p.ProfileBudget("IConfigurationRoot.Reload (1 json source)", () => ((IConfigurationRoot)manager).Reload(), 200, 200);
        }
        finally
        {
            foreach (var s in cfg.Manager.Sources.Except(sourcesBefore).ToList()) _ = cfg.Manager.Sources.Remove(s);
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(tomlPath)) File.Delete(tomlPath);
        }

        return Task.CompletedTask;
    }
}
