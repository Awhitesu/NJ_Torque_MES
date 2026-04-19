using Microsoft.AspNetCore.SignalR;
using MesScanner.Backend.Models;
using MesScanner.Backend.Services;

namespace MesScanner.Backend.Hubs;

public class TorqueHub : Hub
{
    private readonly ILogger<TorqueHub> _logger;
    private readonly TorqueControllerService _torqueService;

    public TorqueHub(ILogger<TorqueHub> logger, TorqueControllerService torqueService)
    {
        _logger = logger;
        _torqueService = torqueService;
    }

    /// <summary>
    /// 前端页面连上 SignalR 时，立即把当前 TCP 连接状态推送给该客户端
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("前端客户端已连接: {ConnectionId}", Context.ConnectionId);

        bool tcpConnected = _torqueService.IsConnected;
        string msg = tcpConnected
            ? "[System] TCP 链路已连通 (前端重连后同步状态)"
            : "[System] TCP 未连接，等待连接控制器...";
        string level = tcpConnected ? "success" : "warn";

        await Clients.Caller.SendAsync("ReceiveLog", new LogEntry
        {
            Level = level,
            Msg = msg,
            IsHeartbeat = false
        });

        // 如果已经连接，也推送一个连接状态事件让前端绿灯亮
        if (tcpConnected)
        {
            await Clients.Caller.SendAsync("ControllerConnected");
        }

        await base.OnConnectedAsync();
    }

    public async Task SendCommand(string mid, string pset = "")
    {
        _logger.LogInformation("Received command from client: {MID} {PSet}", mid, pset);
    }
}
