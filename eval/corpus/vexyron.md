<div align="center">

![VEXYRON — Security Validation Platform](docs/banner.svg)

**Kendi uygulamanı production'a çıkmadan önce tara — kanıtlı, tekrarlanabilir bir sonuçla.**

![Python](https://img.shields.io/badge/python-3.10%2B-3776AB?logo=python&logoColor=white)
![Flask](https://img.shields.io/badge/flask-3.0-000000?logo=flask&logoColor=white)
![Deploy](https://img.shields.io/badge/deploy-%25100%20local-6d28d9)
![Platform](https://img.shields.io/badge/platform-windows%20%7C%20linux%20%7C%20macos-64748b)
![Status](https://img.shields.io/badge/status-aktif%20geliştirme-059669)

</div>

Kendi kendini test eden bir uygulama laboratuvarı: kasıtlı olarak güvenlik açığı barındıran bir web uygulaması + mobil backend API + mock APK (**vulnerable-app**) ve bunları tarayan bir tester aracı (**scanner**) — hem güvenlik (SQLi/XSS/BOLA/mobil vb.) hem smoke test hem de yük/performans testi yapıp **production'a hazır mı, değil mi** diye kanıtlanabilir bir karar verir.

Amaç: web (OWASP Top 10 tarzı), mobil backend API ve mobil APK statik analizini + ağ/port taramasını + yük/stres testini uçtan uca, gerçek bir hedef üzerinde göstermek. Fikir: **bu aracı production'a deploy etmeden önce çalıştırıp**, "sorun yok" ya da "şurada, kanıtıyla birlikte sorun var" diyen somut bir rapor almak.

## Ekran Görüntüleri

<table>
<tr><td>

![Hedefini Tara](docs/screenshot-scan.png)
<p align="center"><sub>Tarama formu — profil seç, kapsam anında görünür, tek tıkla başlat.</sub></p>

</td></tr>
<tr><td>

![Sonuç sayfası](docs/screenshot-results.png)
<p align="center"><sub>Sonuç sayfası — production-readiness verdict'i + kanıtlı bulgular.</sub></p>

</td></tr>
</table>

## İçindekiler

- [Öne Çıkan Özellikler](#öne-çıkan-özellikler)
- [Etik ve Yasal Uyarı](#etik-ve-yasal-uyarı)
- [Yapı](#yapı)
- [Hızlı Başlangıç](#hızlı-başlangıç)
- [Production Readiness Verdict](#production-readiness-verdict)
- [Smoke Test](#smoke-test)
- [Yük / Stres Testi](#yük--stres-testi)
- [scanner CLI seçenekleri](#scanner-cli-seçenekleri)
- [Sonuç: Kaç açık yakalandı?](#sonuç-kaç-açık-yakalandı)

## Öne Çıkan Özellikler

| | |
|---|---|
| **Kapsamlı tarama** | Web (SQLi, XSS, CSRF, IDOR, broken access control, komut enjeksiyonu...), mobil backend/JSON API, mobil APK statik analiz, ağ/port taraması, veritabanı servis maruziyeti |
| **Kanıtlı bulgular** | Her bulgu gönderilen payload + alınan yanıtla gelir — "sorun var" demez, neden öyle düşündüğünü gösterir |
| **Production-readiness verdict** | Severity'ye göre otomatik NOT READY / READY WITH WARNINGS / READY kararı + CI/CD için process exit code |
| **Kontrol grubu ile doğrulanmış** | `vulnerable-app` içinde bilerek güvenli bırakılmış eşleniklerle scanner'ın gerçekten ayrım yaptığı kanıtlanır |
| **Smoke test** | Ağır taramadan önce saniyeler süren "hedef ayakta mı" kontrolü |
| **Yük/stres testi** | Aşamalı (ramp) senaryolar + geçti/kaldı eşikleri — harici bir binary gerekmeden |
| **3 rapor formatı** | HTML (göz atmak için), PDF (paylaşmak için), JSON (CI/CD, makine-okunur) |
| **Bulgu inceleme/kabul akışı** | Gerekçeli "kabul edilmiş risk" / "false positive" işaretleme — bulgu kaybolmaz, sadece verdict'ten çıkar |
| **İki arayüz, tek motor** | webapp ve CLI aynı `run_full_scan()` fonksiyonunu çağırır — biri diğerinden geride kalmaz |

## Etik ve Yasal Uyarı

- `scanner`, **yalnızca kendi izniniz olan sistemleri** taramak için tasarlanmıştır.
- Varsayılan olarak sadece `localhost` / private IP aralıklarını (127.0.0.1, 10.x.x.x, 192.168.x.x, 172.16-31.x.x) tarar. Başka bir hedefi taramaya çalışırsanız `safety.py` bunu reddeder; bilinçli olarak `--i-authorize-this-scan` bayrağını geçmeniz gerekir.
- İzniniz olmayan sistemlere (üçüncü taraf siteler, işvereninize ait olmayan sunucular vb.) karşı bu aracı **kullanmayın** — yetkisiz tarama birçok ülkede suçtur.
- `vulnerable-app` gerçek veri içermez, sadece demo amaçlı sahte kullanıcı/ürün/secret verisiyle gelir.

## Yapı

```
pentest-lab/
├── vulnerable-app/
│   ├── app.py, database.py, templates/, static/   # HEDEF: kasıtlı açık barındıran Flask uygulaması + JSON API
│   ├── mobile-decoded-apk/                        # HEDEF: apktool-decode çıktısını taklit eden demo mobil paket
│   └── VULNERABILITIES.md                         # 22 açığın "cevap anahtarı" + scanner'ın tespit sonucu
├── scanner/
│   ├── core/orchestrator.py   # tek tarama motoru -- hem CLI hem webapp bunu cagirir
│   ├── checks/     # web + API (mobil backend) açık kontrolleri
│   ├── mobile/     # APK statik analiz (manifest + hardcoded secret tarama)
│   ├── network/    # port taraması
│   ├── loadtest/   # yük/stres testi (throughput, gecikme p50/p95/p99, hata oranı, aşamalı/ramp, eşik)
│   ├── smoketest/  # ağır taramadan önce hızlı "ayakta mı" kontrolü
│   ├── report/     # console / HTML / PDF / JSON rapor + production-readiness verdict
│   └── scanner_cli.py         # komut satırı arayüzü
├── webapp/
│   ├── app.py                 # tarayıcı arayüzü: URL yapıştır, Tara'ya bas, sonucu gör
│   └── templates/, static/
├── docs/           # bu README'deki banner + ekran görüntüleri
└── reports/        # scanner çıktıları (html/pdf/json) burada oluşur
```

`scanner_cli.py` ve `webapp/app.py`, aynı tarama motorunu (`scanner/core/orchestrator.py:run_full_scan`) çağırır — iki farklı arayüz, tek bir tarama mantığı. Biri diğerinden geride kalmaz.

## Hızlı Başlangıç

**1) Hedef uygulamayı ayağa kaldır**

```bash
cd vulnerable-app
pip install -r requirements.txt
python run.py
```

`http://127.0.0.1:5000` adresinde çalışmaya başlar. Demo hesaplar için [vulnerable-app/VULNERABILITIES.md](vulnerable-app/VULNERABILITIES.md) dosyasına bakın.

**2) Taramayı çalıştır — üç yoldan biriyle**

**a) `.exe` — Python kurmadan, tek dosya**

[Releases](../../releases) sayfasından `VEXYRON.exe`'yi indir, herhangi bir klasöre koy, çift tıkla. Sunucu başlar ve tarayıcı otomatik açılır; `reports/` klasörü exe'nin yanında oluşur. `vexyron.spec` ile kendin de derleyebilirsin: `pip install pyinstaller && pyinstaller vexyron.spec` (webapp'i paketler; `vulnerable-app` kasıtlı olarak dahil değil, o kaynak koddan `python run.py` ile çalıştırılır).

**b) Tarayıcıdan (webapp, kaynak koddan)**

```bash
cd webapp
pip install -r requirements.txt
python app.py
```

`http://127.0.0.1:5059` adresini aç (port çakışırsa Ayarlar sayfasından veya `PENTEST_LAB_WEBAPP_PORT` ortam değişkeniyle değiştirin), hedef URL'yi yapıştır, **Tara**'ya bas. "Gelişmiş seçenekler" altında login bilgisi, ikinci (düşük yetkili) hesap, ağ taraması, mobil APK yolu, smoke test ve yük testi (aşamalı/ramp + eşik) gibi ayarlara ulaşabilirsin. Sonuç sayfasında verdict banner'ı + tüm bulgular + HTML/PDF/JSON rapor indirme linkleri görünür.

**c) Komut satırından (CLI) — otomasyon/CI için**

```bash
cd scanner
pip install -r requirements.txt
python scanner_cli.py --url http://127.0.0.1:5000 --scan-network --host 127.0.0.1 \
  --apk-path ../vulnerable-app/mobile-decoded-apk \
  --login-path /login --username admin --password admin123 \
  --second-username ayse --second-password ayse2024 \
  --output ../reports/demo
```

webapp (exe veya kaynak koddan, ikisi de aynı kod) ve CLI aynı şeyi yapar: web tarama, mobil backend/API tarama, mobil APK statik analizi, ağ/port taraması ve broken-access-control testi (düşük yetkili `ayse` hesabıyla).

**3) Raporu incele**

- webapp: sonuç sayfasında doğrudan, veya "HTML/PDF/JSON rapor indir" linkleriyle
- CLI: konsolda en üstte büyük bir **PRODUCTION READINESS** banner'ı + severity'ye göre gruplanmış bulgu tablosu
- Her iki yolda da `reports/<isim>/report.html` (göz atmak için), `report.pdf` (paylaşmak için) ve `report.json` (makine-okunur, CI/CD için — üst seviye `"verdict"` alanı içerir) üretilir

> **Windows / Git Bash notu:** Git Bash, `/login` gibi baştan `/` ile başlayan argümanları otomatik olarak bir Windows dosya yoluna çevirebilir (MSYS path mangling) ve CLI bayraklarını bozabilir. PowerShell veya `cmd.exe` kullanmanız, ya da Git Bash'te `MSYS_NO_PATHCONV=1` ortam değişkenini kullanmanız önerilir. webapp bu sorunu yaşamaz (tarayıcıdan form ile çalışır).

## Production Readiness Verdict

`scanner`, tüm bulguları severity'ye göre toplayıp otomatik bir karar üretir:

| Durum | Anlamı | Process exit code |
|---|---|:---:|
| **NOT READY FOR PRODUCTION** | En az bir Critical/High bulgu var | `1` |
| **READY WITH WARNINGS** | Sadece Medium bulgu(lar) var | `0` |
| **READY FOR PRODUCTION** | Sadece Low/Info veya hiç bulgu yok | `0` |

Non-zero exit code, bu aracı doğrudan bir CI/CD pipeline'ında **"production'a deploy etmeden önce çalıştır, kırmızıysa dur"** şeklinde bir gate olarak kullanmayı mümkün kılar. Her bulgu, iddiayı "kanıtlanabilir" kılan somut bir `evidence` alanı taşır (gönderilen payload, alınan HTTP durumu, yanıttaki DB hata mesajı vb.) — sadece "burada sorun var" demez, neden öyle düşündüğünü gösterir.

Yanlış pozitifleri azaltmak için: `sensitive_files` kontrolü ve mobil/API endpoint keşfi, taramaya başlamadan önce bilerek var olmayan rastgele bir path'e istek atıp bir "soft-404" referansı alır (`core/baseline.py`). Bir SPA/catch-all uygulama her bilinmeyen path'e aynı HTTP 200 sayfasını dönüyorsa, bu referansla eşleşen yanıtlar bulgu olarak sayılmaz — sadece gerçekten farklı/var olan kaynaklar raporlanır.

### Bulguları inceleyip kabul etme (suppression)

Her bulgu deterministik bir `id` taşır (aynı açık = aynı id, taramalar arası kalıcı). Bir bulguyu gözden geçirip "kabul edilmiş risk" veya "false positive" olarak işaretlemek için, `--accepted-findings <dosya.json>` ile şu formatta bir dosya verin:

```json
[
  {
    "finding_id": "7d4d3055b895",
    "disposition": "accepted_risk",
    "reason": "Gerekçe buraya -- zorunlu, bos birakilamaz.",
    "reviewed_by": "isminiz",
    "reviewed_at": "2026-08-18"
  }
]
```

(`vulnerable-app/accepted_findings.json` çalışan bir örnektir.) Eşleşen bulgular raporda **kaybolmaz** — "İncelenmiş / Kabul Edilmiş" bölümünde, gerekçesiyle birlikte gösterilmeye devam eder, sadece verdict hesabından çıkar. Gerekçesiz bir kayıt (`reason` boşsa) sessizce yok sayılır. Yani: gerçek, incelenmemiş bir Critical/High bulgu her zaman verdict'i NOT READY'de tutar — bu dosya sonucu asla "her şey yeşil" yapamaz, sadece bilinçli olarak gözden geçirilmiş şeyleri ayırır.

### Kontrol grubu (yönteme güvenmek için)

Scanner'ın gerçekten ayrım yapıp yapmadığını (her "admin" geçen path'i kör kör işaretlemediğini) test edebilmen için `vulnerable-app`'e `/admin` ile birebir aynı görünen ama **gerçekten düzgün korunan** bir `/admin-secure` eklendi (bkz. `app.py:admin_secure()`). Taramada `/admin` bulgu üretir, `/admin-secure` üretmez — ikisini de kodundan ve `ayse` hesabıyla elle deneyerek doğrulayabilirsin. Detay: [vulnerable-app/VULNERABILITIES.md](vulnerable-app/VULNERABILITIES.md#kontrol-grubu--bilerek-güvenli-bunlar-açık-değil).

## Smoke Test

Ağır güvenlik taramasından **önce** çalışan, saniyeler süren hafif bir "hedef ayakta mı" kontrolü (`--smoke-test` / webapp'te "Smoke test" kutusu). Verdiğin path listesine (varsayılan: sadece `/`) sırayla istek atar, her biri için beklenen HTTP koduyla (verilmediyse "5xx değil") karşılaştırıp geçti/kaldı sonucu üretir. Bulgu/verdict sistemine dahil edilmez, taramanın geri kalanını durdurmaz — sadece "bu hedefe tarama başlatmaya değer mi" sorusuna hızlı bir cevaptır.

```bash
python scanner_cli.py --url http://127.0.0.1:5000 --smoke-test \
  --smoke-target / --smoke-target "/login,200"
```

## Yük / Stres Testi

Güvenlik taramasından **ayrı, opsiyonel** bir adım (`--load-test` / webapp'te "Yük testi" kutusu) — hedefe belirtilen süre boyunca eşzamanlı istek göndererek throughput (istek/sn), gecikme dağılımı (p50/p95/p99) ve hata oranını ölçer.

```bash
python scanner_cli.py --url http://127.0.0.1:5000 \
  --login-path /login --username admin --password admin123 \
  --load-test --load-test-path /search --load-test-users 20 --load-test-duration 15
```

Aşamalı (ramp) yük ve geçti/kaldı eşikleri de desteklenir — k6'nın `stages` mantığının step-wise karşılığı, harici bir binary gerekmeden:

```bash
python scanner_cli.py --url http://127.0.0.1:5000 --load-test --load-test-path /search \
  --load-test-stage "10,10" --load-test-stage "50,20" --load-test-stage "10,10" \
  --load-test-threshold-p95-ms 500 --load-test-threshold-error-pct 5
```

- **Varsayılan olarak kapalı** — bilinçli opt-in gerekir, güvenlik taramasının bir parçası değildir.
- **Aynı yetki kontrolüne tabidir** — `safety.py`'deki localhost/private-IP kısıtlaması burada da geçerli, çünkü yanlış kullanılırsa gerçek bir DoS'a dönüşebilir.
- **Sert üst sınırlar var** (`scanner/loadtest/runner.py`): eşzamanlılık en fazla 200, süre en fazla 120 saniye, en fazla 5 aşama — yanlışlıkla aşırı yük vermeyi engellemek için.
- **Hedef path'i önce doğrular** — bir tek ön-kontrol isteği atıp 404 dönerse yükü hiç başlatmadan durur ("%100 hata" gibi yanıltıcı bir sonuç yerine, açıkça "path yanlış" der).
- Login yapıldıysa (cookie veya JWT bearer token), yük testi de **aynı oturumla** çalışır — authenticated bir endpoint'in yük altındaki davranışını da ölçebilirsin.
- Sonuç, güvenlik bulgularından ayrı bir "Yük Testi Sonucu" bölümünde raporlanır (console/HTML/PDF/JSON'da), verdict hesabını etkilemez.

## scanner CLI seçenekleri

| Bayrak | Açıklama |
|---|---|
| `--url` | Taranacak hedef web uygulaması (zorunlu) |
| `--scan-network` | Ağ/port taramasını da çalıştır |
| `--host` | Port taraması için hedef host (varsayılan: `--url`'den çıkarılır) |
| `--no-api` | Mobil backend/JSON API taramasını atla |
| `--api-base` | API taraması için baz URL (varsayılan: `--url` + `/api`) |
| `--openapi` | API keşfi için bir OpenAPI/Swagger JSON spec dosyası |
| `--apk-path` | Mobil statik analiz için decode edilmiş APK klasörü (veya `androguard` kuruluysa ham `.apk`) |
| `--login-path` / `--username` / `--password` | IDOR/CSRF/admin gibi auth gerektiren kontroller için giriş bilgisi |
| `--login-json` / `--login-username-field` / `--login-extra` | Login body'sini JSON olarak gönder, kullanıcı adı alanının adını özelleştir (ör. `email`), sabit ek alanlar ekle (ör. `{"login_type":"staff"}`) — JWT/modern API'ler için |
| `--second-username` / `--second-password` | Broken-access-control kanıtı için DÜŞÜK yetkili ikinci bir hesap |
| `--accepted-findings` | İncelenip "kabul edilmiş risk"/"false positive" işaretlenmiş bulguların olduğu JSON dosyası (bkz. yukarıdaki bölüm) |
| `--smoke-test` | Ağır taramadan ÖNCE hedefin ayakta olup olmadığını hızlıca kontrol et |
| `--smoke-target` | Smoke test için bir path, `PATH[,BEKLENEN_STATUS]` (birden fazla kez verilebilir; varsayılan: `/`) |
| `--load-test` | Güvenlik taramasından sonra yük/stres testi de çalıştır (varsayılan kapalı) |
| `--load-test-path` / `--load-test-method` | Yük testi için path (varsayılan: `--url`'in kendisi) ve HTTP metodu (varsayılan GET) |
| `--load-test-users` / `--load-test-duration` | Eşzamanlı worker sayısı (varsayılan 10, üst sınır 200) ve süre saniye (varsayılan 15, üst sınır 120) |
| `--load-test-stage` | Aşamalı/ramp yük testi için bir aşama, `ESZAMANLI,SURE` (birden fazla kez verilebilir, en fazla 5; verilirse `--load-test-users`/`--load-test-duration` yok sayılır) |
| `--load-test-threshold-p95-ms` / `--load-test-threshold-error-pct` | Yük testi için geçti/kaldı eşiği: maks p95 gecikme (ms) ve maks hata oranı (%) |
| `--output` | Rapor çıktı klasörü (varsayılan: `reports/<timestamp>`) |
| `--i-authorize-this-scan` | Localhost/private IP dışındaki hedefler için zorunlu onay bayrağı |
| `--max-pages` | Crawler'ın en fazla gezeceği sayfa sayısı (varsayılan 30) |
| `--delay` | İstekler arası gecikme, hedefe nazik olmak için |

## Sonuç: Kaç açık yakalandı?

`vulnerable-app` içine 22 açık kasıtlı olarak yerleştirildi (12 web + 5 mobil backend API + 5 mobil APK statik analiz). Son uçtan uca test çalıştırmasında **scanner bunların 22'sini de otomatik olarak tespit etti** (artı açık port taraması gibi birkaç bonus bulgu). Detaylı liste ve her birinin kanıtı için [vulnerable-app/VULNERABILITIES.md](vulnerable-app/VULNERABILITIES.md) dosyasına bakın.

---

<div align="center"><sub>VEXYRON yerel, tek kullanıcılı bir araçtır — bulut hesabı, telemetri veya harici API çağrısı yoktur.</sub></div>
