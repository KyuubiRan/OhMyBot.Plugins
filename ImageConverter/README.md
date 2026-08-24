# ImageConverter

Telegram 图片格式转换插件，使用 OpenCvSharp/OpenCV，不依赖 ImageSharp。

## 指令

回复一条媒体消息后发送：

```text
/imgcvt <png|jpg|webp|sticker> [w=宽度] [h=高度] [q=质量]
```

- `w` / `h`：输出边界，保持原图比例，范围 1-8192。
- `q`：JPEG/WebP 质量，范围 1-100。
- `sticker`：这是 Telegram 专用输出，会调用 `sendSticker`。静态图片输出 WebP，最长边缩放到 512；GIF、动画或视频输出 VP9 WebM，最长 3 秒、30 FPS、512 × 512，并限制为 256 KiB。

动态 sticker 需要服务器安装 FFmpeg（启用 `libvpx-vp9`），默认执行 `ffmpeg`；也可以在 `pluginsettings.json` 里设置 `ImageConverter:FfmpegPath`。Telegram 的 TGS/Lottie 动态贴纸不是视频格式，FFmpeg 不能转换，插件会明确提示不支持。

普通图片单张输入及输出文件上限均为 20 MiB，输入图像上限为 8192 × 8192 且不超过 4000 万像素；静态 sticker 上限为 512 KiB。

Debug 插件包包含 macOS arm64 与 Linux arm64 原生库；Release 插件包只包含部署服务器使用的 Linux arm64 原生库。
