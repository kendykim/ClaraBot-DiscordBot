using System.Collections.Concurrent;
using Lavalink4NET;
using Lavalink4NET.Players;
using Lavalink4NET.Rest.Entities.Tracks;

namespace Clara_bot.Commands;

/// <summary>
/// Plays the exact URL selected by the command with bounded retries and
/// isolated per-guild state. It never changes providers implicitly: YouTube
/// stays YouTube, and SoundCloud is used only for an explicit SoundCloud URL.
/// </summary>
public sealed class ResilientPlaybackRouter
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    private readonly IAudioService _audioService;
    private readonly ConcurrentDictionary<ulong, PlaybackRouteState> _states = new();

    public ResilientPlaybackRouter(IAudioService audioService)
    {
        _audioService = audioService;
    }

    public Task<PlaybackRouteResult> PlayPrimaryAsync(
        ulong guildId,
        string playbackKey,
        string title,
        ILavalinkPlayer player,
        CancellationToken cancellationToken)
    {
        Reset(guildId);
        return TryPlayNextAsync(guildId, playbackKey, title, player, cancellationToken);
    }

    public async Task<PlaybackRouteResult> TryPlayNextAsync(
        ulong guildId,
        string playbackKey,
        string title,
        ILavalinkPlayer player,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(playbackKey);
        ArgumentNullException.ThrowIfNull(player);

        var state = _states.GetOrAdd(guildId, static _ => new PlaybackRouteState());
        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!string.Equals(state.PlaybackKey, playbackKey, StringComparison.Ordinal))
            {
                state.PlaybackKey = playbackKey;
                state.Title = title;
                state.NextAttempt = 0;
            }

            while (state.NextAttempt < MaxAttempts)
            {
                var attempt = state.NextAttempt++;

                try
                {
                    var track = await _audioService.Tracks
                        .LoadTrackAsync(playbackKey, TrackSearchMode.None)
                        .ConfigureAwait(false);

                    if (track is null)
                    {
                        await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    await player.PlayAsync(track).ConfigureAwait(false);
                    return new PlaybackRouteResult(
                        true,
                        attempt + 1,
                        playbackKey,
                        track.Uri?.ToString());
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[{DateTime.Now:HH:mm:ss}] Resilient playback attempt {attempt + 1} " +
                        $"failed for '{title}': {ex.Message}");
                    await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }

            return new PlaybackRouteResult(false, state.NextAttempt, null, null);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public void Reset(ulong guildId)
    {
        _states.TryRemove(guildId, out _);
    }

    private sealed class PlaybackRouteState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public string? PlaybackKey { get; set; }
        public string? Title { get; set; }
        public int NextAttempt { get; set; }
    }
}

public readonly record struct PlaybackRouteResult(
    bool Started,
    int Attempt,
    string? Query,
    string? TrackUri);
