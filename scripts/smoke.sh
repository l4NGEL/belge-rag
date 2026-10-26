#!/usr/bin/env bash
# Calisan stack'e karsi uctan uca duman testi: giris -> belge -> kuyruk/worker -> arama -> SSE sohbet.
# Govde metinleri ASCII: Windows Git Bash komut satiri argumanlarini UTF-8 gondermeyebiliyor.
set -euo pipefail
API="${1:-http://localhost:8081}"

for i in $(seq 1 60); do
  if curl -fsS "$API/health" | grep -q '"status":"Healthy"'; then break; fi
  sleep 2
done
curl -fsS "$API/health"; echo

TOKEN=$(curl -fsS -X POST "$API/api/auth/token" -H 'Content-Type: application/json' \
  -d '{"username":"admin","password":"admin-demo"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
AUTH="Authorization: Bearer $TOKEN"

STATUS_URL=$(curl -fsS -X POST "$API/api/documents" -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"title":"duman","content":"Belge alimi RabbitMQ kuyrugu uzerinden yapilir. Worker servisi parcalari pgvector tablosuna yazar."}' \
  | sed -E 's/.*"statusUrl":"([^"]+)".*/\1/')

for i in $(seq 1 60); do
  S=$(curl -fsS -H "$AUTH" "$API$STATUS_URL")
  echo "$S" | grep -q '"status":"indexed"' && break
  echo "$S" | grep -q '"status":"failed"' && { echo "indeksleme başarısız: $S"; exit 1; }
  sleep 1
done
echo "$S" | grep -q '"status":"indexed"' || { echo "zaman aşımı: $S"; exit 1; }

curl -fsS -H "$AUTH" "$API/api/search?q=pgvector%20worker&topK=1" | grep -q '"documentTitle":"duman"'

OUT=$(curl -fsSN -X POST "$API/api/chat/stream" -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"question":"Belge alimi hangi kuyruk uzerinden yapilir?"}')
echo "$OUT" | grep -q '^event: sources'
echo "$OUT" | grep -q '^event: token'
echo "$OUT" | grep -q '^event: done'
echo "duman testi geçti"
