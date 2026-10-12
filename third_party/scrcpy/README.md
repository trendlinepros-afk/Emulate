# scrcpy server

`scrcpy-server-v5.0.1` is the unmodified device-side server from [scrcpy](https://github.com/Genymobile/scrcpy)
v5.0.1 (Apache License 2.0, see `LICENSE`), downloaded from the official release:
https://github.com/Genymobile/scrcpy/releases/download/v5.0.1/scrcpy-server-v5.0.1
SHA-256: `764eb6f79811d5211fe9df341120882ba9994c7a61b897d7bf3fb662e53bc536`

Emulate uses it for Web access, which needs low-latency H.264 screen streaming and fast touch/key injection.
The build copies it next to `Emulate.exe` as `scrcpy-server.jar`. If you upgrade it, also update
`ScrcpySession.Version` in `src/Emulate/Services/ScrcpySession.cs`, because the server checks that the version matches.
