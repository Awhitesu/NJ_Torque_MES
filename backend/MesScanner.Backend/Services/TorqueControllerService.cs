using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using MesScanner.Backend.Hubs;
using MesScanner.Backend.Models;

namespace MesScanner.Backend.Services;

public class TorqueControllerService : BackgroundService
{
    private const int AutoReconnectIntervalMs = 10_000;
    private readonly ILogger<TorqueControllerService> _logger;
    private readonly IHubContext<TorqueHub> _hubContext;
    private readonly object _configLock = new();
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private string _ip;
    private int _port;
    private bool _transportConnected = false;
    private bool _isConnected = false;
    private bool _allowConnection = true;
    public bool IsConnected => _isConnected;
    private DateTime _lastPacketTime = DateTime.MinValue;

    public TorqueControllerService(
        ILogger<TorqueControllerService> logger,
        IHubContext<TorqueHub> hubContext,
        AppConfigFileService appConfigFileService)
    {
        _logger = logger;
        _hubContext = hubContext;

        var cfg = appConfigFileService.GetDto();
        _ip = string.IsNullOrWhiteSpace(cfg.DesoutterIp) ? "192.168.5.212" : cfg.DesoutterIp.Trim();
        _port = cfg.DesoutterPort > 0 ? cfg.DesoutterPort : 4545;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine("\n[System] 定扭后端引擎 (带心跳) 已启动...");
        
        // 启动独立的心跳监控任务
        _ = Task.Run(() => HeartbeatLoopAsync(stoppingToken));

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_transportConnected && _allowConnection)
            {
                await ConnectAndHandshakeAsync();
            }
            await Task.Delay(AutoReconnectIntervalMs, stoppingToken);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (_isConnected && (DateTime.Now - _lastPacketTime).TotalSeconds > 2)
            {
                await SendPacketAsync("00209999            ");
            }
            await Task.Delay(1000, token);
        }
    }

    private async Task ConnectAndHandshakeAsync()
    {
        try
        {
            string ip;
            int port;
            lock (_configLock)
            {
                ip = _ip;
                port = _port;
            }

            Console.WriteLine($"\n[Service] {DateTime.Now:HH:mm:ss} >>> 尝试连接控制器: {ip}:{port}");
            _tcpClient = new TcpClient();
            
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            {
                await _tcpClient.ConnectAsync(ip, port, cts.Token);
            }

            if (_tcpClient.Connected)
            {
                _stream = _tcpClient.GetStream();
                _transportConnected = true;
                _isConnected = false; // 仅 MID 0002 握手成功后才置 true
                Console.WriteLine($"[Service] {DateTime.Now:HH:mm:ss} ✔ TCP链路已建立，等待 MID 0002 握手...");
                await LogToFrontend("info", "[System] TCP 链路已建立，等待握手确认 (MID 0002)");

                await Task.Delay(500); 
                await SendPacketAsync("00200001001         ", allowWhenNotReady: true); 
                
                // 开启读取循环
                _ = Task.Run(() => ReadLoopAsync());
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Service] {DateTime.Now:HH:mm:ss} ✘ 连接异常: {ex.Message}");
            _transportConnected = false;
            _isConnected = false;
        }
    }

    public (string Ip, int Port) GetControllerEndpoint()
    {
        lock (_configLock)
        {
            return (_ip, _port);
        }
    }

    public async Task UpdateControllerEndpointAsync(string ip, int port, bool reconnect = false)
    {
        lock (_configLock)
        {
            _ip = ip.Trim();
            _port = port;
        }

        await LogToFrontend("info", $"[System] 控制器目标地址已更新: {_ip}:{_port}");
        Console.WriteLine($"[Service] 控制器地址更新为: {_ip}:{_port}, reconnect={reconnect}");

        if (reconnect)
        {
            _allowConnection = true;
            await DisconnectInternalAsync(sendCloseMid: true, notifyFrontend: false);
            await ConnectAndHandshakeAsync();
        }
    }

    public async Task DisconnectAsync()
    {
        _allowConnection = false;
        await DisconnectInternalAsync(sendCloseMid: true, notifyFrontend: true);
    }

    public async Task SendPacketAsync(string asciiStr, bool allowWhenNotReady = false)
    {
        try 
        {
            if (_stream == null || !_transportConnected || (!_isConnected && !allowWhenNotReady))
            {
                var warn = "[System] 控制器未连接，命令未发送";
                Console.WriteLine($"[Service] {warn}");
                await LogToFrontend("warn", warn);
                return;
            }
            
            byte[] asciiData = Encoding.ASCII.GetBytes(asciiStr);
            byte[] fullPacket = new byte[asciiData.Length + 1];
            Array.Copy(asciiData, 0, fullPacket, 0, asciiData.Length);
            fullPacket[fullPacket.Length - 1] = 0x00; // 核心：马头控制器通常需要 NULL 终止符

            await _stream.WriteAsync(fullPacket, 0, fullPacket.Length);


            _lastPacketTime = DateTime.Now;

            bool isHeartbeat = asciiStr.Contains("9999");
            if (!isHeartbeat) Console.WriteLine($"[Service] {DateTime.Now:HH:mm:ss} TX >>> {asciiStr}");
            
            await LogToFrontend("info", $"[TX] {asciiStr}", isHeartbeat);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Service] 发送失败: {ex.Message}");
            _transportConnected = false;
            _isConnected = false;
        }
    }

    private async Task ReadLoopAsync()
    {
        byte[] buffer = new byte[2048];
        try
        {
            while (_transportConnected && _tcpClient != null && _tcpClient.Connected)
            {
                int bytesRead = await _stream!.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;

                string raw = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                
                // 处理马头控制器报文可能连在一起的情况 (Split by NULL)
                var messages = raw.Split('\0', StringSplitOptions.RemoveEmptyEntries);
                foreach (var msg in messages)
                {
                    await ProcessResponse(msg);
                }
            }
        }
        catch { }
        finally
        {
            _transportConnected = false;
            _isConnected = false;
            CleanupSocket();

            if (_allowConnection)
            {
                await LogToFrontend("warn", "[System] 控制器连接断开，等待自动重连...");
                await _hubContext.Clients.All.SendAsync("ReceiveStatus", "未连接");
            }
        }
    }



    private async Task ProcessResponse(string raw)
    {
        string mid = "";
        if (raw.Length >= 8) mid = raw.Substring(4, 4);

        bool isHeartbeat = mid == "9999";
        if (!isHeartbeat) Console.WriteLine($"[Service] {DateTime.Now:HH:mm:ss} RX <<< {raw}");
        
        await LogToFrontend("success", $"[RX] {raw}", isHeartbeat);

        // 处理错误回包 (MID 0004): 20-byte header + failed MID(4) + error code(2)
        if (mid == "0004")
        {
            string failedMid = raw.Length >= 24 ? raw.Substring(20, 4).Trim() : "未知";
            string errorCode = raw.Length >= 26 ? raw.Substring(24, 2).Trim() : "未知";

            if (failedMid == "0060" && errorCode == "09")
            {
                Console.WriteLine($"[Service] ⚠️ 数据订阅 0060 被控制器拒绝，错误代码: {errorCode}，通常表示已订阅，继续等待 0061 数据");
                await LogToFrontend("warn", $"[System] 数据订阅已存在或被控制器忽略 (MID 0060, 错误码:{errorCode})，继续等待上报数据");
            }
            else
            {
                Console.WriteLine($"[Service] ❌ 指令 {failedMid} 被拒绝，错误代码: {errorCode}，原始报文: {raw}");
                await LogToFrontend("error", $"[System] 指令 {failedMid} 被拒绝 (错误码:{errorCode})");
            }
        }

        // 处理握手成功 (MID 0002)
        if (mid == "0002")
        {
            _isConnected = true;
            Console.WriteLine($"[Service] 🤝 MID 0001 握手成功！系统就绪。");
            await LogToFrontend("success", "[System] 控制器握手成功 (MID 0002)");
            await _hubContext.Clients.All.SendAsync("ReceiveStatus", "已连接");
        }

        if (mid == "0061")
        {
            try 
            {
                // 标准 Open Protocol 0061 解析 (根据偏移量提取数据)
                // 基于实测 002481 锁定的偏移量与倍率
                string torqueStr = raw.Length >= 147 ? raw.Substring(141, 6) : "000000";
                double torqueVal = double.Parse(torqueStr) / 1000.0;

                string angleStr = raw.Length >= 175 ? raw.Substring(170, 5) : "00000";
                double angleVal = double.Parse(angleStr) / 10.0;

                // 拧紧状态解析 (标准 Open Protocol)
                // Offset 105: Tightening status (0:NOK, 1:OK)
                // Offset 107: Torque status (0:Low, 1:OK, 2:High)
                string tightStatus = raw.Length >= 106 ? raw.Substring(105, 1) : "0";
                string torqueStatus = raw.Length >= 108 ? raw.Substring(107, 1) : "0";
                
                // 只要任意一个是 '1'，我们就认为是 OK (兼容性更强)
                bool isOk = (tightStatus == "1" || torqueStatus == "1");
                string statusText = isOk ? "OK" : "NOK";

                // 打印调试信息，方便看清报文第 100-120 位的内容
                if (raw.Length >= 120) {
                    string debugArea = raw.Substring(100, 20);
                    Console.WriteLine($"[Service] 状态位调试 (100-120位): [{debugArea}] -> 解析结果: {statusText}");
                }

                await _hubContext.Clients.All.SendAsync("ReceiveData", new TorqueResult {
                    Torque = torqueVal.ToString("F3"), 
                    Angle = angleVal.ToString("F1"),
                    Status = statusText
                });

                Console.WriteLine($"[Service] 实战数据已就绪: {torqueVal:F3}Nm / {angleVal:F1}Deg - {statusText} (T:{tightStatus}, Q:{torqueStatus})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Service] 数据解析异常: {ex.Message}");
            }
            finally 
            {
                // 关键点：无论解析是否成功，必须回复 MID 0062 确认收到
                await SendPacketAsync("00200062001         ");
            }
        }
    }

    private async Task LogToFrontend(string level, string msg, bool isHeartbeat = false)
    {
        await _hubContext.Clients.All.SendAsync("ReceiveLog", new LogEntry { 
            Level = level, Msg = msg, IsHeartbeat = isHeartbeat 
        });
    }

    private void CleanupSocket()
    {
        try { _stream?.Close(); } catch { }
        try { _tcpClient?.Close(); } catch { }
        _stream = null;
        _tcpClient = null;
    }

    private async Task DisconnectInternalAsync(bool sendCloseMid, bool notifyFrontend)
    {
        if (sendCloseMid && _isConnected)
        {
            try
            {
                await SendPacketAsync("00200003001         ");
                await Task.Delay(100);
            }
            catch { }
        }

        _isConnected = false;
        _transportConnected = false;
        CleanupSocket();

        if (notifyFrontend)
        {
            await LogToFrontend("warn", "[System] 控制器连接已断开");
            await _hubContext.Clients.All.SendAsync("ReceiveStatus", "未连接");
        }
    }

    /// <summary>
    /// 前端手动触发重连：如果当前未连接，立即发起一次 TCP 握手
    /// </summary>
    public async Task TriggerReconnectAsync()
    {
        _allowConnection = true;

        if (_isConnected)
        {
            Console.WriteLine("[Service] 已连接，无需重连");
            await LogToFrontend("info", "[System] 当前已连接，无需重连");
            return;
        }
        Console.WriteLine("[Service] 前端请求手动重连...");
        await ConnectAndHandshakeAsync();
    }
}
