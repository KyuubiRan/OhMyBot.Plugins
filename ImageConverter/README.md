# ImageConverter

Telegram 图片格式转换插件，统一使用 FFmpeg/FFprobe 解码与编码，不依赖文件扩展名判断真实格式。

## 指令

回复一条媒体消息后发送：

```text
/imgcvt <png|jpg|webp|sticker> [webp|webm] [w=宽度] [h=高度] [q=质量] [fps=帧率] [bg=black|white]
```

- `w` / `h`：输出边界，保持原图比例，范围 1-8192。
- `q`：所有输出统一使用 1-100 的整数质量；`sticker webm` 中 `100` 是基准质量，`50` 从更高压缩档开始。
- `fps`：只用于 `sticker webm`，范围 1-30；未指定时保留源帧率（最高 30 FPS）。
- `bg`：只用于 `sticker webm`；`black` / `white` 会将透明区域合成到黑底/白底并移除 Alpha，然后按 256 KiB 预算进行 VP9 两遍目标码率压制。未指定时保留透明 Alpha。
- `sticker`：这是 Telegram 专用输出，会调用 `sendSticker`。不指定容器时，静态图自动输出 WebP，动画或视频自动输出 WebM。
- `sticker webp`：强制输出静态 WebP；GIF、MP4 等动画输入只取第一帧。
- `sticker webm`：强制输出 VP9 WebM；JPG、PNG、WebP 等静态输入也会直接转为 WebM。输出最长 3 秒、512 × 512，并限制为 256 KiB；转换会保持指定/源帧率。保留 Alpha 时自适应调整 CRF，指定 `bg` 时按目标码率压制并根据实际输出大小重试。
- 输入类型优先按文件 header/magic bytes 识别，文件名和 MIME 只在无法识别时回退使用。
- GIF、动画或视频转 PNG/JPEG/WebP 时取第一帧；PNG/WebP 会保留源帧透明通道，JPEG 格式不支持透明。

所有转换都需要服务器安装 FFmpeg 与 FFprobe；WebP 需要 `libwebp`，动态 sticker 需要 `libvpx-vp9`。默认执行 `ffmpeg` / `ffprobe`，也可以在 `pluginsettings.json` 里设置 `ImageConverter:FfmpegPath` 与 `ImageConverter:FfprobePath`。Telegram 的 TGS/Lottie 动态贴纸不是视频格式，FFmpeg 不能转换，插件会明确提示不支持。

普通图片单张输入及输出文件上限均为 20 MiB，输入图像上限为 8192 × 8192 且不超过 4000 万像素；静态 sticker 上限为 512 KiB。

插件包不再携带 OpenCV native runtime，避免受部署服务器发行版的动态库版本影响。
