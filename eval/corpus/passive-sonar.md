<p align="center">
  <img src="docs/banner.svg" alt="SONAR-X" width="100%">
</p>

<h1 align="center">Passive Sonar Contact Intelligence</h1>

<p align="center">
  <b>A contact can wait. A bad class cannot be unsaid.</b>
</p>

<p align="center">
  Hydrophone audio → 10 s windows → CNN → mean-P track → CONFIDENT or UNCERTAIN → operator screen.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/vessel%20acc-83.6%25-22C55E?style=for-the-badge&labelColor=0B1220" alt="83.6% vessel accuracy">
  <img src="https://img.shields.io/badge/selective-92.9%25%20@%2068.9%25-38BDF8?style=for-the-badge&labelColor=0B1220" alt="92.9% selective at 68.9% coverage">
  <img src="https://img.shields.io/badge/MMSI%20overlap-0-22C55E?style=for-the-badge&labelColor=0B1220" alt="MMSI overlap 0">
  <img src="https://img.shields.io/badge/license-MIT-334155?style=for-the-badge&labelColor=0B1220" alt="MIT">
</p>

<p align="center">
  Real-time <b>passive sonar decision-support prototype</b>.<br>
  Not a fielded USV system. Not a submarine classifier. Not targeting.
</p>

<p align="center">
  <img src="docs/dashboard.png" alt="Operator dashboard: CONTACT-772 Cargo, CONFIDENT" width="100%">
</p>
<p align="center"><i>CONTACT-772 · Cargo 89% · 6 windows · 60 s · <b>CARGO / CONFIDENT</b></i></p>

<table>
  <tr>
    <td width="50%" valign="top">
      <img src="docs/dashboard-uncertain.png" alt="CONTACT-703 UNCERTAIN, CONTINUE TRACKING" width="100%">
      <p align="center"><sub><b>When it should refuse.</b> CONTACT-703 · Cargo 39% / Tanker 48% · CONTINUE TRACKING</sub></p>
    </td>
    <td width="50%" valign="top">
      <img src="docs/demo.gif" alt="Window 1 of 6 still UNCERTAIN because n is below 5" width="100%">
      <p align="center"><sub><b>When it should wait.</b> Window 1/6 · Cargo 94% · still UNCERTAIN · n &lt; 5</sub></p>
    </td>
  </tr>
</table>

---

## Why this exists

A 10-second clip is a weak decision (**60.3%**). The same model, averaged over a vessel on an MMSI-disjoint split, is **83.6%** against a **75.4%** majority.

The product is not “guess harder.” It is a **confidence gate**:

| Forced classifier | This system |
| --- | --- |
| Cargo 0.43 / Tanker 0.39 → **Cargo** | **UNCERTAIN — continue tracking** |
| Cargo 0.89 → Cargo | **CARGO, CONFIDENT** |

```
10s — Cargo 94% — wait
20s — Cargo 95% — wait
30s — Cargo 93% — wait
40s — Cargo 91%
50s — Cargo 90%
60s — Cargo 89%   →  CONFIDENT / CARGO
```

`wait` means fewer than five windows. Contact first. Decision later.

---

## Pipeline

```
                SONAR AUDIO
                     │
                     ▼
              10 s WINDOW
                     │
                     ▼
            64-LINEAR CNN
                     │
                     ▼
              P(C), P(T), P(F)
                     │
                     ▼
              CONTACT TRACK
                     │
          ┌──────────┴──────────┐
          │                     │
      WINDOW 1              WINDOW N
          │                     │
          └──────────┬──────────┘
                     ▼
              MEAN-P / EVIDENCE
                     │
                     ▼
             CONFIDENCE GATE
                /        \
               /          \
          CONFIDENT      UNCERTAIN
             │              │
             ▼              ▼
       Cargo/Tanker/     Continue
         Fishing          tracking
```

The always-answer vessel-level baseline achieves **83.6% accuracy and 0.719 macro-F1** on 61 MMSI-disjoint test vessels.

With selective prediction, the system achieves **92.9% selective accuracy at 68.9% coverage**, rejecting 31.1% of contacts as uncertain.

> **92.9% is not overall test accuracy.** It is performance only on accepted contacts. Thresholds were frozen on validation before the 61 test ships were scored.

<p align="center">
  <img src="results/risk_coverage.png" alt="Risk-coverage curve for selective UNCERTAIN" width="720">
</p>
<p align="center"><sub>Reliability rises by abstaining — not by a more aggressive classifier.</sub></p>

---

## Core results

61 unseen vessels · n ≥ 5 windows · **MMSI overlap = 0**

| | Result |
| --- | ---: |
| Segment accuracy | 60.3% |
| Vessel accuracy | **83.6%** |
| Macro-F1 | **0.719** |
| Majority baseline | 75.4% |
| Improvement | **+8.2 pp** |
| Selective accuracy | **92.9%** |
| Selective coverage | **68.9%** |
| Macro-F1 @ 68.9% | **0.843** |
| CPU latency | ~16 ms / 10 s window |

Same model, three operating points:

| Coverage | Selective acc | Macro-F1 | Cargo→Tanker |
| ---: | ---: | ---: | ---: |
| 100% | 83.6% | 0.719 | 3 |
| 86.9% | 88.7% | 0.760 | 3 |
| 68.9% | 92.9% | 0.843 | 1 |

OOD Passenger/Tug (never trained): AUROC(1−MSP) = **0.611**. The score is a diagnostic. The UNKNOWN **gate is off**.

---

## Run the operator monitor

```bash
pip install -r requirements.txt
python -m uvicorn api.main:app --port 8000
```

```bash
cd web && npm install && npm run dev
```

Open **[http://localhost:3000](http://localhost:3000)** · Replay tracks · Click the orange contact · Upload a WAV.

```bash
python demo/demo.py --audio examples/uncertain.wav
python demo/demo.py --audio examples/cargo.wav
```

```bash
docker compose up --build
```

Checkpoint weights are **not** in this repository. Experiment files under `experiments/` are placeholders.

```python
from src.inference import ContactAnalyzer

clf = ContactAnalyzer()
snap = clf.analyze_file("examples/cargo.wav")
print(snap["track_id"], snap["label"], snap["status"])
```

| Path | Role |
| --- | --- |
| `src/` | CNN, mean-P tracker, UNCERTAIN rule, contact report |
| `api/` | FastAPI — `/contacts`, `/analyze` |
| `web/` | Next.js maritime monitor |
| `examples/` | cargo / tanker / fishing / uncertain (VLIZ, CC-BY-4.0) |

Map geometry is a **demo USV**, not AIS. Classes are Cargo, Tanker, Fishing.

---

## Data

[VLIZ AIS-annotated hydrophone recordings](https://zenodo.org/doi/10.14284/723) (CC-BY-4.0), near-range (&lt; 3 km). Split is group-aware by MMSI.

---

## Language

Use:

> Passive sonar-based vessel-type classification and contact decision support.

Do not write that this detects submarines, runs on a hull, or that **92.9% is overall accuracy**.
