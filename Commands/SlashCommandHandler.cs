using Discord;
using Discord.Commands;
using Discord.Net;
using Discord.WebSocket;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Clara_bot.Commands
{
    public class SlashCommandHandler
    {
        private readonly DiscordSocketClient _client;
        private readonly CommandService _commands;
        private readonly IServiceProvider _services;
        private readonly BotLoggingService _loggingService;
        private readonly SemaphoreSlim _registrationLock = new(1, 1);
        private bool _commandsRegistered;

        private static readonly Dictionary<string, string> SlashToPrefix = new()
        {
            { "help", "heyclara" },
            { "info", "infoclara" },
            { "ping", "pingclara" },
            { "play", "playclara" },
            { "pause", "pauseclara" },
            { "resume", "resumeclara" },
            { "stop", "stopclara" },
            { "next", "nextclara" },
            { "prev", "prevclara" },
            { "jump", "jumpclara" },
            { "playlist", "showplaylistclara" },
            { "qremove", "removeclara" },
            { "qclear", "clearqueueclara" },
            { "qmove", "movequeueclara" },
            { "loop", "loopclara" },
            { "shuffle", "shufclara" },
            { "speed", "speedclara" },
            { "infoplay", "infoplayclara" },
            { "kick", "kick" },
            { "ban", "ban" },
            { "unban", "unban" },
            { "role", "role" },
            { "warn", "warn" },
            { "clear", "clear" },
            { "stopclear", "stopclear" },
            { "lock", "lock" },
            { "unlock", "unlock" },
            { "vkick", "vkick" },
            { "slowmode", "slowmode" },
            { "roleplay", "roleplayclara" },
            { "queue", "queueclara" },
            { "search", "searchclara" },
        };

        public SlashCommandHandler(DiscordSocketClient client, CommandService commands, IServiceProvider services, BotLoggingService loggingService)

        {

            _client = client;

            _commands = commands;

            _services = services;

            _loggingService = loggingService;

        }

        public async Task RegisterCommandsAsync()
        {
            await _registrationLock.WaitAsync();
            try
            {
                if (_commandsRegistered)
                {
                    return;
                }

            var guildCommands = new List<SlashCommandBuilder>
            {
                new SlashCommandBuilder()
                    .WithName("help")
                    .WithDescription("Hiển thị danh sách các lệnh hỗ trợ (/heyclara)"),

                new SlashCommandBuilder()
                    .WithName("info")
                    .WithDescription("Hiển thị thông tin về bot (/infoclara)"),

                new SlashCommandBuilder()
                    .WithName("ping")
                    .WithDescription("Kiểm tra CPU, RAM, độ trễ và tốc độ internet (/pingclara)"),

                new SlashCommandBuilder()
                    .WithName("play")
                    .WithDescription("Phát YouTube; hỗ trợ tìm theo link Spotify (/playclara)")
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("query")
                        .WithDescription("Tên bài, link YouTube hoặc link track/playlist Spotify")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true)),

                new SlashCommandBuilder()
                    .WithName("pause")
                    .WithDescription("Tạm dừng phát nhạc (/pauseclara)"),

                new SlashCommandBuilder()
                    .WithName("resume")
                    .WithDescription("Tiếp tục phát nhạc (/resumeclara)"),

                new SlashCommandBuilder()
                    .WithName("stop")
                    .WithDescription("Dừng phát nhạc và rời khỏi kênh voice (/stopclara)"),

                new SlashCommandBuilder()
                    .WithName("next")
                    .WithDescription("Chuyển sang bài tiếp theo (/nextclara)"),

                new SlashCommandBuilder()
                    .WithName("prev")
                    .WithDescription("Lùi về bài trước (/prevclara)"),

                new SlashCommandBuilder()
                    .WithName("jump")
                    .WithDescription("Nhảy tới bài số n trong playlist (/jumpclara)")
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("position")
                        .WithDescription("Số thứ tự bài hát")
                        .WithType(ApplicationCommandOptionType.Integer)
                        .WithRequired(true)),

                new SlashCommandBuilder()
                    .WithName("playlist")
                    .WithDescription("Hiển thị danh sách bài đang phát (/showplaylistclara)"),

                new SlashCommandBuilder()
                    .WithName("qremove")
                    .WithDescription("Xóa một bài chưa phát khỏi hàng chờ")
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("position")
                        .WithDescription("Số thứ tự bài cần xóa")
                        .WithType(ApplicationCommandOptionType.Integer)
                        .WithMinValue(1)
                        .WithRequired(true)),

                new SlashCommandBuilder()
                    .WithName("qclear")
                    .WithDescription("Xóa mọi bài đang chờ, giữ nguyên bài hiện tại"),

                new SlashCommandBuilder()
                    .WithName("qmove")
                    .WithDescription("Di chuyển một bài chưa phát trong hàng chờ")
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("from")
                        .WithDescription("Vị trí hiện tại")
                        .WithType(ApplicationCommandOptionType.Integer)
                        .WithMinValue(1)
                        .WithRequired(true))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("to")
                        .WithDescription("Vị trí mới")
                        .WithType(ApplicationCommandOptionType.Integer)
                        .WithMinValue(1)
                        .WithRequired(true)),

                new SlashCommandBuilder()
                    .WithName("loop")
                    .WithDescription("Bật hoặc tắt chế độ lặp lại playlist (/loopclara)"),

                new SlashCommandBuilder()
                    .WithName("shuffle")
                    .WithDescription("Trộn ngẫu nhiên các bài trong playlist (/shufclara)"),

                new SlashCommandBuilder()
                    .WithName("speed")
                    .WithDescription("Điều chỉnh tốc độ phát nhạc (/speedclara)"),

                new SlashCommandBuilder()
                    .WithName("infoplay")
                    .WithDescription("Hiển thị thông tin bài hát đang phát (/infoplayclara)"),

                new SlashCommandBuilder()
                    .WithName("kick")
                    .WithDescription("Đuổi thành viên khỏi server")
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("user")
                        .WithDescription("Người dùng cần đuổi")
                        .WithType(ApplicationCommandOptionType.User)
                        .WithRequired(true))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("reason")
                        .WithDescription("Lý do đuổi")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(false)),

                new SlashCommandBuilder()
                    .WithName("ban")
                    .WithDescription("Cấm thành viên khỏi server")
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("user")
                        .WithDescription("Người dùng cần cấm")
                        .WithType(ApplicationCommandOptionType.User)
                        .WithRequired(true))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("duration")
                        .WithDescription("Thời gian cấm (ví dụ: 1h, 2d, 1w)")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(false))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("reason")
                        .WithDescription("Lý do cấm")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(false)),

                new SlashCommandBuilder()
                    .WithName("clear")
                    .WithDescription("Xóa tin nhắn trong kênh")
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("amount")
                        .WithDescription("Số lượng tin nhắn (1-1000)")
                        .WithType(ApplicationCommandOptionType.Integer)
                        .WithRequired(false)),

                new SlashCommandBuilder()
                    .WithName("slowmode")
                    .WithDescription("Bật/tắt chế độ chậm trên kênh")
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("duration")
                        .WithDescription("Thời gian (giây) hoặc 'off'")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(false)),

                new SlashCommandBuilder()
                    .WithName("queue")
                    .WithDescription("Bật/tắt chế độ hàng chờ. Khi bật, bài hát mới sẽ vào playlist"),

                new SlashCommandBuilder()
                    .WithName("search")
                    .WithDescription("Tìm kiếm bài hát trên YouTube, dùng /play <số> để phát")
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("query")
                        .WithDescription("Tên bài hát hoặc từ khóa tìm kiếm")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true)),

                // ===== LỆNH BỔ SUNG =====
                new SlashCommandBuilder()
                    .WithName("unban")
                    .WithDescription("Gỡ cấm người dùng khỏi server")
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("user")
                        .WithDescription("ID hoặc tên người dùng")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("reason")
                        .WithDescription("Lý do gỡ cấm")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(false)),

                new SlashCommandBuilder()
                    .WithName("role")
                    .WithDescription("Thêm hoặc xóa vai trò cho thành viên")
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("target")
                        .WithDescription("Người dùng, all, bots hoặc humans")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("roles")
                        .WithDescription("Vai trò: +RoleName, -RoleName")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true)),

                new SlashCommandBuilder()
                    .WithName("warn")
                    .WithDescription("Gửi cảnh báo cho thành viên hoặc toàn bộ vai trò qua DM")
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("user")
                        .WithDescription("Người dùng cần cảnh báo (dùng user HOẶC role)")
                        .WithType(ApplicationCommandOptionType.User)
                        .WithRequired(false))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("role")
                        .WithDescription("Vai trò cần cảnh báo - tất cả thành viên có vai trò này (dùng user HOẶC role)")
                        .WithType(ApplicationCommandOptionType.Role)
                        .WithRequired(false))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("reason")
                        .WithDescription("Lý do cảnh báo")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(false)),

                new SlashCommandBuilder()
                    .WithName("stopclear")
                    .WithDescription("Dừng quá trình xóa tin nhắn trong kênh này"),

                new SlashCommandBuilder()
                    .WithName("lock")
                    .WithDescription("Khóa kênh, vô hiệu hóa gửi tin nhắn")
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("channel")
                        .WithDescription("Kênh cần khóa")
                        .WithType(ApplicationCommandOptionType.Channel)
                        .WithRequired(false))
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("reason")
                        .WithDescription("Lý do khóa")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(false)),

                new SlashCommandBuilder()
                    .WithName("unlock")
                    .WithDescription("Mở khóa kênh, cho phép gửi tin nhắn")
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("channel")
                        .WithDescription("Kênh cần mở khóa")
                        .WithType(ApplicationCommandOptionType.Channel)
                        .WithRequired(false)),

                new SlashCommandBuilder()
                    .WithName("vkick")
                    .WithDescription("Đuổi người dùng khỏi kênh thoại")
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("user")
                        .WithDescription("Người dùng cần đuổi khỏi voice")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(true)),

                new SlashCommandBuilder()
                    .WithName("roleplay")
                    .WithDescription("Bật/tắt chế độ roleplay Clara")
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("state")
                        .WithDescription("on hoặc off")
                        .WithType(ApplicationCommandOptionType.String)
                        .AddChoice("Bật", "on")
                        .AddChoice("Tắt", "off")
                        .WithRequired(true)),
            };

            try
            {
                // Register global commands
                await _client.BulkOverwriteGlobalApplicationCommandsAsync(guildCommands.Select(c => c.Build()).ToArray());
                _loggingService.Log($"✅ Đã register {guildCommands.Count} global slash commands.");

                // Xóa các guild commands cũ (nếu có) để tránh bị trùng lặp
                foreach (var guild in _client.Guilds)
                {
                    try
                    {
                        var existingGuildCommands = await guild.GetApplicationCommandsAsync();
                        if (existingGuildCommands.Count > 0)
                        {
                            await guild.BulkOverwriteApplicationCommandAsync(Array.Empty<ApplicationCommandProperties>());
                            _loggingService.Log($"   Đã xóa {existingGuildCommands.Count} guild commands cũ trong: {guild.Name}");
                        }
                    }
                    catch { /* Bỏ qua lỗi nếu không xóa được */ }
                }

                _loggingService.Log("✅ Hoàn tất đăng ký slash commands.");
                _commandsRegistered = true;
            }
            catch (Exception ex)
            {
                _loggingService.Log($"❌ Lỗi register slash commands: {ex.Message}");
            }
            }
            finally
            {
                _registrationLock.Release();
            }
        }

        public async Task<bool> TryDeferAsync(SocketSlashCommand command)
        {
            try
            {
                await command.DeferAsync().ConfigureAwait(false);
                return true;
            }
            catch (HttpException ex) when ((int?)ex.DiscordCode == 10062)
            {
                _loggingService.Log($"⚠️ Slash interaction /{command.CommandName} đã hết hạn trước khi Discord nhận defer (10062). Bỏ qua interaction này.");
                return false;
            }
        }

        public async Task HandleDeferredCommandAsync(SocketSlashCommand command)
        {
            var guild = (command.Channel as SocketGuildChannel)?.Guild;
            if (guild == null)
            {
                await command.ModifyOriginalResponseAsync(p =>
                    p.Content = "❌ Lệnh này chỉ sử dụng được trong server.");
                return;
            }

            if (!SlashToPrefix.TryGetValue(command.CommandName, out var prefixCmd))
            {
                await command.ModifyOriginalResponseAsync(p =>
                    p.Content = $"❌ Lệnh `/{command.CommandName}` không được hỗ trợ.");
                return;
            }

            try
            {
                var argsJoined = command.Data.Options is { Count: > 0 }
                    ? string.Join(" ", command.Data.Options.Select(o =>
                    {
                        if (o.Value == null) return "";
                        // Nếu là IUser, lấy ID
                        if (o.Value is IUser user)
                            return user.Id.ToString();
                        // Nếu là IRole, lấy ID
                        if (o.Value is IRole role)
                            return $"&{role.Id}"; // Prefix & để đánh dấu là role
                        return o.Value.ToString() ?? "";
                    }))
                    : "";

                _loggingService.Log($"[SlashCommand] Command: {command.CommandName}, Options count: {command.Data.Options?.Count ?? 0}");
                if (command.Data.Options != null)
                {
                    foreach (var opt in command.Data.Options)
                    {
                        _loggingService.Log($"[SlashCommand]   Option '{opt.Name}': '{opt.Value}'");
                    }
                }
                _loggingService.Log($"[SlashCommand] argsJoined: '{argsJoined}'");

                var searchInput = $"{prefixCmd} {argsJoined}".TrimEnd();
                _loggingService.Log($"[SlashCommand] searchInput: '{searchInput}'");
                var search = _commands.Search(searchInput);
                if (!search.IsSuccess)
                {
                    await command.ModifyOriginalResponseAsync(p =>
                        p.Content = $"❌ Không tìm thấy lệnh `{prefixCmd}`.");
                    return;
                }

                var cmdInfo = search.Commands[0].Command;

                var argPos = prefixCmd.Length;
                var ctx = new SlashContext(_client, guild, new DeferredEditChannel(command, command.Channel, _loggingService), command.User);
                var parseResult = await cmdInfo.ParseAsync(ctx, argPos, search, null, _services);
                if (!parseResult.IsSuccess)
                {
                    await command.ModifyOriginalResponseAsync(p =>
                        p.Content = $"❌ Lỗi parse: {parseResult.ErrorReason}");
                    return;
                }

                // Kiểm tra preconditions (permissions) trước khi execute
                var preconditionResult = await cmdInfo.CheckPreconditionsAsync(ctx, _services);
                if (!preconditionResult.IsSuccess)
                {
                    await command.ModifyOriginalResponseAsync(p =>
                        p.Content = $"❌ {preconditionResult.ErrorReason}");
                    return;
                }

                _loggingService.Log($"[SlashCommand] Executing {command.CommandName} for guild {guild.Name}...");
                var result = await cmdInfo.ExecuteAsync(ctx, parseResult, _services);
                _loggingService.Log($"[SlashCommand] Result: IsSuccess={result.IsSuccess}, Error={result.Error}, Replied={ctx.Replied}");

                if (!result.IsSuccess && result.Error != CommandError.UnknownCommand)
                {
                    // Nếu module chưa gửi reply (DeferredEditChannel chưa được dùng), edit deferred
                    if (!ctx.Replied)
                    {
                        if (result is ExecuteResult { Error: CommandError.Exception } execResult)
                        {
                            await command.ModifyOriginalResponseAsync(p =>
                                p.Content = $"❌ Lỗi: {execResult.Exception?.Message ?? result.ErrorReason}");
                            _loggingService.Log($"Lỗi slash command: {execResult.Exception}");
                        }
                        else
                        {
                            await command.ModifyOriginalResponseAsync(p =>
                                p.Content = $"❌ {result.ErrorReason}");
                        }
                    }
                }
                else if (result.IsSuccess && !ctx.Replied)
                {
                    // Chờ lệnh async hoàn thành (tối đa 2 giây) trước khi gửi fallback response
                    int delayMs = 0;
                    while (delayMs < 2000 && !ctx.Replied)
                    {
                        await Task.Delay(50);
                        delayMs += 50;
                    }

                    // Nếu vẫn chưa reply sau delay, mới gửi fallback response
                    if (!ctx.Replied)
                    {
                        await command.ModifyOriginalResponseAsync(p =>
                            p.Content = $"✅ Đã thực thi `/{command.CommandName}`");
                    }
                }
            }
            catch (Exception ex)
            {
                await command.ModifyOriginalResponseAsync(p =>
                    p.Content = $"❌ Lỗi xử lý lệnh: {ex.Message}");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Lỗi slash command: {ex}");
            }
        }
    }

    internal class SlashContext : ICommandContext
    {
        public IDiscordClient Client { get; }
        public IGuild Guild { get; }
        public IMessageChannel Channel { get; }
        public IUser User { get; }
        public IUserMessage? Message => null;
        public bool Replied => Channel is DeferredEditChannel editChannel && editChannel.Replied;

        public SlashContext(DiscordSocketClient client, SocketGuild guild, IMessageChannel channel, IUser user)
        {
            Client = client;
            Guild = guild;
            Channel = channel;
            User = user;
        }
    }

#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type
    internal class DeferredEditChannel : IMessageChannel
    {
        private readonly SocketSlashCommand _command;
        private readonly IMessageChannel _inner;
        private readonly BotLoggingService _loggingService;
        public bool Replied { get; private set; }

        public DeferredEditChannel(SocketSlashCommand command, IMessageChannel inner, BotLoggingService loggingService)
        {
            _command = command;
            _inner = inner;
            _loggingService = loggingService;
        }

        public string Name => _inner.Name;
        public DateTimeOffset CreatedAt => _inner.CreatedAt;
        public ulong Id => _inner.Id;

        public ChannelType ChannelType => ChannelType.Text;

        public async Task<IUserMessage> SendMessageAsync(string text = null, bool isTTS = false, Embed? embed = null, RequestOptions? options = null, AllowedMentions? allowedMentions = null, MessageReference? reference = null, MessageComponent? components = null, ISticker[]? stickers = null, Embed[]? embeds = null, MessageFlags flags = MessageFlags.None)
        {
            _loggingService.Log($"[DeferredEditChannel] SendMessageAsync called, text length: {text?.Length ?? 0}, components: {components != null}, Replied before: {Replied}");
            if (!Replied)
            {
                Replied = true;
                _loggingService.Log($"[DeferredEditChannel] Modifying original response...");
                try
                {
                    await _command.ModifyOriginalResponseAsync(p =>
                    {
                        if (!string.IsNullOrEmpty(text)) p.Content = text;
                        if (embed != null) p.Embed = embed!;
                        if (embeds != null) p.Embeds = embeds!;
                        if (components != null) p.Components = components!;
                    });
                    _loggingService.Log("[DeferredEditChannel] Response modified successfully");
                }
                catch (Exception ex)
                {
                    _loggingService.Log($"[DeferredEditChannel] ERROR modifying response: {ex.GetType().Name}: {ex.Message}");
                    // Fallback: try to send as followup
                    try
                    {
                        await _command.FollowupAsync(text, embed: embed, components: components);
                        _loggingService.Log("[DeferredEditChannel] Fallback FollowupAsync succeeded");
                    }
                    catch (Exception ex2)
                    {
                        _loggingService.Log($"[DeferredEditChannel] Fallback also failed: {ex2.GetType().Name}: {ex2.Message}");
                    }
                }
                return new DeferredMessage(_command, _loggingService);
            }
            return await _inner.SendMessageAsync(text, isTTS, embed, options, allowedMentions, reference, components, stickers, embeds, flags);
        }

        public async Task<IUserMessage> SendMessageAsync(string text, bool isTTS, Embed? embed, RequestOptions? options, AllowedMentions? allowedMentions, MessageReference? reference, MessageComponent? components, ISticker[]? stickers, Embed[]? embeds, MessageFlags flags, PollProperties? poll = null)
        {
            _loggingService.Log($"[DeferredEditChannel] SendMessageAsync(with poll) called, text length: {text?.Length ?? 0}, components: {components != null}, Replied before: {Replied}");
            if (!Replied)
            {
                Replied = true;
                try
                {
                    await _command.ModifyOriginalResponseAsync(p =>
                    {
                        if (!string.IsNullOrEmpty(text)) p.Content = text;
                        if (embed != null) p.Embed = embed!;
                        if (embeds != null) p.Embeds = embeds!;
                        if (components != null) p.Components = components!;
                    });
                    _loggingService.Log("[DeferredEditChannel] Response modified (with poll) successfully");
                }
                catch (Exception ex)
                {
                    _loggingService.Log($"[DeferredEditChannel] ERROR modifying response (with poll): {ex.GetType().Name}: {ex.Message}");
                    try
                    {
                        await _command.FollowupAsync(text, embed: embed, components: components);
                        _loggingService.Log("[DeferredEditChannel] Fallback FollowupAsync (with poll) succeeded");
                    }
                    catch (Exception ex2)
                    {
                        _loggingService.Log($"[DeferredEditChannel] Fallback (with poll) also failed: {ex2.GetType().Name}: {ex2.Message}");
                    }
                }
                return new DeferredMessage(_command, _loggingService);
            }
            return await _inner.SendMessageAsync(text, isTTS, embed, options, allowedMentions, reference, components, stickers, embeds, flags, poll);
        }

        public Task<IUserMessage> SendFileAsync(string filePath, string text = null, bool isTTS = false, Embed embed = null, RequestOptions options = null, bool isSpoiler = false, AllowedMentions allowedMentions = null, MessageReference reference = null, MessageComponent components = null, ISticker[] stickers = null, Embed[] embeds = null, MessageFlags flags = MessageFlags.None, PollProperties poll = null) => _inner.SendFileAsync(filePath, text, isTTS, embed, options, isSpoiler, allowedMentions, reference, components, stickers, embeds, flags, poll);
        public Task<IUserMessage> SendFileAsync(Stream stream, string filename, string text = null, bool isTTS = false, Embed embed = null, RequestOptions options = null, bool isSpoiler = false, AllowedMentions allowedMentions = null, MessageReference reference = null, MessageComponent components = null, ISticker[] stickers = null, Embed[] embeds = null, MessageFlags flags = MessageFlags.None, PollProperties poll = null) => _inner.SendFileAsync(stream, filename, text, isTTS, embed, options, isSpoiler, allowedMentions, reference, components, stickers, embeds, flags, poll);
        public Task<IUserMessage> SendFileAsync(FileAttachment attachment, string text = null, bool isTTS = false, Embed embed = null, RequestOptions options = null, AllowedMentions allowedMentions = null, MessageReference reference = null, MessageComponent components = null, ISticker[] stickers = null, Embed[] embeds = null, MessageFlags flags = MessageFlags.None, PollProperties poll = null) => _inner.SendFileAsync(attachment, text, isTTS, embed, options, allowedMentions, reference, components, stickers, embeds, flags, poll);
        public Task<IUserMessage> SendFilesAsync(IEnumerable<FileAttachment> attachments, string text = null, bool isTTS = false, Embed embed = null, RequestOptions options = null, AllowedMentions allowedMentions = null, MessageReference reference = null, MessageComponent components = null, ISticker[] stickers = null, Embed[] embeds = null, MessageFlags flags = MessageFlags.None, PollProperties poll = null) => _inner.SendFilesAsync(attachments, text, isTTS, embed, options, allowedMentions, reference, components, stickers, embeds, flags, poll);

        public Task<IMessage> GetMessageAsync(ulong id, CacheMode mode = CacheMode.AllowDownload, RequestOptions options = null) => _inner.GetMessageAsync(id, mode, options);
        public IAsyncEnumerable<IReadOnlyCollection<IMessage>> GetMessagesAsync(int limit = 100, CacheMode mode = CacheMode.AllowDownload, RequestOptions options = null) => _inner.GetMessagesAsync(limit, mode, options);
        public IAsyncEnumerable<IReadOnlyCollection<IMessage>> GetMessagesAsync(ulong fromMessageId, Direction dir, int limit = 100, CacheMode mode = CacheMode.AllowDownload, RequestOptions options = null) => _inner.GetMessagesAsync(fromMessageId, dir, limit, mode, options);
        public IAsyncEnumerable<IReadOnlyCollection<IMessage>> GetMessagesAsync(IMessage fromMessage, Direction dir, int limit = 100, CacheMode mode = CacheMode.AllowDownload, RequestOptions options = null) => _inner.GetMessagesAsync(fromMessage, dir, limit, mode, options);
        public Task<IReadOnlyCollection<IMessage>> GetPinnedMessagesAsync(RequestOptions options = null) => _inner.GetPinnedMessagesAsync(options);
        public Task DeleteMessageAsync(ulong messageId, RequestOptions options = null) => _inner.DeleteMessageAsync(messageId, options);
        public Task DeleteMessageAsync(IMessage message, RequestOptions options = null) => _inner.DeleteMessageAsync(message, options);
        public Task TriggerTypingAsync(RequestOptions options = null) => _inner.TriggerTypingAsync(options);
        public IDisposable EnterTypingState(RequestOptions options = null) => _inner.EnterTypingState(options);
        public Task<IUser> GetUserAsync(ulong id, CacheMode mode = CacheMode.AllowDownload, RequestOptions options = null) => _inner.GetUserAsync(id, mode, options);
        public IAsyncEnumerable<IReadOnlyCollection<IUser>> GetUsersAsync(CacheMode mode = CacheMode.AllowDownload, RequestOptions options = null) => _inner.GetUsersAsync(mode, options);
        public Task<IUserMessage> ModifyMessageAsync(ulong messageId, Action<MessageProperties> func, RequestOptions options = null) => _inner.ModifyMessageAsync(messageId, func, options);
    }
#pragma warning restore CS8625

    internal class DeferredMessage : IUserMessage
    {
        private readonly SocketSlashCommand _command;
        private readonly BotLoggingService _loggingService;

        public DeferredMessage(SocketSlashCommand command, BotLoggingService loggingService)
        {
            _command = command;
            _loggingService = loggingService;
        }

        public Task ModifyAsync(Action<MessageProperties> func, RequestOptions? options = null)
            => _command.ModifyOriginalResponseAsync(func);

        public ulong Id => 0;
        public DateTimeOffset CreatedAt => DateTimeOffset.Now;
        public DateTimeOffset Timestamp => DateTimeOffset.Now;
        public DateTimeOffset? EditedTimestamp => null;
        public string Content => "";
        public IUser Author => _command.User;
        public IMessageChannel Channel => _command.Channel;
        public MessageSource Source => MessageSource.Bot;
        public bool IsPinned => false;
        public bool IsSuppressed => false;
        public bool IsTTS => false;
        public bool MentionedEveryone => false;
        public MessageType Type => MessageType.Default;
        public MessageFlags? Flags => null;
        public string CleanContent => "";
        public bool IsThread => false;
        public IReadOnlyCollection<ulong> MentionedUserRoles => Array.Empty<ulong>();
        public IReadOnlyCollection<ulong> MentionedChannelIds => Array.Empty<ulong>();
        public IReadOnlyCollection<ulong> MentionedRoleIds => Array.Empty<ulong>();
        public IReadOnlyCollection<ulong> MentionedUserIds => Array.Empty<ulong>();
        public IReadOnlyDictionary<IEmote, ReactionMetadata> Reactions => new Dictionary<IEmote, ReactionMetadata>();
        public IReadOnlyCollection<IAttachment> Attachments => Array.Empty<IAttachment>();
        public IReadOnlyCollection<IEmbed> Embeds => Array.Empty<IEmbed>();
        public IReadOnlyCollection<ITag> Tags => Array.Empty<ITag>();
        public IReadOnlyCollection<IStickerItem> Stickers => Array.Empty<IStickerItem>();
        public IReadOnlyCollection<ISticker> StickerItems => Array.Empty<ISticker>();
        public MessageReference? Reference => null;
        public IMessageInteraction? Interaction => null;
        public IThreadChannel? Thread => null;
        public MessageActivity? Activity => null;
        public MessageApplication? Application => null;
        public IReadOnlyCollection<IMessageComponent> Components => Array.Empty<IMessageComponent>();
        public MessageRoleSubscriptionData? RoleSubscriptionData => null;
        public PurchaseNotification PurchaseNotification => default;
        public MessageCallData? CallData => null;
        public MessageResolvedData? ResolvedData => null;
        public IMessageInteractionMetadata? InteractionMetadata => null;
        public IReadOnlyCollection<MessageSnapshot> ForwardedMessages => Array.Empty<MessageSnapshot>();
        public Poll? Poll => null;
        public IUserMessage? ReferencedMessage => null;
        public Task AddReactionAsync(IEmote emote, RequestOptions? options = null) => Task.CompletedTask;
        public Task RemoveReactionAsync(IEmote emote, IUser user, RequestOptions? options = null) => Task.CompletedTask;
        public Task RemoveReactionAsync(IEmote emote, ulong userId, RequestOptions? options = null) => Task.CompletedTask;
        public Task RemoveAllReactionsAsync(RequestOptions? options = null) => Task.CompletedTask;
        public Task RemoveAllReactionsForEmoteAsync(IEmote emote, RequestOptions? options = null) => Task.CompletedTask;
        public IAsyncEnumerable<IReadOnlyCollection<IUser>> GetReactionUsersAsync(IEmote emote, int limit, RequestOptions? options = null, ReactionType type = ReactionType.Normal) => AsyncEnumerable.Empty<IReadOnlyCollection<IUser>>();
        public Task PinAsync(RequestOptions? options = null) => Task.CompletedTask;
        public Task UnpinAsync(RequestOptions? options = null) => Task.CompletedTask;
        public string Resolve(TagHandling userHandling = 0, TagHandling channelHandling = 0, TagHandling roleHandling = 0, TagHandling everyoneHandling = 0, TagHandling emojiHandling = 0) => "";
        public Task DeleteAsync(RequestOptions? options = null) => Task.CompletedTask;
        public Task CrosspostAsync(RequestOptions? options = null) => Task.CompletedTask;
        public Task EndPollAsync(RequestOptions? options = null) => Task.CompletedTask;
        public IAsyncEnumerable<IReadOnlyCollection<IUser>> GetPollAnswerVotersAsync(uint answerId, int? limit = null, ulong? afterId = null, RequestOptions? options = null) => AsyncEnumerable.Empty<IReadOnlyCollection<IUser>>();

        public async Task<IUserMessage> SendMessageAsync(string? text = null, bool isTTS = false, Embed? embed = null, RequestOptions? options = null, AllowedMentions? allowedMentions = null, MessageReference? reference = null, MessageComponent? components = null, ISticker[]? stickers = null, Embed[]? embeds = null, MessageFlags flags = MessageFlags.None)
        {
            _loggingService.Log($"[DeferredMessage] SendMessageAsync called, text length: {text?.Length ?? 0}, components: {components != null}");
            await ModifyAsync(p =>
            {
                if (!string.IsNullOrEmpty(text)) p.Content = text;
                if (embed != null) p.Embed = embed!;
                if (embeds != null) p.Embeds = embeds!;
                if (components != null) p.Components = components!;
            });
            return this; // Return the deferred message itself
        }

        public async Task<IUserMessage> SendMessageAsync(string text, bool isTTS, Embed? embed, RequestOptions? options, AllowedMentions? allowedMentions, MessageReference? reference, MessageComponent? components, ISticker[]? stickers, Embed[]? embeds, MessageFlags flags, PollProperties? poll = null)
        {
            _loggingService.Log($"[DeferredMessage] SendMessageAsync(with poll) called, text length: {text?.Length ?? 0}, components: {components != null}");
            await ModifyAsync(p =>
            {
                if (!string.IsNullOrEmpty(text)) p.Content = text;
                if (embed != null) p.Embed = embed!;
                if (embeds != null) p.Embeds = embeds!;
                if (components != null) p.Components = components!;
            });
            return this; // Return the deferred message itself
        }
    }

}
