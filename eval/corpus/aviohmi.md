<div align="center">

# AvioHMI

**A full-stack avionics mission-computer & HMI suite**

![Java](https://img.shields.io/badge/Core-Java%2017-b07219?logo=openjdk&logoColor=white)
![Flutter](https://img.shields.io/badge/Cockpit%20MFD-Flutter-02569B?logo=flutter&logoColor=white)
![React](https://img.shields.io/badge/Ground%20Control-React%20%2B%20TypeScript-3178C6?logo=react&logoColor=white)
![CUDA](https://img.shields.io/badge/Vision-C%2B%2B%20%2F%20CUDA%20%2B%20JNI-76B900?logo=nvidia&logoColor=white)
![FFmpeg](https://img.shields.io/badge/Video-FFmpeg-007808?logo=ffmpeg&logoColor=white)
![PostGIS](https://img.shields.io/badge/Data-PostgreSQL%20%2F%20PostGIS-336791?logo=postgresql&logoColor=white)
![OpenSky](https://img.shields.io/badge/Live%20Data-OpenSky%20ADS--B-00b894?logo=airplane&logoColor=white)
![WebXR](https://img.shields.io/badge/AR%20%2F%20VR-WebXR-8a5cf6)
![Docker](https://img.shields.io/badge/Deploy-Docker%20Compose-2496ED?logo=docker&logoColor=white)
![License](https://img.shields.io/badge/license-MIT-informational)

</div>

AvioHMI is a mission computer and ground-control suite that flies a fleet of
aircraft — either a 3-aircraft simulation or **real ADS-B traffic pulled live
from the OpenSky Network** — and streams position, speed, heading, attitude,
and fuel state over WebSocket to a web-based ground-control dashboard, a
touch-screen cockpit display, and both AR (phone camera + compass) and VR
(WebXR) 3D views, while an ffmpeg → CUDA pipeline decodes a real sensor video
and feeds GPU-accelerated Sobel edge detection into a live HUD overlay. It's
built the way an embedded HMI product would be — layered domain logic, a real
network protocol between components, persistence with replay, graceful
degradation when a link (or GPU, or database, or API key) is missing, and a
CI pipeline that builds every module.

## Screenshots

**Fleet & Map**, tracking real airline traffic live from OpenSky Network:

![Ground Control — Fleet & Map showing 30 real aircraft tracked live over Turkey via OpenSky Network](docs/screenshots/ground-control.png)

**Dashboard** — fleet-wide KPIs, status breakdown, recent alerts:

![Ground Control — Dashboard with KPI cards, fleet status donut, and recent alerts](docs/screenshots/dashboard.png)

<table>
<tr>
<td width="50%">

**Reports** — per-aircraft distance/time breakdown

![Ground Control Reports page](docs/screenshots/reports.png)

</td>
<td width="50%">

**Alerts** — live warnings + rolling history

![Ground Control Alerts page](docs/screenshots/alerts.png)

</td>
</tr>
</table>

**Cockpit MFD** — touch multi-function display (PFD / NAV / SYS):

<table>
<tr>
<td width="33%">

Attitude indicator & tapes

![Cockpit MFD PFD page](docs/screenshots/cockpit-mfd-pfd.png)

</td>
<td width="33%">

Heading-up compass rose

![Cockpit MFD NAV page](docs/screenshots/cockpit-mfd-nav.png)

</td>
<td width="33%">

Link/telemetry/alert status

![Cockpit MFD SYS page](docs/screenshots/cockpit-mfd-sys.png)

</td>
</tr>
</table>

## Features

- **Multi-page ops dashboard** — a sidebar + topbar shell (React Router)
  with dedicated Dashboard, Fleet & Map, Playback, Alerts, Sensor Feed, AR
  View, Reports, and Settings pages, all sharing one `TelemetryContext` so
  state (selected aircraft, alert history) survives navigation.
- **AR View (wayfinding)** — a live `getUserMedia` camera feed with an arrow
  composited via canvas that points at a real waypoint's true compass
  bearing, computed from the device's actual GPS position (`Geolocation`) and
  compass heading (`DeviceOrientationEvent`, with the iOS permission-prompt
  path handled). Genuine sensor-driven AR, not a fixed overlay — phone-only,
  since desktop browsers don't expose a compass.
- **Multi-aircraft simulation** — three aircraft (`AVIO101/102/103`) fly the
  same route with distinct personalities: one flies clean, one periodically
  drifts off-course and blips over the speed limit, one is a chronic
  speeder. Click any aircraft on the map or in the fleet list to inspect it.
- **Live alerts** — overspeed, off-course (cross-track distance from the
  planned route), and low-fuel warnings computed from real telemetry and
  surfaced as a banner.
- **Altitude/speed history chart** and a **mission log** for the selected
  aircraft.
- **Waypoint inspector** — click a waypoint on the map for its coordinates
  and altitude.
- **History playback** — telemetry is persisted to PostgreSQL/PostGIS; a
  scrubber lets you rewind and replay any aircraft's recorded flight.
- **CUDA sensor feed** — Core decodes a real frame from a sample sensor video
  with **ffmpeg** every few seconds, runs it through the CUDA Sobel kernel,
  composites live telemetry into a HUD, and Ground Control polls the result —
  a real (if small) multimedia-decode → GPU-processing → HMI-overlay pipeline,
  not just a static test image. The pipeline itself is proper C++ (RAII image
  classes, a `SobelProcessor` owning its own device-memory lifetime) called
  **in-process from Java via JNI** (`video-processing/hud_native.dll`) rather
  than shelled out to per frame — falling back to spawning `edge_detect.exe`
  as a subprocess if the native library wasn't built for the platform.
- **Resilient clients** — both Ground Control and Cockpit MFD auto-reconnect
  and degrade gracefully (a local simulator standing in for Ground Control,
  a LINK/NO LINK indicator on the MFD) if Core isn't reachable.
- **Demo operator login** — a UX-only sign-in screen for the "ops center"
  feel (see the security note below).

## Architecture

```mermaid
flowchart LR
    subgraph Core["Mission Computer Core (Java)"]
        SIM["SensorSimulator × 3\nAVIO101 / 102 / 103"]
        WS["WebSocketServer\n:8765"]
        HTTPSRV["TelemetryHttpServer\n:8766"]
        REPO["TelemetryRepository"]
        HUDT["HUD overlay thread"]
        SIM --> WS
        SIM --> REPO
        HUDT --> HTTPSRV
    end

    FFMPEG["ffmpeg\ndecodes sample_sensor_feed.mp4"]
    CUDA["video-processing (C++/CUDA)\nSobel + HUD compositing\ncalled via JNI, subprocess fallback"]
    PG[("PostgreSQL / PostGIS")]
    GC["Ground Control (React)"]
    MFD["Cockpit MFD (Flutter)"]

    HUDT -- "extract current frame" --> FFMPEG
    FFMPEG -- "current_frame.pgm" --> HUDT
    HUDT -- "heading / speed / altitude + frame" --> CUDA
    CUDA -- "live_overlay.bmp" --> HUDT
    REPO <--> PG

    WS -- "route + telemetry JSON" --> GC
    WS -- "route + telemetry JSON" --> MFD
    HTTPSRV -- "/history (playback)" --> GC
    HTTPSRV -- "/overlay.bmp (sensor feed)" --> GC
```

Core is the single source of truth. `TelemetryServerApp` runs the route
simulation for all three aircraft and broadcasts every tick as JSON over a
plain-JDK WebSocket server — no external libraries; the handshake, framing,
and close sequence are all hand-written in `avio.transport`, since the
project has no Maven/Gradle available. On connect, a client first gets a
`{"type":"route",...}` message with the waypoints, then a
`{"type":"telemetry",...}` message per aircraft roughly every 500ms with
position/speed/heading/roll/pitch/fuel and the distance/ETA to the next
waypoint. Roll and pitch are derived server-side from the heading/altitude
delta between ticks; fuel depletes over each lap and resets when it loops.

Telemetry is written to PostgreSQL/PostGIS when reachable (`docker compose up
-d postgres`); otherwise Core falls back to an in-memory store and says so on
startup. A small `com.sun.net.httpserver.HttpServer` (also JDK-only) exposes
`/history` for playback and `/overlay.bmp`. That overlay comes from a real
pipeline: every few seconds Core shells out to `ffmpeg` to decode the current
frame of `video-processing/assets/sample_sensor_feed.mp4` into a PGM, then
hands that frame to `video-processing/edge_detect.exe`, which runs it through
the CUDA Sobel kernel and composites the lead aircraft's live
heading/speed/altitude into the HUD. If `ffmpeg` isn't on PATH, `avio.transport.FfmpegLocator`
also checks the usual WinGet install location; if neither is found, it falls
back to the synthetic test frame so the feature degrades instead of breaking.

Both clients are resilient to Core being unavailable:
- **Ground Control** connects via a `useTelemetrySocket` hook with
  auto-reconnect. If Core isn't reachable within 3s it falls back to a local
  simulator (`mock/telemetryFeed.ts`) so the panel is still demoable
  standalone — a "● LIVE" vs "● OFFLINE PREVIEW" badge shows which is active.
- **Cockpit MFD**'s `FlightState` connects the same way over
  `web_socket_channel`, with its own 2s reconnect loop and a LINK/NO LINK
  indicator in the page-button row.

> **Security note:** Ground Control's login screen (`components/LoginGate.tsx`)
> is a UX-only demo gate — fixed credentials shown right on the screen, no
> backend check, no hashing, no session token. It exists to give the
> dashboard an "ops center" feel and must not be mistaken for real access
> control.

### Security model (Core + clients)

| Layer | Dev default | Production recommendation |
|-------|-------------|---------------------------|
| **Ground Control login** | JWT via `POST /auth/login` when Core is up; local fallback if Core is down | Set `AVIO_AUTH_ENABLED=true`; rotate `AVIO_JWT_SECRET` |
| **Core HTTP/WS** | Open when auth disabled; JWT or `AVIO_API_KEY` when enabled | `AVIO_AUTH_ENABLED=true` + strong secrets |
| **CORS** | `http://localhost:5173` | Set `AVIO_CORS_ORIGIN` to your dashboard origin |
| **Postgres** | `aviohmi` / `aviohmi` on port `5433` | Strong passwords, private network only |
| **TLS** | Self-signed `dev-keystore.p12` | Proper PKI; `AVIO_KEYSTORE_PASSWORD` |
| **Telemetry retention** | 7 days (`TELEMETRY_RETENTION_DAYS`) | Tune per ops policy; hourly purge when Postgres is up |
| **Observability** | `GET /metrics` JSON (`wsClients`, `telemetryRecords`, `uptimeSeconds`) | Scrape from your monitoring stack |

**Auth env vars:** `AVIO_AUTH_ENABLED`, `AVIO_AUTH_USER`, `AVIO_AUTH_PASSWORD`, `AVIO_JWT_SECRET`, `AVIO_JWT_TTL_SECONDS`, `AVIO_API_KEY`.

**Client env vars:** `VITE_CORE_HOST`, `VITE_AVIO_API_KEY` (Ground Control); `--dart-define=CORE_SECURE=true --dart-define=AVIO_API_KEY=...` (Flutter MFD). JWT stored in `sessionStorage` as `aviohmi.token`.

**Link health:** Core sends WebSocket ping every 30s; Ground Control reconnects if no telemetry for 15s.

**Tests:** `core/test.sh` (unit + WebSocket + Postgres), `npm test` (Vitest), `npm run test:e2e` (Playwright).

## Modules

| Module | Stack | What it does |
|---|---|---|
| [`core/`](core) | Java 17, layered architecture, Dockerized | Simulates 3 aircraft along a route (or streams real ADS-B via OpenSky); serves live telemetry (WebSocket), history (HTTP), and the HUD overlay (HTTP) |
| [`cockpit-mfd/`](cockpit-mfd) | Flutter (Android/iOS/web/**Linux**/Windows/macOS) | Touch multi-function display: PFD (attitude/speed/altitude/heading), NAV, and SYS pages |
| [`ground-control/`](ground-control) | React + TypeScript + Vite + React Router | Multi-page ops dashboard: Dashboard, Fleet & Map, Playback, Alerts, Sensor Feed, AR View, VR View, Reports, Settings |
| [`video-processing/`](video-processing) | C++ / CUDA (nvcc) + ffmpeg + JNI | RAII C++ classes run GPU Sobel edge detection and composite a live-telemetry HUD, called in-process from Java via a JNI bridge |
| [`db/`](db) + `docker-compose.yml` | PostgreSQL + PostGIS | Schema for routes/telemetry, backing history playback; `docker compose up -d` also builds/runs Core and Ground Control as containers |
| [`.github/workflows/ci.yml`](.github/workflows/ci.yml), [`.gitlab-ci.yml`](.gitlab-ci.yml), [`Jenkinsfile`](Jenkinsfile) | GitHub Actions, GitLab CI, Jenkins | Equivalent build/test pipelines for whichever CI system a team already runs |

## Running the connected system

**Option A — Docker Compose** (Postgres + Core + Ground Control, no local JDK/Node needed):
```bash
docker compose up -d --build
```

**Option B — native** (needed either way for Cockpit MFD, and for the CUDA/JNI HUD pipeline, which isn't containerized — it's a native Windows library that also needs GPU passthrough):
```bash
docker compose up -d postgres          # optional but recommended: enables /history playback
cd core && ./build.sh && ./serve.sh    # starts ws://localhost:8765 and http://localhost:8766
```

Then, in separate terminals:
```bash
cd ground-control && npm install && npm run dev     # open http://localhost:5173, sign in as operator/aviohmi
cd cockpit-mfd && flutter pub get && flutter run
```

Ground Control's badge should read "● LIVE" and Cockpit MFD's PFD page should
show one of the same aircraft moving along the same route.

**Cockpit MFD also targets embedded Linux.** Flutter's Linux desktop target
is enabled and builds a real GTK binary — `flutter build linux` just can't
cross-compile it from a Windows/macOS host, so `cockpit-mfd/build-linux-docker.sh`
builds it inside a Linux container instead (output:
`build/linux/x64/release/bundle/cockpit_mfd`). The same codebase runs on real
embedded Linux avionics hardware via [flutter-elinux](https://github.com/sony/flutter-elinux).

## Running each module standalone

**Core** (Java, no Maven/Gradle required):
```bash
cd core
./build.sh   # compiles everything into out/
./run.sh     # console demo: runs one aircraft once, prints ticks (MissionComputerApp)
./serve.sh   # server: broadcasts 3 aircraft over ws://localhost:8765 + http://localhost:8766, looping forever
./test.sh    # runs the NavigationService smoke test
```

**Cockpit MFD** (Flutter):
```bash
cd cockpit-mfd
flutter pub get
flutter run          # or: flutter run -d windows / -d chrome
flutter test
```

**Ground Control** (React + TypeScript):
```bash
cd ground-control
npm install
npm run dev
```

**Video processing** (CUDA — requires nvcc + an MSVC/gcc host compiler; ffmpeg optional):
```bash
cd video-processing
./build.bat                                          # Windows: sets up MSVC env and runs nvcc
./edge_detect.exe 250 460 1500 out.bmp               # synthetic test frame
./edge_detect.exe 250 460 1500 out.bmp frame.pgm     # process a real frame instead (any P5 PGM)

# extract a real frame from the bundled sample video first:
ffmpeg -y -ss 3.0 -i assets/sample_sensor_feed.mp4 -vframes 1 -pix_fmt gray frame.pgm
```

**Database**:
```bash
docker compose up -d postgres   # exposes 5433 on the host — 5432 is often already taken locally
```

## Roadmap

- Feed the CUDA pipeline from a live camera/RTSP source via ffmpeg instead of
  the bundled sample video, for a true real-time sensor feed.
- Package Cockpit MFD for an embedded Linux + touchscreen target (e.g.
  Raspberry Pi) once Flutter's Linux/embedded toolchain is set up there.
- Replace the demo login gate with real authentication if this ever needs to
  be more than a portfolio piece.

## Development process

Built iteratively, one demoable increment per sprint, Scrum-style: a product
backlog of user stories ([docs/BACKLOG.md](docs/BACKLOG.md)) pulled into
sprints with a definition of done, each sprint closing with a working,
demoable increment — see [docs/SPRINTS.md](docs/SPRINTS.md) for the
sprint-by-sprint retro notes, reconstructed from the actual git history.

## License

MIT — see [LICENSE](LICENSE).
