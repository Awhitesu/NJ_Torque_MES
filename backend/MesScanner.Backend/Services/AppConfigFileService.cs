using System.Text.Json;
using MesScanner.Backend.Models;

namespace MesScanner.Backend.Services;

public class AppConfigFileService
{
    private readonly ILogger<AppConfigFileService> _logger;
    private readonly object _sync = new();
    private readonly string _configDirectory;
    private readonly string _configFilePath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private AppRuntimeFileConfig _runtimeConfig = AppRuntimeFileConfig.CreateDefault();

    public AppConfigFileService(IWebHostEnvironment env, ILogger<AppConfigFileService> logger)
    {
        _logger = logger;

        var contentRoot = env.ContentRootPath;
        var projectRoot = Directory.GetParent(contentRoot)?.Parent?.FullName ?? contentRoot;
        _configDirectory = Path.Combine(projectRoot, "Config");
        _configFilePath = Path.Combine(_configDirectory, "app-config.json");

        LoadOrCreate();
    }

    public string ConfigFilePath => _configFilePath;

    public AppConfigDto GetDto()
    {
        lock (_sync)
        {
            return ToDto(_runtimeConfig);
        }
    }

    public AppConfigDto SaveDto(AppConfigDto dto)
    {
        lock (_sync)
        {
            _runtimeConfig = Normalize(FromDto(dto));
            PersistUnsafe();
            return ToDto(_runtimeConfig);
        }
    }

    public string GetLogSavePath()
    {
        lock (_sync)
        {
            return _runtimeConfig.System.LogSavePath;
        }
    }

    private void LoadOrCreate()
    {
        lock (_sync)
        {
            Directory.CreateDirectory(_configDirectory);

            if (!File.Exists(_configFilePath))
            {
                _runtimeConfig = AppRuntimeFileConfig.CreateDefault();
                PersistUnsafe();
                _logger.LogInformation("配置文件不存在，已自动创建: {Path}", _configFilePath);
                return;
            }

            try
            {
                var json = File.ReadAllText(_configFilePath);
                var parsed = JsonSerializer.Deserialize<AppRuntimeFileConfig>(json, _jsonOptions);
                _runtimeConfig = Normalize(parsed ?? AppRuntimeFileConfig.CreateDefault());
                PersistUnsafe();
                _logger.LogInformation("已加载配置文件: {Path}", _configFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取配置文件失败，已回退默认配置并覆盖写入: {Path}", _configFilePath);
                _runtimeConfig = AppRuntimeFileConfig.CreateDefault();
                PersistUnsafe();
            }
        }
    }

    private void PersistUnsafe()
    {
        Directory.CreateDirectory(_configDirectory);
        var json = JsonSerializer.Serialize(_runtimeConfig, _jsonOptions);
        File.WriteAllText(_configFilePath, json);
    }

    private static AppRuntimeFileConfig Normalize(AppRuntimeFileConfig cfg)
    {
        var d = AppRuntimeFileConfig.CreateDefault();
        cfg.Mes ??= new MesConfigSection();
        cfg.TorqueController ??= new TorqueControllerSection();
        cfg.Scanner ??= new ScannerSection();
        cfg.System ??= new SystemSection();

        cfg.Mes.OrderApiUrl = KeepOrDefault(cfg.Mes.OrderApiUrl, d.Mes.OrderApiUrl);
        cfg.Mes.RouteApiUrl = KeepOrDefault(cfg.Mes.RouteApiUrl, d.Mes.RouteApiUrl);
        cfg.Mes.SingleMaterialApiUrl = KeepOrDefault(cfg.Mes.SingleMaterialApiUrl, d.Mes.SingleMaterialApiUrl);
        cfg.Mes.FullMaterialApiUrl = KeepOrDefault(cfg.Mes.FullMaterialApiUrl, d.Mes.FullMaterialApiUrl);
        cfg.Mes.MesUploadApiUrl = KeepOrDefault(cfg.Mes.MesUploadApiUrl, d.Mes.MesUploadApiUrl);
        cfg.Mes.TechnicsProcessCode = KeepOrDefault(cfg.Mes.TechnicsProcessCode, d.Mes.TechnicsProcessCode);
        cfg.Mes.TechnicsProcessName = KeepOrDefault(cfg.Mes.TechnicsProcessName, d.Mes.TechnicsProcessName);
        cfg.Mes.UserName = KeepOrDefault(cfg.Mes.UserName, d.Mes.UserName);
        cfg.Mes.UserAccount = KeepOrDefault(cfg.Mes.UserAccount, d.Mes.UserAccount);
        cfg.Mes.DeviceCode = cfg.Mes.DeviceCode?.Trim() ?? "";
        cfg.Mes.DeviceName = cfg.Mes.DeviceName?.Trim() ?? "";

        cfg.TorqueController.Ip = KeepOrDefault(cfg.TorqueController.Ip, d.TorqueController.Ip);
        cfg.TorqueController.Port = cfg.TorqueController.Port > 0 ? cfg.TorqueController.Port : d.TorqueController.Port;

        cfg.Scanner.Ip = cfg.Scanner.Ip?.Trim() ?? "";
        cfg.Scanner.Port = cfg.Scanner.Port > 0 ? cfg.Scanner.Port : 0;
        cfg.Scanner.BarcodeRegex = KeepOrDefault(cfg.Scanner.BarcodeRegex, d.Scanner.BarcodeRegex);

        cfg.System.LogSavePath = KeepOrDefault(cfg.System.LogSavePath, d.System.LogSavePath);
        cfg.System.TighteningMaxRetries = NormalizeRetryCount(cfg.System.TighteningMaxRetries, d.System.TighteningMaxRetries);
        cfg.System.AdminUsername = KeepOrDefault(cfg.System.AdminUsername, d.System.AdminUsername);
        cfg.System.AdminPassword = KeepOrDefault(cfg.System.AdminPassword, d.System.AdminPassword);
        return cfg;
    }

    private static string KeepOrDefault(string? raw, string fallback)
    {
        var v = raw?.Trim();
        return string.IsNullOrWhiteSpace(v) ? fallback : v;
    }

    private static int NormalizeRetryCount(int raw, int fallback)
    {
        var candidate = raw > 0 ? raw : fallback;
        return Math.Clamp(candidate, 1, 20);
    }

    private static AppConfigDto ToDto(AppRuntimeFileConfig cfg)
    {
        return new AppConfigDto
        {
            OrderApiUrl = cfg.Mes.OrderApiUrl,
            RouteApiUrl = cfg.Mes.RouteApiUrl,
            SingleMaterialApiUrl = cfg.Mes.SingleMaterialApiUrl,
            FullMaterialApiUrl = cfg.Mes.FullMaterialApiUrl,
            MesUploadApiUrl = cfg.Mes.MesUploadApiUrl,
            TechnicsProcessCode = cfg.Mes.TechnicsProcessCode,
            TechnicsProcessName = cfg.Mes.TechnicsProcessName,
            UserName = cfg.Mes.UserName,
            UserAccount = cfg.Mes.UserAccount,
            DeviceCode = cfg.Mes.DeviceCode,
            DeviceName = cfg.Mes.DeviceName,
            DesoutterIp = cfg.TorqueController.Ip,
            DesoutterPort = cfg.TorqueController.Port,
            ScannerIp = cfg.Scanner.Ip,
            ScannerPort = cfg.Scanner.Port,
            BarcodeRegex = cfg.Scanner.BarcodeRegex,
            LogSavePath = cfg.System.LogSavePath,
            TighteningMaxRetries = cfg.System.TighteningMaxRetries,
            AdminUsername = cfg.System.AdminUsername,
            AdminPassword = cfg.System.AdminPassword
        };
    }

    private static AppRuntimeFileConfig FromDto(AppConfigDto dto)
    {
        return new AppRuntimeFileConfig
        {
            Mes = new MesConfigSection
            {
                OrderApiUrl = dto.OrderApiUrl,
                RouteApiUrl = dto.RouteApiUrl,
                SingleMaterialApiUrl = dto.SingleMaterialApiUrl,
                FullMaterialApiUrl = dto.FullMaterialApiUrl,
                MesUploadApiUrl = dto.MesUploadApiUrl,
                TechnicsProcessCode = dto.TechnicsProcessCode,
                TechnicsProcessName = dto.TechnicsProcessName,
                UserName = dto.UserName,
                UserAccount = dto.UserAccount,
                DeviceCode = dto.DeviceCode,
                DeviceName = dto.DeviceName
            },
            TorqueController = new TorqueControllerSection
            {
                Ip = dto.DesoutterIp,
                Port = dto.DesoutterPort
            },
            Scanner = new ScannerSection
            {
                Ip = dto.ScannerIp,
                Port = dto.ScannerPort,
                BarcodeRegex = dto.BarcodeRegex
            },
            System = new SystemSection
            {
                LogSavePath = dto.LogSavePath,
                TighteningMaxRetries = dto.TighteningMaxRetries,
                AdminUsername = dto.AdminUsername,
                AdminPassword = dto.AdminPassword
            }
        };
    }
}
