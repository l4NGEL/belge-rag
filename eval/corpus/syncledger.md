<p align="center">
  <img src="./assets/icon.png" width="120" alt="SyncLedger uygulama ikonu" />
</p>

<h1 align="center">SyncLedger</h1>
<p align="center"><b>Offline-first gelir/gider takip uygulaması</b></p>

React Native + TypeScript ile yazılmış, gerçek bir bulut backend'ine (Firebase Firestore) bağlı, offline'ken de tam işlevsel çalışan bir gelir/gider takip uygulaması. Amaç basit bir CRUD listesi değil; **offline-first mimari, gerçek zamanlı çoklu cihaz senkronu, outbox kuyruğu ve optimistic concurrency ile çakışma çözümleme** gibi senior seviye mühendislik problemlerini uçtan uca göstermek.

## Neden bu proje?

Çoğu portfolyo uygulaması "API'den veri çek, listele" seviyesinde kalıyor. SyncLedger bunun yerine gerçek dünyada mobil uygulamaların karşılaştığı zor problemi çözüyor: **kullanıcı offline'ken yaptığı değişiklikler, başka bir cihazdan gelen değişikliklerle çakışırsa ne olur?** Ve: **iki farklı cihaz (telefon + web) aynı veriye nasıl güvenle erişir?**

## Mimari

```
                     ┌─────────────────────┐
                     │   Firebase Firestore │
                     │  (transactions koll.) │
                     └──────────┬───────────┘
                        push ▲     ▼ onSnapshot (pull)
                              │     │
        ┌─────────────────────┴─────┴─────────────────────┐
        │                    Redux state                   │
        │        (AsyncStorage'a debounce ile persist)      │
        └─────────────────────┬──────────────────────────┬─┘
                               │                          │
                     kullanıcı işlemi              başka cihazdan
                   (ekle/güncelle/sil)              gelen değişiklik
                        status: pending          status: synced/conflict
```

- **Push yönü** (`src/sync/syncEngine.ts`): outbox pattern — `pending` durumundaki kayıtları FIFO sırayla Firestore'a gönderir. Ağ hatasında kalan kuyruğu bir sonraki senkrona bırakır (crash-safe).
- **Pull yönü** (`src/sync/useRemoteSync.ts`): Firestore'u `onSnapshot` ile gerçek zamanlı dinler. Başka bir cihazdan gelen her değişikliği local state'e uygular — bu olmadan cihazlar birbirinin verisini asla göremez.

### Optimistic concurrency control
Her kayıt bir `version` alanı taşır. Bir kaydı gönderirken bizim bildiğimiz `syncedVersion`, Firestore'daki güncel versiyonla uyuşmuyorsa — demek ki kayıt aramızda başka bir cihazdan değişmiş — **conflict** döner (`src/sync/firestoreServer.ts`, Firestore transaction içinde atomik kontrol).

### Çakışma çözümleme
`ConflictModal` üç strateji sunar:
- **Yerel sürümü tut** — sunucu versiyonunu benimseyip kendi değerlerimizle üzerine yaz
- **Sunucu sürümünü kabul et** — uzak değerleri al
- **Birleştir** — örnek strateji: tutarları topla (gerçek uygulamada alan bazlı 3-way merge de yapılabilir)

## Demo akışı (uygulama içinde)
- Sağ üstteki switch ile **çevrimdışı modu** aç/kapat
- Çevrimdışıyken işlem ekle → "Bekliyor" durumunda kuyrukta kalır
- Tekrar çevrimiçi yap → otomatik senkronlanır
- Bir işleme dokunup düzenle, "Uzaktan değişikliği simüle et" ile başka bir cihazın aynı kaydı değiştirdiğini taklit et → bir sonraki senkronda gerçek bir çakışma oluşur
- **Gerçek çoklu cihaz testi**: aynı anda telefon (Expo Go) ve web'de (`npm run web`) aç, birinde eklediğin işlem birkaç saniye içinde diğerinde de görünür

## Teknoloji
- React Native (Expo, SDK 54) + TypeScript
- Redux Toolkit — local state + outbox durumu
- **Firebase Firestore** — gerçek bulut backend, `onSnapshot` ile realtime senkron
- AsyncStorage — offline-first local persistans
- NetInfo — gerçek bağlantı algılama
- Jest — reducer ve sync engine testleri (17 test, çakışma + pull-sync senaryoları dahil; Firestore jest mock ile taklit edilir, testler gerçek ağa bağımlı değildir)

## Klasör yapısı
```
src/
  app/            # store, typed hooks
  features/       # transactions slice (local state + conflict resolution)
  sync/           # firestoreServer, syncEngine (push), useRemoteSync (pull), networkSlice
  screens/        # HomeScreen
  components/     # TransactionRow, AddTransactionModal, ConflictModal, SyncBadge
```

## Çalıştırma
```bash
npm install
npm run android   # veya npm run ios / npm run web
```

Kendi Firebase projenle bağlamak istersen `src/sync/firebaseConfig.ts` içindeki config'i değiştir ve Firestore Rules'ta `transactions` koleksiyonuna okuma/yazma izni ver.

## Test
```bash
npx jest
npx tsc --noEmit
```

## Güvenlik notu
Bu bir portfolyo/demo projesidir; Firestore kuralları kimlik doğrulama olmadan açık okuma/yazmaya izin verir. Üretim uygulamasında Firebase Auth ile kullanıcı bazlı erişim kısıtlaması eklenmesi gerekir.
