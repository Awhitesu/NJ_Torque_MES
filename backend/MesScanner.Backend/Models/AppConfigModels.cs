namespace MesScanner.Backend.Models;

public class AppConfigDto
{
    public string OrderApiUrl { get; set; } = string.Empty;
    public string RouteApiUrl { get; set; } = string.Empty;
    public string SingleMaterialApiUrl { get; set; } = string.Empty;
    public string FullMaterialApiUrl { get; set; } = string.Empty;
    public string MesUploadApiUrl { get; set; } = string.Empty;
    public string TechnicsProcessCode { get; set; } = string.Empty;
    public string TechnicsProcessName { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string UserAccount { get; set; } = string.Empty;
    public string DeviceCode { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;

    public string DesoutterIp { get; set; } = string.Empty;
    public int DesoutterPort { get; set; }

    public string ScannerIp { get; set; } = string.Empty;
    public int ScannerPort { get; set; }
    public string BarcodeRegex { get; set; } = string.Empty;

    public string LogSavePath { get; set; } = string.Empty;
    public string AdminUsername { get; set; } = string.Empty;
    public string AdminPassword { get; set; } = string.Empty;
}

public class AppRuntimeFileConfig
{
    public MesConfigSection Mes { get; set; } = new();
    public TorqueControllerSection TorqueController { get; set; } = new();
    public ScannerSection Scanner { get; set; } = new();
    public SystemSection System { get; set; } = new();

    public static AppRuntimeFileConfig CreateDefault()
    {
        return new AppRuntimeFileConfig
        {
            Mes = new MesConfigSection
            {
                OrderApiUrl = "/mes-api/api/OrderInfo/GetOtherOrderInfoByProcess",
                RouteApiUrl = "/mes-api/api/OrderInfo/GetTechRouteListByCode",
                SingleMaterialApiUrl = "/mes-api/api/ProduceMessage/SingleCheckInput",
                FullMaterialApiUrl = "/mes-api/api/ProduceMessage/CompleteCheckInput",
                MesUploadApiUrl = "/mes-push/api/ProduceMessage/PushPackMessageToMes",
                TechnicsProcessCode = "CTP_P1240",
                TechnicsProcessName = "默认工序",
                UserName = "admin",
                UserAccount = "admin",
                DeviceCode = "",
                DeviceName = ""
            },
            TorqueController = new TorqueControllerSection
            {
                Ip = "192.168.5.212",
                Port = 4545
            },
            Scanner = new ScannerSection
            {
                Ip = "",
                Port = 0,
                BarcodeRegex = ".*"
            },
            System = new SystemSection
            {
                LogSavePath = @"C:\NJ_Torque_Logs",
                AdminUsername = "admin",
                AdminPassword = "123"
            }
        };
    }
}

public class MesConfigSection
{
    public string OrderApiUrl { get; set; } = string.Empty;
    public string RouteApiUrl { get; set; } = string.Empty;
    public string SingleMaterialApiUrl { get; set; } = string.Empty;
    public string FullMaterialApiUrl { get; set; } = string.Empty;
    public string MesUploadApiUrl { get; set; } = string.Empty;
    public string TechnicsProcessCode { get; set; } = string.Empty;
    public string TechnicsProcessName { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string UserAccount { get; set; } = string.Empty;
    public string DeviceCode { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
}

public class TorqueControllerSection
{
    public string Ip { get; set; } = string.Empty;
    public int Port { get; set; }
}

public class ScannerSection
{
    public string Ip { get; set; } = string.Empty;
    public int Port { get; set; }
    public string BarcodeRegex { get; set; } = string.Empty;
}

public class SystemSection
{
    public string LogSavePath { get; set; } = string.Empty;
    public string AdminUsername { get; set; } = string.Empty;
    public string AdminPassword { get; set; } = string.Empty;
}

public record DirectoryPickRequest(string? Title);
