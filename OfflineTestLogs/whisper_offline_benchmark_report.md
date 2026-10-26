# Offline Whisper.Unity Benchmark Report (English Language)

- **Test Date**: 2026-10-26 13:27:36
- **Engine / Model**: `com.whisper.unity` (`ggml-tiny.bin`, 75 MB)
- **Language Setting**: `en` (English)
- **Connection Mode**: 100% Offline (Local On-Device Inference)
- **Platform / Unity Version**: `WindowsEditor` / Unity `6000.3.8f1`
- **Model Initialization Time (GPU)**: `356 ms` (Warm-up: `239 ms`)
- **Model Initialization Time (CPU)**: `162 ms` (Warm-up: `3023 ms`)

## 1. Offline English Transcription & Keyword Response Time Results

| # | Audio Clip | Audio Duration (s) | GPU Response Time (ms) | GPU RTF | CPU Response Time (ms) | CPU RTF | Transcribed Text (EN) | Triggered 3D Action |
|---|------------|-------------------:|-----------------------:|--------:|-----------------------:|--------:|-----------------------|---------------------|
| 1 | `cmd_01_up_right_jump` | 2.99 s | **102 ms** | 0.034x | **2700 ms** | 0.905x | "Up, right, jump," | `UP (+Y) -> RIGHT (+X) -> JUMP` |
| 2 | `cmd_02_left_forward_spin` | 3.36 s | **72 ms** | 0.021x | **2718 ms** | 0.810x | "Left, forward, spin." | `LEFT (-X) -> FORWARD (+Z) -> SPIN ON` |
| 3 | `cmd_03_bigger_green` | 2.28 s | **64 ms** | 0.028x | **2821 ms** | 1.240x | "Bigger, green." | `SCALE UP -> COLOR GREEN` |
| 4 | `cmd_04_smaller_red_stop` | 3.40 s | **82 ms** | 0.024x | **2747 ms** | 0.809x | "Smaller, red, stop." | `SCALE DOWN -> COLOR RED -> STOP` |
| 5 | `cmd_05_down_back_reset` | 4.22 s | **94 ms** | 0.022x | **2715 ms** | 0.643x | "Down, back, blue, reset." | `DOWN (-Y) -> BACK (-Z) -> COLOR BLUE -> RESET` |
| 6 | `jfk` | 11.00 s | **271 ms** | 0.025x | **2966 ms** | 0.270x | "And so, my fellow Americans, ask not what your country can do for you, ask what you can do for your country." | `SAMPLE DEMO (JUMP + SPIN)` |

## 2. Summary Statistics

- **Total Clips Tested**: `6` (5 English Voice Command Clips + 1 JFK Speech Sample)
- **GPU Offline Response Time**: Avg `114.2 ms` | Min `64 ms` | Max `271 ms` | Avg RTF `0.026x` (~38.8x faster than real-time)
- **CPU Offline Response Time**: Avg `2777.8 ms` | Min `2700 ms` | Max `2966 ms` | Avg RTF `0.779x` (~1.3x faster than real-time)
