# Pod

## Basit bir Pod (nginx)

Dosya: [simple-web.yaml](simple-web.yaml)

```bash
kubectl apply -f simple-web.yaml
kubectl get pods
```

Pod'u sil:

```bash
kubectl delete pod simple-web
```

## .NET API'yi Pod olarak çalıştır (Microservice1.API)

Dosya: [microservice1-pod.yaml](microservice1-pod.yaml)

Pod `ecommerce-dev` namespace'inde çalışır. Namespace, ResourceQuota ve LimitRange henüz yoksa önce [namespace](../namespace/namespace.md) dosyasındaki adımları uygula.

### Adım 1: Dockerfile'ın bulunduğu klasöre git

Dockerfile'daki `COPY` yolları `Microservice1.API` klasörüne göre yazıldığı için build bu klasörden çalıştırılmalı.

```bash
cd Microservice1.API
```

### Adım 2: Image'ı build et

Registry kullanmıyoruz, bu yüzden image adında registry öneki yok. `latest` yerine sabit bir tag (`1.0`) kullan. Böylece Kubernetes varsayılan olarak registry'den çekmeye çalışmaz.

```bash
docker build -t microservice1-api:1.0 .
docker images microservice1-api
```

### Adım 3: Image'ı kind cluster'ına ulaştır

kind node'ları kendi içinde ayrı bir container runtime kullanır, bu yüzden bilgisayardaki Docker image'ı node'lar otomatik görmez. Cluster tipine göre iki yol var.

**Docker Desktop (kind modu, bu repodaki cluster):** Node'lar Docker'ın containerd image store'unu paylaşır. Ekstra bir şey yapmana gerek yok, Adım 2'de build ettiğin image node'larda hazırdır.

**Kendi kurduğun kind cluster'ı (`setup-kind.sh`):** Image'ı node'lara yükle. `egitim` yerine cluster adını yaz (`kind get clusters` ile bakabilirsin).

```bash
kind load docker-image microservice1-api:1.0 --name egitim
```

Image'ın node'da olduğunu kontrol et (isteğe bağlı):

```bash
docker exec egitim-worker crictl images | grep microservice1-api
```

### Adım 4: Pod'u oluştur

`imagePullPolicy: IfNotPresent` ayarı sayesinde image node'da varsa registry'ye gidilmez.

```bash
kubectl apply -f kubernetes/week-3/pod/microservice1-pod.yaml
```

### Adım 5: Pod'un çalıştığını kontrol et

`STATUS` sütunu `Running`, `READY` sütunu `1/1` olmalı.

```bash
kubectl get pods -n ecommerce-dev -o wide
kubectl describe pod microservice1-api -n ecommerce-dev
kubectl logs microservice1-api -n ecommerce-dev
```

`ErrImagePull` veya `ErrImageNeverPull` görürsen image node'da yok demektir, Adım 3'ü tekrar kontrol et.

### Adım 6: Dışarıdan erişim için port-forward aç

Pod'a cluster dışından doğrudan erişilemez. `port-forward` yerel `8080` portunu pod'un `8080` portuna yönlendirir. Komut çalıştığı sürece açık kalır, bu yüzden ayrı bir terminalde çalıştır.

```bash
kubectl port-forward pod/microservice1-api 8080:8080 -n ecommerce-dev
```

Yerel port 8080 doluysa farklı bir port seç (örneğin `9090:8080`) ve sonraki adımlarda onu kullan.

### Adım 7: Endpoint'e istek at

Başka bir terminalde:

```bash
curl http://localhost:8080/weatherforecast
```

Beklenen çıktı, 5 günlük rastgele bir hava durumu listesidir (JSON):

```json
[{"date":"2026-10-21","temperatureC":38,"summary":"Sweltering","temperatureF":100}, ...]
```

Tarayıcıdan da açabilirsin:

- http://localhost:8080/scalar/v1 (Scalar API dokümantasyonu)
- http://localhost:8080/openapi/v1.json (OpenAPI şeması)

### Adım 8: Temizle

Port-forward'u `Ctrl+C` ile durdur, sonra pod'u sil.

```bash
kubectl delete pod microservice1-api -n ecommerce-dev
```

## Probe'lı Pod (Health Check)

Dosya: [microservice1-probe-pod.yaml](microservice1-probe-pod.yaml)

Kubernetes, container'ın durumunu probe'larla kontrol eder. `Microservice1.API` için `Program.cs` içine üç health check endpoint'i eklendi:

| Endpoint | Ne kontrol eder | Hangi probe kullanır |
|---|---|---|
| `/health/startup` | Açılış işleri (seed data yükleme) bitti mi? Bitene kadar `503` döner | `startupProbe` |
| `/health/live` | Uygulama süreci cevap veriyor mu? "live" etiketli `self` check'i çalışır, bağımlılık kontrol etmez | `livenessProbe` |
| `/health/ready` | Uygulama trafik almaya hazır mı? "ready" etiketli `database` check'i çalışır | `readinessProbe` |

API, EF Core InMemory (bellek içi veritabanı) kullanır. Uygulama açılırken `DatabaseSeeder` arka planda 5 ürünlük seed data yükler. Yavaş bir açılışı taklit etmek için yüklemeden önce `Startup:SeedDelaySeconds` kadar bekler (pod'da `Startup__SeedDelaySeconds=20` ortam değişkeniyle 20 sn). Bu sürede HTTP sunucusu ayaktadır ama `/health/startup` `503` döner. `startupProbe` olmasa `livenessProbe` bu yavaş açılışı hata sanıp container'ı öldürebilirdi.

`/health/ready` içindeki `database` check'i, `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore` paketinin hazır `AddDbContextCheck<AppDbContext>` check'idir. `DbContext` ile veritabanına bağlanılabildiğini (`CanConnectAsync`) kontrol eder. Bağımlılık check'leri bilerek yalnızca readiness'a eklendi. `/health/live` yalnızca "live" etiketli `self` check'ini çalıştırır. Veritabanı çökünce pod'lar restart edilmesin, sadece trafikten çıksın diye.

Üç probe'un görevi:

| Probe | Başarısız olursa ne olur |
|---|---|
| `startupProbe` | Başarılı olana kadar liveness ve readiness çalışmaz. `failureThreshold x periodSeconds` süresi dolarsa container restart edilir. Yavaş açılan uygulamalar için gereklidir. |
| `livenessProbe` | Container yeniden başlatılır (restart). |
| `readinessProbe` | Pod Service endpoint'lerinden çıkarılır, trafik almaz. Restart edilmez. |

### Adım 1: Health check'li yeni image'ı build et

Kod değiştiği için tag'i `1.6` yap. Aynı tag'i yeniden kullanırsan node'daki eski image kullanılabilir (`imagePullPolicy: IfNotPresent`).

```bash
cd Microservice1.API
docker build -t microservice1-api:1.6 .
```

### Adım 2: Pod'u oluştur

```bash
kubectl apply -f kubernetes/week-3/pod/microservice1-probe-pod.yaml
```

### Adım 3: Probe'ların çalıştığını kontrol et

Pod oluştuktan sonra yaklaşık 20 sn `READY 0/1` kalır (seed data yükleniyor, `startupProbe` henüz başarılı değil). Sonra `1/1` olur, `RESTARTS` sütunu `0` kalır. `describe` çıktısında üç probe'un ayarları görünür.

```bash
kubectl get pod microservice1-probe -n ecommerce-dev -w
kubectl logs microservice1-probe -n ecommerce-dev
kubectl describe pod microservice1-probe -n ecommerce-dev | grep -E 'Liveness|Readiness|Startup'
```

### Adım 4: Endpoint'leri port-forward ile test et

Port-forward'u ayrı bir terminalde çalıştır.

```bash
kubectl port-forward pod/microservice1-probe 8080:8080 -n ecommerce-dev
```

```bash
curl -i http://localhost:8080/health/startup
curl -i http://localhost:8080/health/live
curl -i http://localhost:8080/health/ready
curl http://localhost:8080/products
```

Üç health endpoint'i de `HTTP 200` döner. `/health/ready` yanıtında her check'in durumu görünür:

```json
{"status":"Healthy","checks":{"database":{"status":"Healthy","description":null}}}
``` `/products` seed data ile yüklenen 5 ürünü JSON olarak döner.

### Adım 5: Probe başarısız olunca ne olduğunu gör (isteğe bağlı)

**startupProbe:** `microservice1-probe-pod.yaml` içinde `Startup__SeedDelaySeconds` değerini `"90"` yap (startup bütçesi 30 x 2 = 60 sn). Pod'u silip yeniden oluşturunca `startupProbe` süresi dolar, container `failed startup probe, will be restarted` olayıyla sürekli yeniden başlatılır. Çözüm `failureThreshold` değerini büyütmektir.

**readiness ve liveness:** `microservice1-probe-pod.yaml` içinde `livenessProbe` veya `readinessProbe` altındaki `path` değerini var olmayan bir yola (örneğin `/health/yok`) değiştirip pod'u silip yeniden oluştur.

- **`readinessProbe` yanlışsa:** Pod `Running` olur ama `READY 0/1` kalır, restart olmaz. Event: `Readiness probe failed: HTTP probe failed with statuscode: 404`.
- **`livenessProbe` yanlışsa:** Container sürekli öldürülüp yeniden başlatılır, `RESTARTS` artar ve sonunda `CrashLoopBackOff` olur. Event: `Container ... failed liveness probe, will be restarted`.

Event'leri görmek için:

```bash
kubectl get events -n ecommerce-dev --field-selector reason=Unhealthy
kubectl describe pod microservice1-probe -n ecommerce-dev
```

### Adım 6: Temizle

Port-forward'u `Ctrl+C` ile durdur, sonra pod'u sil.

```bash
kubectl delete pod microservice1-probe -n ecommerce-dev
```
