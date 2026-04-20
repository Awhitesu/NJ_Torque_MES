using System.Text;
using MesScanner.Backend.Hubs;
using MesScanner.Backend.Services;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

// 1. 注册服务
builder.Services.AddSignalR();
builder.Services.AddSingleton<TorqueControllerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TorqueControllerService>());
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

// 3. 业务 API
app.MapGet("/", () => "Torque MES Backend Running...");

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


// 手动触发重新连接控制器（前端"连接接口"按钮调用）
app.MapPost("/reconnect", async (TorqueControllerService service) =>
{
    await service.TriggerReconnectAsync();
    return Results.Ok();
});

// 保存日志到本地文件
app.MapPost("/saveLogs", async (LogSaveRequest req) =>
{
    try 
    {
        string savePath = string.IsNullOrEmpty(req.Path) ? "C:\\NJ_Torque_Logs" : req.Path;
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
