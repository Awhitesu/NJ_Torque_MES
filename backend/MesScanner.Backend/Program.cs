using System.Text;
using MesScanner.Backend.Hubs;
using MesScanner.Backend.Models;
using MesScanner.Backend.Services;
using Microsoft.AspNetCore.SignalR;
using ScanModule;

var builder = WebApplication.CreateBuilder(args);

// 1. 注册服务
builder.Services.AddSignalR();
builder.Services.AddSingleton<AppConfigFileService>();
builder.Services.AddSingleton<WindowsDirectoryPickerService>();
builder.Services.AddSingleton<TorqueControllerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TorqueControllerService>());
builder.Services.AddBarcodeScannerModule();
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyHeader()
              .AllowAnyMethod()
              .SetIsOriginAllowed(_ => true)
              .AllowCredentials();
    });
});

var app = builder.Build();

// 2. 配置中间件
app.UseCors("AllowAll");
app.MapHub<TorqueHub>("/torqueHub");
app.MapBarcodeScannerModule();

// 3. 业务 API
app.MapGet("/", () => "Torque MES Backend Running...");
app.MapGet("/app-config", (AppConfigFileService cfgService) => Results.Ok(cfgService.GetDto()));

app.MapPost("/app-config", async (
    AppConfigDto req,
    AppConfigFileService cfgService,
    TorqueControllerService torqueService) =>
{
    var saved = cfgService.SaveDto(req);
    await torqueService.UpdateControllerEndpointAsync(saved.DesoutterIp, saved.DesoutterPort, reconnect: false);
    return Results.Ok(new
    {
        config = saved,
        filePath = cfgService.ConfigFilePath
    });
});

app.MapPost("/app-config/pick-directory", (DirectoryPickRequest req, WindowsDirectoryPickerService picker) =>
{
    var path = picker.PickDirectory(req.Title ?? "选择目录");
    if (string.IsNullOrWhiteSpace(path))
    {
        return Results.BadRequest(new { message = "未选择目录" });
    }

    return Results.Ok(new { path });
});

// 执行定扭相关指令 (前端手动测试用)
app.MapPost("/command", async (string mid, string pset, TorqueControllerService service) => {
    // 统一采用 20 字节标准 Header: Length(4)+MID(4)+Rev(3)+NoAck(1)+Station(2)+Spindle(2)+Spare(4)
    if (mid == "0018") {
         // MID 0018: Select PSet. Length 23 = Header(20) + PSetID(3)
         await service.SendPacketAsync($"00230018001         {pset.PadLeft(3, '0')}");
    } else if (mid == "0043") {
         await service.SendPacketAsync("00200043001         ");
    } else if (mid == "0042") {
         await service.SendPacketAsync("00200042001         ");
    } else if (mid == "0044") {
         await service.SendPacketAsync("00200044001         ");
    } else if (mid == "0060") {
         await service.SendPacketAsync("00200060001         ");
    } else if (mid == "0003") {
         await service.SendPacketAsync("00200003001         ");
    } else if (mid == "0129") {
         await service.SendPacketAsync("00200129001         ");
    }

    return Results.Ok();
});

// 获取当前控制器目标地址
app.MapGet("/controller/config", (TorqueControllerService service) =>
{
    var endpoint = service.GetControllerEndpoint();
    return Results.Ok(new { ip = endpoint.Ip, port = endpoint.Port });
});

// 更新控制器目标地址，支持可选立即重连
app.MapPost("/controller/config", async (
    ControllerConfigRequest req,
    TorqueControllerService service,
    AppConfigFileService cfgService) =>
{
    if (string.IsNullOrWhiteSpace(req.Ip))
    {
        return Results.BadRequest(new { message = "IP 不能为空" });
    }

    if (req.Port <= 0 || req.Port > 65535)
    {
        return Results.BadRequest(new { message = "端口范围应为 1-65535" });
    }

    await service.UpdateControllerEndpointAsync(req.Ip, req.Port, req.Reconnect);
    var cfg = cfgService.GetDto();
    cfg.DesoutterIp = req.Ip.Trim();
    cfg.DesoutterPort = req.Port;
    cfgService.SaveDto(cfg);
    return Results.Ok(new { message = "控制器地址已更新", ip = req.Ip, port = req.Port, reconnect = req.Reconnect });
});


// 手动触发重新连接控制器（前端"连接接口"按钮调用）
app.MapPost("/reconnect", async (TorqueControllerService service) =>
{
    await service.TriggerReconnectAsync();
    return Results.Ok();
});

// 手动断开控制器连接（并关闭自动重连）
app.MapPost("/disconnect", async (TorqueControllerService service) =>
{
    await service.DisconnectAsync();
    return Results.Ok();
});

// 保存日志到本地文件
app.MapPost("/saveLogs", async (LogSaveRequest req, AppConfigFileService cfgService) =>
{
    try 
    {
        string savePath = string.IsNullOrEmpty(req.Path) ? cfgService.GetLogSavePath() : req.Path;
        if (!Directory.Exists(savePath)) Directory.CreateDirectory(savePath);
        
        string fullPath = Path.Combine(savePath, req.FileName);
        await File.WriteAllTextAsync(fullPath, req.Content);
        return Results.Ok(new { message = "Save success", path = fullPath });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.Run();

public record LogSaveRequest(string FileName, string Content, string Path);
public record ControllerConfigRequest(string Ip, int Port, bool Reconnect = false);
