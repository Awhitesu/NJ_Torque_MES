using MesScanner.Backend.Hubs;
using MesScanner.Backend.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddSignalR();
builder.Services.AddSingleton<TorqueControllerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TorqueControllerService>());

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.WithOrigins("http://localhost:5173") // Vite 默认端口
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

var app = builder.Build();

app.UseCors("AllowAll");

// Configure the HTTP request pipeline.
app.MapHub<TorqueHub>("/torqueHub");

app.MapGet("/", () => "MES Torque Backend is Running");

// 给 Hub 提供发送指令到 Service 的接口
app.MapPost("/api/command", async (string mid, string pset, TorqueControllerService service) => {
    if (mid == "0018") {
         await service.SendPacketAsync($"00230018            {pset.PadLeft(3, '0')}");
    } else if (mid == "0043") {
         await service.SendPacketAsync("00200043001         ");
    } else if (mid == "0042") {
         await service.SendPacketAsync("00200042001         ");
    } else if (mid == "0044") {
         await service.SendPacketAsync("00200044001         ");
    } else if (mid == "0060") {
         await service.SendPacketAsync("00200060001         ");
    } else if (mid == "0003") {
         await service.SendPacketAsync("00200003            ");
    }
    return Results.Ok();
});

// 手动触发重新连接控制器（前端"连接接口"按钮调用）
app.MapPost("/api/reconnect", async (TorqueControllerService service) =>
{
    await service.TriggerReconnectAsync();
    return Results.Ok();
});

app.Run("http://localhost:5246");
