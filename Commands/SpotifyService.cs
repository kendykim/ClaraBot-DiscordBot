using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace Clara_bot.Commands
{
    /// <summary>
    /// Reads public Spotify link metadata only. Spotify is never used as an
    /// audio source; callers use the returned title and artist to search YouTube.
    /// </summary>
    public static class SpotifyService
    {
        private const int MaxRequestAttempts = 3;
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(25);
        private static readonly HttpClient Http = new(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        private static readonly Regex SpotifyUrlRegex = new(
            @"^https?://open\.spotify\.com/(?:intl-[a-z]{2}/)?(?<type>track|playlist|album)/(?<id>[a-zA-Z0-9]+)(?:[/?#].*)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex TrackRowRegex = new(
            @"<li\b[^>]*data-testid=[""']tracklist-row-[^""']+[""'][^>]*>(?<row>.*?)</li>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static readonly Regex TitleRegex = new(
            @"<h3\b[^>]*>(?<value>.*?)</h3>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static readonly Regex ArtistRegex = new(
            @"<h4\b[^>]*>(?<value>.*?)</h4>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static readonly Regex TrackTitleRegex = new(
            @"<h1\b[^>]*data-testid=[""']entity-title[""'][^>]*>(?<value>.*?)</h1>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static readonly Regex TrackArtistRegex = new(
            @"<h2\b[^>]*data-testid=[""']subtitle[""'][^>]*>(?<value>.*?)</h2>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static readonly Regex HtmlTagRegex = new(
            @"<[^>]+>",
            RegexOptions.Compiled | RegexOptions.Singleline);

        public static bool IsSpotifyUrl(string url) => TryParseSpotifyUrl(url, out _, out _);

        public static bool IsSpotifyTrack(string url) =>
            TryParseSpotifyUrl(url, out var type, out _) && type == "track";

        public static bool IsSpotifyPlaylist(string url) =>
            TryParseSpotifyUrl(url, out var type, out _) && type is "playlist" or "album";

        public static async Task<SpotifyTrackInfo?> GetTrackInfoAsync(string spotifyUrl)
        {
            if (!TryParseSpotifyUrl(spotifyUrl, out var type, out var id) || type != "track")
            {
                return null;
            }

            try
            {
                var embedUrl = $"https://open.spotify.com/embed/track/{id}";
                var html = await DownloadEmbedHtmlAsync(embedUrl).ConfigureAwait(false);
                if (html is null)
                {
                    return null;
                }

                var titleMatch = TrackTitleRegex.Match(html);
                var artistMatch = TrackArtistRegex.Match(html);
                var title = titleMatch.Success
                    ? DecodeText(titleMatch.Groups["value"].Value)
                    : string.Empty;
                var artist = artistMatch.Success
                    ? DecodeText(artistMatch.Groups["value"].Value)
                    : string.Empty;

                return string.IsNullOrWhiteSpace(title)
                    ? null
                    : MakeTrack(title, artist);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[Spotify link] Không đọc được metadata track: {exception.Message}");
                return null;
            }
        }

        public static async Task<List<SpotifyTrackInfo>> GetPlaylistTracksAsync(string playlistUrl)
        {
            if (!TryParseSpotifyUrl(playlistUrl, out var type, out var id) || type is not ("playlist" or "album"))
            {
                return new List<SpotifyTrackInfo>();
            }

            try
            {
                var embedUrl = $"https://open.spotify.com/embed/{type}/{id}";
                var html = await DownloadEmbedHtmlAsync(embedUrl).ConfigureAwait(false);
                if (html is null)
                {
                    return new List<SpotifyTrackInfo>();
                }

                var tracks = ParseEmbedTrackList(html);
                Console.WriteLine($"[Spotify link] Đọc được {tracks.Count} track từ {type} Embed công khai.");
                return tracks;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[Spotify link] Không đọc được danh sách track: {exception.Message}");
                return new List<SpotifyTrackInfo>();
            }
        }

        internal static List<SpotifyTrackInfo> ParseEmbedTrackList(string html)
        {
            var tracks = new List<SpotifyTrackInfo>();
            foreach (Match rowMatch in TrackRowRegex.Matches(html))
            {
                var row = rowMatch.Groups["row"].Value;
                var titleMatch = TitleRegex.Match(row);
                var artistMatch = ArtistRegex.Match(row);
                if (!titleMatch.Success)
                {
                    continue;
                }

                var title = DecodeText(titleMatch.Groups["value"].Value);
                var artist = artistMatch.Success
                    ? DecodeText(artistMatch.Groups["value"].Value)
                    : string.Empty;

                if (!string.IsNullOrWhiteSpace(title))
                {
                    tracks.Add(MakeTrack(title, artist));
                }
            }

            return tracks;
        }

        private static bool TryParseSpotifyUrl(string url, out string type, out string id)
        {
            var match = SpotifyUrlRegex.Match(url.Trim());
            type = match.Success ? match.Groups["type"].Value.ToLowerInvariant() : string.Empty;
            id = match.Success ? match.Groups["id"].Value : string.Empty;
            return match.Success;
        }

        private static HttpRequestMessage CreatePublicRequest(string url, string accept)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 ClaraBot/1.2");
            request.Headers.TryAddWithoutValidation("Accept", accept);
            request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US"));
            request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en", 0.9));
            return request;
        }

        private static async Task<string?> DownloadEmbedHtmlAsync(string embedUrl)
        {
            for (var attempt = 1; attempt <= MaxRequestAttempts; attempt++)
            {
                try
                {
                    using var request = CreatePublicRequest(embedUrl, "text/html,application/xhtml+xml");
                    using var timeoutSource = new CancellationTokenSource(RequestTimeout);
                    using var response = await Http.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeoutSource.Token).ConfigureAwait(false);

                    if (response.IsSuccessStatusCode)
                    {
                        return await response.Content
                            .ReadAsStringAsync(timeoutSource.Token)
                            .ConfigureAwait(false);
                    }

                    var retryable = response.StatusCode == HttpStatusCode.RequestTimeout ||
                                    (int)response.StatusCode == 429 ||
                                    (int)response.StatusCode >= 500;
                    Console.WriteLine($"[Spotify link] Embed HTTP {(int)response.StatusCode}, lần {attempt}/{MaxRequestAttempts}.");
                    if (!retryable)
                    {
                        return null;
                    }
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine($"[Spotify link] Embed timeout sau {RequestTimeout.TotalSeconds:0} giây, lần {attempt}/{MaxRequestAttempts}.");
                }
                catch (HttpRequestException exception)
                {
                    Console.WriteLine($"[Spotify link] Lỗi mạng lần {attempt}/{MaxRequestAttempts}: {exception.Message}");
                }

                if (attempt < MaxRequestAttempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt)).ConfigureAwait(false);
                }
            }

            return null;
        }

        private static string DecodeText(string html) =>
            WebUtility.HtmlDecode(HtmlTagRegex.Replace(html, string.Empty)).Trim();

        private static SpotifyTrackInfo MakeTrack(string title, string artist)
        {
            return new SpotifyTrackInfo
            {
                Title = title,
                Artist = artist,
                SearchQuery = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}",
            };
        }
    }

    public sealed class SpotifyTrackInfo
    {
        public string Title { get; init; } = string.Empty;
        public string Artist { get; init; } = string.Empty;
        public string SearchQuery { get; init; } = string.Empty;
    }
}
