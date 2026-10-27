# HorizontalPodAutoscaler (HPA)

HPA, bir ReplicaSet'in Pod sayısını CPU gibi metriklere bakarak otomatik artırır ve azaltır. Yük artınca Pod ekler, yük düşünce Pod siler. Metrikleri **metrics-server** sağlar, bu yüzden HPA'dan önce onu kurmak gerekir.

Tüm komutlar repo kök klasöründen çalıştırılır. Dosyalar:

| Dosya | Görevi |
|---|---|
| [microservice1-replicaset.yaml](microservice1-replicaset.yaml) | HPA'nın ölçekleyeceği ReplicaSet (`replicas` yazılmaz) |
| [microservice1-hpa.yaml](microservice1-hpa.yaml) | HPA (min 2, max 6, hedef CPU %50) |

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

## Adım 3: ReplicaSet ve HPA'yı oluştur

```bash
kubectl apply -f kubernetes/week-4/hpa/microservice1-replicaset.yaml
kubectl apply -f kubernetes/week-4/hpa/microservice1-hpa.yaml
```

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

HPA'yı ve ReplicaSet'i (Pod'larıyla birlikte) sil. metrics-server cluster'da kalır, sonraki örneklerde de kullanılır.

```bash
kubectl delete -f kubernetes/week-4/hpa/microservice1-hpa.yaml --ignore-not-found
kubectl delete hpa microservice1-hpa-rs -n ecommerce-dev --ignore-not-found
kubectl delete -f kubernetes/week-4/hpa/microservice1-replicaset.yaml
```
