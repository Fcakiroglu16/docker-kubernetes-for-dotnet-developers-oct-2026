# HorizontalPodAutoscaler (HPA)

HPA, bir ReplicaSet'in Pod sayısını CPU gibi metriklere bakarak otomatik artırır ve azaltır. Yük artınca Pod ekler, yük düşünce Pod siler. Metrikleri **metrics-server** sağlar, bu yüzden HPA'dan önce onu kurmak gerekir.

Tüm komutlar repo kök klasöründen çalıştırılır. Dosyalar:

| Dosya | Görevi |
|---|---|
| [microservice1-replicaset.yaml](microservice1-replicaset.yaml) | HPA'nın ölçekleyeceği ReplicaSet (`replicas` yazılmaz) |
| [microservice1-service.yaml](microservice1-service.yaml) | ReplicaSet'in Pod'larına trafik dağıtan LoadBalancer Service (`http://localhost:8082`) |
| [microservice1-hpa.yaml](microservice1-hpa.yaml) | HPA (min 2, max 6, hedef CPU %50) |
| [microservice1-load-deployment.yaml](microservice1-load-deployment.yaml) | Postman ile yük testi: Deployment + LoadBalancer Service (bkz. [Postman ile CPU ve memory yükü](#postman-ile-cpu-ve-memory-yükü)) |
| [microservice1-load-hpa.yaml](microservice1-load-hpa.yaml) | Postman ile yük testi: HPA (min 2, max 6, hedef CPU %50 ve memory %70) |

> HPA, ölçekleme (`scale`) alt kaynağı olan her nesneyi hedefleyebilir: ReplicaSet, Deployment, StatefulSet. Bu örnekte ReplicaSet kullanılır. Deployment konusuna geçildiğinde aynı HPA'da yalnızca `scaleTargetRef.kind` ve `name` değişir.

## Adım 1: metrics-server'ı kur

Cluster'da metrics-server yoksa `kubectl top` ve HPA çalışmaz. HPA'nın hedefi `<unknown>` görünür. Önce kurulu mu diye bak:

```bash
kubectl top nodes
```

`error: Metrics API not available` görürsen aşağıdaki adımlarla kur. Tablo (node'ların CPU ve memory değerleri) görürsen kurulu demektir, Adım 2'ye geç.

### 1.1 Resmi manifest'i uygula

Sürümü sabitle (`latest` kullanma). Manifest ServiceAccount, RBAC kuralları, Service, Deployment ve `v1beta1.metrics.k8s.io` APIService nesnelerini `kube-system` namespace'inde oluşturur.

```bash
kubectl apply -f https://github.com/kubernetes-sigs/metrics-server/releases/download/v0.9.0/components.yaml
```

### 1.2 Pod'un neden Ready olmadığını gör

Birkaç saniye bekle ve Pod'a bak. `READY` sütunu `0/1` kalır:

```bash
kubectl get pods -n kube-system -l k8s-app=metrics-server
```

APIService de hazır değildir (`AVAILABLE` sütunu `False (MissingEndpoints)`):

```bash
kubectl get apiservice v1beta1.metrics.k8s.io
```

Log'a bak:

```bash
kubectl logs -n kube-system deploy/metrics-server --tail=3
```

Şuna benzer bir hata görürsün:

```
Failed to scrape node ... tls: failed to verify certificate: x509: cannot validate certificate for 172.18.0.5 because it doesn't contain any IP SANs
```

Sebep: metrics-server her node'un kubelet'ine (`:10250`) HTTPS ile bağlanıp metrik çeker. kind'da kubelet sertifikası node IP'sini içermez, bu yüzden doğrulama başarısız olur.

### 1.3 `--kubelet-insecure-tls` bayrağını ekle

Bayrak, kubelet sertifikasının doğrulanmasını kapatır. Deployment'ın container argümanlarına ekliyoruz:

```bash
kubectl patch deployment metrics-server -n kube-system --type=json \
  -p '[{"op":"add","path":"/spec/template/spec/containers/0/args/-","value":"--kubelet-insecure-tls"}]'
```

> Bu bayrak yalnızca yerel/eğitim ortamı içindir, production'da kullanılmaz. Komutu **bir kez** çalıştır. Tekrar çalıştırırsan bayrak argüman listesine ikinci kez eklenir.

### 1.4 Yeni Pod'un hazır olmasını bekle

Patch Deployment'ı yeniden başlatır. Rollout bitene kadar bekle:

```bash
kubectl rollout status deployment/metrics-server -n kube-system --timeout=180s
```

Bayrağın eklendiğini kontrol et (argümanların sonunda `--kubelet-insecure-tls` görünmeli):

```bash
kubectl get deployment metrics-server -n kube-system -o jsonpath='{.spec.template.spec.containers[0].args}'
```

### 1.5 Metrics API'nin hazır olduğunu doğrula

`AVAILABLE` sütunu `True` olmalı:

```bash
kubectl get apiservice v1beta1.metrics.k8s.io
```

### 1.6 Metrikleri oku

Metrikler ilk scrape'ten sonra gelir (`--metric-resolution=15s`). İlk çağrıda hata alırsan 15-30 sn bekleyip tekrar dene:

```bash
kubectl top nodes
kubectl top pods -A
```

### metrics-server'ı kaldırmak istersen

```bash
kubectl delete -f https://github.com/kubernetes-sigs/metrics-server/releases/download/v0.9.0/components.yaml
```

## Adım 2: Hazırlık

Namespace, ResourceQuota ve LimitRange yoksa önce [namespace.md](../../week-3/namespace/namespace.md) adımlarını uygula. Image'ı build et (Docker Desktop kind modunda node'lar image'ı otomatik görür, kendi kind cluster'ında ayrıca `kind load docker-image` gerekir, bkz. [pod.md](../../week-3/1-pod/pod.md)):

```bash
docker build -t microservice1-api:1.0 Microservice1.API
```

## Adım 3: ReplicaSet, Service ve HPA'yı oluştur

```bash
kubectl apply -f kubernetes/week-4/hpa/microservice1-replicaset.yaml
kubectl apply -f kubernetes/week-4/hpa/microservice1-service.yaml
kubectl apply -f kubernetes/week-4/hpa/microservice1-hpa.yaml
```

Service, `app: microservice1-hpa` label'lı tüm Pod'lara trafik dağıtır. HPA yeni Pod açtığında Pod Ready olunca Service'e otomatik eklenir. `EXTERNAL-IP` dolduktan sonra `http://localhost:8082` ile erişilir:

```bash
kubectl get svc microservice1-hpa-svc -n ecommerce-dev
kubectl get endpointslices -n ecommerce-dev -l kubernetes.io/service-name=microservice1-hpa-svc
```

> Readiness probe `/health/ready` kullanır ve Redis'e de bakar. Redis kurulu değilse Pod Ready olmaz ve Service'ten trafik alamaz.

ReplicaSet `replicas` yazılmadığı için 1 Pod ile başlar, HPA `minReplicas` değerine (2) çıkarır:

```bash
kubectl get rs,pods,hpa -n ecommerce-dev
```

İlk 30-60 sn `TARGETS` sütununda `cpu: <unknown>/50%` görünebilir. Pod'ların readiness'i bitmeden ve ilk metrik gelmeden HPA hesap yapamaz. Beklemek yeterli:

```bash
kubectl get hpa microservice1-hpa -n ecommerce-dev
```

Boştayken `TARGETS` düşüktür (örneğin `cpu: 8%/50%`) ve `REPLICAS` `2` kalır.

### HPA nasıl karar verir?

```
istenenPod = ceil( mevcutPod x (mevcutOrtalamaCPU / hedefCPU) )
```

- Yüzde, Pod'un `requests.cpu` değerine göre hesaplanır. Request `100m` ve hedef `%50` ise Pod başına `50m` kullanım hedeflenir. Bu yüzden Pod şablonunda `requests` **zorunludur**.
- Örnek: 2 Pod, ortalama `%200` -> `ceil(2 x 200 / 50) = 8`. `maxReplicas: 6` olduğu için 6 olur.
- `behavior.scaleUp`: her 15 sn'de en fazla 2 Pod ekler. `behavior.scaleDown`: yük düştükten 60 sn sonra, her 30 sn'de en fazla 1 Pod siler. Böylece Pod sayısı sürekli inip çıkmaz (flapping).
- ReplicaSet'e sabit `replicas` yazılırsa her `kubectl apply` HPA'nın ayarladığı sayıyı ezer. Bu yüzden yazılmadı.

## Adım 4: Yük ver ve ölçeklendiğini izle

Henüz Service konusuna geçilmediği için dışarıdan istekle yük vermiyoruz. Bunun yerine Pod'ların **içinde** CPU harcayan bir döngü başlatıyoruz. Döngü `timeout 240` ile 240 sn sonra kendiliğinden biter, ayrıca durdurmana gerek yoktur.

Bir terminalde HPA'yı canlı izle:

```bash
kubectl get hpa microservice1-hpa -n ecommerce-dev -w
```

Başka bir terminalde Pod'ları izle:

```bash
kubectl get pods -n ecommerce-dev -w
```

Üçüncü terminalde her Pod'un içinde bir CPU döngüsü başlat:

```bash
for p in $(kubectl get pod -n ecommerce-dev -l app=microservice1-hpa -o name); do
  kubectl exec -n ecommerce-dev $p -- sh -c 'nohup timeout 240 sh -c "while :; do :; done" >/dev/null 2>&1 &'
done
```

Yaklaşık 15-30 sn sonra `TARGETS` hedefin çok üstüne çıkar ve `REPLICAS` artar. Testte şu sıra görüldü: `2 -> 3 -> 5 -> 6`. Her Pod CPU limitine (`500m`) dayandığı için ortalama %500'e kadar çıktı.

```bash
kubectl top pods -n ecommerce-dev
```

Yük **yalnızca ilk 2 Pod'da** vardır, yeni açılan Pod'lar boştur. Bu yüzden 6 Pod olunca ortalama yaklaşık `%170`'e düşer ama hedefin (%50) hâlâ üstündedir ve 6'da kalır. Gerçek bir uygulamada trafik Service ile tüm Pod'lara dağıldığı için ortalama Pod sayısı arttıkça hedefe doğru iner.

Neden ve ne zaman ölçeklendiğini olaylardan oku:

```bash
kubectl describe hpa microservice1-hpa -n ecommerce-dev
```

## Adım 5: Yük bitince küçülmeyi izle

240 sn sonra döngüler biter. `TARGETS` birkaç saniyede `%1-5` seviyesine iner. `scaleDown.stabilizationWindowSeconds: 60` nedeniyle HPA yaklaşık 1 dk bekler, sonra 30 sn arayla birer Pod siler. Testte `6 -> 5 -> 4 -> 3 -> 2` yaklaşık 1,5 dk sürdü. `minReplicas: 2` altına inmez.

```bash
kubectl get hpa microservice1-hpa -n ecommerce-dev -w
```

> Varsayılan scale-down bekleme süresi 300 sn'dir (5 dk). Eğitimde beklemeyi kısaltmak için 60 sn yapıldı. Production'da kısa tutmak Pod sayısının sık dalgalanmasına yol açabilir.

Döngüyü erken durdurmak istersen Pod'lardaki `timeout` süreçlerini sonlandır:

```bash
for p in $(kubectl get pod -n ecommerce-dev -l app=microservice1-hpa -o name); do
  kubectl exec -n ecommerce-dev $p -- pkill -f "while :" || true
done
```

## Komutla HPA oluşturmak (alternatif)

YAML yerine hızlı bir yol. `behavior` gibi ayarlar için YAML gerekir. Önce YAML ile oluşturduğun HPA'yı sil, sonra:

```bash
kubectl delete -f kubernetes/week-4/hpa/microservice1-hpa.yaml
kubectl autoscale rs microservice1-hpa-rs --cpu=50% --min=2 --max=6 -n ecommerce-dev
kubectl get hpa -n ecommerce-dev
```

## Temizle

HPA'yı, Service'i ve ReplicaSet'i (Pod'larıyla birlikte) sil. metrics-server cluster'da kalır, sonraki örneklerde de kullanılır.

```bash
kubectl delete -f kubernetes/week-4/hpa/microservice1-hpa.yaml --ignore-not-found
kubectl delete hpa microservice1-hpa-rs -n ecommerce-dev --ignore-not-found
kubectl delete -f kubernetes/week-4/hpa/microservice1-service.yaml
kubectl delete -f kubernetes/week-4/hpa/microservice1-replicaset.yaml
```

---

# Postman ile CPU ve memory yükü

Yukarıdaki örnekte yük `kubectl exec` ile Pod'un içinden verildi ve yalnızca ilk Pod'lar yük aldı. Bu bölümde gerçek hayattaki gibi **HTTP isteği** ile yük verilir: Postman'den atılan istekler Service üzerinden tüm Pod'lara dağılır, HPA'nın açtığı yeni Pod'lar da yükü paylaşır. CPU ve memory yükü ayrı endpoint'lerle verilir, böylece her metriğin ölçeklemeyi tek başına tetiklediği görülür.

## Microservice1'deki yük endpoint'leri

Kod: [LoadGenerator.cs](../../../Microservice1.API/LoadGenerator.cs) ve [Program.cs](../../../Microservice1.API/Program.cs).

| Endpoint | Ne yapar |
|---|---|
| `GET /load/cpu?ms=200` | İstek süresince (`ms`) bir çekirdeği meşgul eder. İstek bitince CPU hemen düşer |
| `GET /load/memory?mb=20&seconds=60` | `mb` kadar veriyi bir `MemoryStream`'e yazar, `seconds` sn tutar, sonra bırakır |
| `DELETE /load/memory` | İsteğin düştüğü Pod'daki tüm belleği hemen bırakır |
| `GET /load/status` | Pod adı, tutulan bellek ve working set |

Her yanıtta `pod` alanı vardır; isteğin hangi Pod'a gittiği görünür.

Memory endpoint'inde dikkat edilenler:

- `MemoryStream`'e veri **yazılır**. Yazılmadan ayrılan bellek fiziksel olarak kullanılmaz ve HPA'nın baktığı **working set** metriğine yansımaz.
- Süre dolunca stream silinir ve `GC.Collect(..., GCCollectionMode.Aggressive, ...)` çağrılır. Normal GC boşalan belleği işletim sistemine hemen geri vermeyebilir; o zaman working set yüksek kalır ve HPA **hiç küçülmez**.
- Pod OOMKilled olmasın diye Pod başına en fazla 250 MB tutulur. Aşan istek `429 Too Many Requests` döner. Yük testinde 429 görmek normaldir: o Pod dolu demektir.

## Adım 1: Image'ı build et

metrics-server kurulu olmalı (bkz. Adım 1). Yük endpoint'leri `7.0` image'ındadır:

```bash
docker build -t microservice1-api:7.0 Microservice1.API
```

## Adım 2: Deployment, Service ve HPA'yı oluştur

```bash
kubectl apply -f kubernetes/week-4/hpa/microservice1-load-deployment.yaml
kubectl apply -f kubernetes/week-4/hpa/microservice1-load-hpa.yaml
kubectl get deploy,pods,svc -n ecommerce-dev -l app=microservice1-load
kubectl get svc microservice1-load-svc -n ecommerce-dev
kubectl get hpa microservice1-load-hpa -n ecommerce-dev
```

Service tipi `LoadBalancer`'dır. Docker Desktop (kind modu) bunu bilgisayarın `localhost` adresine bağlar: `EXTERNAL-IP` dolduktan sonra Postman'den `http://localhost:8081` ile erişilir. NodePort'a Docker Desktop'ta bilgisayardan erişilemez (bkz. [service.md](../Service/service.md)); `port-forward` ise tek bir Pod'a bağlanır ve yükü dağıtmaz, HPA testi için uygun değildir.

HPA iki metrik kullanır: CPU (`requests.cpu` 100m'nin %50'si) ve memory (`requests.memory` 256Mi'nin %70'i). Birden fazla metrik varsa HPA her biri için ayrı Pod sayısı hesaplar ve **en büyüğünü** seçer. Küçülme ise ancak iki metrik de hedefin altına inince olur.

Boşta şuna benzer görünür:

```
NAME                     REFERENCE                       TARGETS                         MINPODS   MAXPODS   REPLICAS
microservice1-load-hpa   Deployment/microservice1-load   cpu: 4%/50%, memory: 20%/70%    2         6         2
```

> Readiness probe bu örnekte `/health/live` kullanır. `/health/ready` Redis'e de bakar; Redis kurulu değilse Pod hiç Ready olmaz ve Service'ten trafik alamaz.

Bağlantıyı test et (yanıtlardaki `pod` alanı farklı Pod adları göstermeli):

```bash
curl http://localhost:8081/load/status
```

## Adım 3: İzleme terminalleri

Testler boyunca iki terminal açık kalsın:

```bash
kubectl get hpa microservice1-load-hpa -n ecommerce-dev -w
kubectl get pods -n ecommerce-dev -l app=microservice1-load -w
```

Pod başına kullanım için arada `kubectl top pods -n ecommerce-dev -l app=microservice1-load` çalıştır.

## Adım 4: CPU yükü (Postman)

Postman'de bir collection oluştur ve şu isteği ekle:

```
GET http://localhost:8081/load/cpu?ms=200
```

Collection'ı **Run** ile aç, **Performance** sekmesini seç: `Virtual users: 10`, `Test duration: 3 mins`, `Load profile: Fixed`. **Run** ile başlat.

> Birden fazla virtual user kullan. Tek bağlantı (keep-alive) üzerinden giden istekler hep aynı Pod'a düşebilir; o zaman yük dağılmaz ve yeni Pod'lar boş kalır. Yanıtlardaki `pod` alanının değiştiğini kontrol et.

Testte görülen (memory sabit kaldı, ölçeklemeyi yalnızca CPU tetikledi):

```
t=0s    cpu: 139%/50%, memory: 38%/70%   replicas=2
t=15s   cpu: 311%/50%, memory: 28%/70%   replicas=4
t=30s   cpu: 343%/50%, memory: 25%/70%   replicas=6
```

6 Pod olunca `kubectl top` yükün **tüm Pod'lara** dağıldığını gösterir (her biri yaklaşık 250-350m). Ortalama hâlâ %50'nin üstünde olduğu için `maxReplicas: 6`'da kalır.

Testi durdur (veya süresinin bitmesini bekle). CPU yaklaşık 30 sn içinde `%1-2`'ye iner (metrics-server ortalaması nedeniyle biraz gecikir). `scaleDown.stabilizationWindowSeconds: 60` nedeniyle HPA 1 dk bekler, sonra 30 sn arayla birer Pod siler. Testte `6 -> 5 -> 4 -> 3 -> 2` yük bittikten yaklaşık 2,5 dk sonra tamamlandı.

## Adım 5: Memory yükü (Postman)

HPA `2` replikaya döndükten sonra collection'a şu isteği ekle (CPU isteğini kaldır veya ayrı collection kullan):

```
GET http://localhost:8081/load/memory?mb=20&seconds=60
```

Collection'ı **Run** ile aç, **Functional** sekmesinde `Iterations: 150`, `Delay: 1000 ms` ver ve başlat. Saniyede 20 MB x 60 sn = cluster genelinde yaklaşık 1200 MB tutulan bellek. CPU neredeyse hiç harcanmaz. (Performance sekmesi de kullanılabilir; istekler çok hızlı gittiği için Pod'lar hemen 250 MB sınırına dolar ve çoğu istek 429 döner.)

Testte görülen (CPU %7-15'te kaldı, ölçeklemeyi yalnızca memory tetikledi):

```
t=0s    cpu: 7%/50%,  memory: 20%/70%    replicas=2
t=30s   cpu: 15%/50%, memory: 68%/70%    replicas=2
t=40s   cpu: 14%/50%, memory: 117%/70%   replicas=2    <- iki Pod da 250 MB sınırında, istekler 429 dönmeye başlar
t=61s   cpu: 11%/50%, memory: 77%/70%    replicas=4
t=91s   cpu: 11%/50%, memory: 88%/70%    replicas=4
t=101s  cpu: 11%/50%, memory: 68%/70%    replicas=6
```

Dikkat edilecekler:

- `memory: 74%/70%` gibi hedefe yakın değerlerde ölçekleme olmaz. HPA'nın **%10 toleransı** vardır: oran 1,1'i (`%77`) aşmadan Pod eklemez, 0,9'un (`%63`) altına inmeden silmez.
- Memory, CPU gibi anında dağılmaz. Eski Pod'ların tuttuğu bellek süresi dolana kadar orada kalır; yeni Pod'lar yalnızca yeni istekleri alır. Bu yüzden memory ile ölçekleme CPU'ya göre daha kademelidir.
- Gerçek uygulamalarda .NET bellek boşalsa bile working set hemen düşmeyebilir. Memory metriğiyle ölçeklenen bir uygulama küçülmeyebilir. Production'da genellikle CPU veya istek sayısı (custom metric) tercih edilir.

Runner'ı durdur (veya 150 isteğin bitmesini bekle). Tutulan bellek en geç 60 sn içinde bırakılır (beklemek istemezsen `DELETE /load/memory` isteğini birkaç kez gönder; her istek düştüğü Pod'u boşaltır). Testte memory yük bittikten yaklaşık 70 sn sonra `%85`'ten `%25`'e indi, 60 sn bekleme sonrası `6 -> 5 -> 4 -> 3 -> 2` küçülme yük bittikten yaklaşık 3,5 dk sonra tamamlandı.

Ölçekleme olaylarını ve nedenlerini oku:

```bash
kubectl describe hpa microservice1-load-hpa -n ecommerce-dev
```

## Temizle

```bash
kubectl delete -f kubernetes/week-4/hpa/microservice1-load-hpa.yaml
kubectl delete -f kubernetes/week-4/hpa/microservice1-load-deployment.yaml
```
