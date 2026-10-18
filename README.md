# PhoneMic

**把手机当成电脑的麦克风。** 手机负责采集，电脑负责播放到一个虚拟音频设备，于是微信、QQ、Zoom、Discord、OBS、游戏语音里都能把手机当作麦克风使用。

- 电脑端：`PhoneMicServer.exe`（Windows 10/11，单文件，无需安装 .NET）
- 手机端：`PhoneMic.apk`（Android 7.0 及以上）

两者都在 [Releases](../../releases) 里，由 GitHub Actions 自动构建。

---

## 它是怎么工作的

```
 手机麦克风 ──► AudioRecord 48kHz 单声道 PCM16
                        │
                        │  UDP，每 10 ms 一个包
                        ▼
             PhoneMicServer.exe
                        │  抖动缓冲（默认 60 ms）+ 重排序
                        ▼
              VB-Audio "CABLE Input"
                        │
                        ▼
        CABLE Output ＝ 系统里的一个麦克风
                        │
                        ▼
         微信 / QQ / Zoom / Discord / OBS
```

### 为什么必须装 VB-Audio Virtual Cable

Windows 不允许普通程序凭空创建一个音频输入设备，那需要经过签名的内核驱动。所以「虚拟麦克风」这件事本身一定有驱动参与，所有同类工具（WO Mic、DroidCam 的麦克风模式）都是这么做的。

本项目的做法是不重复造驱动，而是复用免费成熟的 **VB-Audio Virtual Cable**：程序把声音播放进 `CABLE Input`，系统和其它软件就把 `CABLE Output` 当成一个麦克风。少了这一步，`PhoneMicServer.exe` 只会把手机的声音从音箱放出来，不会出现在任何软件的麦克风列表里。

---

## 快速开始

### 1. 电脑端

1. 安装 [VB-Audio Virtual Cable](https://vb-audio.com/Cable/)，装完重启一次。
2. 从 [Releases](../../releases) 下载 `PhoneMicServer.exe`，双击运行。
3. 窗口里会打印本机地址，例如：

   ```
   输出设备: CABLE Input (VB-Audio Virtual Cable)
   监听端口: UDP 8765

   在手机上填入下面任意一个地址：
       192.168.1.10  (Wi-Fi)
   ```

4. 保持窗口开着。

### 2. 手机端

1. 安装 `PhoneMic.apk`（第一次安装需要在系统里允许「安装未知来源应用」）。
2. 手机和电脑连同一个 Wi-Fi。
3. 打开 PhoneMic，填入电脑上打印的 IP 和端口 `8765`，点「开始」。
4. 允许麦克风权限和通知权限。

### 3. 在其它软件里选用

打开微信 / QQ / Zoom / Discord / OBS，把**麦克风**选成：

```
CABLE Output (VB-Audio Virtual Cable)
```

**不要**选 `CABLE Input`，那是我们播放端写入的方向。

---

## 电脑端参数

```
PhoneMicServer.exe [选项]

  --port <n>         UDP 监听端口，默认 8765
  --device <文本>    输出设备名称片段，默认自动找 CABLE Input
  --latency <ms>     抖动缓冲目标，默认 60（10–500）
  --list             列出所有播放设备后退出
  --help             显示帮助
```

例子：

```powershell
# 看看系统里都有哪些播放设备
.\PhoneMicServer.exe --list

# 用第二个虚拟线（CABLE-B），端口改成 9000
.\PhoneMicServer.exe --device "CABLE-B Input" --port 9000
```

---

## 常见问题

**手机连不上电脑**

- 确认两台设备在同一个 Wi-Fi，并且路由器没开「AP 隔离」。
- Windows 防火墙第一次会弹窗询问，要允许**专用网络**。如果当时点了取消：控制面板 → Windows Defender 防火墙 → 允许应用通过防火墙 → 找到 PhoneMicServer.exe 勾上专用。
- 电脑上如果装了 VPN 或多张网卡，手机要填和 Wi-Fi 同一网段的那个 IP。

**有声音但语音软件里没有麦克风**

- 没有装 VB-Audio Virtual Cable，或者没重启。
- 电脑端启动时如果提示「没有找到 CABLE Input」，用 `--list` 看实际名称。

**延迟偏高 / 断断续续**

- 把 `--latency` 调大，例如 `--latency 100`，换取更稳的声音。
- 关掉手机上的省电模式，它可能限制后台网络。
- 尽量用 5 GHz Wi-Fi。

**手机锁屏后停止**

- 需要在系统设置里把 PhoneMic 的电池策略设为「不限制」或「允许后台活动」，各厂商的省电策略会杀掉前台服务。

---

## 协议说明

每个 UDP 包 = 16 字节头 + 960 字节 PCM，正好 10 ms 音频。

| 偏移 | 长度 | 含义 |
|---|---|---|
| 0 | 4 | 魔数 `PMIC`（小端 `0x43494D50`） |
| 4 | 4 | 序号，uint32，递增 |
| 8 | 8 | 发送端单调时钟，毫秒 |
| 16 | 960 | PCM S16LE，48000 Hz，单声道，480 个采样点 |

选 UDP 而不是 TCP 的原因：TCP 的重传会造成队头阻塞，延迟会越积越大；语音要的是**稳定且可预测的延迟**，丢一两个包比整体卡顿好得多。

---

## 从源码构建

两种产物都由 `.github/workflows/build.yml` 在云端构建，推送 `main` 分支就会自动产出并发布。

本地构建：

```powershell
# 电脑端，需要 .NET 8 SDK
dotnet publish server/PhoneMicServer.csproj -c Release -o publish

# 手机端，需要 JDK 17 + Android SDK
cd android
gradle :app:assembleDebug
# 产物在 app/build/outputs/apk/debug/app-debug.apk
```

---

## 目录结构

```
phone-mic/
├── server/                     Windows 服务端（.NET 8 + NAudio）
│   ├── PhoneMicServer.csproj
│   └── Program.cs              UDP 接收、抖动缓冲、WASAPI 输出
├── android/                    Android 客户端（纯 Java，零依赖）
│   ├── app/src/main/java/com/phonemic/app/MainActivity.java
│   └── app/src/main/java/com/phonemic/app/StreamService.java
├── docs/wiki-Home.md           Wiki 首页正文
└── .github/workflows/build.yml 云端构建 + 发布
```

---

## 已知限制

- 只支持同一个局域网，没有做公网穿透。
- APK 使用 debug 签名，因为把 release 签名密钥放进公开仓库并不安全。自己长期使用请用自己的密钥重新签名。
- 只传输麦克风，不传输系统声音（那是另一个方向，需要 WASAPI loopback）。
- 没有回声消除。如果同时用音箱外放，对方可能听到回声，建议戴耳机。

## 许可证

[MIT](LICENSE)
