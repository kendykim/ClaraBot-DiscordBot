using Discord.Commands;
using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Lavalink4NET;
using Lavalink4NET.Extensions;
using Lavalink4NET.Players;
using Lavalink4NET.Rest.Entities.Tracks;
using Lavalink4NET.Filters;
using Lavalink4NET.Protocol.Payloads.Events;
using System.Diagnostics;

namespace Clara_bot.Commands
{
    public class MusicModule : ModuleBase<ICommandContext>
    {
        private static readonly ConcurrentDictionary<ulong, CancellationTokenSource> PlaybackTokens = new();
        private static readonly ConcurrentDictionary<ulong, PlaybackQueue> PlaybackQueues = new();
        private static readonly ConcurrentDictionary<ulong, SemaphoreSlim> StopLocks = new();
        private static readonly ConcurrentDictionary<ulong, bool> QueueModeEnabled = new();
        private static readonly ConcurrentDictionary<ulong, SearchResult> LastSearchResults = new();
        private static readonly ConcurrentDictionary<ulong, (ulong ChannelId, ulong MessageId)> LastSearchMessage = new();
        private static readonly HttpClient HttpClient = new();
        private const int PlaylistPageSize = 10;
        private const int SearchResultLimit = 10;
        private readonly IAudioService _audioService;
        private readonly LavalinkErrorHandler _errorHandler;
        private readonly ResilientPlaybackRouter _playbackRouter;
        private readonly LavalinkPlaybackEvents _playbackEvents;

        private sealed class SearchResult
        {
            public required List<SearchTrack> Tracks { get; init; }
            public required string Query { get; init; }
            public DateTime CreatedAt { get; init; } = DateTime.Now;
        }

        private sealed class SearchTrack
        {
            public required string Identifier { get; init; }
            public required string Title { get; init; }
            public required string Author { get; init; }
            public required TimeSpan Duration { get; init; }
        }

        private sealed class QueueItem
        {
            public required string Identifier { get; init; }
            public required string Title { get; init; }
        }

        private sealed class PlaylistLoadResult
        {
            public required IReadOnlyList<QueueItem> Items { get; init; }
            public bool IsLoginRequired { get; init; }
            public string? ErrorMessage { get; init; }
        }

        private sealed class PlaybackQueue
        {
            public required List<QueueItem> Items { get; init; }
            public ulong TextChannelId { get; init; }
            public int Index { get; set; }
            public int RequestedIndex { get; set; }
            public int PendingLoaders { get; set; }
            public bool LoopEnabled { get; set; }
            public int ConsecutivePlaybackFailures { get; set; }
        }

        public MusicModule(
            IAudioService audioService,
            DiscordSocketClient client,
            LavalinkErrorHandler errorHandler,
            ResilientPlaybackRouter playbackRouter,
            LavalinkPlaybackEvents playbackEvents)
        {
            _audioService = audioService;
            _client = client;
            _errorHandler = errorHandler;
            _playbackRouter = playbackRouter;
            _playbackEvents = playbackEvents;
        }

        private readonly DiscordSocketClient _client;

        private static void CancelPlayback(ulong guildId)
        {
            PlaybackQueues.TryRemove(guildId, out _);
            ResetPlaybackSpeed(guildId); // Reset tốc độ khi kết thúc phát nhạc
            if (PlaybackTokens.TryRemove(guildId, out var cts))
            {
                try
                {
                    cts.Cancel();
                }
                finally
                {
                    cts.Dispose();
                }
            }
        }

        public static void CleanupAllPlayback()
        {
            foreach (var guildId in PlaybackQueues.Keys.ToArray())
            {
                CancelPlayback(guildId);
            }
        }

        private static CancellationToken ReplacePlaybackToken(ulong guildId)
        {
            CancelPlayback(guildId);
            var cts = new CancellationTokenSource();
            PlaybackTokens[guildId] = cts;
            return cts.Token;
        }

        private static bool IsYouTubePlaylistUrl(Uri uri)
        {
            if (!IsYouTubeUrl(uri))
            {
                return false;
            }

            if (uri.AbsolutePath.Contains("/playlist", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return uri.Query.Contains("list=", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLoginRequiredPlaybackError(string errorText)
        {
            return errorText.Contains("requires login", StringComparison.OrdinalIgnoreCase) ||
                   errorText.Contains("all clients failed to load the item", StringComparison.OrdinalIgnoreCase) ||
                   errorText.Contains("sign in to confirm your age", StringComparison.OrdinalIgnoreCase) ||
                   errorText.Contains("confirm your age", StringComparison.OrdinalIgnoreCase) ||
                   errorText.Contains("age-restricted", StringComparison.OrdinalIgnoreCase) ||
                   errorText.Contains("age restricted", StringComparison.OrdinalIgnoreCase) ||
                   errorText.Contains("video is unavailable", StringComparison.OrdinalIgnoreCase) ||
                   errorText.Contains("content warning", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsMissingLavalinkSession(Exception exception)
        {
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                if (current.Message.Contains("Session not found", StringComparison.OrdinalIgnoreCase) ||
                    (current.Message.Contains("404", StringComparison.OrdinalIgnoreCase) &&
                     current.Message.Contains("/v4/sessions/", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }

        private async Task<ILavalinkPlayer> GetOrCreatePlayerAsync(
            ulong guildId,
            ulong voiceChannelId,
            LavalinkPlayerOptions playerOptions,
            CancellationToken cancellationToken)
        {
            ILavalinkPlayer? player = null;
            try
            {
                player = await _audioService.Players.GetPlayerAsync(guildId).ConfigureAwait(false);
                if (player is null)
                {
                    return await _audioService.Players.JoinAsync(
                        guildId,
                        voiceChannelId,
                        playerOptions,
                        cancellationToken).ConfigureAwait(false);
                }

                // Forces a request through the player's current Lavalink session.
                // A node restart invalidates that session while the local player
                // can still remain cached in PlayerManager.
                await player.RefreshAsync(cancellationToken).ConfigureAwait(false);
                return player;
            }
            catch (Exception ex) when (IsMissingLavalinkSession(ex))
            {
                player ??= await _audioService.Players.GetPlayerAsync(guildId).ConfigureAwait(false);
                if (player is not null)
                {
                    try
                    {
                        await player.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception disposeException) when (IsMissingLavalinkSession(disposeException))
                    {
                        // The remote session is already gone. DisposeAsync still
                        // notifies PlayerManager so the stale local handle is evicted.
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                return await _audioService.Players.JoinAsync(
                    guildId,
                    voiceChannelId,
                    playerOptions,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private static bool IsYouTubeUrl(Uri uri)
        {
            return uri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                   uri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildYouTubeRestrictionHint()
        {
            return "⚠️ YouTube đang chặn nội dung này do yêu cầu đăng nhập/giới hạn tuổi. Hãy kiểm tra youtube-plugin của Lavalink (client list/cookies hoặc poToken + visitorData) rồi restart Lavalink.";
        }

        private async Task NotifyNowPlayingAsync(ulong guildId, string title)
        {
            if (!PlaybackQueues.TryGetValue(guildId, out var queue))
            {
                return;
            }

            var guild = _client.GetGuild(guildId);
            var textChannel = guild?.GetTextChannel(queue.TextChannelId);
            if (textChannel is not null)
            {
                await textChannel.SendMessageAsync($"▶️ Đang phát: **{title}**").ConfigureAwait(false);
            }
        }

        private async Task NotifyPlaylistIssueAsync(ulong guildId, string message)
        {
            if (!PlaybackQueues.TryGetValue(guildId, out var queue))
            {
                return;
            }

            var guild = _client.GetGuild(guildId);
            var textChannel = guild?.GetTextChannel(queue.TextChannelId);
            if (textChannel is not null)
            {
                await textChannel.SendMessageAsync(message).ConfigureAwait(false);
            }
        }

        private async Task<bool> RegisterPlaylistPlaybackFailureAsync(
            ulong guildId,
            PlaybackQueue queue,
            int trackIndex,
            string title,
            string reason)
        {
            int failures;
            lock (queue)
            {
                failures = ++queue.ConsecutivePlaybackFailures;
            }

            if (failures >= 3)
            {
                await NotifyPlaylistIssueAsync(
                    guildId,
                    $"⛔ Đã dừng playlist sau {failures} bài lỗi liên tiếp. Lỗi gần nhất: **{title}** — {reason} Hãy kiểm tra log Lavalink/YouTube trước khi thử lại.")
                    .ConfigureAwait(false);
                return true;
            }

            await NotifyPlaylistIssueAsync(
                guildId,
                $"⚠️ Bỏ qua bài {trackIndex + 1}: **{title}** — {reason}")
                .ConfigureAwait(false);
            return false;
        }

        private string BuildPlaybackErrorMessage(int trackIndex, string trackTitle, LavalinkLogAnalyzer.LavalinkError analyzedError)
        {
            var baseMessage = $"⚠️ Bỏ qua bài {trackIndex + 1}: **{trackTitle}**";

            // Add category-specific message
            var categoryMessage = analyzedError.Category switch
            {
                LavalinkLogAnalyzer.ErrorCategory.AgeRestricted =>
                    "vì video này có giới hạn tuổi.",
                
                LavalinkLogAnalyzer.ErrorCategory.LoginRequired =>
                    "vì video yêu cầu đăng nhập.",
                
                LavalinkLogAnalyzer.ErrorCategory.ContentUnavailable =>
                    "vì video đã bị xóa hoặc không khả dụng.",
                
                LavalinkLogAnalyzer.ErrorCategory.NetworkError =>
                    "do lỗi kết nối mạng với YouTube.",
                
                LavalinkLogAnalyzer.ErrorCategory.InvalidTrack =>
                    "vì bài hát không hợp lệ hoặc không có âm thanh.",
                
                LavalinkLogAnalyzer.ErrorCategory.HttpError =>
                    $"vì YouTube trả về lỗi ({analyzedError.Message}).",
                
                LavalinkLogAnalyzer.ErrorCategory.YoutubePluginError =>
                    "vì vấn đề cấu hình YouTube plugin.",
                
                LavalinkLogAnalyzer.ErrorCategory.PlaylistError =>
                    "vì không thể tải playlist.",
                
                _ => "do lỗi phát nhạc."
            };

            var fullMessage = baseMessage + " " + categoryMessage;

            // Add suggested action if available
            if (!string.IsNullOrWhiteSpace(analyzedError.SuggestedAction))
            {
                fullMessage += $"\n💡 {analyzedError.SuggestedAction}";
            }

            return fullMessage;
        }


        private static async Task<PlaylistLoadResult> LoadPlaylistTrackIdentifiersAsync(string url, CancellationToken cancellationToken)
        {
            var requestUri = new Uri($"http://127.0.0.1:2333/v4/loadtracks?identifier={Uri.EscapeDataString(url)}");

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.TryAddWithoutValidation("Authorization", "youshallnotpass");

            using var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("loadType", out var loadTypeElement))
            {
                return new PlaylistLoadResult
                {
                    Items = Array.Empty<QueueItem>(),
                    ErrorMessage = "Phản hồi Lavalink thiếu loadType.",
                };
            }

            var loadType = loadTypeElement.GetString() ?? string.Empty;
            if (string.Equals(loadType, "error", StringComparison.OrdinalIgnoreCase))
            {
                var errorMessage = "Lavalink trả về lỗi khi tải playlist.";
                if (document.RootElement.TryGetProperty("data", out var errorData) &&
                    errorData.TryGetProperty("message", out var messageElement) &&
                    messageElement.ValueKind == JsonValueKind.String)
                {
                    var parsed = messageElement.GetString();
                    if (!string.IsNullOrWhiteSpace(parsed))
                    {
                        errorMessage = parsed;
                    }
                }

                return new PlaylistLoadResult
                {
                    Items = Array.Empty<QueueItem>(),
                    IsLoginRequired = IsLoginRequiredPlaybackError(errorMessage),
                    ErrorMessage = errorMessage,
                };
            }

            if (!string.Equals(loadType, "playlist", StringComparison.OrdinalIgnoreCase))
            {
                return new PlaylistLoadResult
                {
                    Items = Array.Empty<QueueItem>(),
                    ErrorMessage = $"Lavalink trả về loadType={loadType}.",
                };
            }

            if (!document.RootElement.TryGetProperty("data", out var dataElement))
            {
                return new PlaylistLoadResult
                {
                    Items = Array.Empty<QueueItem>(),
                    ErrorMessage = "Phản hồi Lavalink thiếu dữ liệu playlist.",
                };
            }

            if (!dataElement.TryGetProperty("tracks", out var tracksElement) || tracksElement.ValueKind != JsonValueKind.Array)
            {
                return new PlaylistLoadResult
                {
                    Items = Array.Empty<QueueItem>(),
                    ErrorMessage = "Không tìm thấy danh sách tracks trong playlist.",
                };
            }

            var tracks = new List<QueueItem>(tracksElement.GetArrayLength());
            foreach (var element in tracksElement.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (element.TryGetProperty("info", out var infoElement) &&
                    infoElement.TryGetProperty("uri", out var uriElement) &&
                    uriElement.ValueKind == JsonValueKind.String)
                {
                    var uri = uriElement.GetString();
                    if (!string.IsNullOrWhiteSpace(uri))
                    {
                        var title = "Không rõ tiêu đề";
                        if (infoElement.TryGetProperty("title", out var titleElement) &&
                            titleElement.ValueKind == JsonValueKind.String)
                        {
                            title = titleElement.GetString() ?? title;
                        }

                        tracks.Add(new QueueItem
                        {
                            Identifier = uri,
                            Title = title,
                        });
                    }
                }
            }

            return new PlaylistLoadResult
            {
                Items = tracks,
            };
        }

        private static string BuildPlaylistPageContent(PlaybackQueue queue, int page)
        {
            var totalItems = queue.Items.Count;
            var totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)PlaylistPageSize));
            page = Math.Clamp(page, 0, totalPages - 1);

            var start = page * PlaylistPageSize;
            var end = Math.Min(start + PlaylistPageSize, totalItems);

            var lines = new List<string>(end - start + 1)
            {
                $"📃 Playlist đang phát — Trang {page + 1}/{totalPages}"
            };

            for (var i = start; i < end; i++)
            {
                var item = queue.Items[i];
                lines.Add($"{i + 1}. {item.Title}");
            }

            return string.Join('\n', lines);
        }

        private static MessageComponent BuildPlaylistPaginationComponent(ulong guildId, ulong requesterId, int page, int totalItems)
        {
            var totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)PlaylistPageSize));
            var previousPage = Math.Max(0, page - 1);
            var nextPage = Math.Min(totalPages - 1, page + 1);


            var component = new ComponentBuilder()
                .WithButton("⬅️ Trước", $"showplaylistclara:prev:{guildId}:{requesterId}:{previousPage}", ButtonStyle.Secondary, disabled: page <= 0)
                .WithButton("➡️ Sau", $"showplaylistclara:next:{guildId}:{requesterId}:{nextPage}", ButtonStyle.Secondary, disabled: page >= totalPages - 1);

            return component.Build();
        }

        public static async Task<bool> TryHandleShowPlaylistComponentAsync(SocketMessageComponent component)
        {
            var customId = component.Data.CustomId;
            if (string.IsNullOrWhiteSpace(customId) || !customId.StartsWith("showplaylistclara:", StringComparison.Ordinal))
            {
                return false;
            }

            var parts = customId.Split(':');
            // Format mới: showplaylistclara:prev|next:guildId:requesterId:page
            if (parts.Length != 5 ||
                (parts[1] != "prev" && parts[1] != "next") ||
                !ulong.TryParse(parts[2], out var guildId) ||
                !ulong.TryParse(parts[3], out var requesterId) ||
                !int.TryParse(parts[4], out var page))
            {
                await component.DeferAsync().ConfigureAwait(false);
                return true;
            }

            if (component.User.Id != requesterId)
            {
                await component.RespondAsync("❌ Bạn không thể điều khiển danh sách này.", ephemeral: true).ConfigureAwait(false);
                return true;
            }

            if (!PlaybackQueues.TryGetValue(guildId, out var queue))
            {
                await component.UpdateAsync(msg =>
                {
                    msg.Content = "⚠️ Đang không phát nhạc trong playlist.";
                    msg.Components = new ComponentBuilder().Build();
                }).ConfigureAwait(false);
                return true;
            }

            string content;
            MessageComponent components;
            lock (queue)
            {
                if (queue.Items.Count == 0)
                {
                    content = "⚠️ Đang không phát nhạc trong playlist.";
                    components = new ComponentBuilder().Build();
                }
                else
                {
                    var totalPages = Math.Max(1, (int)Math.Ceiling(queue.Items.Count / (double)PlaylistPageSize));
                    var safePage = Math.Clamp(page, 0, totalPages - 1);
                    content = BuildPlaylistPageContent(queue, safePage);
                    components = BuildPlaylistPaginationComponent(guildId, requesterId, safePage, queue.Items.Count);
                }
            }

            await component.UpdateAsync(msg =>
            {
                msg.Content = content;
                msg.Components = components;
            }).ConfigureAwait(false);

            return true;
        }

        private async Task MonitorAndAutoDisconnectAsync(
            ulong guildId,
            string trackUri,
            string trackTitle,
            string legacyIdentifier,
            bool legacyFallbackAttempted,
            CancellationToken cancellationToken)
        {
            var hasObservedTrackStart = false;
            var nullTicksBeforeStart = 0;
            DateTimeOffset? observedTrackStartAt = null;
            var stablePlaybackRecorded = false;

            while (true)
            {
                var playbackEvent = await _playbackEvents
                    .WaitAsync(guildId, TimeSpan.FromSeconds(16), cancellationToken)
                    .ConfigureAwait(false);

                if (IsTerminalPlaybackFailure(playbackEvent) &&
                    IsPlaybackEventForTrack(playbackEvent, legacyIdentifier))
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Lavalink {playbackEvent.Kind} guild {guildId}, track {playbackEvent.TrackIdentifier}: {SummarizePlaybackError(playbackEvent.Error)}");
                    await NotifyPlaylistIssueAsync(
                        guildId,
                        $"⚠️ Không thể phát **{trackTitle}** từ URL YouTube đã chọn ({(playbackEvent.Kind == PlaybackEventKind.Stuck ? "track bị kẹt" : "YouTube từ chối stream")}).")
                        .ConfigureAwait(false);

                    // This callback is definitive. Mark the attempt as failed
                    // so the queue advances (or disconnects) without replaying
                    // the same unplayable URL through the legacy retry path.
                    hasObservedTrackStart = true;
                    observedTrackStartAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10);
                    legacyFallbackAttempted = true;
                }

                ILavalinkPlayer? player;
                try
                {
                    player = await _audioService.Players.GetPlayerAsync(guildId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Không thể lấy player guild {guildId} khi stop: {ex.Message}");
                    await ReplyAsync("⚠️ Đã xóa hàng chờ và trạng thái phát cục bộ, nhưng hiện không thể liên lạc với Lavalink để rời voice.");
                    return;
                }

                if (player is null)
                {
                    return;
                }

                var currentTrack = player.CurrentTrack;
                if (currentTrack is null)
                {
                    if (!hasObservedTrackStart)
                    {
                        nullTicksBeforeStart++;
                        if (nullTicksBeforeStart >= 1)
                        {
                            if (!legacyFallbackAttempted)
                            {
                                legacyFallbackAttempted = true;
                                var legacyTrack = await _audioService.Tracks
                                    .LoadTrackAsync(legacyIdentifier, TrackSearchMode.None)
                                    .ConfigureAwait(false);
                                if (legacyTrack is not null)
                                {
                                    await player.PlayAsync(legacyTrack).ConfigureAwait(false);
                                    await NotifyPlaylistIssueAsync(
                                        guildId,
                                        $"⚠️ URL đã chọn bị lỗi với **{trackTitle}**; đang thử phát lại cùng URL.")
                                        .ConfigureAwait(false);
                                    trackUri = legacyIdentifier;
                                    nullTicksBeforeStart = 0;
                                    observedTrackStartAt = null;
                                    stablePlaybackRecorded = false;
                                    continue;
                                }
                            }

                            _playbackRouter.Reset(guildId);
                            await NotifyPlaylistIssueAsync(guildId, $"⚠️ Không thể phát bài **{trackTitle}** sau khi đã thử lại URL đã chọn.").ConfigureAwait(false);
                            await player.DisconnectAsync().ConfigureAwait(false);
                            return;
                        }
                        continue;
                    }

                    PlaybackQueues.TryGetValue(guildId, out var queue);

                    var playedDuration = observedTrackStartAt.HasValue
                        ? DateTimeOffset.UtcNow - observedTrackStartAt.Value
                        : TimeSpan.Zero;
                    if (playedDuration < TimeSpan.FromSeconds(6))
                    {
                        if (!legacyFallbackAttempted)
                        {
                            legacyFallbackAttempted = true;
                            var legacyTrack = await _audioService.Tracks
                                .LoadTrackAsync(legacyIdentifier, TrackSearchMode.None)
                                .ConfigureAwait(false);
                            if (legacyTrack is not null)
                            {
                                await player.PlayAsync(legacyTrack).ConfigureAwait(false);
                                await NotifyPlaylistIssueAsync(
                                    guildId,
                                    $"⚠️ URL đã chọn dừng sớm với **{trackTitle}**; đang thử phát lại cùng URL.")
                                    .ConfigureAwait(false);
                                trackUri = legacyIdentifier;
                                hasObservedTrackStart = false;
                                nullTicksBeforeStart = 0;
                                observedTrackStartAt = null;
                                stablePlaybackRecorded = false;
                                continue;
                            }
                        }

                        _playbackRouter.Reset(guildId);
                    }

                    // Queue mode ON: tự động chuyển sang bài kế tiếp
                    if (QueueModeEnabled.GetValueOrDefault(guildId, false) && queue is not null)
                    {
                        int nextIndex;
                        string nextIdentifier = "";
                        string nextTitle = "";
                        bool hasNext = false;
                        lock (queue)
                        {
                            // Kiểm tra nếu có yêu cầu nhảy (next/prev/jump)
                            var requested = queue.RequestedIndex;
                            var current = queue.Index;
                            if (requested != current)
                            {
                                // Có yêu cầu nhảy, dùng requestedIndex
                                nextIndex = requested;
                                queue.Index = nextIndex;
                            }
                            else
                            {
                                // Tự nhiên hết bài, chuyển tiếp
                                nextIndex = current + 1;
                                queue.Index = nextIndex;
                                queue.RequestedIndex = nextIndex;
                            }

                            if (nextIndex >= 0 && nextIndex < queue.Items.Count)
                            {
                                nextIdentifier = queue.Items[nextIndex].Identifier;
                                nextTitle = queue.Items[nextIndex].Title;
                                hasNext = true;
                            }
                        }

                        if (hasNext)
                        {
                            var nextPrimary = await _playbackRouter
                                .PlayPrimaryAsync(guildId, nextIdentifier, nextTitle, player, cancellationToken)
                                .ConfigureAwait(false);
                            legacyFallbackAttempted = false;
                            if (nextPrimary.Started)
                            {
                                await NotifyNowPlayingAsync(guildId, nextTitle).ConfigureAwait(false);
                                trackUri = nextPrimary.TrackUri ?? nextIdentifier;
                                legacyIdentifier = nextIdentifier;
                                trackTitle = nextTitle;
                                hasObservedTrackStart = false;
                                nullTicksBeforeStart = 0;
                                observedTrackStartAt = null;
                                stablePlaybackRecorded = false;
                                continue;
                            }

                            var nextLegacy = await _audioService.Tracks
                                .LoadTrackAsync(nextIdentifier, TrackSearchMode.None)
                                .ConfigureAwait(false);
                            if (nextLegacy is not null)
                            {
                                legacyFallbackAttempted = true;
                                await player.PlayAsync(nextLegacy).ConfigureAwait(false);
                                await NotifyNowPlayingAsync(guildId, nextTitle).ConfigureAwait(false);
                                trackUri = nextIdentifier;
                                legacyIdentifier = nextIdentifier;
                                trackTitle = nextTitle;
                                hasObservedTrackStart = false;
                                nullTicksBeforeStart = 0;
                                observedTrackStartAt = null;
                                stablePlaybackRecorded = false;
                                continue;
                            }
                        }

                        // Hết queue, ngắt kết nối
                        await player.DisconnectAsync().ConfigureAwait(false);
                        return;
                    }

                    if (queue?.LoopEnabled == true)
                    {
                        var loopPrimary = await _playbackRouter
                            .PlayPrimaryAsync(guildId, legacyIdentifier, trackTitle, player, cancellationToken)
                            .ConfigureAwait(false);
                        legacyFallbackAttempted = false;
                        if (loopPrimary.Started)
                        {
                            await NotifyNowPlayingAsync(guildId, trackTitle).ConfigureAwait(false);
                            trackUri = loopPrimary.TrackUri ?? legacyIdentifier;
                            hasObservedTrackStart = false;
                            nullTicksBeforeStart = 0;
                            observedTrackStartAt = null;
                            stablePlaybackRecorded = false;
                            continue;
                        }

                        var loopLegacy = await _audioService.Tracks
                            .LoadTrackAsync(legacyIdentifier, TrackSearchMode.None)
                            .ConfigureAwait(false);
                        if (loopLegacy is not null)
                        {
                            legacyFallbackAttempted = true;
                            await player.PlayAsync(loopLegacy).ConfigureAwait(false);
                            await NotifyNowPlayingAsync(guildId, trackTitle).ConfigureAwait(false);
                            trackUri = legacyIdentifier;
                            hasObservedTrackStart = false;
                            nullTicksBeforeStart = 0;
                            observedTrackStartAt = null;
                            stablePlaybackRecorded = false;
                            continue;
                        }
                    }

                    await player.DisconnectAsync().ConfigureAwait(false);
                    return;
                }

                var currentUri = currentTrack.Uri?.ToString() ?? string.Empty;
                if (!string.Equals(currentUri, trackUri, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                hasObservedTrackStart = true;
                nullTicksBeforeStart = 0;
                observedTrackStartAt ??= DateTimeOffset.UtcNow;
                if (!stablePlaybackRecorded && DateTimeOffset.UtcNow - observedTrackStartAt.Value >= TimeSpan.FromSeconds(6))
                {
                    _playbackRouter.Reset(guildId);
                    stablePlaybackRecorded = true;
                }
            }
        }

        private async Task PlayPlaylistAsync(ulong guildId, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!PlaybackQueues.TryGetValue(guildId, out var queue))
                {
                    return;
                }

                int index;
                int requestedIndex;
                lock (queue)
                {
                    index = queue.Index;
                    requestedIndex = queue.RequestedIndex;
                    if (requestedIndex != index)
                    {
                        index = requestedIndex;
                        queue.Index = index;
                    }
                }

                if (index < 0)
                {
                    index = 0;
                }

                string? identifier;
                string title = "Không rõ tiêu đề";
                bool waitingForItems;
                bool hasItem;
                lock (queue)
                {
                    if (index >= queue.Items.Count)
                    {
                        waitingForItems = queue.PendingLoaders > 0;
                        hasItem = false;
                        identifier = null;
                    }
                    else
                    {
                        waitingForItems = false;
                        hasItem = true;
                        identifier = queue.Items[index].Identifier;
                        title = queue.Items[index].Title;
                    }
                }

                if (waitingForItems)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!hasItem)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(identifier))
                {
                    lock (queue)
                    {
                        queue.Index = index + 1;
                        queue.RequestedIndex = queue.Index;
                    }

                    continue;
                }

                var player = await _audioService.Players.GetPlayerAsync(guildId).ConfigureAwait(false);
                if (player is null)
                {
                    return;
                }

                _playbackEvents.Reset(guildId);
                var primary = await _playbackRouter
                    .PlayPrimaryAsync(guildId, identifier, title, player, cancellationToken)
                    .ConfigureAwait(false);
                var legacyFallbackAttempted = false;
                if (!primary.Started)
                {
                    legacyFallbackAttempted = true;
                    var legacyTrack = await _audioService.Tracks
                        .LoadTrackAsync(identifier, TrackSearchMode.None)
                        .ConfigureAwait(false);
                    if (legacyTrack is null)
                    {
                        await NotifyPlaylistIssueAsync(
                            guildId,
                            $"⚠️ Bỏ qua bài {index + 1}: **{title}** vì URL đã chọn không tải được sau khi thử lại.")
                            .ConfigureAwait(false);
                        lock (queue)
                        {
                            queue.Index = index + 1;
                            queue.RequestedIndex = queue.Index;
                        }

                        continue;
                    }

                    await player.PlayAsync(legacyTrack).ConfigureAwait(false);
                    await NotifyPlaylistIssueAsync(
                        guildId,
                        $"⚠️ Lần phát đầu thất bại với **{title}**; đã thử lại cùng URL.")
                        .ConfigureAwait(false);
                }
                var hasObservedTrackStart = false;
                var nowPlayingNotified = false;
                var stablePlaybackRecorded = false;
                var nullTicksBeforeStart = 0;
                DateTimeOffset? observedTrackStartAt = null;

                while (true)
                {
                    var playbackEvent = await _playbackEvents
                        .WaitAsync(guildId, TimeSpan.FromSeconds(16), cancellationToken)
                        .ConfigureAwait(false);

                    if (IsTerminalPlaybackFailure(playbackEvent) &&
                        IsPlaybackEventForTrack(playbackEvent, identifier))
                    {
                        var failureReason = playbackEvent.Kind == PlaybackEventKind.Stuck
                            ? "track bị kẹt và vượt quá ngưỡng của Lavalink."
                            : "YouTube từ chối cung cấp audio stream cho video đã chọn.";
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Lavalink {playbackEvent.Kind} guild {guildId}, track {playbackEvent.TrackIdentifier}: {SummarizePlaybackError(playbackEvent.Error)}");

                        var stopPlaylist = await RegisterPlaylistPlaybackFailureAsync(
                            guildId,
                            queue,
                            index,
                            title,
                            failureReason)
                            .ConfigureAwait(false);
                        if (stopPlaylist)
                        {
                            PlaybackQueues.TryRemove(guildId, out _);
                            try
                            {
                                await player.DisconnectAsync().ConfigureAwait(false);
                            }
                            catch (Exception disconnectException)
                            {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Không thể ngắt player guild {guildId} sau chuỗi lỗi: {disconnectException.Message}");
                            }
                            return;
                        }

                        lock (queue)
                        {
                            queue.Index = index + 1;
                            queue.RequestedIndex = queue.Index;
                        }

                        // TrackException is terminal for this selected URL. Do
                        // not replay it: the following TrackEnded callback is
                        // stale and will be drained before the next item starts.
                        break;
                    }

                    if (!PlaybackQueues.TryGetValue(guildId, out queue))
                    {
                        return;
                    }

                    lock (queue)
                    {
                        requestedIndex = queue.RequestedIndex;
                    }

                    if (requestedIndex != index)
                    {
                        player = await _audioService.Players.GetPlayerAsync(guildId).ConfigureAwait(false);
                        if (player is null)
                        {
                            return;
                        }

                        await player.StopAsync().ConfigureAwait(false);
                        break;
                    }

                    player = await _audioService.Players.GetPlayerAsync(guildId).ConfigureAwait(false);
                    if (player is null)
                    {
                        return;
                    }

                    var currentTrack = player.CurrentTrack;
                    if (currentTrack is null)
                    {
                        if (!hasObservedTrackStart)
                        {
                            nullTicksBeforeStart++;
                            if (nullTicksBeforeStart >= 1)
                            {
                                if (!legacyFallbackAttempted)
                                {
                                    legacyFallbackAttempted = true;
                                    var legacyTrack = await _audioService.Tracks
                                        .LoadTrackAsync(identifier, TrackSearchMode.None)
                                        .ConfigureAwait(false);
                                    if (legacyTrack is not null)
                                    {
                                        await player.PlayAsync(legacyTrack).ConfigureAwait(false);
                                        await NotifyPlaylistIssueAsync(
                                            guildId,
                                            $"⚠️ URL đã chọn bị lỗi với **{title}**; đang thử phát lại cùng URL.")
                                            .ConfigureAwait(false);
                                        hasObservedTrackStart = false;
                                        nowPlayingNotified = false;
                                        stablePlaybackRecorded = false;
                                        nullTicksBeforeStart = 0;
                                        observedTrackStartAt = null;
                                        continue;
                                    }
                                }

                                _playbackRouter.Reset(guildId);

                                var stopPlaylist = await RegisterPlaylistPlaybackFailureAsync(
                                    guildId,
                                    queue,
                                    index,
                                    title,
                                    "Lavalink không xác nhận track đã bắt đầu.")
                                    .ConfigureAwait(false);
                                if (stopPlaylist)
                                {
                                    PlaybackQueues.TryRemove(guildId, out _);
                                    return;
                                }

                                lock (queue)
                                {
                                    queue.Index = index + 1;
                                    queue.RequestedIndex = queue.Index;
                                }

                                break;
                            }

                            continue;
                        }

                        var playedDuration = observedTrackStartAt.HasValue
                            ? DateTimeOffset.UtcNow - observedTrackStartAt.Value
                            : TimeSpan.Zero;
                        if (playedDuration < TimeSpan.FromSeconds(6))
                        {
                            if (!legacyFallbackAttempted)
                            {
                                legacyFallbackAttempted = true;
                                var legacyTrack = await _audioService.Tracks
                                    .LoadTrackAsync(identifier, TrackSearchMode.None)
                                    .ConfigureAwait(false);
                                if (legacyTrack is not null)
                                {
                                    await player.PlayAsync(legacyTrack).ConfigureAwait(false);
                                    await NotifyPlaylistIssueAsync(
                                        guildId,
                                        $"⚠️ URL đã chọn dừng sớm với **{title}**; đang thử phát lại cùng URL.")
                                        .ConfigureAwait(false);
                                    hasObservedTrackStart = false;
                                    nowPlayingNotified = false;
                                    stablePlaybackRecorded = false;
                                    nullTicksBeforeStart = 0;
                                    observedTrackStartAt = null;
                                    continue;
                                }
                            }

                            _playbackRouter.Reset(guildId);

                            var stopPlaylist = await RegisterPlaylistPlaybackFailureAsync(
                                guildId,
                                queue,
                                index,
                                title,
                                "audio dừng ngay sau khi bắt đầu; xem TrackException trong log Lavalink.")
                                .ConfigureAwait(false);
                            if (stopPlaylist)
                            {
                                PlaybackQueues.TryRemove(guildId, out _);
                                return;
                            }
                        }

                        if (queue.LoopEnabled)
                        {
                            var loopPrimary = await _playbackRouter
                                .PlayPrimaryAsync(guildId, identifier, title, player, cancellationToken)
                                .ConfigureAwait(false);
                            legacyFallbackAttempted = false;
                            if (loopPrimary.Started)
                            {
                                hasObservedTrackStart = false;
                                nowPlayingNotified = false;
                                stablePlaybackRecorded = false;
                                nullTicksBeforeStart = 0;
                                observedTrackStartAt = null;
                                continue;
                            }

                            var loopLegacy = await _audioService.Tracks
                                .LoadTrackAsync(identifier, TrackSearchMode.None)
                                .ConfigureAwait(false);
                            if (loopLegacy is not null)
                            {
                                legacyFallbackAttempted = true;
                                await player.PlayAsync(loopLegacy).ConfigureAwait(false);
                                hasObservedTrackStart = false;
                                nowPlayingNotified = false;
                                stablePlaybackRecorded = false;
                                nullTicksBeforeStart = 0;
                                observedTrackStartAt = null;
                                continue;
                            }
                        }

                        lock (queue)
                        {
                            queue.Index = index + 1;
                            queue.RequestedIndex = queue.Index;
                        }

                        break;
                    }

                    if (!nowPlayingNotified)
                    {
                        await NotifyNowPlayingAsync(guildId, title).ConfigureAwait(false);
                        nowPlayingNotified = true;
                    }

                    hasObservedTrackStart = true;
                    nullTicksBeforeStart = 0;
                    observedTrackStartAt ??= DateTimeOffset.UtcNow;
                    if (!stablePlaybackRecorded && DateTimeOffset.UtcNow - observedTrackStartAt.Value >= TimeSpan.FromSeconds(6))
                    {
                        lock (queue)
                        {
                            queue.ConsecutivePlaybackFailures = 0;
                        }

                            stablePlaybackRecorded = true;
                            _playbackRouter.Reset(guildId);
                    }
                }
            }

            PlaybackQueues.TryRemove(guildId, out _);
            var finalPlayer = await _audioService.Players.GetPlayerAsync(guildId).ConfigureAwait(false);
            if (finalPlayer is not null)
            {
                await finalPlayer.DisconnectAsync().ConfigureAwait(false);
            }
        }

        private async Task LoadRemainingSpotifyPlaylistTracksAsync(
            ulong guildId,
            PlaybackQueue targetQueue,
            IReadOnlyList<SpotifyTrackInfo> spotifyTracks,
            IUserMessage statusMessage,
            CancellationToken cancellationToken)
        {
            var addedCount = 0;
            try
            {
                for (var index = 1; index < spotifyTracks.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!PlaybackQueues.TryGetValue(guildId, out var currentQueue) ||
                        !ReferenceEquals(currentQueue, targetQueue))
                    {
                        return;
                    }

                    var spotifyTrack = spotifyTracks[index];
                    if (string.IsNullOrWhiteSpace(spotifyTrack.SearchQuery))
                    {
                        continue;
                    }

                    try
                    {
                        var youtubeTrack = await ResolveSpotifyTrackOnYouTubeAsync(spotifyTrack)
                            .ConfigureAwait(false);
                        if (youtubeTrack is null)
                        {
                            continue;
                        }

                        lock (targetQueue)
                        {
                            targetQueue.Items.Add(youtubeTrack);
                        }

                        addedCount++;
                    }
                    catch (Exception exception)
                    {
                        Console.WriteLine($"[Spotify link] Bỏ qua track {index + 1}: {exception.Message}");
                    }
                }

                try
                {
                    await statusMessage.ModifyAsync(message =>
                        message.Content = $"✅ Đã bổ sung {addedCount} bài hát vào queue.").ConfigureAwait(false);
                }
                catch
                {
                    // The status message may have been deleted while loading.
                }
            }
            catch (OperationCanceledException)
            {
                // A newer play/stop command replaced this playlist.
            }
            finally
            {
                lock (targetQueue)
                {
                    targetQueue.PendingLoaders = Math.Max(0, targetQueue.PendingLoaders - 1);
                }
            }
        }

        private async Task<QueueItem?> ResolveSpotifyTrackOnYouTubeAsync(SpotifyTrackInfo spotifyTrack)
        {
            var queries = new[]
            {
                spotifyTrack.SearchQuery,
                string.IsNullOrWhiteSpace(spotifyTrack.Artist)
                    ? spotifyTrack.Title
                    : $"{spotifyTrack.Title} {spotifyTrack.Artist}",
                spotifyTrack.Title,
            }
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var query in queries)
            {
                foreach (var (identifier, searchMode) in new[]
                {
                    (query, TrackSearchMode.YouTube),
                    ($"ytsearch:{query}", TrackSearchMode.None),
                })
                {
                    try
                    {
                        var track = await _audioService.Tracks
                            .LoadTrackAsync(identifier, searchMode)
                            .ConfigureAwait(false);
                        if (track is null)
                        {
                            continue;
                        }

                        var youtubeUrl = track.Uri?.ToString();
                        if (string.IsNullOrWhiteSpace(youtubeUrl) && !string.IsNullOrWhiteSpace(track.Identifier))
                        {
                            youtubeUrl = $"https://www.youtube.com/watch?v={track.Identifier}";
                        }

                        if (!string.IsNullOrWhiteSpace(youtubeUrl))
                        {
                            return new QueueItem
                            {
                                Identifier = youtubeUrl,
                                Title = string.IsNullOrWhiteSpace(track.Title)
                                    ? $"{spotifyTrack.Title} - {spotifyTrack.Artist}".Trim(' ', '-')
                                    : track.Title,
                            };
                        }
                    }
                    catch (Exception exception)
                    {
                        Console.WriteLine($"[Spotify → YouTube] Search lỗi với '{identifier}': {exception.Message}");
                    }
                }
            }

            Console.WriteLine($"[Spotify → YouTube] Không có kết quả cho: {spotifyTrack.Title} - {spotifyTrack.Artist}");
            return null;
        }

        [Command("queueclara")]
        [Alias("queue")]
        [Summary("Bật/tắt chế độ hàng chờ. Khi bật, /play sẽ thêm bài vào playlist thay vì ghi đè.")]
        public async Task QueueAsync()
        {
            var current = QueueModeEnabled.GetValueOrDefault(Context.Guild.Id, false);
            if (current)
            {
                QueueModeEnabled.TryRemove(Context.Guild.Id, out _);
                await ReplyAsync("⏹️ Đã **tắt** chế độ hàng chờ. Các bài hát mới sẽ ghi đè lên bài đang phát.");
            }
            else
            {
                QueueModeEnabled[Context.Guild.Id] = true;
                await ReplyAsync("✅ Đã **bật** chế độ hàng chờ. Các bài hát mới sẽ được thêm vào playlist.");
            }
        }

        private async Task AddToQueueOrPlayNowAsync(string identifier, string title, ILavalinkPlayer player, CancellationToken playbackToken)
        {
            if (!QueueModeEnabled.GetValueOrDefault(Context.Guild.Id, false))
            {
                await PlayTrackImmediatelyAsync(identifier, title, player, playbackToken);
                return;
            }

            var currentTrack = player.CurrentTrack;
            if (currentTrack is null)
            {
                await PlayTrackImmediatelyAsync(identifier, title, player, playbackToken);
                return;
            }

            // A track is playing, add to queue
            if (PlaybackQueues.TryGetValue(Context.Guild.Id, out var queue))
            {
                lock (queue)
                {
                    queue.Items.Add(new QueueItem { Identifier = identifier, Title = title });
                }
                await ReplyAsync($"✅ Đã thêm **{title}** vào hàng chờ (vị trí thứ {queue.Items.Count}).");
            }
            else
            {
                var currentUri = currentTrack.Uri?.ToString() ?? identifier;
                var currentTitle = currentTrack.Title ?? "Đang phát";
                var newQueue = new PlaybackQueue
                {
                    Items = new List<QueueItem>
                    {
                        new QueueItem { Identifier = currentUri, Title = currentTitle },
                        new QueueItem { Identifier = identifier, Title = title },
                    },
                    TextChannelId = Context.Channel.Id,
                    Index = 0,
                    RequestedIndex = 0,
                };
                PlaybackQueues[Context.Guild.Id] = newQueue;

                await ReplyAsync($"✅ Đã thêm **{title}** vào hàng chờ (vị trí thứ 2).");
            }
        }

        private async Task PlayTrackImmediatelyAsync(string identifier, string title, ILavalinkPlayer player, CancellationToken playbackToken)
        {
            ResetPlaybackSpeed(Context.Guild.Id);
            player.Filters.Timescale = null;
            await player.Filters.CommitAsync().ConfigureAwait(false);

            _playbackEvents.Reset(Context.Guild.Id);
            var primary = await _playbackRouter
                .PlayPrimaryAsync(Context.Guild.Id, identifier, title, player, playbackToken)
                .ConfigureAwait(false);
            var legacyFallbackAttempted = false;
            var activeTrackUri = primary.TrackUri ?? identifier;
            if (!primary.Started)
            {
                legacyFallbackAttempted = true;
                var legacyTrack = await _audioService.Tracks
                    .LoadTrackAsync(identifier, TrackSearchMode.None)
                    .ConfigureAwait(false);
                if (legacyTrack is null)
                {
                    await ReplyAsync($"❌ Không thể tải bài hát từ URL đã chọn sau khi thử lại: **{title}**");
                    return;
                }

                await player.PlayAsync(legacyTrack).ConfigureAwait(false);
                activeTrackUri = identifier;
                await ReplyAsync($"⚠️ Lần phát đầu thất bại với **{title}**; đã thử lại cùng URL.");
            }

            await ReplyAsync($"▶️ Đang phát: **{title}**");

            PlaybackQueues[Context.Guild.Id] = new PlaybackQueue
            {
                Items = new List<QueueItem>
                {
                    new QueueItem { Identifier = identifier, Title = title },
                },
                TextChannelId = Context.Channel.Id,
                Index = 0,
                RequestedIndex = 0,
            };

            if (!string.IsNullOrWhiteSpace(identifier))
            {
                _ = Task.Run(
                    () => MonitorAndAutoDisconnectAsync(
                        Context.Guild.Id,
                        activeTrackUri,
                        title,
                        identifier,
                        legacyFallbackAttempted,
                        playbackToken),
                    playbackToken);
            }
        }

        [Command("searchclara", RunMode = RunMode.Async)]
        [Alias("search", "s")]
        [Summary("Tìm kiếm bài hát trên YouTube và hiển thị danh sách kết quả")]
        public async Task SearchAsync([Remainder] string query)
        {
            query = query.Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                await ReplyAsync("❌ Vui lòng nhập tên bài hát cần tìm!");
                return;
            }

            // Sửa tin nhắn search cũ thành "đã từng tìm: ..."
            if (LastSearchMessage.TryRemove(Context.Guild.Id, out var oldRef))
            {
                var oldQuery = LastSearchResults.TryGetValue(Context.Guild.Id, out var oldResult) ? oldResult.Query : "";
                try
                {
                    if (_client.GetChannel(oldRef.ChannelId) is IMessageChannel oldChannel
                        && await oldChannel.GetMessageAsync(oldRef.MessageId) is IUserMessage oldMsg)
                    {
                        await oldMsg.ModifyAsync(m =>
                        {
                            m.Content = $"🔍 Đã từng tìm: **{oldQuery}**";
                            m.Embeds = new Optional<Embed[]>(Array.Empty<Embed>());
                        });
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Không thể sửa tin nhắn search cũ: {ex.Message}");
                }
            }

            var searchingMsg = await ReplyAsync($"🔍 Đang tìm kiếm: **{query}**...");

            try
            {
                var tracks = await SearchYouTubeAsync($"ytsearch:{query}", SearchResultLimit, CancellationToken.None);

                if (tracks.Count == 0)
                {
                    await searchingMsg.ModifyAsync(m => m.Content = "❌ Không tìm thấy kết quả nào!");
                    return;
                }

                LastSearchResults[Context.Guild.Id] = new SearchResult
                {
                    Tracks = tracks,
                    Query = query,
                };

                var embed = new EmbedBuilder()
                    .WithTitle($"🔍 Kết quả tìm kiếm: {query}")
                    .WithDescription("Dùng `play <số>` hoặc `p <số>` để phát/thêm vào hàng chờ.")
                    .WithColor(Color.Blue)
                    .WithFooter($"Tìm thấy {tracks.Count} kết quả");

                for (int i = 0; i < tracks.Count; i++)
                {
                    var track = tracks[i];
                    var duration = track.Duration.TotalHours >= 1
                        ? track.Duration.ToString(@"h\:mm\:ss")
                        : track.Duration.ToString(@"mm\:ss");
                    embed.AddField($"{i + 1}. {track.Title}", $"🎤 {track.Author} | ⏱️ {duration}", false);
                }

                // Edit tin nhắn "Đang tìm kiếm..." thành kết quả
                await searchingMsg.ModifyAsync(m =>
                {
                    m.Content = "";
                    m.Embed = embed.Build();
                });

                // Lưu ID tin nhắn kết quả để edit lại khi search mới
                var messageId = searchingMsg.Id;
                if (messageId == 0)
                {
                    // Slash command: lấy tin nhắn bot mới nhất có embed trong channel
                    var recent = await Context.Channel.GetMessagesAsync(5).FlattenAsync();
                    var botMsg = recent.FirstOrDefault(m => m.Author.Id == _client.CurrentUser.Id && m.Embeds.Count > 0);
                    if (botMsg is not null)
                        messageId = botMsg.Id;
                }

                if (messageId != 0)
                    LastSearchMessage[Context.Guild.Id] = (Context.Channel.Id, messageId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Lỗi tìm kiếm: {ex}");
                await ReplyAsync($"❌ Đã xảy ra lỗi khi tìm kiếm: {ex.Message}");
            }
        }

        private static async Task<List<SearchTrack>> SearchYouTubeAsync(string identifier, int limit, CancellationToken cancellationToken)
        {
            var requestUri = new Uri($"http://127.0.0.1:2333/v4/loadtracks?identifier={Uri.EscapeDataString(identifier)}");

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.TryAddWithoutValidation("Authorization", "youshallnotpass");

            using var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("loadType", out var loadTypeElement))
                return new List<SearchTrack>();

            var loadType = loadTypeElement.GetString() ?? string.Empty;
            if (!string.Equals(loadType, "search", StringComparison.OrdinalIgnoreCase))
                return new List<SearchTrack>();

            if (!document.RootElement.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
                return new List<SearchTrack>();

            var results = new List<SearchTrack>();
            foreach (var element in dataElement.EnumerateArray())
            {
                if (results.Count >= limit) break;

                if (!element.TryGetProperty("info", out var info)) continue;

                var title = info.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() ?? "Không rõ tiêu đề" : "Không rõ tiêu đề";
                var author = info.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.String
                    ? a.GetString() ?? "Không rõ nghệ sĩ" : "Không rõ nghệ sĩ";
                var uri = info.TryGetProperty("uri", out var u) && u.ValueKind == JsonValueKind.String
                    ? u.GetString() ?? "" : "";
                var durationMs = info.TryGetProperty("length", out var d) && d.ValueKind == JsonValueKind.Number
                    ? d.GetInt64() : 0;

                if (string.IsNullOrWhiteSpace(uri)) continue;

                results.Add(new SearchTrack
                {
                    Identifier = uri,
                    Title = title,
                    Author = author,
                    Duration = TimeSpan.FromMilliseconds(durationMs),
                });
            }

            return results;
        }

        [Command("playclara", RunMode = RunMode.Async)]
        [Alias("play", "p")]
        public async Task PlayAsync([Remainder] string query)
        {
            query = query.Trim().Trim('<', '>', '`', '"');
            if (Context.User is not SocketGuildUser user || user.VoiceChannel == null)
            {
                await ReplyAsync("❌ Bạn phải vào kênh voice trước!");
                return;
            }

            // Kiểm tra nếu query là số (chọn từ kết quả tìm kiếm)
            if (int.TryParse(query, out var searchIndex))
            {
                if (!LastSearchResults.TryGetValue(Context.Guild.Id, out var searchResult) || searchResult.Tracks.Count == 0)
                {
                    await ReplyAsync("❌ Chưa có kết quả tìm kiếm. Dùng `search <tên bài>` trước!");
                    return;
                }

                // Kiểm tra kết quả tìm kiếm có quá cũ không (15 phút)
                if ((DateTime.Now - searchResult.CreatedAt).TotalMinutes > 15)
                {
                    LastSearchResults.TryRemove(Context.Guild.Id, out _);
                    await ReplyAsync("❌ Kết quả tìm kiếm đã hết hạn. Dùng `search <tên bài>` để tìm lại!");
                    return;
                }

                if (searchIndex < 1 || searchIndex > searchResult.Tracks.Count)
                {
                    await ReplyAsync($"❌ Số không hợp lệ. Chọn từ 1 đến {searchResult.Tracks.Count}.");
                    return;
                }

                var selectedTrack = searchResult.Tracks[searchIndex - 1];
                query = selectedTrack.Identifier;
            }

            ILavalinkPlayer? player;

            try
            {
                var playerOptions = new LavalinkPlayerOptions
                {
                    SelfDeaf = true,
                };
                player = await GetOrCreatePlayerAsync(
                    Context.Guild.Id,
                    user.VoiceChannel.Id,
                    playerOptions,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                await ReplyAsync($"❌ Lỗi khi tạo player / load track: {ex.Message}");
                return;
            }

            // Queue mode ON + đang có track: giữ PlayPlaylistAsync sống, không ReplacePlaybackToken
            bool keepQueue = QueueModeEnabled.GetValueOrDefault(Context.Guild.Id, false) && player.CurrentTrack is not null;
            var playbackToken = keepQueue ? CancellationToken.None : ReplacePlaybackToken(Context.Guild.Id);

            try
            {
                var isUrl = Uri.TryCreate(query, UriKind.Absolute, out var uri);
                if (isUrl && uri is not null && IsYouTubePlaylistUrl(uri))
                {
                    var playlistLoadResult = await LoadPlaylistTrackIdentifiersAsync(query, playbackToken);
                    if (playlistLoadResult.Items.Count == 0)
                    {
                        if (playlistLoadResult.IsLoginRequired)
                        {
                            await ReplyAsync($"❌ Không thể load playlist do YouTube yêu cầu đăng nhập/giới hạn tuổi.\n{BuildYouTubeRestrictionHint()}");
                            return;
                        }

                        var detail = string.IsNullOrWhiteSpace(playlistLoadResult.ErrorMessage)
                            ? "Không load được playlist hoặc playlist không có bài nào."
                            : playlistLoadResult.ErrorMessage;
                        await ReplyAsync($"❌ {detail}");
                        return;
                    }

                    if (keepQueue && PlaybackQueues.TryGetValue(Context.Guild.Id, out var q))
                    {
                        lock (q) { q.Items.AddRange(playlistLoadResult.Items); }
                        await ReplyAsync($"✅ Đã thêm {playlistLoadResult.Items.Count} bài vào hàng chờ.");
                    }
                    else
                    {
                        PlaybackQueues[Context.Guild.Id] = new PlaybackQueue
                        {
                            Items = new List<QueueItem>(playlistLoadResult.Items),
                            TextChannelId = Context.Channel.Id,
                            Index = 0,
                            RequestedIndex = 0,
                        };
                        await ReplyAsync($"📃 Đã nhận playlist: {playlistLoadResult.Items.Count} bài. Bắt đầu phát...");
                        _ = Task.Run(() => PlayPlaylistAsync(Context.Guild.Id, playbackToken), playbackToken);
                    }
                    return;
                }

                // Spotify links only provide public title/artist metadata.
                // The audio source is always the matching YouTube result.
                if (isUrl && uri is not null && SpotifyService.IsSpotifyUrl(query))
                {
                    // Convert every public Spotify playlist/album entry into a
                    // YouTube result, then hand the result to the normal queue.
                    if (SpotifyService.IsSpotifyPlaylist(query))
                    {
                        var spotifyTracks = await SpotifyService.GetPlaylistTracksAsync(query);
                        if (spotifyTracks.Count == 0)
                        {
                            await ReplyAsync("❌ Không thể đọc danh sách bài hát công khai từ link Spotify.");
                            return;
                        }

                        var statusMsg = await ReplyAsync("🔄 Đang loading các bài hát trong playlist để bổ sung đầy đủ cho queue...");
                        var firstSpotifyTrack = spotifyTracks[0];
                        var firstYoutubeTrack = await ResolveSpotifyTrackOnYouTubeAsync(firstSpotifyTrack);
                        if (firstYoutubeTrack is null)
                        {
                            await statusMsg.ModifyAsync(message =>
                                message.Content = "❌ Không tìm thấy bài đầu tiên của playlist trên YouTube.");
                            return;
                        }

                        var firstQueueItem = new QueueItem
                        {
                            Identifier = firstYoutubeTrack.Identifier,
                            Title = firstYoutubeTrack.Title,
                        };

                        PlaybackQueue targetQueue;
                        if (keepQueue && PlaybackQueues.TryGetValue(Context.Guild.Id, out var sq2))
                        {
                            targetQueue = sq2;
                            lock (targetQueue)
                            {
                                targetQueue.Items.Add(firstQueueItem);
                                targetQueue.PendingLoaders++;
                            }
                        }
                        else
                        {
                            targetQueue = new PlaybackQueue
                            {
                                Items = new List<QueueItem> { firstQueueItem },
                                TextChannelId = Context.Channel.Id,
                                Index = 0,
                                RequestedIndex = 0,
                                PendingLoaders = 1,
                            };
                            PlaybackQueues[Context.Guild.Id] = targetQueue;
                            _ = Task.Run(() => PlayPlaylistAsync(Context.Guild.Id, playbackToken), playbackToken);
                        }

                        _ = Task.Run(
                            () => LoadRemainingSpotifyPlaylistTracksAsync(
                                Context.Guild.Id,
                                targetQueue,
                                spotifyTracks,
                                statusMsg,
                                playbackToken),
                            playbackToken);
                        return;
                    }

                    // Resolve a single Spotify track name, then play YouTube.
                    var spotifyInfo = await SpotifyService.GetTrackInfoAsync(query);
                    if (spotifyInfo is not null)
                    {
                        var spotifyTrack = await ResolveSpotifyTrackOnYouTubeAsync(spotifyInfo);
                        if (spotifyTrack is not null)
                        {
                            await AddToQueueOrPlayNowAsync(spotifyTrack.Identifier, spotifyTrack.Title, player, playbackToken);
                            return;
                        }
                        await ReplyAsync("❌ Không tìm thấy bài hát YouTube tương ứng.");
                        return;
                    }

                    await ReplyAsync("❌ Không thể đọc tên bài hát từ link Spotify công khai.");
                    return;
                }

                var searchMode = isUrl ? TrackSearchMode.None : TrackSearchMode.YouTube;
                var track = await _audioService.Tracks.LoadTrackAsync(query, searchMode);

                if (track is null)
                {
                    if (isUrl && uri is not null && IsYouTubeUrl(uri))
                    {
                        await ReplyAsync($"❌ Lavalink không load được link YouTube.\n{BuildYouTubeRestrictionHint()}");
                        return;
                    }
                    await ReplyAsync("❌ Không tìm thấy bài hát!");
                    return;
                }

                var trackId = track.Uri?.ToString() ?? string.Empty;
                var trackName = string.IsNullOrWhiteSpace(track.Title) ? "Không rõ tiêu đề" : track.Title;
                await AddToQueueOrPlayNowAsync(trackId, trackName, player, playbackToken);
            }
            catch (Exception ex)
            {
                if (IsLoginRequiredPlaybackError(ex.ToString()))
                {
                    await ReplyAsync($"❌ Bài hát này bị YouTube yêu cầu đăng nhập/giới hạn tuổi nên không thể phát.\n{BuildYouTubeRestrictionHint()}");
                    return;
                }
                await ReplyAsync($"❌ Không thể phát: {ex.Message}");
            }
        }

        [Command("stopclara", RunMode = RunMode.Async)]
        [Alias("stop")]
        [Summary("Dừng phát, xóa phiên phát hiện tại và rời khỏi kênh voice.")]
        public async Task StopAsync()
        {
            var guildId = Context.Guild.Id;
            var stopLock = StopLocks.GetOrAdd(guildId, static _ => new SemaphoreSlim(1, 1));
            await stopLock.WaitAsync().ConfigureAwait(false);

            try
            {
                // Hủy monitor trước để nó không tự chuyển bài trong lúc lệnh stop đang chạy.
                CancelPlayback(guildId);
                _playbackRouter.Reset(guildId);

                var player = await _audioService.Players.GetPlayerAsync(guildId).ConfigureAwait(false);
                if (player is null)
                {
                    await ReplyAsync("⏹️ Đã dọn trạng thái phát; bot hiện không ở trong voice.");
                    return;
                }

                Exception? stopFailure = null;
                Exception? disconnectFailure = null;

                if (player.CurrentTrack is not null)
                {
                    try
                    {
                        await player.StopAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        stopFailure = ex;
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Không thể dừng player guild {guildId}: {ex.Message}");
                    }
                }

                // Disconnect vẫn phải được thử ngay cả khi Lavalink từ chối StopAsync.
                // Retry ngắn xử lý trường hợp player vừa đổi trạng thái khi monitor bị hủy.
                for (var attempt = 1; attempt <= 2; attempt++)
                {
                    try
                    {
                        await player.DisconnectAsync().ConfigureAwait(false);
                        disconnectFailure = null;
                        break;
                    }
                    catch (Exception ex)
                    {
                        disconnectFailure = ex;
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Không thể ngắt voice guild {guildId} (lần {attempt}/2): {ex.Message}");
                        if (attempt < 2)
                            await Task.Delay(250).ConfigureAwait(false);
                    }
                }

                if (disconnectFailure is not null)
                {
                    await ReplyAsync("⚠️ Đã xóa hàng chờ và dừng phiên phát, nhưng Lavalink chưa cho phép bot rời voice sau 2 lần thử. Hãy dùng `/stop` lại sau vài giây.");
                }
                else if (stopFailure is not null)
                {
                    await ReplyAsync("⏹️ Lavalink không xác nhận dừng track, nhưng bot đã xóa phiên phát và rời voice an toàn.");
                }
                else
                {
                    await ReplyAsync("⏹️ Đã dừng, xóa hàng chờ và rời voice.");
                }
            }
            finally
            {
                // Cleanup lặp lại có chủ đích để mọi đường return/exception đều về cùng trạng thái.
                CancelPlayback(guildId);
                _playbackRouter.Reset(guildId);
                stopLock.Release();
            }
        }

        [Command("pauseclara", RunMode = RunMode.Async)]
        [Alias("pause")]
        public async Task PauseAsync()
        {
            var player = await _audioService.Players.GetPlayerAsync(Context.Guild.Id);
            if (player == null)
            {
                await ReplyAsync("❌ Bot không đang ở voice.");
                return;
            }

            await player.PauseAsync();
            await ReplyAsync("⏸️ Đã tạm dừng.");
        }

        [Command("resumeclara", RunMode = RunMode.Async)]
        [Alias("resume")]
        public async Task ResumeAsync()
        {
            var player = await _audioService.Players.GetPlayerAsync(Context.Guild.Id);
            if (player == null)
            {
                await ReplyAsync("❌ Bot không đang ở voice.");
                return;
            }

            await player.ResumeAsync();
            await ReplyAsync("▶️ Tiếp tục phát.");
        }

        [Command("nextclara", RunMode = RunMode.Async)]
        [Alias("next")]
        public async Task NextAsync()
        {
            if (!PlaybackQueues.TryGetValue(Context.Guild.Id, out var queue))
            {
                await ReplyAsync("⚠️ Đang không phát nhạc trong playlist.");
                return;
            }

            var nextIndex = -1;
            bool isSingleTrack;
            lock (queue)
            {
                isSingleTrack = queue.Items.Count <= 1;
            }

            if (isSingleTrack)
            {
                await ReplyAsync("⚠️ Đang không phát nhạc trong playlist.");
                return;
            }

            lock (queue)
            {
                queue.LoopEnabled = false;
                var candidate = queue.RequestedIndex + 1;
                if (candidate < queue.Items.Count)
                {
                    queue.RequestedIndex = candidate;
                    nextIndex = candidate;
                }
            }

            if (nextIndex == -1)
            {
                await ReplyAsync("ℹ️ Đã ở bài cuối.");
                return;
            }

            var player = await _audioService.Players.GetPlayerAsync(Context.Guild.Id);
            if (player is not null)
            {
                await player.StopAsync();
            }

            await ReplyAsync("⏭️ Đã chuyển bài.");
        }

        [Command("jumpclara", RunMode = RunMode.Async)]
        [Alias("jump")]
        public async Task JumpAsync(int n)
        {
            if (n <= 0)
            {
                await ReplyAsync("❌ Số bài hát phải lớn hơn 0.");
                return;
            }

            var guildId = Context.Guild.Id;
            var player = await _audioService.Players.GetPlayerAsync(guildId);
            if (player is null || player.CurrentTrack is null)
            {
                await ReplyAsync("⚠️ Đang không phát nhạc trong playlist.");
                return;
            }

            if (!PlaybackQueues.TryGetValue(guildId, out var queue) || queue.Items.Count == 0)
            {
                await ReplyAsync("⚠️ Đang không phát nhạc trong playlist.");
                return;
            }

            // Queue mode OFF + 1 bài: không phải playlist
            if (!QueueModeEnabled.GetValueOrDefault(guildId, false) && queue.Items.Count <= 1)
            {
                await ReplyAsync("⚠️ Đang không phát nhạc trong playlist.");
                return;
            }

            var targetIndex = n - 1;
            var targetTitle = string.Empty;
            var canJump = false;

            lock (queue)
            {
                queue.LoopEnabled = false;
                if (targetIndex >= 0 && targetIndex < queue.Items.Count)
                {
                    queue.RequestedIndex = targetIndex;
                    targetTitle = queue.Items[targetIndex].Title;
                    canJump = true;
                }
            }

            if (!canJump)
            {
                await ReplyAsync($"❌ Playlist hiện tại chỉ có {queue.Items.Count} bài.");
                return;
            }

            await player.StopAsync();
            await ReplyAsync($"⏭️ Đã chuyển tới bài {n}: **{targetTitle}**");
        }

        [Command("removeclara", RunMode = RunMode.Async)]
        [Alias("qremove", "removequeue")]
        [Summary("Xóa một bài chưa phát khỏi hàng chờ theo số thứ tự.")]
        public async Task RemoveFromQueueAsync(int position)
        {
            if (!PlaybackQueues.TryGetValue(Context.Guild.Id, out var queue))
            {
                await ReplyAsync("❌ Không có hàng chờ đang hoạt động.");
                return;
            }

            string? removedTitle = null;
            string? error = null;
            lock (queue)
            {
                var targetIndex = position - 1;
                if (queue.RequestedIndex != queue.Index)
                    error = "⚠️ Bot đang chuyển bài, hãy thử lại sau vài giây.";
                else if (targetIndex <= queue.Index)
                    error = "❌ Chỉ có thể xóa các bài chưa phát sau bài hiện tại.";
                else if (targetIndex >= queue.Items.Count)
                    error = $"❌ Hàng chờ hiện chỉ có {queue.Items.Count} bài.";
                else
                {
                    removedTitle = queue.Items[targetIndex].Title;
                    queue.Items.RemoveAt(targetIndex);
                }
            }

            await ReplyAsync(error ?? $"🗑️ Đã xóa bài {position}: **{removedTitle}** khỏi hàng chờ.");
        }

        [Command("clearqueueclara", RunMode = RunMode.Async)]
        [Alias("qclear", "clearqueue")]
        [Summary("Xóa toàn bộ các bài chưa phát, giữ nguyên bài hiện tại.")]
        public async Task ClearQueueAsync()
        {
            if (!PlaybackQueues.TryGetValue(Context.Guild.Id, out var queue))
            {
                await ReplyAsync("❌ Không có hàng chờ đang hoạt động.");
                return;
            }

            var removedCount = 0;
            var isTransitioning = false;
            lock (queue)
            {
                isTransitioning = queue.RequestedIndex != queue.Index;
                if (!isTransitioning)
                {
                    removedCount = queue.Items.Count - queue.Index - 1;
                    if (removedCount > 0)
                        queue.Items.RemoveRange(queue.Index + 1, removedCount);
                }
            }

            if (isTransitioning)
                await ReplyAsync("⚠️ Bot đang chuyển bài, hãy thử lại sau vài giây.");
            else if (removedCount == 0)
                await ReplyAsync("ℹ️ Hàng chờ không có bài nào đang chờ.");
            else
                await ReplyAsync($"🧹 Đã xóa **{removedCount}** bài đang chờ; bài hiện tại vẫn tiếp tục phát.");
        }

        [Command("movequeueclara", RunMode = RunMode.Async)]
        [Alias("qmove", "movequeue")]
        [Summary("Di chuyển một bài chưa phát sang vị trí khác trong hàng chờ.")]
        public async Task MoveQueueItemAsync(int from, int to)
        {
            if (!PlaybackQueues.TryGetValue(Context.Guild.Id, out var queue))
            {
                await ReplyAsync("❌ Không có hàng chờ đang hoạt động.");
                return;
            }

            string? movedTitle = null;
            string? error = null;
            lock (queue)
            {
                var fromIndex = from - 1;
                var toIndex = to - 1;
                if (queue.RequestedIndex != queue.Index)
                    error = "⚠️ Bot đang chuyển bài, hãy thử lại sau vài giây.";
                else if (fromIndex <= queue.Index || toIndex <= queue.Index)
                    error = "❌ Chỉ có thể sắp xếp các bài chưa phát sau bài hiện tại.";
                else if (fromIndex >= queue.Items.Count || toIndex >= queue.Items.Count)
                    error = $"❌ Vị trí phải nằm trong khoảng {queue.Index + 2}–{queue.Items.Count}.";
                else
                {
                    var item = queue.Items[fromIndex];
                    queue.Items.RemoveAt(fromIndex);
                    queue.Items.Insert(toIndex, item);
                    movedTitle = item.Title;
                }
            }

            await ReplyAsync(error ?? $"↕️ Đã chuyển **{movedTitle}** từ vị trí {from} sang {to}.");
        }

        [Command("infoplayclara", RunMode = RunMode.Async)]
        [Alias("infoplay")]
        public async Task InfoPlayAsync()
        {
            var guildId = Context.Guild.Id;
            var player = await _audioService.Players.GetPlayerAsync(guildId);
            if (player is null || player.CurrentTrack is null)
            {
                await ReplyAsync("❌ Bot chưa phát nhạc nào.");
                return;
            }

            var track = player.CurrentTrack;
            var title = track.Title ?? "Không rõ tiêu đề";
            var author = track.Author ?? "Không rõ tác giả";
            var uri = track.Uri?.ToString() ?? track.Identifier;
            var duration = track.Duration;
            var position = player.Position?.Position ?? TimeSpan.Zero;
            var voiceChannel = _client.GetGuild(guildId)?.CurrentUser.VoiceChannel;
            var channelBitrateKbps = voiceChannel is null ? 0 : voiceChannel.Bitrate / 1000;
            var qualityLabel = GetDiscordAudioQualityLabel(channelBitrateKbps);

            var embed = new EmbedBuilder()
                .WithTitle("🎵 Thông tin bài hát đang phát")
                .AddField("Tiêu đề", title, false)
                .AddField("Tác giả", author, false)
                .AddField("Thời lượng", FormatDuration(duration), true)
                .AddField("Đã phát", FormatDuration(position), true)
                .AddField("Chất lượng Discord", qualityLabel, true)
                .AddField("Bitrate voice", channelBitrateKbps > 0 ? $"{channelBitrateKbps} kbps" : "Không xác định", true)
                .AddField("Server Boost", Context.Guild.PremiumTier.ToString(), true)
                .WithFooter("Lavalink: Opus quality 10/10 • Resampling HIGH • 48 kHz stereo")
                .WithUrl(uri)
                .WithColor(Color.Purple);

            await ReplyAsync(embed: embed.Build());
        }

        private static string GetDiscordAudioQualityLabel(int bitrateKbps)
        {
            return bitrateKbps switch
            {
                <= 0 => "Không xác định",
                <= 96 => "Tiêu chuẩn (tối đa 96 kbps)",
                <= 128 => "Boost Level 1 (tối đa 128 kbps)",
                <= 256 => "Boost Level 2 (tối đa 256 kbps)",
                _ => "Boost Level 3 (tối đa 384 kbps)",
            };
        }

        private static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalHours >= 1)
                return duration.ToString(@"h\:mm\:ss");
            return duration.ToString(@"m\:ss");
        }

        private static string SummarizePlaybackError(string? error)
        {
            if (string.IsNullOrWhiteSpace(error))
            {
                return "Không có chi tiết từ Lavalink.";
            }

            var firstLine = error.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim() ?? error.Trim();
            return firstLine.Length <= 240 ? firstLine : firstLine[..240] + "…";
        }

        private static bool IsTerminalPlaybackFailure(PlaybackEvent playbackEvent) =>
            playbackEvent.Kind is PlaybackEventKind.Exception or PlaybackEventKind.Stuck ||
            playbackEvent.Kind == PlaybackEventKind.Ended && playbackEvent.EndReason == TrackEndReason.LoadFailed;

        private static bool IsPlaybackEventForTrack(PlaybackEvent playbackEvent, string expectedIdentifier)
        {
            if (string.IsNullOrWhiteSpace(playbackEvent.TrackIdentifier))
            {
                return true;
            }

            var expectedVideoId = ExtractYouTubeVideoId(expectedIdentifier);
            return string.IsNullOrWhiteSpace(expectedVideoId) ||
                   string.Equals(expectedVideoId, playbackEvent.TrackIdentifier, StringComparison.OrdinalIgnoreCase);
        }

        private static string? ExtractYouTubeVideoId(string identifier)
        {
            if (!Uri.TryCreate(identifier, UriKind.Absolute, out var uri) || !IsYouTubeUrl(uri))
            {
                return null;
            }

            if (uri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
            {
                return uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            }

            foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=', 2);
                if (pair.Length == 2 && string.Equals(pair[0], "v", StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(pair[1]);
                }
            }

            return null;
        }

        [Command("showplaylistclara", RunMode = RunMode.Async)]
        [Alias("showplaylist", "playlist")]
        public async Task ShowPlaylistAsync()
        {
            var guildId = Context.Guild.Id;
            var player = await _audioService.Players.GetPlayerAsync(guildId);
            if (player is null || player.CurrentTrack is null)
            {
                await ReplyAsync("⚠️ Đang không phát nhạc trong playlist.");
                return;
            }

            if (!PlaybackQueues.TryGetValue(guildId, out var queue) || queue.Items.Count <= 1)
            {
                await ReplyAsync("⚠️ Đang không phát nhạc trong playlist.");
                return;
            }

            string content;
            MessageComponent components;
            lock (queue)
            {
                if (queue.Items.Count == 0)
                {
                    content = "⚠️ Đang không phát nhạc trong playlist.";
                    components = new ComponentBuilder().Build();
                }
                else
                {
                    content = BuildPlaylistPageContent(queue, 0);
                    components = BuildPlaylistPaginationComponent(guildId, Context.User.Id, 0, queue.Items.Count);
                }
            }

            await ReplyAsync(content, components: components);
        }

        [Command("prevclara", RunMode = RunMode.Async)]
        [Alias("prev", "previous")]
        public async Task PreviousAsync()
        {
            if (!PlaybackQueues.TryGetValue(Context.Guild.Id, out var queue))
            {
                await ReplyAsync("⚠️ Đang không phát nhạc trong playlist.");
                return;
            }

            var prevIndex = -1;
            bool isSingleTrack;
            lock (queue)
            {
                isSingleTrack = queue.Items.Count <= 1;
            }

            if (isSingleTrack)
            {
                await ReplyAsync("⚠️ Đang không phát nhạc trong playlist.");
                return;
            }

            lock (queue)
            {
                queue.LoopEnabled = false;
                var candidate = queue.RequestedIndex - 1;
                if (candidate >= 0)
                {
                    queue.RequestedIndex = candidate;
                    prevIndex = candidate;
                }
            }

            if (prevIndex == -1)
            {
                await ReplyAsync("ℹ️ Đã ở bài đầu.");
                return;
            }

            var player = await _audioService.Players.GetPlayerAsync(Context.Guild.Id);
            if (player is not null)
            {
                await player.StopAsync();
            }

            await ReplyAsync("⏮️ Đã lùi bài.");
        }

        [Command("loopclara", RunMode = RunMode.Async)]
        [Alias("loop")]
        public async Task LoopAsync()
        {
            if (!PlaybackQueues.TryGetValue(Context.Guild.Id, out var queue))
            {
                await ReplyAsync("❌ Không có playlist đang phát.");
                return;
            }

            lock (queue)
            {
                queue.LoopEnabled = !queue.LoopEnabled;
            }

            var state = queue.LoopEnabled ? "**bật** 🔁" : "**tắt** ⏹️";
            await ReplyAsync($"🔁 Loop đã {state}.");
        }

        [Command("shufclara", RunMode = RunMode.Async)]
        [Alias("shuffle", "shuf")]
        public async Task ShuffleAsync()
        {
            if (!PlaybackQueues.TryGetValue(Context.Guild.Id, out var queue))
            {
                await ReplyAsync("❌ Không có playlist đang phát.");
                return;
            }

            int currentIndex;
            List<QueueItem> allItems;
            lock (queue)
            {
                currentIndex = queue.Index;
                allItems = queue.Items;
            }

            if (allItems.Count <= 2)
            {
                await ReplyAsync("⚠️ Cần ít nhất 2 bài chưa phát để xáo trộn.");
                return;
            }

            var unplayedItems = new List<QueueItem>();
            for (var i = currentIndex + 1; i < allItems.Count; i++)
            {
                unplayedItems.Add(allItems[i]);
            }

            if (unplayedItems.Count <= 1)
            {
                await ReplyAsync("⚠️ Cần ít nhất 2 bài chưa phát để xáo trộn.");
                return;
            }

            var rng = new Random();
            for (var i = unplayedItems.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (unplayedItems[i], unplayedItems[j]) = (unplayedItems[j], unplayedItems[i]);
            }

            lock (queue)
            {
                var newItems = new List<QueueItem>(allItems.Count);
                for (var i = 0; i <= currentIndex; i++)
                {
                    newItems.Add(allItems[i]);
                }

                newItems.AddRange(unplayedItems);
                queue.Items.Clear();
                queue.Items.AddRange(newItems);
            }

            await ReplyAsync("🔀 Đã xáo trộn playlist (bài đang phát được giữ nguyên).");
        }

        // Dictionary lưu tốc độ hiện tại của mỗi guild (không áp dụng cho bài mới)
        private static readonly ConcurrentDictionary<ulong, float> CurrentPlaybackSpeeds = new();

        [Command("speedclara", RunMode = RunMode.Async)]
        [Alias("speed")]
        public async Task SpeedAsync()
        {
            var player = await _audioService.Players.GetPlayerAsync(Context.Guild.Id);
            if (player is null)
            {
                await ReplyAsync("❌ Bot không đang ở trong voice channel.");
                return;
            }

            // Chỉ hoạt động khi có nhạc đang phát và có playlist
            if (!PlaybackQueues.TryGetValue(Context.Guild.Id, out var queue) || queue.Items.Count == 0)
            {
                await ReplyAsync("❌ Không có playlist đang phát. Lệnh chỉ hoạt động với playlist.");
                return;
            }

            if (player.State != PlayerState.Playing && player.State != PlayerState.Paused)
            {
                await ReplyAsync("❌ Không có nhạc đang phát.");
                return;
            }

            // Lấy tốc độ hiện tại (mặc định 1.0)
            var currentSpeed = CurrentPlaybackSpeeds.GetValueOrDefault(Context.Guild.Id, 1.0f);

            var component = BuildSpeedComponent(Context.Guild.Id, Context.User.Id, currentSpeed, enabled: true);
            await ReplyAsync($"⚡ Chọn tốc độ phát nhạc (hiện tại: **{currentSpeed:F2}x**):", components: component);
        }

        private static MessageComponent BuildSpeedComponent(ulong guildId, ulong userId, float selectedSpeed, bool enabled, bool showCancel = true)
        {
            var speeds = new[] { 0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f };
            var builder = new ComponentBuilder();

            foreach (var speed in speeds)
            {
                var label = $"{speed:F2}x";
                var isSelected = Math.Abs(speed - selectedSpeed) < 0.01f;
                var style = isSelected ? ButtonStyle.Success : ButtonStyle.Secondary;
                
                builder.WithButton(
                    label, 
                    $"speedclara:{guildId}:{userId}:{speed}", 
                    style, 
                    disabled: !enabled || isSelected
                );
            }

            // Thêm nút Hủy
            if (showCancel && enabled)
            {
                builder.WithButton(
                    "❌ Hủy",
                    $"speedclara:{guildId}:{userId}:cancel",
                    ButtonStyle.Danger,
                    disabled: false
                );
            }

            return builder.Build();
        }

        public static async Task<bool> TryHandleSpeedComponentAsync(SocketMessageComponent component, IAudioService audioService)
        {
            var customId = component.Data.CustomId;
            if (string.IsNullOrWhiteSpace(customId) || !customId.StartsWith("speedclara:", StringComparison.Ordinal))
            {
                return false;
            }

            var parts = customId.Split(':');
            if (parts.Length != 4 ||
                !ulong.TryParse(parts[1], out var guildId) ||
                !ulong.TryParse(parts[2], out var requesterId))
            {
                await component.DeferAsync().ConfigureAwait(false);
                return true;
            }

            // Kiểm tra nếu là nút Hủy
            var isCancel = parts[3] == "cancel";
            if (!isCancel && !float.TryParse(parts[3], out _))
            {
                await component.DeferAsync().ConfigureAwait(false);
                return true;
            }

            if (component.User.Id != requesterId)
            {
                await component.RespondAsync("❌ Bạn không thể điều khiển tốc độ này.", ephemeral: true).ConfigureAwait(false);
                return true;
            }

            // Xử lý nút Hủy - bôi xám tất cả các nút, không thay đổi tốc độ
            if (isCancel)
            {
                var currentSpeed = CurrentPlaybackSpeeds.GetValueOrDefault(guildId, 1.0f);
                var disabledComponent = BuildSpeedComponent(guildId, requesterId, currentSpeed, enabled: false, showCancel: false);

                await component.UpdateAsync(msg =>
                {
                    msg.Content = $"⚡ Chọn tốc độ phát nhạc (hiện tại: **{currentSpeed:F2}x**) - Đã hủy thao tác.";
                    msg.Components = disabledComponent;
                }).ConfigureAwait(false);
                return true;
            }

            var speed = float.Parse(parts[3]);
            var player = await audioService.Players.GetPlayerAsync(guildId);
            if (player is null)
            {
                await component.UpdateAsync(msg =>
                {
                    msg.Content = "⚠️ Bot không còn ở trong voice channel.";
                    msg.Components = new ComponentBuilder().Build();
                }).ConfigureAwait(false);
                return true;
            }

            try
            {
                // Áp dụng Timescale filter để thay đổi tốc độ
                // speed = 1.0 là bình thường, < 1 chậm hơn, > 1 nhanh hơn
                player.Filters.Timescale = new Lavalink4NET.Filters.TimescaleFilterOptions
                {
                    Speed = speed,
                    Pitch = 1.0f,
                    Rate = 1.0f
                };
                await player.Filters.CommitAsync().ConfigureAwait(false);
                
                // Lưu tốc độ hiện tại
                CurrentPlaybackSpeeds[guildId] = speed;

                // Vô hiệu hóa tất cả các nút sau khi chọn
                var disabledComponent = BuildSpeedComponent(guildId, requesterId, speed, enabled: false, showCancel: false);

                await component.UpdateAsync(msg =>
                {
                    msg.Content = $"✅ Tốc độ phát nhạc đã được đặt: **{speed:F2}x**";
                    msg.Components = disabledComponent;
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Lỗi khi đổi tốc độ: {ex}");
                await component.UpdateAsync(msg =>
                {
                    msg.Content = "❌ Không thể thay đổi tốc độ phát nhạc.";
                    msg.Components = new ComponentBuilder().Build();
                }).ConfigureAwait(false);
            }

            return true;
        }

        // Reset tốc độ khi kết thúc phát nhạc hoặc play bài mới
        public static void ResetPlaybackSpeed(ulong guildId)
        {
            CurrentPlaybackSpeeds.TryRemove(guildId, out _);
        }
    }
}
