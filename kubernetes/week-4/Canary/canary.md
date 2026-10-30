# Canary Deployment

Canary, yeni sürümü **önce trafiğin küçük bir kısmına** açar. Sorun çıkmazsa payı kademeli artırır, sonunda tüm trafiği yeni sürüme taşır. Sorun çıkarsa yalnızca küçük bir kullanıcı grubu etkilenir ve canary kapatılır. İsim, madenlerde zehirli gaz uyarısı için kullanılan kanaryadan gelir.

[Blue/Green](../BlueGreen/bluegreen.md)'den farkı: orada iki ortam arasında tek hamlede geçiş yapılır. Canary'de iki sürüm **bir süre birlikte** trafik alır ve oran adım adım değişir.

Bu doküman [Deployment](../Deployment/deployment.md) ve [Service](../Service/service.md) konularına dayanır. Tüm komutlar repo kök klasöründen çalıştırılır. Dosyalar:

| Dosya | Görevi |
|---|---|
| [stable-deployment.yaml](stable-deployment.yaml) | Kararlı sürüm: `microservice1-api:1.0`, 3 Pod, `track: stable` |
| [canary-deployment.yaml](canary-deployment.yaml) | Yeni sürüm: `microservice1-api:1.1`, 1 Pod, `track: canary` |
| [service.yaml](service.yaml) | `canary-svc`: **her iki** Deployment'ın Pod'larını seçer |
| [client-pod.yaml](client-pod.yaml) | Cluster içinden istek atmak için `busybox` Pod |

## Nasıl çalışır?

```
                                  ┌─> app-stable  (3 Pod, 1.0)  ~%75
istemci ──> canary-svc (app=canary-api) ─┤
                                  └─> app-canary  (1 Pod, 1.1)  ~%25
```

- İki Deployment'ın Pod'ları **aynı `app: canary-api` label'ını** taşır, `track` label'ı ise farklıdır (`stable` / `canary`).
- Service'in `selector`'ü yalnızca `app: canary-api` içerir (`track` yazılmaz), bu yüzden **ikisinin Pod'larını birlikte** seçer.
- Service trafiği Pod'lara eşit olasılıkla dağıttığı için **canary payı = canary Pod sayısı / toplam Pod sayısı**. 3 stable + 1 canary yaklaşık %25 canary demektir.
- Oranı değiştirmek için Pod sayıları değiştirilir: canary'yi artır, stable'ı azalt.

## Hazırlık

Namespace, ResourceQuota ve LimitRange yoksa önce [namespace.md](../../week-3/namespace/namespace.md) adımlarını uygula. Image'ı build et (Docker Desktop kind modunda node'lar image'ı otomatik görür, kendi kind cluster'ında ayrıca `kind load docker-image` gerekir, bkz. [pod.md](../../week-3/1-pod/pod.md)). Yeni sürümü temsil etmek için aynı image'a ikinci bir tag ver:

```bash
docker build -t microservice1-api:1.0 Microservice1.API
docker tag microservice1-api:1.0 microservice1-api:1.1
```

Hangi sürümün cevap verdiğini görmek için Deployment'lar `App__Mode` ortam değişkenini `stable` / `canary` olarak ayarlar. Uygulamanın `GET /config` endpoint'i bunu `mode` alanında döndürür, koda dokunmaya gerek yoktur.

Aşağıdaki adımlarda kullanılacak yardımcı komut. İstek sayısını verirsin, hangi sürümün kaç isteğe cevap verdiğini sayar:

```bash
cnt() { kubectl exec client -n ecommerce-dev -- sh -c "for i in \$(seq $1); do wget -qO- -T 3 http://canary-svc/config 2>/dev/null || echo '{\"mode\":\"HATA\"}'; echo; done" | sed 's/.*"mode":"\([^"]*\)".*/\1/' | sort | uniq -c | tr '\n' ' '; echo; }
```

> **Quota notu:** `ecommerce-dev` quota'sında `limits.ephemeral-storage: 10Gi` sınırı vardır ve LimitRange her container'a varsayılan olarak `1Gi` verir. Açık değer yazılmazsa namespace'e **en fazla 10 Pod** sığar. 10 Pod'a ulaşınca yeni Pod `exceeded quota` ile reddedilir ve Deployment `Progressing` kalmaz. Bu yüzden Canary YAML'larında `ephemeral-storage` açıkça (`128Mi` / `256Mi`) yazıldı.

## Adım 1: Stable'ı ve %25 canary'yi kur

```bash
kubectl apply -f kubernetes/week-4/Canary/stable-deployment.yaml
kubectl apply -f kubernetes/week-4/Canary/canary-deployment.yaml
kubectl apply -f kubernetes/week-4/Canary/service.yaml
kubectl apply -f kubernetes/week-4/Canary/client-pod.yaml
kubectl rollout status deploy/app-stable -n ecommerce-dev
kubectl rollout status deploy/app-canary -n ecommerce-dev
kubectl wait --for=condition=Ready pod/client -n ecommerce-dev
```

Service'in 4 Pod'u (3 stable + 1 canary) tek listede tuttuğunu göster:

```bash
kubectl get pods -n ecommerce-dev -l app=canary-api -L track -o wide
kubectl get endpointslices -n ecommerce-dev
```

200 istek at ve dağılıma bak:

```bash
cnt 200
```

Testte 3 stable + 1 canary için iki turda `51 canary / 149 stable` ve `46 canary / 154 stable` çıktı, yani canary payı yaklaşık **%25**.

> Dağılım rastgeledir, birkaç yüzlük örnekte oran birkaç puan sapar. Oranı değerlendirmek için en az 200 istek at ve Pod'lar yeni açıldıysa birkaç saniye bekle. İlk denememizde 3:1 için beklenmedik şekilde `140 canary / 60 stable` çıktı. Aynı ayar tekrar ölçülünce normale döndü (`35`, `55`, `53` canary / 200), bu yüzden tek bir ölçüme güvenme.

## Adım 2: Canary payını kademeli artır

Her adımda önce canary'yi artır, Ready olmasını bekle, sonra stable'ı azalt. Böylece toplam kapasite düşmez:

```bash
kubectl scale deploy/app-canary --replicas=2 -n ecommerce-dev
kubectl rollout status deploy/app-canary -n ecommerce-dev
kubectl scale deploy/app-stable --replicas=2 -n ecommerce-dev
cnt 200
```

Sonraki adımlar için `canary=3 stable=1` ve `canary=4 stable=0` değerleriyle aynı komutları tekrarla.

Testte (her adım iki tur, 200 istek):

| stable | canary | Beklenen canary payı | Ölçülen canary (turlar) |
|---|---|---|---|
| 9 | 1 | %10 | 19, 24 (%10-12) |
| 3 | 1 | %25 | 51, 46 (%23-25) |
| 2 | 2 | %50 | 97, 98 (%49) |
| 1 | 3 | %75 | 163, 143 (%72-82) |
| 0 | 4 | %100 | 200, 200 (%100) |

Ölçülen değerler beklenen orana yakındır, rastgele sapmalar normaldir. Her adımda izleme/metrik/hata oranına bakıp devam edip etmeyeceğine karar verirsin. Gerçek bir canary sürecinde bu kararı genelde hata oranı ve gecikme gibi ölçümler belirler.

**Daha küçük pay** (örneğin %10) için daha çok Pod gerekir: 9 stable + 1 canary. Pod sayısı arttıkça oran inceleşir ama kaynak maliyeti de artar.

## Adım 3: Terfi (promote)

Canary sağlıklıysa yeni sürüm tüm trafiği alır (`stable=0 canary=4`). Sonraki sürümde roller yer değiştirir: kararlı sürümün image'ı yeni sürüme güncellenir ve canary kapatılır. Kalıcı olarak YAML'da `app-stable` image'ını `1.1` yap ve canary'yi sıfırla:

```bash
kubectl set image deploy/app-stable microservice1-api=microservice1-api:1.1 -n ecommerce-dev
kubectl scale deploy/app-canary --replicas=0 -n ecommerce-dev
```

> `app-stable` image'ı değişince Deployment'ın kendi rolling update'i devreye girer ve Pod'ları kademeli olarak yeniler. Terfiyi YAML üzerinden yapmak (`image` satırını değiştirip `kubectl apply`) hem kayıt altına alınır hem tekrarlanabilir.

## Geri alma (rollback)

Canary'de sorun görürsen canary'yi kapat ve stable'ı eski kapasiteye çıkar. Stable hiç silinmediyse geri alma anidir:

```bash
kubectl scale deploy/app-stable --replicas=4 -n ecommerce-dev
kubectl scale deploy/app-canary --replicas=0 -n ecommerce-dev
cnt 100
```

Testte 100 isteğin hepsi `stable` cevap verdi.

## Hatalı canary kullanıcıyı etkilemez mi?

Service yalnızca **Ready** Pod'lara trafik gönderir. Canary Pod açılmıyor veya Ready olmuyorsa trafik almaz, kullanıcılar yalnızca stable ile devam eder. Bunu denemek için canary'ye var olmayan bir image ver:

```bash
kubectl set image deploy/app-canary microservice1-api=microservice1-api:9.9 -n ecommerce-dev
kubectl scale deploy/app-canary --replicas=1 -n ecommerce-dev
kubectl get pods -n ecommerce-dev -l app=canary-api -L track
kubectl get endpointslices -n ecommerce-dev -o jsonpath='{range .items[*].endpoints[*]}{.addresses[0]}{" ready="}{.conditions.ready}{"\n"}{end}'
cnt 200
```

Testte canary Pod `ImagePullBackOff` durumunda kaldı, endpoint'i `ready=false` oldu ve 200 isteğin **hepsi** stable'a gitti, hata yoktur. Bu, canary'nin güvenlik ağıdır: açılış sorunu yaşayan bir sürüm hiç trafik almaz.

> Bu koruma yalnızca **Pod'un açılmasını ve readiness probe'unu** kapsar. Pod Ready olup yanlış sonuç döndürüyorsa (mantık hatası) Service bunu bilemez, o durumu izleme/metrik ile yakalamak gerekir.

## Sınırlar

- **Oran Pod sayısına bağlıdır.** %1 gibi ince bir pay için yüzlerce Pod gerekir. Bunun için genellikle trafiği ağırlıkla bölebilen bir Ingress controller veya service mesh kullanılır (bu doküman kapsamında değildir).
- **Kullanıcı bazlı yönlendirme yoktur.** Her istek bağımsız ve rastgele bir Pod'a gider. "Şu kullanıcılar hep canary'ye" demek için başlık/çerez bazlı yönlendirme gerekir. `sessionAffinity: ClientIP` ile aynı istemci hep aynı Pod'a gönderilebilir (bkz. [service.md](../Service/service.md)), ama yine de pay Pod sayısıyla belirlenir.
- **İki sürüm birlikte çalışır.** Veritabanı ve API değişiklikleri hem eski hem yeni sürümle uyumlu olmalıdır.

## Blue/Green, Rolling Update ve Canary

| | Rolling update | Blue/Green | Canary |
|---|---|---|---|
| Geçiş | Kademeli, kontrolsüz oran | Tek hamle | Kademeli, **kontrollü oran** |
| Yeni sürümü az sayıda kullanıcıyla deneme | Hayır | Hayır (yalnızca preview) | **Evet** |
| Geri alma | Yeni rollout | Anında (selector) | Anında (canary'yi kapat) |
| Ek kaynak | `maxSurge` kadar | 2 kat | Canary Pod'ları kadar |
| Karmaşıklık | Düşük (yerleşik) | Orta | Orta (elle yönetilir) |

## Temizle

```bash
kubectl delete -f kubernetes/week-4/Canary/
docker rmi microservice1-api:1.1
```
