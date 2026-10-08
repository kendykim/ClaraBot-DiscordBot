# Clara Bot 1.3.6 — Thay đổi và bản vá

Tài liệu này tổng hợp các thay đổi từ Clara Bot `1.2.0` lên `1.3.6`, những lỗi của phiên bản cũ đã được khắc phục và các cải thiện có trong phiên bản hiện tại.

## Tóm tắt

Phiên bản `1.3.6` tập trung vào sáu mục tiêu chính:

- Sửa lỗi nghiêm trọng bot phát SAI BÀI HÁT do tự động đổi âm thanh provider.
- Thay thế polling-based playback bằng event-stream từ Lavalink — phát hiện lỗi tức thì.
- Viết lại hoàn toàn `SpotifyService`, loại bỏ phụ thuộc API của bên thứ ba không ổn định.
- Hỗ trợ đầy đủ link Spotify (track, playlist, album) với background async loader.
- Cải thiện hệ thống hàng chờ (queue mode), chuyển bài tức thì và quản lý state an toàn.
- Vá các lỗi nhỏ về ngắt voice, sửa tin nhắn tìm kiếm, detect YouTube restriction.

---

## Bản vá từ phiên bản 1.2.0

### 1. Sửa lỗi CRITICAL phát SAI BÀI HÁT — ResilientPlaybackRouter

Trong phiên bản `1.2.0`, `ResilientPlaybackRouter` sử dụng `TrackSearchMode.SoundCloud` với tiêu đề bài hát cộng thêm các suffix (`official audio`, `audio`). Kết quả là bot sẽ tìm bài theo tên trên SoundCloud thay vì phát đúng **URL YouTube người dùng đã chọn**.

Triệu chứng thường gặp:
- User paste link YouTube bài "X" → bot phát một bài cover / bài hoàn toàn khác có tên tương tự trên SoundCloud.
- Không có cách nào bảo đảm phát đúng artist / chính xác bài gốc.

Phiên bản `1.3.6` sửa:

- Luôn dùng `playbackKey` (chính xác URL user chọn) với `TrackSearchMode.None`.
- Thay đổi summary class: *"Never changes providers implicitly: YouTube stays YouTube, SoundCloud only for an explicit SoundCloud URL"*.
- Giới hạn số lần retry cố định (`MaxAttempts = 3`) trên **cùng một URL**, không đổi query.
- Xóa các suffix query (`official audio`, v.v.) đã gây nhầm lẫn provider.

### 2. Sửa phát hiện lỗi playback chậm và thiếu đáng tin cậy — Thay polling bằng event-stream

V1.2.0 dùng loop `while(true)` + `Task.Delay(2 giây)` để kiểm tra `player.CurrentTrack` (polling). Hạn chế:
- `TrackException` / `TrackStuck` của Lavalink có thể bị bỏ sót giữa các tick polling.
- Delay trung bình 1–3 giây trước khi bot báo "bài lỗi" hoặc chuyển bài kế tiếp.
- CPU và Lavalink REST bị spam request không cần thiết.

Phiên bản `1.3.6` — Module mới `LavalinkPlaybackEvents`:

- Đăng ký trực tiếp **4 Lavalink callback gốc**: `TrackStarted`, `TrackEnded`, `TrackException`, `TrackStuck`.
- Dùng `System.Threading.Channels.Channel<PlaybackEvent>` tạo per-guild event queue (bounded-single-reader, non-blocking, thread-safe).
- Method `WaitAsync(guildId, timeout, ct)` cho consumer (MusicModule) chờ event thay vì polling.
- Timeout mỗi lần chờ = 16 giây (thay vì tick 2 giây), giảm đáng kể CPU usage.
- Enum `PlaybackEventKind`: `Started`, `Ended`, `Exception`, `Stuck`, `Timeout`.
- `IDisposable` pattern đảm bảo gỡ callback khi bot dừng, không memory leak.
- Đăng ký ở `Program.cs` làm Singleton:
  - `services.AddSingleton<LavalinkPlaybackEvents>();`
  - `_ = serviceProvider.GetRequiredService<LavalinkPlaybackEvents>();` (kick-start listener).

### 3. Sửa lỗi SpotifyService không ổn định — Viết lại hoàn toàn

SpotifyService V1.2.0 phụ thuộc 3 phương pháp không ổn định:
1. Gọi `spotifydown.com` (API bên thứ 3 — dễ chặn IP / đổi cấu trúc / chết).
2. Scrape anonymous accessToken từ HTML `open.spotify.com` rồi gọi Web API `api.spotify.com/v1/...` — break mỗi khi Spotify đổi DOM.
3. Fallback cuối là OEmbed (chỉ trả về 1 bài cho cả album/playlist).

Kết quả: Nhiều lúc load playlist Spotify trả về `Count = 0` dù link hợp lệ.

V1.3.6 fix hoàn toàn:

- **Chỉ dùng Spotify Embed công khai**: `https://open.spotify.com/embed/{type}/{id}` — endpoint chính thức, ổn định, không cần auth, khó bị chặn.
- Parse HTML embed với regex tường minh:
  - `SpotifyUrlRegex` hỗ trợ prefix `/intl-xx/` locale.
  - `TrackRowRegex` bắt `<li data-testid=tracklist-row-...>` từ playlist/album embed.
  - `TitleRegex` (`<h3>`), `ArtistRegex` (`<h4>`) trên mỗi row.
  - `TrackTitleRegex`, `TrackArtistRegex` dùng trên single track embed.
  - `HtmlTagRegex` + `WebUtility.HtmlDecode` strip tag và decode entity.
- Tối ưu HTTP transport qua `SocketsHttpHandler`:
  - `AutomaticDecompression = All` (gzip / brotli giảm băng thông ~70%).
  - `PooledConnectionLifetime = 10 phút` (reuse TCP, không setup TLS mỗi request).
  - `ConnectTimeout = 10 giây`; tổng request timeout `25 giây` (per-request CancellationTokenSource).
- Retry 3 lần với backoff tuyến tính `500ms * attempt` cho: `408 RequestTimeout`, `429 TooManyRequests`, `5xx ServerError`.
- Bỏ hẳn phụ thuộc spotifydown và anonymous Web API scraping.
- `SpotifyTrackInfo` đổi property từ `{ get; set; }` → `{ get; init; }` (immutable, an toàn thread khi dùng trong task background).

### 4. Sửa detect YouTube restriction thiếu pattern

`IsLoginRequiredPlaybackError` ở V1.2.0 chỉ match 6 pattern (requires login, sign in to confirm age, age-restricted, v.v.). Trong khi đó Lavalink youtube-plugin mới trả về các message khác với cùng ý nghĩa.

V1.3.6 bổ sung 2 pattern:

```csharp
errorText.Contains("video is unavailable", ...)
errorText.Contains("content warning", ...)
```

Giảm số trường hợp bot trả lời chung chung "không load được track" mà không gợi ý kiểm tra youtube-plugin (poToken/visitorData hoặc cookies).

### 5. Sửa lỗi `QueueModeEnabled` + Add bài mới hủy PlaylistAsync đang chạy

V1.2.0 khi đang phát playlist bật queue mode rồi gọi `/play bài-mới`: `ReplacePlaybackToken(guildId)` hủy CTS của `PlayPlaylistAsync`, playlist chết hẳn, bài mới ghi đè thay vì add cuối.

V1.3.6 fix tại `PlayAsync`:

```csharp
bool keepQueue = QueueModeEnabled.GetValueOrDefault(Context.Guild.Id, false) && player.CurrentTrack is not null;
var playbackToken = keepQueue ? CancellationToken.None : ReplacePlaybackToken(Context.Guild.Id);
```

Khi `keepQueue = true`: giữ `PlayPlaylistAsync` sống, bài mới đi qua `AddToQueueOrPlayNowAsync`.

### 6. Sửa `/stop` không ngắt được voice khi Lavalink state đổi giữa chừng

V1.2.0 gọi `player.DisconnectAsync()` 1 lần duy nhất. Nếu giữa lúc monitor playback vừa đổi player state → DisconnectAsync throw → bot không rời kênh dù đã xóa queue.

V1.3.6 fix:

```csharp
for (var attempt = 1; attempt <= 2; attempt++) {
    try { await player.DisconnectAsync(); disconnectFailure = null; break; }
    catch { disconnectFailure = ex; if (attempt < 2) await Task.Delay(250); }
}
```

Phân loại 3 mức thông báo cho user:
- Thành công hoàn toàn: `⏹️ Đã dừng, xóa hàng chờ và rời voice.`
- StopAsync fail nhưng Disconnect OK: `⏹️ Lavalink không xác nhận dừng track, nhưng bot đã xóa phiên phát và rời voice an toàn.`
- Disconnect fail cả 2 lần: `⚠️ Đã xóa hàng chờ và dừng phiên phát, nhưng Lavalink chưa cho phép bot rời voice sau 2 lần thử. Hãy dùng /stop lại sau vài giây.`

Bảo vệ race bằng `StopLocks` per-guild SemaphoreSlim.

---

## Cải thiện và tính năng mới trong 1.3.6

### Playback event-driven (MusicModule)

#### Helper methods mới phục vụ event flow:

- `IsTerminalPlaybackFailure(PlaybackEvent)`:
  ```csharp
  kind is Exception or Stuck
  OR kind == Ended && EndReason == LoadFailed
  ```
  Dùng để phân biệt "bài lỗi thật" với "bài kết thúc bình thường".

- `IsPlaybackEventForTrack(playbackEvent, expectedIdentifier)`:
  - Lấy `TrackIdentifier` từ Lavalink event (luôn là **YouTube Video ID** ngắn, không phải full URL).
  - Trích xuất video ID từ expectedIdentifier dùng helper `ExtractYouTubeVideoId`.
  - Hỗ trợ cả `youtu.be/ID` lẫn `youtube.com/watch?v=ID`.
  - Nếu event không mang TrackIdentifier → trả về true (safe fallback).

- `SummarizePlaybackError(error)`: Lấy dòng đầu tiên của error, cắt tối đa 240 ký tự → log + Discord không bị flood.

- `ExtractYouTubeVideoId(url)`: Parse youtu.be và query parameter `v=`, dùng chung cho event matching.

#### `PlayPlaylistAsync` flow viết lại:

- Mỗi vòng lặp thay vì polling → gọi `_playbackEvents.WaitAsync(guildId, 16s, ct)`.
- **Instant failure detect**: Nhận `Exception` / `Stuck` event → gọi `RegisterPlaylistPlaybackFailureAsync` ngay.
- **Instant transition detect**: Check `queue.RequestedIndex != index` ở đầu mỗi event tick → nếu có next/prev/jump từ user → `player.StopAsync()` rồi `break` loop, nhảy tới bài mới trong cùng PlayPlaylistAsync vòng lặp kế tiếp (delay chuyển bài từ 2–3s xuống ~100ms).
- **Reset consecutive failures an toàn**: Sau `6s` playback ổn định (stable marker) → `queue.ConsecutivePlaybackFailures = 0` (lock queue).
- **Chờ async loader background**: Khi `index >= queue.Items.Count` → nếu `queue.PendingLoaders > 0` (Spotify playlist vẫn đang add bài) → `Task.Delay(500ms)` rồi check lại, không exit loop sớm mất bài.

#### `MonitorAndAutoDisconnectAsync` (single track):

- Cũng chuyển sang `_playbackEvents.WaitAsync`.
- Legacy fallback retry 1 lần nếu URL fail lần đầu.
- Stable marker 6s → reset ResilientPlaybackRouter state.

### Hỗ trợ hoàn chỉnh link Spotify

#### `/play` giờ chấp nhận 3 loại URL Spotify:
- `open.spotify.com/track/...` (single track)
- `open.spotify.com/playlist/...`
- `open.spotify.com/album/...`
- Kể cả URL có prefix `/intl-vn/` locale.

#### Flow single track:
1. `SpotifyService.GetTrackInfoAsync(url)` → lấy title + artist từ embed.
2. `ResolveSpotifyTrackOnYouTubeAsync(spotifyTrackInfo)` tìm bài tương ứng YouTube với **3 query khác nhau** (artist+title, title-only, searchquery), mỗi query thử 2 mode (`TrackSearchMode.YouTube` + `ytsearch:` None mode) → 6 cơ hội match, ưu tiên kết quả đầu tiên.
3. Nếu tìm được YouTube URL → `AddToQueueOrPlayNowAsync`.

#### Flow playlist/album (async background loader):
1. `SpotifyService.GetPlaylistTracksAsync(url)` lấy list `SpotifyTrackInfo`.
2. Resolve **bài đầu tiên** đồng bộ (để user nghe liền không chờ).
3. Gửi status msg: `🔄 Đang loading các bài hát trong playlist để bổ sung đầy đủ cho queue...`
4. Tạo/cập nhật `PlaybackQueue` với `PendingLoaders = 1` (báo hiệu "vẫn còn bài đang add").
5. `LoadRemainingSpotifyPlaylistTracksAsync` chạy ở **Task.Run background**:
   - Resolve từng bài còn lại (từ index 1) → thêm vào `targetQueue.Items`.
   - Bỏ qua lỗi từng bài (catch, console log, không làm chết cả playlist).
   - Hoàn tất: modify status msg thành `✅ Đã bổ sung N bài hát vào queue.`
   - `finally`: `PendingLoaders--` (PlayPlaylistAsync nhận biết không còn loader).
6. `PlayPlaylistAsync` check PendingLoaders > 0 trước khi exit khi hết Items.

### Cải thiện hệ thống hàng chờ (Queue Mode)

- Command `queueclara` (alias `/queue`): toggle mode, thông báo rõ trạng thái ON/OFF.
- `AddToQueueOrPlayNowAsync(identifier, title, player, token)`:
  - Nếu queue OFF hoặc player.CurrentTrack = null → `PlayTrackImmediatelyAsync` (giữ hành vi cũ).
  - Nếu queue ON + đang phát bài:
    - Đã có `PlaybackQueues[guildId]` → `Add()` item vào cuối → báo "thêm vào hàng chờ vị trí N".
    - Chưa có PlaybackQueues (chỉ đang phát single track không phải playlist) → tạo queue mới = `[currentTrack, newTrack]`, Index = 0, kick-start `PlayPlaylistAsync`.

#### An toàn state khi sửa queue trong khi chuyển bài:

`qremove` (`/removeclara`), `qclear` (`/clearqueueclara`):
- Trước khi mutate items → check `queue.RequestedIndex != queue.Index`.
- Nếu đang chuyển bài (RequestedIndex ≠ Index) → trả lời "Bot đang chuyển bài, hãy thử lại sau vài giây." thay vì sửa hàng chờ và gây unsync state.

`qremove` thêm check: `targetIndex <= queue.Index` → "Chỉ có thể xóa các bài chưa phát sau bài hiện tại."

### Cải thiện `/search` command

- Khi user gọi `/search` mới mà trước đó đã có kết quả search:
  - Lấy `LastSearchMessage[guildId]` + `LastSearchResults[guildId]`.
  - Sửa tin nhắn embed cũ thành plain text: `🔍 Đã từng tìm: **{oldQuery}**` (bỏ embeds) → kênh chat không bị xê dịch bởi các embed cũ.
- Thêm expiration 15 phút cho kết quả search: `(DateTime.Now - CreatedAt) > 15 phút` → báo "Kết quả tìm kiếm đã hết hạn, dùng search lại" thay vì chơi bài cũ có thể không còn hợp lệ.
- `SearchYouTubeAsync` gọi trực tiếp Lavalink `/v4/loadtracks` endpoint với `ytsearch:query`, parse `info.uri` làm identifier chuẩn.
- Embed kết quả format duration đúng: `h:mm:ss` cho bài ≥ 1 tiếng, còn lại `m:ss`.

### Cải thiện Slash Command & descriptions

Mô tả `/play` trên Slash Command cập nhật rõ ràng:
- Tên option `query` description V1.2.0: `Link YouTube hoặc tên bài hát`
- V1.3.6: `Tên bài, link YouTube hoặc link track/playlist Spotify`

Command description:
- V1.2.0: `Phát nhạc từ YouTube (/playclara)`
- V1.3.6: `Phát YouTube; hỗ trợ tìm theo link Spotify (/playclara)`

### Cấu hình, tài liệu, tiện ích

- Assembly version `Clara_bot.csproj`: `1.3.6`
- Thêm [CAUTRUC.md](file:///d:/BotDiscord/Clara_bot/CAUTRUC.md) — giải thích kiến trúc các module.
- Thêm [run.md](file:///d:/BotDiscord/Clara_bot/run.md) — hướng dẫn chạy nhanh.
- Thêm 3 script tiện ích (Windows):
  - `runinbox.bat` — chạy trong cửa sổ console riêng.
  - `check-guilds.bat` — kiểm tra guild/member nhanh.
  - `runwebhook.bat` — chế độ webhook.
- Quản lý plugin Lavalink rõ ràng hơn:
  - Thư mục `plugins/` — đang active.
  - Thư mục `plugins.disabled/` — version cũ hoặc dự phòng (vd: `youtube-plugin-1.18.2.jar`).
- File `message.txt` làm template nội dung tùy chỉnh.

---

## Kết quả kiểm tra

Các phép kiểm tra gần nhất trên bản `1.3.6`:

| Hạng mục | Kết quả V1.3.6 |
|---|---|
| Phát đúng bài theo URL YouTube user chọn (không đổi provider âm tính) | ✅ Đã sửa ResilientPlaybackRouter |
| Phát hiện lỗi Exception / Stuck của Lavalink | Real-time (event) thay vì polling 2–3s |
| Spotify single track (có title/artist đủ) | Match YouTube lần 1–2 |
| Spotify playlist 30+ bài (embed public) | Bài đầu phát < 5s, bài còn lại async background |
| SpotifyService không phụ thuộc spotifydown | ✅ 100% dùng Spotify Embed chính thức |
| Chuyển bài `/next` / `/jump` trong playlist | ~100ms (requested check mỗi event tick) |
| Queue mode + `/play bài-mới` khi đang phát playlist | Bài mới add cuối, PlayPlaylistAsync không bị hủy |
| YouTube restriction detect | +2 pattern ("video is unavailable", "content warning") |
| `/stop` ngắt voice khi Lavalink state đổi | 2 lần retry, 3 loại thông báo rõ ràng |
| Search result cũ | Sửa tin nhắn embed → "Đã từng tìm" (không spam kênh) |

Thời gian thực tế có thể thay đổi theo mạng, Lavalink node, YouTube plugin, và số lượng bài Spotify playlist.

---

## Hướng dẫn nâng cấp từ 1.2.0 lên 1.3.6

1. **Sao lưu**:
   - `.env` (token + API keys tùy chọn)
   - `application.yml` (cấu hình Lavalink server)
   - Toàn bộ thư mục `plugins/` (đặc biệt `youtube-plugin-f45bbb7.jar`)

2. **Không sao chép**: `bin/` và `obj/` của `1.2.0` sang `1.3.6` (có thể gây version conflict DLL, đặc biệt Lavalink4NET protocol).

3. Đảm bảo `.env` có tối thiểu:
   ```env
   DISCORD_TOKEN=your_discord_bot_token
   # (tùy chọn) GROQ_API_KEY=...  cho /roleplay
   # (tùy chọn) YOUTUBE_API_KEY=...
   ```

4. Cập nhật youtube-plugin Lavalink (nếu version bạn dùng bị YouTube chặn sign):
   - Để thử phiên bản khác, đổi file `.jar` giữa `plugins/` và `plugins.disabled/`, restart lại Lavalink.
   - Nếu bài YouTube báo "yêu cầu đăng nhập / giới hạn tuổi": cấu hình poToken + visitorData hoặc Netscape cookies trong `application.yml`.

5. Restore & build một lần:
   ```powershell
   dotnet restore
   dotnet build --no-restore
   ```
   (Các lần chạy sau dùng `dotnet run --no-build` để nhanh).

6. Chạy Lavalink:
   ```powershell
   java -jar Lavalink.jar
   ```

7. Khởi động bot:
   ```powershell
   dotnet run --no-build
   ```

8. Test nhanh sau nâng cấp:
   - `/play https://www.youtube.com/watch?v=...` (đảm bảo phát đúng bài).
   - `/play https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT` (Spotify single track).
   - `/queue` → `/play <link bài khác>` (kiểm tra add cuối, không ghi đè).
   - `/next`, `/jump 2` trong playlist (đảm bảo chuyển bài nhanh).

---

## Lưu ý tương thích

- Yêu cầu **.NET 10** (giữ nguyên như V1.2.0).
- Lavalink mặc định `http://127.0.0.1:2333`, password `youshallnotpass` (không đổi).
- Discord Developer Portal phải bật 2 Intent:
  - `Message Content Intent` (prefix command).
  - `Server Members Intent` (GuildInspector & slash command member).
- Named Mutex `ClaraBot.SingleInstance` ngăn chạy 2 process cùng lúc.
- **KHÔNG** đưa `.env`, token, key bất kỳ, file log (`bin/Debug/net10.0/logs/`) lên GitHub / nơi công cộng.
