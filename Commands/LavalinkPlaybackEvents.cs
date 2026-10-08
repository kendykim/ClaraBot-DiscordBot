using System.Collections.Concurrent;
using System.Threading.Channels;
using Lavalink4NET;
using Lavalink4NET.Events.Players;
using Lavalink4NET.Protocol.Payloads.Events;

namespace Clara_bot.Commands;

/// <summary>
/// Converts Lavalink's player callbacks into a per-guild event stream.  Music
/// playback can therefore react to the node itself instead of polling
/// ILavalinkPlayer.CurrentTrack on a timer.
/// </summary>
public sealed class LavalinkPlaybackEvents : IDisposable
{
    private readonly IAudioService _audioService;
    private readonly ConcurrentDictionary<ulong, Channel<PlaybackEvent>> _guildEvents = new();
    private int _disposed;

    public LavalinkPlaybackEvents(IAudioService audioService)
    {
        _audioService = audioService;
        _audioService.TrackStarted += OnTrackStartedAsync;
        _audioService.TrackEnded += OnTrackEndedAsync;
        _audioService.TrackException += OnTrackExceptionAsync;
        _audioService.TrackStuck += OnTrackStuckAsync;
    }

    public async ValueTask<PlaybackEvent> WaitAsync(ulong guildId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var channel = GetChannel(guildId);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            return await channel.Reader.ReadAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PlaybackEvent(PlaybackEventKind.Timeout, null, null, null);
        }
    }

    public void Reset(ulong guildId)
    {
        if (!_guildEvents.TryGetValue(guildId, out var channel))
        {
            return;
        }

        while (channel.Reader.TryRead(out _))
        {
        }
    }

    private Channel<PlaybackEvent> GetChannel(ulong guildId) =>
        _guildEvents.GetOrAdd(guildId, static _ => Channel.CreateUnbounded<PlaybackEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false }));

    private Task OnTrackStartedAsync(object sender, TrackStartedEventArgs eventArgs)
    {
        Publish(eventArgs.Player.GuildId, new PlaybackEvent(PlaybackEventKind.Started, eventArgs.Track.Identifier, null, null));
        return Task.CompletedTask;
    }

    private Task OnTrackEndedAsync(object sender, TrackEndedEventArgs eventArgs)
    {
        Publish(eventArgs.Player.GuildId, new PlaybackEvent(PlaybackEventKind.Ended, eventArgs.Track.Identifier, eventArgs.Reason, null));
        return Task.CompletedTask;
    }

    private Task OnTrackExceptionAsync(object sender, TrackExceptionEventArgs eventArgs)
    {
        Publish(eventArgs.Player.GuildId, new PlaybackEvent(PlaybackEventKind.Exception, eventArgs.Track.Identifier, null, eventArgs.Exception.Message));
        return Task.CompletedTask;
    }

    private Task OnTrackStuckAsync(object sender, TrackStuckEventArgs eventArgs)
    {
        Publish(eventArgs.Player.GuildId, new PlaybackEvent(PlaybackEventKind.Stuck, eventArgs.Track.Identifier, null, $"Track bị kẹt quá {eventArgs.Threshold.TotalSeconds:0.#} giây."));
        return Task.CompletedTask;
    }

    private void Publish(ulong guildId, PlaybackEvent playbackEvent)
    {
        GetChannel(guildId).Writer.TryWrite(playbackEvent);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _audioService.TrackStarted -= OnTrackStartedAsync;
        _audioService.TrackEnded -= OnTrackEndedAsync;
        _audioService.TrackException -= OnTrackExceptionAsync;
        _audioService.TrackStuck -= OnTrackStuckAsync;
        foreach (var channel in _guildEvents.Values)
        {
            channel.Writer.TryComplete();
        }
    }
}

public enum PlaybackEventKind
{
    Started,
    Ended,
    Exception,
    Stuck,
    Timeout,
}

public sealed record PlaybackEvent(
    PlaybackEventKind Kind,
    string? TrackIdentifier,
    TrackEndReason? EndReason,
    string? Error);
