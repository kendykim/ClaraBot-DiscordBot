# Clara Bot

```text
   ___ _                     ___       _     _
  / __\ | __ _ _ __ __ _    / __\ ___ | |_  / \
 / /  | |/ _` | '__/ _` |  /__\/// _ \| __|/  /
/ /___| | (_| | | | (_| | / \/  \ (_) | |_/\_/
\____/|_|\__,_|_|  \__,_| \_____/\___/ \__\/
```

> Logo được tạo bằng `FiggleFonts.Ogre`, cùng font ASCII mà bot hiển thị khi khởi động.

Clara là Discord bot đa năng viết bằng C# và .NET 10, tập trung vào phát nhạc qua Lavalink, quản lý cộng đồng và trò chuyện roleplay bằng AI. Bot hỗ trợ slash command, prefix command, hàng đợi nhạc, **event-stream phát hiện lỗi tức thì**, tự phục hồi kết nối và chẩn đoán hệ thống.

## Phiên bản hiện tại

| Phiên bản | Ngày cập nhật | Ghi chú |
|---|---|---|
| **1.3.6** | 2026-10-08 | Sửa CRITICAL phát sai bài hát, event-stream playback, SpotifyService viết lại, hỗ trợ đầy đủ Spotify playlist/album, queue mode cải thiện |

Chi tiết thay đổi theo từng phiên bản xem [fix.md](file:///d:/BotDiscord/Clara_bot/fix.md). Giải thích kiến trúc module xem [CAUTRUC.md](file:///d:/BotDiscord/Clara_bot/CAUTRUC.md).

## Tổng quan

| Thành phần | Công nghệ |
|---|---|
| Runtime | .NET 10 |
| Discord SDK | Discord.Net 3.19.1 |
| Audio | Lavalink4NET 4.2.1 |
| Playback event | LavalinkPlaybackEvents (System.Threading.Channels) — mới 1.3.6 |
| Lavalink | `127.0.0.1:2333` |
| Spotify | Spotify Embed public (không cần key) — viết lại 1.3.6 |
| AI roleplay | Groq API |
| Phiên bản | **1.3.6** |

## Tính năng

- Phát nhạc và playlist YouTube qua Lavalink (đảm bảo phát **đúng bài** theo URL user chọn — fix 1.3.6).
- Nhận link **track, playlist, album** Spotify công khai, tìm bài tương ứng trên YouTube; playlist/album load async background; không cần Spotify API/Premium.
- Hàng đợi, tìm kiếm, lặp, trộn, chuyển bài và điều chỉnh tốc độ phát.
- **Event-stream playback** thay cho polling (1.3.6): phát hiện `TrackException`/`TrackStuck` real-time, chuyển bài `/next` / `/jump` ~100ms.
- Lavalink dùng Opus quality 10/10, resampling HIGH và buffer chống giật; bitrate âm thanh tuân theo voice channel và Boost Level của server Discord.
- Slash command được đăng ký tự động và xử lý bằng deferred response.
- Công cụ moderation: kick, ban, unban, role, warn, clear, lock và slowmode.
- Roleplay AI theo từng kênh bằng Groq API.
- Theo dõi CPU, RAM, latency và tốc độ mạng.
- Tự động giám sát, kết nối lại Discord và Lavalink (reconnect với exponential backoff + jitter).
- Ghi log theo phiên vào `bin/Debug/net10.0/logs`.
- Chế độ kiểm tra kết nối Discord và danh sách guild độc lập.
- Kết quả tìm kiếm cũ được tự động làm gọn (không spam embed); hạn chế 15 phút expiration.

## Yêu cầu

- [.NET 10 SDK](https://dotnet.microsoft.com/download) để build từ source.
- Java 17 trở lên để chạy Lavalink.
- Discord bot token.
- Bật `Message Content Intent` và `Server Members Intent` trong Discord Developer Portal.
- Groq API key nếu sử dụng roleplay AI.
- Lavalink và plugin YouTube nếu sử dụng tính năng âm nhạc.

## Cài đặt

```powershell
git clone https://github.com/chlorinebot/Clara_bot.git
cd Clara_bot
dotnet restore
dotnet build --no-restore
```

### Biến môi trường

Tạo file `.env` ở thư mục gốc:

```env
DISCORD_TOKEN=your_discord_bot_token
GROQ_API_KEY=your_groq_api_key
YOUTUBE_API_KEY=your_youtube_api_key
```

| Biến | Bắt buộc | Mục đích |
|---|---:|---|
| `DISCORD_TOKEN` | Có | Xác thực bot với Discord |
| `GROQ_API_KEY` | Không | Bật tính năng roleplay AI |
| `YOUTUBE_API_KEY` | Không | Hỗ trợ chức năng YouTube cần API |

> **Bảo mật**: Không commit `.env`, token, API key hoặc webhook URL lên GitHub / nơi công cộng.

## Cấu hình Lavalink

```text
Clara_bot/
├── Lavalink.jar
├── application.yml
├── plugins/                     # Plugin đang active
│   └── youtube-plugin-*.jar
└── plugins.disabled/            # Version cũ / dự phòng (thêm mới 1.3.6)
    └── youtube-plugin-*.jar
```

Mặc định bot kết nối tới `http://127.0.0.1:2333` với password `youshallnotpass`.

Khởi động Lavalink trong terminal riêng:

```powershell
java -jar Lavalink.jar
```

Đợi Lavalink báo sẵn sàng trước khi dùng lệnh âm nhạc. Bot vẫn có thể kết nối Discord khi Lavalink chưa hoạt động và sẽ giám sát node trong nền.

**Gợi ý YouTube plugin (1.3.6)**:
- Nếu bài báo "video is unavailable", "content warning", "yêu cầu đăng nhập" hoặc "giới hạn tuổi": đổi plugin giữa `plugins/` và `plugins.disabled/`, restart Lavalink.
- Hoặc cấu hình `poToken` + `visitorData` hoặc Netscape cookies trong `application.yml`.

## Khởi động bot

Sau khi đã build:

```powershell
dotnet run --no-build
```

Sau khi thay đổi source code:

```powershell
dotnet build --no-restore
dotnet run --no-build
```

Hoặc chạy binary:

```powershell
.\bin\Debug\net10.0\Clara_bot.exe
```

> Xem [run.md](file:///d:/BotDiscord/Clara_bot/run.md) cho lệnh nhanh 2 dòng.

### Script tiện ích Windows (thêm 1.3.6)

| Script | Mục đích |
|---|---|
| `runinbox.bat` | Chạy bot trong cửa sổ console riêng |
| `check-guilds.bat` | Kiểm tra nhanh guild / member bot có quyền truy cập |
| `runwebhook.bat` | Chạy chế độ webhook |

## Kiểm tra kết nối

Kiểm tra token và Discord REST API mà không khởi động gateway:

```powershell
dotnet run --no-build -- --check-discord
```

Kiểm tra các guild bot có quyền truy cập:

```powershell
dotnet run --no-build -- --check-guilds
```

## Lệnh

### Thông tin và hệ thống

| Slash command | Prefix command | Mô tả |
|---|---|---|
| `/help` | `/heyclara`, `/help` | Hiển thị trợ giúp |
| `/info` | `/infoclara` | Thông tin bot và phiên bản |
| `/ping` | `/pingclara` | CPU, RAM, latency và tốc độ mạng |

### Âm nhạc

| Slash command | Prefix command | Mô tả |
|---|---|---|
| `/play query:<query>` | `/playclara <query>` | **Cập nhật 1.3.6**: Phát YouTube; hỗ trợ link **track/playlist/album Spotify**; phát đúng bài URL user chọn |
| `/search query:<query>` | `/searchclara <query>` | Tìm kiếm bài hát (kết quả hết hạn sau 15 phút; embed cũ tự làm gọn) |
| `/pause` | `/pauseclara` | Tạm dừng |
| `/resume` | `/resumeclara` | Tiếp tục phát |
| `/stop` | `/stopclara` | Dừng và rời voice (2 lần retry Disconnect + 3 mức thông báo — 1.3.6) |
| `/next` | `/nextclara` | Chuyển bài kế tiếp (~100ms — 1.3.6) |
| `/prev` | `/prevclara` | Quay lại bài trước |
| `/jump position:<n>` | `/jumpclara <n>` | Nhảy tới vị trí trong playlist (~100ms — 1.3.6) |
| `/playlist` | `/showplaylistclara` | Hiển thị playlist hiện tại |
| `/qremove position:<n>` | `/removeclara <n>` | Xóa bài khỏi hàng đợi (an toàn khi đang chuyển bài — 1.3.6) |
| `/qclear` | `/clearqueueclara` | Xóa hàng đợi (an toàn khi đang chuyển bài — 1.3.6) |
| `/qmove from:<n> to:<n>` | `/movequeueclara <from> <to>` | Di chuyển bài trong hàng đợi |
| `/queue` | `/queueclara` | Bật hoặc tắt queue mode (toggle + thông báo rõ ON/OFF — 1.3.6) |
| `/loop` | `/loopclara` | Bật hoặc tắt lặp playlist |
| `/shuffle` | `/shufclara` | Trộn playlist |
| `/speed` | `/speedclara` | Điều chỉnh tốc độ phát |
| `/infoplay` | `/infoplayclara` | Thông tin bài đang phát |

**Lưu ý /play (1.3.6)**:
- Hỗ trợ đầy đủ 3 loại URL Spotify:
  - `open.spotify.com/track/...` (single track)
  - `open.spotify.com/playlist/...` (playlist — async background loader)
  - `open.spotify.com/album/...` (album — async background loader)
  - Kể cả URL có prefix `/intl-vn/` locale.
- Khi bật `/queue` mode và đang phát bài khác, `/play <bài-mới>` sẽ add cuối thay vì ghi đè.

### Moderation

| Slash command | Mô tả |
|---|---|
| `/kick` | Đuổi thành viên khỏi server |
| `/ban` | Cấm thành viên, hỗ trợ thời hạn và lý do |
| `/unban` | Gỡ cấm thành viên |
| `/role` | Thêm hoặc xóa role |
| `/warn` | Cảnh cáo thành viên |
| `/clear` | Xóa tin nhắn trong kênh |
| `/stopclear` | Dừng tác vụ xóa tin nhắn |
| `/lock` | Khóa kênh |
| `/unlock` | Mở khóa kênh |
| `/vkick` | Ngắt thành viên khỏi voice |
| `/slowmode` | Cấu hình slowmode |

Bot phải có quyền phù hợp và role nằm cao hơn thành viên cần quản lý.

### Roleplay

| Slash command | Prefix command | Mô tả |
|---|---|---|
| `/roleplay state:on` | `/roleplayclara on` | Bật roleplay trong kênh |
| `/roleplay state:off` | `/roleplayclara off` | Tắt roleplay trong kênh |

## Cấu trúc project

```text
Clara_bot/
├── Commands/                    # Command modules và dịch vụ bot
│   ├── CommandHandler.cs        # Routing lệnh + tích hợp roleplay
│   ├── GeneralModule.cs         # Lệnh hệ thống (heyclara, pingclara)
│   ├── MusicModule.cs           # Lệnh nhạc, queue, playback event-flow (viết lại 1.3.6)
│   ├── ModerationModule.cs      # Lệnh moderation
│   ├── RoleplayModule.cs        # Roleplay AI Groq
│   ├── SlashCommandHandler.cs   # Đăng ký + xử lý slash command
│   ├── SpotifyService.cs        # Spotify embed parser (viết lại hoàn toàn 1.3.6)
│   ├── LavalinkHealthMonitor.cs # Health check Lavalink
│   ├── LavalinkPlaybackEvents.cs# Event-stream playback (mới 1.3.6)
│   ├── ResilientPlaybackRouter.cs  # Retry không đổi provider âm tính (sửa 1.3.6)
│   └── GuildInspector.cs        # Check guild/member
├── plugins/                     # Plugin Lavalink đang active
├── plugins.disabled/            # Plugin dự phòng (mới 1.3.6)
├── Program.cs                   # Bootstrap, DI, gateway, reconnect logic
├── Clara_bot.csproj             # Cấu hình .NET (version 1.3.6)
├── application.yml              # Cấu hình Lavalink
├── Lavalink.jar
├── message.txt                  # Template nội dung tùy chỉnh (mới 1.3.6)
├── runinbox.bat                 # Script tiện ích (mới 1.3.6)
├── check-guilds.bat             # Script tiện ích (mới 1.3.6)
├── runwebhook.bat               # Script tiện ích (mới 1.3.6)
├── README.md                    # File này
├── fix.md                       # Chi tiết thay đổi / bản vá (mới)
├── CAUTRUC.md                   # Giải thích kiến trúc module (mới)
└── run.md                       # Hướng dẫn chạy nhanh (mới)
```

## Bản vá quan trọng từ 1.2.0 (tóm tắt)

Bảng tóm tắt các sửa lỗi chính trong 1.3.6 (chi tiết xem [fix.md](file:///d:/BotDiscord/Clara_bot/fix.md)):

| # | Bản vá | Mức độ |
|---|---|---|
| 1 | **Sửa phát SAI BÀI HÁT**: ResilientPlaybackRouter đổi từ SoundCloud theo tên → dùng chính xác URL user chọn (`TrackSearchMode.None`). Không đổi provider âm tính. | CRITICAL 🔴 |
| 2 | **Playback event-stream**: Thêm `LavalinkPlaybackEvents` module đăng ký 4 Lavalink callback gốc (`TrackStarted/Ended/Exception/Stuck`) + per-guild `System.Threading.Channels.Channel`; thay thế polling 2–3s → real-time. Giảm CPU usage. | CAO 🟠 |
| 3 | **SpotifyService viết lại**: Bỏ `spotifydown.com` + anonymous Web API scrape (không ổn định) → **chỉ dùng Spotify Embed public** chính thức (endpoint ổn định, không cần auth, khó chặn). HTTP tối ưu (gzip/brotli, PooledConnectionLifetime, retry 3 lần backoff). | CAO 🟠 |
| 4 | **Playlist/album Spotify async loader**: `/play <playlist/album>` phát bài đầu tiên < 5s; các bài còn lại load `Task.Run` background không block UI. `PendingLoaders` giữ PlayPlaylistAsync sống cho đến khi load xong. | TRUNG BÌNH 🟡 |
| 5 | **Queue mode + Add bài**: Đang phát playlist bật queue mode → `/play bài-mới` add cuối (không hủy PlaylistAsync CTS). `qremove`/`qclear` check `RequestedIndex != Index` → báo "đang chuyển bài, thử lại" thay vì sửa queue lung tung. | TRUNG BÌNH 🟡 |
| 6 | **Chuyển bài tức thì**: `PlayPlaylistAsync` check requested index ở đầu mỗi event tick; `/next`/`/jump` delay từ 2–3s xuống ~100ms. | TRUNG BÌNH 🟡 |
| 7 | **YouTube restriction detect**: Thêm 2 pattern `"video is unavailable"` + `"content warning"` vào `IsLoginRequiredPlaybackError`. Gợi ý người dùng kiểm tra youtube-plugin. | THẤP 🟢 |
| 8 | **/stop ngắt voice**: 2 lần retry `DisconnectAsync`; 3 mức thông báo thành công / Disconnect OK nhưng Stop fail / Disconnect fail cả 2 lần. Race guard `StopLocks` per-guild SemaphoreSlim. | THẤP 🟢 |
| 9 | **/search cải thiện**: Embed cũ tự sửa thành "🔍 Đã từng tìm:..." (không xê dịch kênh chat); expiration 15 phút; format duration chuẩn `h:mm:ss` / `m:ss`. | THẤP 🟢 |

## Xử lý sự cố

### Bot không kết nối Discord

```powershell
dotnet run --no-build -- --check-discord
```

- Kiểm tra `DISCORD_TOKEN` và privileged intents.
- Đảm bảo không có instance Clara khác đang chạy (Named Mutex `ClaraBot.SingleInstance` ngăn 2 process).
- Xem log mới nhất trong `bin/Debug/net10.0/logs`.
- Project ưu tiên IPv4 vì một số mạng NAT64/IPv6 có thể làm Discord gateway kẹt ở `Connecting`.

### Startup dừng lâu ở bước restore

```powershell
dotnet restore
dotnet build --no-restore
dotnet run --no-build
```

### Bot online nhưng không phát nhạc

- Kiểm tra Java và tiến trình Lavalink.
- Kiểm tra `application.yml`, password và plugin YouTube.
- Xem log Lavalink để phát hiện video giới hạn tuổi, yêu cầu đăng nhập hoặc lỗi cipher.
- **1.3.6**: Nếu bot trả lời "bài video này không khả dụng / content warning":
  - Đổi youtube-plugin version (copy file `.jar` từ `plugins.disabled/` → `plugins/` rồi restart Lavalink).
  - Hoặc cấu hình poToken + visitorData hoặc Netscape cookies trong `application.yml`.

### Spotify playlist trả về ít bài / không load được

- 1.3.6 **bỏ hoàn toàn spotifydown.com** (API này thường chết).
- Đảm bảo URL playlist là **public** (không private / collaborative chỉ riêng mình bạn xem được).
- Log console sẽ báo "Failed to resolve <tên bài>" với từng bài lỗi riêng (không làm chết cả playlist).

### Roleplay không phản hồi

- Kiểm tra `GROQ_API_KEY`.
- Bật `Message Content Intent`.
- Bật roleplay trong đúng kênh.

## Bảo mật

- Không commit `.env`, file log, token hoặc API key.
- Không đăng secret vào issue, ảnh chụp màn hình hoặc CI log.
- Nếu secret từng bị push lên GitHub, hãy thu hồi và tạo secret mới.
- Chỉ cấp cho bot các quyền Discord thực sự cần thiết.
- **File log nhạy cảm**: thư mục `bin/Debug/net10.0/logs/` chứa token Discord trong các request body — tuyệt đối không up lên nơi công cộng.

## Tác giả

- Kim Tuấn
- GitHub: [chlorinebot](https://github.com/chlorinebot)
- **Donate:** [https://i.pinimg.com/736x/1c/5c/5b/1c5c5beddb559e0f2b85b2f354ef75e1.jpg](https://i.pinimg.com/736x/1c/5c/5b/1c5c5beddb559e0f2b85b2f354ef75e1.jpg)

## License

Dự án được phát hành theo [MIT License](LICENSE).

Copyright © 2026 Kim Tuấn. Người dùng được phép sử dụng, sao chép, chỉnh sửa, hợp nhất, xuất bản, phân phối, cấp phép lại và bán các bản sao của phần mềm theo các điều kiện trong giấy phép.
