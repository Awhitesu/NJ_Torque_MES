using MesScanner.Backend.Hubs;
using MesScanner.Backend.Models;
using MesScanner.Backend.Services;
using ScanModule;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();
builder.Services.AddSingleton<AppConfigFileService>();
builder.Services.AddSingleton<WindowsDirectoryPickerService>();
builder.Services.AddSingleton<TorqueControllerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TorqueControllerService>());
builder.Services.AddBarcodeScannerModule();
builder.Services.AddHttpClient();
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
var proxyMethods = new[] { "GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS" };

app.UseCors("AllowAll");
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHub<TorqueHub>("/torqueHub");
app.MapHub<TorqueHub>("/api/torqueHub");
app.MapBarcodeScannerModule();
app.MapGroup("/api").MapBarcodeScannerModule();

app.MapMethods("/mes-api/{**path}", proxyMethods, (
    HttpContext httpContext,
    string? path,
    IHttpClientFactory httpClientFactory,
    AppConfigFileService cfgService) =>
{
    return ProxyRequestAsync(
        httpContext,
        httpClientFactory.CreateClient(),
        cfgService.GetMesApiProxyTarget(),
        path);
});

app.MapMethods("/mes-push/{**path}", proxyMethods, (
    HttpContext httpContext,
    string? path,
    IHttpClientFactory httpClientFactory,
    AppConfigFileService cfgService) =>
{
    return ProxyRequestAsync(
        httpContext,
        httpClientFactory.CreateClient(),
        cfgService.GetMesPushProxyTarget(),
        path);
});

app.MapGet("/", () => "Torque MES Backend Running...");

var getAppConfigHandler = (AppConfigFileService cfgService) => Results.Ok(cfgService.GetDto());
app.MapGet("/app-config", getAppConfigHandler);
app.MapGet("/api/app-config", getAppConfigHandler);

var saveAppConfigHandler = async (
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
};
app.MapPost("/app-config", saveAppConfigHandler);
app.MapPost("/api/app-config", saveAppConfigHandler);

var pickDirHandler = (DirectoryPickRequest req, WindowsDirectoryPickerService picker) =>
{
    var path = picker.PickDirectory(req.Title ?? "选择目录");
    if (string.IsNullOrWhiteSpace(path))
    {
        return Results.BadRequest(new { message = "未选择目录" });
    }

    return Results.Ok(new { path });
};
app.MapPost("/app-config/pick-directory", pickDirHandler);
app.MapPost("/api/app-config/pick-directory", pickDirHandler);

var commandHandler = async (string mid, string pset, TorqueControllerService service) =>
{
    if (mid == "0018")
    {
        await service.SendPacketAsync($"00230018001         {pset.PadLeft(3, '0')}");
    }
    else if (mid == "0043")
    {
        await service.SendPacketAsync("00200043001         ");
    }
    else if (mid == "0042")
    {
        await service.SendPacketAsync("00200042001         ");
    }
    else if (mid == "0044")
    {
        await service.SendPacketAsync("00200044001         ");
    }
    else if (mid == "0060")
    {
        await service.SendPacketAsync("00200060001         ");
    }
    else if (mid == "0003")
    {
        await service.SendPacketAsync("00200003001         ");
    }
    else if (mid == "0129")
    {
        await service.SendPacketAsync("00200129001         ");
    }

    return Results.Ok();
};
app.MapPost("/command", commandHandler);
app.MapPost("/api/command", commandHandler);

var getControllerConfigHandler = (TorqueControllerService service) =>
{
    var endpoint = service.GetControllerEndpoint();
    return Results.Ok(new { ip = endpoint.Ip, port = endpoint.Port });
};
app.MapGet("/controller/config", getControllerConfigHandler);
app.MapGet("/api/controller/config", getControllerConfigHandler);

var saveControllerConfigHandler = async (
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
};
app.MapPost("/controller/config", saveControllerConfigHandler);
app.MapPost("/api/controller/config", saveControllerConfigHandler);

var reconnectHandler = async (TorqueControllerService service) =>
{
    await service.TriggerReconnectAsync();
    return Results.Ok();
};
app.MapPost("/reconnect", reconnectHandler);
app.MapPost("/api/reconnect", reconnectHandler);

var disconnectHandler = async (TorqueControllerService service) =>
{
    await service.DisconnectAsync();
    return Results.Ok();
};
app.MapPost("/disconnect", disconnectHandler);
app.MapPost("/api/disconnect", disconnectHandler);

var saveLogsHandler = async (LogSaveRequest req, AppConfigFileService cfgService) =>
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
};
app.MapPost("/saveLogs", saveLogsHandler);
app.MapPost("/api/saveLogs", saveLogsHandler);

app.MapFallbackToFile("index.html");

app.Run();

static async Task<IResult> ProxyRequestAsync(
    HttpContext context,
    HttpClient client,
    string targetBaseUrl,
    string? path)
{
    if (string.IsNullOrWhiteSpace(targetBaseUrl))
    {
        return Results.Problem("代理目标地址未配置");
    }

    var requestUri = BuildProxyUri(targetBaseUrl, path, context.Request.QueryString);
    using var proxyRequest = new HttpRequestMessage(new HttpMethod(context.Request.Method), requestUri);

    if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
    {
        proxyRequest.Content = new StreamContent(context.Request.Body);
    }

    foreach (var header in context.Request.Headers)
    {
        if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (!proxyRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
        {
            proxyRequest.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
    }

    using var proxyResponse = await client.SendAsync(proxyRequest, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);

    context.Response.StatusCode = (int)proxyResponse.StatusCode;

    foreach (var header in proxyResponse.Headers)
    {
        context.Response.Headers[header.Key] = header.Value.ToArray();
    }
    foreach (var header in proxyResponse.Content.Headers)
    {
        context.Response.Headers[header.Key] = header.Value.ToArray();
    }

    context.Response.Headers.Remove("transfer-encoding");
    await proxyResponse.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    return Results.Empty;
}

static string BuildProxyUri(string targetBaseUrl, string? path, QueryString queryString)
{
    var baseUrl = targetBaseUrl.Trim().TrimEnd('/');
    var relativePath = string.IsNullOrWhiteSpace(path) ? string.Empty : $"/{path.TrimStart('/')}";
    return $"{baseUrl}{relativePath}{queryString}";
}

public record LogSaveRequest(string FileName, string Content, string Path);
public record ControllerConfigRequest(string Ip, int Port, bool Reconnect = false);
