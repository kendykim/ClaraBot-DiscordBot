using Discord;

using Discord.WebSocket;

using Discord.Commands;

using Microsoft.Extensions.DependencyInjection;

using System;

using System.Reflection;

using System.Net.Http;

using System.Threading;

using System.Threading.Tasks;

using Figgle;

using Figgle.Fonts;

using Lavalink4NET;

using Lavalink4NET.Extensions;

using Clara_bot.Commands;

using DotNetEnv;



namespace Clara_bot

{

    class Program

    {

        static async Task Main(string[] args)

        {
            // Mạng hiện tại quảng bá đường NAT64/IPv6 tới Discord nhưng TCP
            // handshake bị treo. IPv4 đã được kiểm tra hoạt động ổn định.
            AppContext.SetSwitch("System.Net.DisableIPv6", true);

            if (args.Contains("--check-discord", StringComparer.OrdinalIgnoreCase))
            {
                Environment.ExitCode = await CheckDiscordConnectionAsync();
                return;
            }

            using var instanceMutex = new Mutex(
                initiallyOwned: true,
                name: "ClaraBot.SingleInstance",
                createdNew: out var isFirstInstance);

            if (!isFirstInstance)
            {
                Console.WriteLine("❌ Clara Bot đã đang chạy. Dừng instance cũ trước khi khởi động instance mới.");
                return;
            }

            if (args.Contains("--check-guilds"))
            {
                var inspector = new Commands.GuildInspector();
                await inspector.RunInspectionAsync();
                return;
            }

            // Close codes không nên retry (unrecoverable)

            var doNotRetryCodes = new HashSet<int> { 4004, 4010, 4011, 4012, 4013 };

            

            Env.Load();

            Console.OutputEncoding = System.Text.Encoding.UTF8;

            Console.InputEncoding = System.Text.Encoding.UTF8;

            /* Xóa // đi để sử dụng mã hóa UTF-8 */

            var services = new ServiceCollection();



            var socketConfig = new DiscordSocketConfig

            {

                GatewayIntents = GatewayIntents.Guilds |

                                 GatewayIntents.GuildMessages |

                                 GatewayIntents.MessageContent |

                                 GatewayIntents.GuildVoiceStates |

                                 GatewayIntents.GuildMembers,

            };



            services.AddSingleton(new DiscordSocketClient(socketConfig));

            services.AddSingleton<CommandService>();

            services.AddSingleton<CommandHandler>(provider => 
            {
                var client = provider.GetRequiredService<DiscordSocketClient>();
                var commands = provider.GetRequiredService<CommandService>();
                var services = provider;
                var statusModule = provider.GetRequiredService<StatusModule>();
                var loggingService = provider.GetRequiredService<BotLoggingService>();
                return new CommandHandler(client, commands, services, statusModule, loggingService);
            });
            services.AddSingleton<SlashCommandHandler>(provider => 
            {
                var client = provider.GetRequiredService<DiscordSocketClient>();
                var commands = provider.GetRequiredService<CommandService>();
                var services = provider;
                var loggingService = provider.GetRequiredService<BotLoggingService>();
                return new SlashCommandHandler(client, commands, services, loggingService);
            });
            services.AddSingleton<StatusModule>(provider => 
            {
                var client = provider.GetRequiredService<DiscordSocketClient>();
                var loggingService = provider.GetRequiredService<BotLoggingService>();
                return new StatusModule(client, null, loggingService);
            });
            services.AddSingleton<LavalinkErrorHandler>();
            services.AddSingleton<ResilientPlaybackRouter>();
            services.AddSingleton<BotLoggingService>();
            services.AddSingleton<LavalinkHealthMonitor>();
            services.AddSingleton<LavalinkPlaybackEvents>();



            services.AddLavalink();

            services.ConfigureLavalink(config =>

            {

                config.BaseAddress = new Uri("http://127.0.0.1:2333");

                config.Passphrase = "youshallnotpass";

            });



            await using var serviceProvider = services.BuildServiceProvider();



            var client = serviceProvider.GetRequiredService<DiscordSocketClient>();
            var audioService = serviceProvider.GetRequiredService<IAudioService>();
            _ = serviceProvider.GetRequiredService<LavalinkPlaybackEvents>();
            var commandHandler = serviceProvider.GetRequiredService<CommandHandler>();
            var loggingService = serviceProvider.GetRequiredService<BotLoggingService>();
            var reconnectLock = new SemaphoreSlim(1, 1);
            var failedRetryCount = 0;
            var isReconnecting = false;
            var readyInitializationStarted = 0;
            var startupStopwatch = System.Diagnostics.Stopwatch.StartNew();

            Env.Load();

            var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");

            if (string.IsNullOrWhiteSpace(token))

            {

                Console.WriteLine("❌ DISCORD_TOKEN không tìm thấy.");

                Console.WriteLine("Vui lòng thực hiện một cách trong hai cách sau:");

                Console.WriteLine("   Cách 1: Tạo file .env với nội dung: DISCORD_TOKEN=your_token_here (khuyên dùng)");

                Console.WriteLine("   Cách 2: Chạy lệnh: set DISCORD_TOKEN=your_token_here && dotnet run (có thể không ổn định)");

                Environment.Exit(1);

                return;

            }



            string banner = FiggleFonts.Ogre.Render("Clara Bot!");
            Console.WriteLine(banner);
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "Unknown";
            Console.WriteLine($"Version: {version}");



            client.Log += msg => { loggingService.Log(msg.Message); return Task.CompletedTask; };

            var slashCommandHandler = serviceProvider.GetRequiredService<SlashCommandHandler>();
            var lavalinkHealthMonitor = serviceProvider.GetRequiredService<LavalinkHealthMonitor>();



            client.Ready += () =>
            {
                var isFirstReady = Interlocked.Exchange(ref readyInitializationStarted, 1) == 0;
                if (isFirstReady)
                {
                    startupStopwatch.Stop();
                    loggingService.Log($"✅ Bot {client.CurrentUser.Username} đã online sau {startupStopwatch.Elapsed.TotalSeconds:F1}s!");
                }
                else
                {
                    loggingService.Log($"✅ Bot {client.CurrentUser.Username} đã khôi phục kết nối!");
                }

                // Báo Ready ngay cho luồng kết nối. Các request REST và kiểm tra
                // Lavalink chạy nền để không làm chậm (hoặc gây timeout) startup.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await client.SetActivityAsync(new Game("/heyclara", ActivityType.Listening));
                        if (isFirstReady)
                        {
                            serviceProvider.GetRequiredService<StatusModule>().Start();
                            lavalinkHealthMonitor.Start();
                        }

                        // Handler tự bỏ qua nếu commands đã đăng ký thành công, nhưng
                        // vẫn cho phép retry sau một lỗi tạm thời của Discord API.
                        await slashCommandHandler.RegisterCommandsAsync();

                    }
                    catch (Exception ex)
                    {
                        loggingService.Log($"⚠️ Lỗi tác vụ hậu khởi động: {ex.Message}");
                    }
                });

                return Task.CompletedTask;
            };

            client.Connected += () =>
            {
                loggingService.Log($"✅ Kết nối Discord ổn định. Tổng retry thất bại: {failedRetryCount}");
                return Task.CompletedTask;
            };



            async Task<bool> EnsureBotOnlineAsync(TimeSpan timeout)

            {

                if (client.ConnectionState == ConnectionState.Connected && client.CurrentUser is not null)

                {

                    return true;

                }



                var readySignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                Task Handler()

                {

                    readySignal.TrySetResult(true);

                    return Task.CompletedTask;

                }



                client.Ready += Handler;

                try

                {

                    var completedTask = await Task.WhenAny(readySignal.Task, Task.Delay(timeout));

                    return completedTask == readySignal.Task;

                }

                finally

                {

                    client.Ready -= Handler;

                }

            }



            // Tính exponential backoff với jitter

            TimeSpan CalculateBackoff(int attempt, TimeSpan baseDelay, TimeSpan maxDelay)

            {

                var exponential = baseDelay * Math.Pow(2, attempt - 1);

                var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));

                var result = exponential + jitter;

                return result > maxDelay ? maxDelay : result;

            }



            async Task<bool> ConnectDiscordWithRetryAsync(int maxAttempts, TimeSpan baseDelay, bool isReconnectLoop = false)

            {

                for (var attempt = 1; attempt <= maxAttempts; attempt++)

                {

                    try

                    {

                        if (client.LoginState != LoginState.LoggedIn)

                        {

                            await client.LoginAsync(TokenType.Bot, token);

                        }



                        if (client.ConnectionState == ConnectionState.Disconnected)

                        {

                            await client.StartAsync();

                        }



                        var isOnline = await EnsureBotOnlineAsync(TimeSpan.FromSeconds(20));

                        if (!isOnline)
                {
                    failedRetryCount++;
                    var delay = CalculateBackoff(attempt, baseDelay, TimeSpan.FromSeconds(60));
                    loggingService.Log($"❌ Kết nối thành công nhưng bot chưa online sau thời gian chờ. Lần thử {attempt}/{maxAttempts}. Tổng retry lỗi: {failedRetryCount}. Chờ {delay.TotalSeconds:F1}s...");
                    if (client.ConnectionState != ConnectionState.Disconnected)
                    {
                        await client.StopAsync();
                    }



                    if (attempt < maxAttempts || isReconnectLoop)
                    {
                        await Task.Delay(delay);
                    }



                    if (!isReconnectLoop && attempt >= maxAttempts)
                        break;
                    continue;
                }



                loggingService.Log($"✅ Bot online thành công ở lần thử {attempt}/{maxAttempts}.");
                loggingService.Log("✅ Kết nối Discord đã được khôi phục!");
                return true;

                    }

                    catch (Exception ex)
                {
                    failedRetryCount++;
                    var delay = CalculateBackoff(attempt, baseDelay, TimeSpan.FromSeconds(60));
                    loggingService.Log($"❌ Kết nối Discord thất bại {attempt}/{maxAttempts}. Tổng retry lỗi: {failedRetryCount}. Chi tiết: {ex.Message}. Chờ {delay.TotalSeconds:F1}s...");
                    if (client.ConnectionState != ConnectionState.Disconnected)
                    {
                        try
                        {
                            await client.StopAsync();
                        }
                        catch (Exception stopEx)
                        {
                            loggingService.Log($"⚠️ Không thể reset Discord client: {stopEx.Message}");
                        }
                    }
                    if (attempt < maxAttempts || isReconnectLoop)
                    {
                        await Task.Delay(delay);
                    }
                }

                }



                return false;

            }



            await commandHandler.InitializeAsync();

            

            // Xử lý slash commands

            client.SlashCommandExecuted += async command =>

            {

                serviceProvider.GetRequiredService<StatusModule>().RecordInteraction();

                // Acknowledge immediately: Discord invalidates an interaction
                // that has no initial response within roughly three seconds.
                if (!await slashCommandHandler.TryDeferAsync(command).ConfigureAwait(false))
                {
                    return;
                }

                _ = Task.Run(async () =>

                {

                    try

                    {

                        await slashCommandHandler.HandleDeferredCommandAsync(command);

                    }

                    catch (Exception ex)

                    {

                        Console.WriteLine($"Lỗi slash command: {ex}");

                    }

                });

            };



            var cts = new CancellationTokenSource();



            async Task EnsureBotOnlineWithRetryAsync()
            {
                var reconnectAttempt = 0;
                loggingService.Log($"[{DateTime.Now:HH:mm:ss}] 🔄 Bắt đầu vòng lặp reconnect...");
                while (!cts.Token.IsCancellationRequested)
                {
                    reconnectAttempt++;
                    var backoff = CalculateBackoff(reconnectAttempt, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(120));
                    loggingService.Log($"[{DateTime.Now:HH:mm:ss}] 🔄 Thử kết nối lại lần {reconnectAttempt} (chờ {backoff.TotalSeconds:F1}s)...");

                    try
                    {
                        await Task.Delay(backoff, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        loggingService.Log($"[{DateTime.Now:HH:mm:ss}] ⏹️ Reconnect bị hủy.");
                        break;
                    }



                    // Nếu đã tự reconnect rồi thì thoát loop
                    if (client.ConnectionState == ConnectionState.Connected && client.CurrentUser is not null)
                    {
                        loggingService.Log($"[{DateTime.Now:HH:mm:ss}] ✅ Kết nối Discord đã được khôi phục (tự động)! Đang khôi phục trạng thái...");
                        await client.SetActivityAsync(new Game("/heyclara", ActivityType.Listening));
                        break;
                    }



                    // Nếu không còn reconnect flag (có thể đã reconnect thành công bởi luồng khác) thì thoát

                    if (!isReconnecting)
                    {
                        loggingService.Log($"[{DateTime.Now:HH:mm:ss}] ℹ️ Reconnect flag đã tắt, thoát vòng lặp.");
                        break;
                    }



                    // Chỉ cleanup player khi Discord không tự động resume được session

                    // (nếu auto-resume thành công, Lavalink vẫn giữ trạng thái player)



                    // Thử login nếu chưa logged in
                    if (client.LoginState != LoginState.LoggedIn)
                    {
                        try
                        {
                            await client.LoginAsync(TokenType.Bot, token);
                            loggingService.Log($"[{DateTime.Now:HH:mm:ss}] ✅ Login thành công.");
                        }
                        catch (Exception loginEx)
                        {
                            loggingService.Log($"[{DateTime.Now:HH:mm:ss}] ❌ Login thất bại: {loginEx.Message}");
                            continue;
                        }
                    }



                    // Thử start nếu chưa connected
                    if (client.ConnectionState == ConnectionState.Disconnected)
                    {
                        try
                        {
                            await client.StartAsync();
                            loggingService.Log($"[{DateTime.Now:HH:mm:ss}] ✅ Start thành công.");
                        }
                        catch (Exception startEx)
                        {
                            loggingService.Log($"[{DateTime.Now:HH:mm:ss}] ❌ Start thất bại: {startEx.Message}");
                            continue;
                        }
                    }



                    // Chờ bot online
                    var isOnline = await EnsureBotOnlineAsync(TimeSpan.FromSeconds(20));
                    if (isOnline)
                    {
                        loggingService.Log($"[{DateTime.Now:HH:mm:ss}] ✅ Bot online thành công ở lần thử {reconnectAttempt}!");
                        loggingService.Log($"[{DateTime.Now:HH:mm:ss}] ✅ Kết nối Discord đã được khôi phục!");
                        await client.SetActivityAsync(new Game("/heyclara", ActivityType.Listening));
                        break;
                    }



                    failedRetryCount++;
                    loggingService.Log($"[{DateTime.Now:HH:mm:ss}] ❌ Bot chưa online sau thời gian chờ. Tổng retry lỗi: {failedRetryCount}. Thử lại...");
                }
                loggingService.Log($"[{DateTime.Now:HH:mm:ss}] 🏁 Kết thúc vòng lặp reconnect.");
            }



            client.Disconnected += async ex =>
            {
                await reconnectLock.WaitAsync();
                try
                {
                    // Nếu đang ở trạng thái Connected rồi thì Discord.NET đã tự reconnect, không làm gì
                    if (client.ConnectionState == ConnectionState.Connected && client.CurrentUser is not null)
                    {
                        return;
                    }



                    // Nếu đang trong quá trình reconnect thì bỏ qua
                    if (isReconnecting)
                    {
                        loggingService.Log($"[{DateTime.Now:HH:mm:ss}] ⚠️ Đang trong quá trình reconnect, bỏ qua sự kiện Disconnected mới.");
                        return;
                    }



                    var closeCode = (int?)null;

                    var shouldStopRetrying = false;



                    if (ex != null)

                    {

                        // Phát hiện close code từ WebSocketClosedException

                        if (ex is System.Net.WebSockets.WebSocketException wsEx)

                        {

                            closeCode = (int?)wsEx.WebSocketErrorCode;

                        }



                        // Trích xuất close code từ message nếu có

                        var closeCodeMatch = System.Text.RegularExpressions.Regex.Match(

                            ex.Message ?? "",

                            @"\((\d{4})\)|CloseCode:\s*(\d{4})|code\s*(\d{4})"

                        );

                        if (closeCodeMatch.Success)

                        {

                            closeCode = int.Parse(closeCodeMatch.Groups[1].Value);

                        }



                        // Check các close code không nên retry

                        if (closeCode.HasValue && doNotRetryCodes.Contains(closeCode.Value))

                        {

                            shouldStopRetrying = true;

                            loggingService.Log($"[{DateTime.Now:HH:mm:ss}] ❌ Close code {closeCode.Value} là lỗi không thể phục hồi. Dừng retry.");

                            switch (closeCode.Value)

                            {

                                case 4004:
                                    loggingService.Log("   → Token không hợp lệ. Kiểm tra DISCORD_TOKEN.");
                                    break;
                                case 4010:
                                    loggingService.Log("   → Shard ID không hợp lệ.");
                                    break;
                                case 4011:
                                    loggingService.Log("   → Bot cần sharding. Cần cấu hình sharding.");
                                    break;
                                case 4012:
                                    loggingService.Log("   → API version không hợp lệ.");
                                    break;
                                case 4013:
                                    loggingService.Log("   → Intent không hợp lệ. Kiểm tra GatewayIntents.");
                                    break;

                            }

                        }

                    }



                    loggingService.Log($"[{DateTime.Now:HH:mm:ss}] ⚠️ Mất kết nối Discord: {ex?.Message ?? "Không có chi tiết lỗi"} (CloseCode: {closeCode?.ToString() ?? "N/A"}");



                    if (shouldStopRetrying)

                    {

                        loggingService.Log($"[{DateTime.Now:HH:mm:ss}] ⛔ Dừng chương trình do lỗi không thể phục hồi.");

                        cts.Cancel();

                        return;

                    }



                    // Đánh dấu đang reconnect
                    isReconnecting = true;
                    loggingService.Log($"[{DateTime.Now:HH:mm:ss}] 🔄 Bắt đầu quá trình reconnect...");



                    // Chạy reconnect trong task riêng để không block gateway task

                    // Bắt đầu với 1.5s delay trước khi reconnect (chờ Discord ổn định)

                    _ = Task.Run(async () =>

                    {

                        await Task.Delay(TimeSpan.FromSeconds(1.5));

                        try

                        {

                            await EnsureBotOnlineWithRetryAsync();

                        }

                        finally

                        {

                            isReconnecting = false;

                            loggingService.Log($"[{DateTime.Now:HH:mm:ss}] ✅ Kết thúc quá trình reconnect.");

                        }

                    });

                }

                finally

                {

                    reconnectLock.Release();

                }

            };



            var isConnected = await ConnectDiscordWithRetryAsync(5, TimeSpan.FromSeconds(5));

            if (!isConnected)
            {
                loggingService.Log($"❌ Dừng chương trình vì không kết nối được Discord. Tổng retry thất bại: {failedRetryCount}");
                loggingService.StopAndSaveFinalLogs();
                return;
            }



            // Xử lý khi chương trình bị đóng (Ctrl+C, etc.)
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                loggingService.Log("🛑 Nhận được tín hiệu dừng chương trình...");
                loggingService.StopAndSaveFinalLogs();
                cts.Cancel();
            };

            try
            {
                await Task.Delay(-1, cts.Token);
            }
            catch (OperationCanceledException)
            {
                loggingService.Log("🛑 Chương trình đã dừng.");
                loggingService.StopAndSaveFinalLogs();
            }

        }

        private static async Task<int> CheckDiscordConnectionAsync()
        {
            Env.Load();
            var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
            if (string.IsNullOrWhiteSpace(token))
            {
                Console.Error.WriteLine("❌ Không tìm thấy DISCORD_TOKEN trong môi trường hoặc file .env.");
                return 2;
            }

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/v10/users/@me");
                request.Headers.TryAddWithoutValidation("Authorization", $"Bot {token}");
                request.Headers.TryAddWithoutValidation("User-Agent", "ClaraBot/1.1");
                using var response = await client.SendAsync(request);
                response.EnsureSuccessStatusCode();
                using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
                var user = body.RootElement;
                stopwatch.Stop();
                Console.WriteLine($"✅ Discord API kết nối tốt ({stopwatch.ElapsedMilliseconds} ms).");
                Console.WriteLine($"   Bot: {user.GetProperty("username").GetString()} ({user.GetProperty("id").GetString()})");
                return 0;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                Console.Error.WriteLine($"❌ Không thể kết nối Discord sau {stopwatch.ElapsedMilliseconds} ms: {ex.Message}");
                return 1;
            }
        }

    }

}

