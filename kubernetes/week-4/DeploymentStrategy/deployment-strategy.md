# Deployment Strategy

Deployment'ın Pod şablonu değişince eski Pod'ların yeni sürümle nasıl değiştirileceğini **strateji** belirler. Seçim, güncelleme sırasında kesinti olup olmayacağını, ne kadar ek kaynak gerekeceğini ve güncellemenin ne kadar süreceğini değiştirir.

Önce [deployment.md](../Deployment/deployment.md) konusunu oku. Bu dokümanda `RollingUpdate`'in ayarları ve `Recreate` aynı uygulama üzerinde yan yana karşılaştırılır. Tüm komutlar repo kök klasöründen çalıştırılır. Dosyalar:

| Dosya | Strateji | `maxSurge` / `maxUnavailable` |
|---|---|---|
| [strategy-rolling-default.yaml](strategy-rolling-default.yaml) | RollingUpdate (varsayılan değerler) | `25%` / `25%` |
| [strategy-rolling-zero-downtime.yaml](strategy-rolling-zero-downtime.yaml) | RollingUpdate (kesintisiz) | `1` / `0` |
| [strategy-rolling-fast.yaml](strategy-rolling-fast.yaml) | RollingUpdate (hızlı, 2 kat kaynak) | `100%` / `0` |
| [strategy-rolling-inplace.yaml](strategy-rolling-inplace.yaml) | RollingUpdate (ek kaynak yok) | `0` / `1` |
| [strategy-recreate.yaml](strategy-recreate.yaml) | Recreate | yok |

Hepsi aynı uygulamayı çalıştırır: 4 replika, `microservice1-api:1.0`, açılışta 10 sn seed bekleme (yeni Pod'un Ready olması zaman alsın diye). Tek fark `strategy` bölümüdür.

## İki strateji türü

Kubernetes yalnızca iki yerleşik strateji sunar:

- **RollingUpdate** (varsayılan): Pod'lar kademeli değiştirilir. Hızı ve kesinti riski `maxSurge` ve `maxUnavailable` ile ayarlanır.
- **Recreate**: Önce tüm eski Pod'lar silinir, sonra yenileri açılır.

> Blue/Green ve Canary gibi stratejiler Deployment'ın yerleşik özelliği değildir. Trafiği sürümler arasında yönlendirmek için Service (veya Ingress) gerekir. Bu konular Service anlatıldıktan sonra ele alınır.

## RollingUpdate: iki ayar

| Ayar | Anlamı |
|---|---|
| `maxSurge` | Güncelleme sırasında istenen `replicas` sayısının **üstüne** açılabilecek en fazla Pod. Fazla kaynak maliyetini belirler |
| `maxUnavailable` | Güncelleme sırasında **kullanılamaz** olabilecek en fazla Pod. Kapasite kaybını (kesintiyi) belirler |

- İkisi de sayı (`1`) veya yüzde (`25%`) olabilir. Yüzdeler `replicas` sayısına göre hesaplanır: `maxSurge` yukarı, `maxUnavailable` aşağı yuvarlanır. 4 Pod için `25%` = `1` ve `1`'dir.
- Varsayılan her ikisi için de `25%`'tir.
- İkisi aynı anda `0` olamaz. Ek Pod açılmaz ve hiçbiri kapatılamazsa rollout ilerleyemez. API bunu reddeder: `maxUnavailable: may not be 0 when maxSurge is 0`.
- `Recreate` stratejisinde `rollingUpdate` alanı yazılamaz. API bunu da reddeder.

İlerleme her zaman **readiness probe**'a bağlıdır. Yeni Pod Ready olmadan "kullanılabilir" sayılmaz, bu yüzden uygulamanın açılma süresi güncelleme süresini doğrudan etkiler.

## Hazırlık

Namespace, ResourceQuota ve LimitRange yoksa önce [namespace.md](../../week-3/namespace/namespace.md) adımlarını uygula. Image'ı build et (Docker Desktop kind modunda node'lar image'ı otomatik görür, kendi kind cluster'ında ayrıca `kind load docker-image` gerekir, bkz. [pod.md](../../week-3/1-pod/pod.md)):

```bash
docker build -t microservice1-api:1.0 Microservice1.API
docker tag microservice1-api:1.0 microservice1-api:1.1
```

> `ecommerce-dev` quota'sında `limits.cpu` toplamı `4`'tür. Bu örneklerde Pod başına limit `250m` yapıldı, böylece `fast` stratejisinin geçici 8 Pod'u (8 x `250m` = `2` çekirdek) sığar. Birden fazla örneği aynı anda çalıştırma, her denemeden sonra temizle.

## Her stratejiyi dene

Her strateji için adımlar aynıdır. Önce oluştur ve ilk rollout'u bekle:

```bash
kubectl apply -f kubernetes/week-4/DeploymentStrategy/strategy-rolling-zero-downtime.yaml
kubectl rollout status deploy/strategy-zero-downtime -n ecommerce-dev
```

Bir terminalde Pod'ları izle:

```bash
kubectl get pods -n ecommerce-dev -w
```

Başka bir terminalde image'ı güncelle ve ilerlemeyi izle:

```bash
kubectl set image deploy/strategy-zero-downtime microservice1-api=microservice1-api:1.1 -n ecommerce-dev
kubectl rollout status deploy/strategy-zero-downtime -n ecommerce-dev
```

Güncelleme sırasında anlık sayıları görmek için:

```bash
kubectl get deploy strategy-zero-downtime -n ecommerce-dev
```

`READY`, `UP-TO-DATE` ve `AVAILABLE` sütunları ilerlemeyi gösterir. Denemeden sonra temizle:

```bash
kubectl delete -f kubernetes/week-4/DeploymentStrategy/strategy-rolling-zero-downtime.yaml
```

Diğer stratejiler için dosya ve Deployment adını değiştir: `strategy-default`, `strategy-fast`, `strategy-inplace`, `strategy-recreate` (dosya adları tablodaki gibidir).

## Sonuçlar

Aynı koşullarda (4 replika, 10 sn seed bekleme, `1.0 -> 1.1`) her stratejiyi güncelleyip saniyede bir ölçtük. Değerler bu cluster'a aittir, süreler yaklaşık olarak yorumlanmalıdır:

| Strateji | `maxSurge` / `maxUnavailable` | Rollout süresi | En çok çalışan Pod | En az hazır Pod | Kesinti |
|---|---|---|---|---|---|
| `strategy-zero-downtime` | `1` / `0` | ~62 sn | 5 | **4** | Yok |
| `strategy-fast` | `100%` / `0` | ~16 sn | **8** | **4** | Yok |
| `strategy-default` | `25%` / `25%` | ~31 sn | 5 | 3 | Yok (kapasite %75'e düşer) |
| `strategy-inplace` | `0` / `1` | ~63 sn | 4 | 3 | Yok (kapasite %75'e düşer) |
| `strategy-recreate` | yok | ~18 sn | 4 | **0** | **~15 sn tam kesinti** |

Nasıl okunur:

- **`zero-downtime` (1/0):** Her adımda 1 yeni Pod açılır, Ready olunca 1 eski Pod silinir. 4 adım x ~15 sn (10 sn seed + readiness) = ~62 sn. En az hazır Pod hiç 4'ün altına inmez. Bedeli yavaşlıktır ve 1 Pod'luk geçici ek kaynak.
- **`fast` (100%/0):** 4 yeni Pod aynı anda açılır (toplam 8 Pod). Hepsi Ready olunca 4 eski Pod birden silinir. Süre tek bir Pod'un açılma süresi kadardır (~16 sn) ve yine kesinti yoktur. Bedeli geçici olarak **2 kat kaynak**: quota veya node kapasitesi yetmezse Pod'lar `Pending` kalır.
- **`default` (25%/25%):** Aynı anda hem 1 Pod fazladan açılır hem 1 Pod kapatılabilir, yani 2 Pod paralel değişir. Bu yüzden `zero-downtime`'ın yaklaşık yarı süresinde biter. Bedeli kapasitenin kısa süre 4'ten 3'e düşmesidir.
- **`inplace` (0/1):** Fazladan Pod açılmaz, her adımda önce 1 eski Pod kapatılır, sonra yenisi açılır. Ek kaynak gerektirmez ama kapasite süresince 3'te kalır ve süre `zero-downtime` kadar uzundur.
- **`recreate`:** 4 eski Pod aynı anda `Terminating` olur, 4 yeni Pod başlar. Yeni Pod'lar yaklaşık 15 sn Ready olmadığı için o sürede hizmet veren Pod yoktur. Süre kısa (~18 sn) ama kesinti vardır.

## Hangisini seçmeli?

| Durum | Strateji |
|---|---|
| Çoğu web API'si, kesinti istenmiyor | `RollingUpdate` `maxUnavailable: 0` + küçük `maxSurge` (`1`) |
| Kesinti yok ve hızlı güncelleme gerekli, kaynak bol | `RollingUpdate` `maxSurge: 100%`, `maxUnavailable: 0` |
| Ek kaynak yok (quota/node dolu), kapasite kaybı kabul edilebilir | `RollingUpdate` `maxSurge: 0`, `maxUnavailable: 1` |
| Eski ve yeni sürüm aynı anda çalışamaz (örneğin veritabanı şeması uyumsuz) | `Recreate` |
| Hızlı, basit, kesinti önemsiz (geliştirme ortamı) | Varsayılan değerler veya `Recreate` |

> Rolling update sırasında eski ve yeni sürüm bir süre **birlikte** çalışır. API ve veritabanı değişikliklerinin geriye dönük uyumlu olması gerekir. Bu mümkün değilse `Recreate` kullanılır.

## Temizle

Denemelerden sonra kalan Deployment'ları ve ek image tag'ini sil:

```bash
kubectl delete -f kubernetes/week-4/DeploymentStrategy/
docker rmi microservice1-api:1.1
```
