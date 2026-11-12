using System.Text.Json.Serialization;
using MagOps.Infrastructure.Storage;

namespace MagOps.Infrastructure.Services;

public sealed class EquipmentCatalog
{
    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = [];

    [JsonPropertyName("models_by_category")]
    public Dictionary<string, List<string>> ModelsByCategory { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class EquipmentCatalogService
{
    public const string LegacyPumpCategory = "光泵探头";
    public const string MagnetometerSensorCategory = "磁测探头";
    public const string GpamM350Rm = "GPAM-M350-RM";
    public const string DjiMatrice400 = "DJI Matrice 400";
    public const string DjiMatrice350Rtk = "DJI Matrice 350 RTK";

    private static readonly HashSet<string> HiddenCatalogCategories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "总场磁力仪",
            "采集器"
        };

    private readonly JsonStore _store;

    public EquipmentCatalogService(JsonStore store) => _store = store;

    public EquipmentCatalog Load(string root)
    {
        string path = ProjectPaths.Json(root, "equipment_catalog.json");
        EquipmentCatalog result = Defaults();
        if (!File.Exists(path))
            return result;

        EquipmentCatalog loaded = _store.Read(
            path,
            new EquipmentCatalog());

        foreach (string rawCategory in loaded.Categories ?? [])
        {
            string category = NormalizeCategory(rawCategory);
            if (category.Length == 0 ||
                HiddenCatalogCategories.Contains(category))
            {
                continue;
            }

            if (!result.Categories.Contains(
                    category,
                    StringComparer.OrdinalIgnoreCase))
            {
                result.Categories.Add(category);
            }
        }

        foreach (var (rawCategory, rawModels) in
                 loaded.ModelsByCategory ??
                 new Dictionary<string, List<string>>())
        {
            string category = NormalizeCategory(rawCategory);
            if (category.Length == 0 ||
                HiddenCatalogCategories.Contains(category))
            {
                continue;
            }

            if (!result.Categories.Contains(
                    category,
                    StringComparer.OrdinalIgnoreCase))
            {
                result.Categories.Add(category);
            }

            if (!result.ModelsByCategory.TryGetValue(
                    category,
                    out List<string>? models))
            {
                models = [];
                result.ModelsByCategory[category] = models;
            }

            foreach (string rawModel in rawModels ?? [])
            {
                string model = NormalizeModel(rawModel);
                if (model.Length == 0)
                    continue;

                // The old legacy pump category contained a placeholder instead
                // of a real model. Do not carry it into the official catalog.
                if (category.Equals(
                        MagnetometerSensorCategory,
                        StringComparison.OrdinalIgnoreCase) &&
                    model.Equals(
                        "固定型号光泵探头（待补录具体型号）",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!models.Contains(
                        model,
                        StringComparer.OrdinalIgnoreCase))
                {
                    models.Add(model);
                }
            }
        }

        EnsureBuiltInModel(
            result,
            MagnetometerSensorCategory,
            GpamM350Rm);
        EnsureBuiltInModel(
            result,
            "无人机",
            DjiMatrice400);
        EnsureBuiltInModel(
            result,
            "无人机",
            DjiMatrice350Rtk);

        return result;
    }

    public EquipmentCatalog AddModel(
        string root,
        string category,
        string model)
    {
        category = NormalizeCategory(category);
        model = NormalizeModel(model);
        if (category.Length == 0 || model.Length == 0)
            throw new ArgumentException("设备类别和型号不能为空");
        if (HiddenCatalogCategories.Contains(category))
        {
            throw new InvalidOperationException(
                $"设备类别“{category}”已从新增设备目录中移除。");
        }

        using var writeLock =
            WorkspaceFileLock.AcquireForRoot(root);
        EquipmentCatalog catalog = Load(root);

        if (!catalog.Categories.Contains(
                category,
                StringComparer.OrdinalIgnoreCase))
        {
            catalog.Categories.Add(category);
        }

        if (!catalog.ModelsByCategory.TryGetValue(
                category,
                out List<string>? models))
        {
            models = [];
            catalog.ModelsByCategory[category] = models;
        }

        if (!models.Contains(
                model,
                StringComparer.OrdinalIgnoreCase))
        {
            models.Add(model);
        }

        _store.Write(
            ProjectPaths.Json(
                root,
                "equipment_catalog.json"),
            catalog);
        return catalog;
    }

    public static string NormalizeCategory(string? value)
    {
        string category = (value ?? "").Trim();
        return category.Equals(
                LegacyPumpCategory,
                StringComparison.OrdinalIgnoreCase)
            ? MagnetometerSensorCategory
            : category;
    }

    public static string NormalizeModel(string? value)
    {
        string model = (value ?? "").Trim();
        if (model.Length == 0)
            return "";

        string key = string.Concat(
            model
                .ToLowerInvariant()
                .Replace('－', '-')
                .Replace('—', '-')
                .Where(c =>
                    !char.IsWhiteSpace(c) &&
                    c != '-' &&
                    c != '_'));

        if (key is
            "dj400" or
            "m400" or
            "matrice400" or
            "djimatrice400")
        {
            return DjiMatrice400;
        }

        if (key is
            "dj350" or
            "m350" or
            "m350rtk" or
            "matrice350rtk" or
            "djimatrice350rtk" or
            "经纬m350rtk")
        {
            return DjiMatrice350Rtk;
        }

        return model;
    }

    private static void EnsureBuiltInModel(
        EquipmentCatalog catalog,
        string category,
        string model)
    {
        if (!catalog.ModelsByCategory.TryGetValue(
                category,
                out List<string>? models))
        {
            models = [];
            catalog.ModelsByCategory[category] = models;
        }

        if (!models.Contains(
                model,
                StringComparer.OrdinalIgnoreCase))
        {
            models.Insert(0, model);
        }
    }

    public static EquipmentCatalog Defaults() => new()
    {
        Categories =
        [
            "日变基站",
            "无人机",
            MagnetometerSensorCategory,
            "其他"
        ],
        ModelsByCategory =
            new Dictionary<string, List<string>>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["日变基站"] = ["GSM-19T"],
                ["无人机"] =
                [
                    DjiMatrice400,
                    DjiMatrice350Rtk
                ],
                [MagnetometerSensorCategory] =
                    [GpamM350Rm],
                ["其他"] = []
            }
    };
}
