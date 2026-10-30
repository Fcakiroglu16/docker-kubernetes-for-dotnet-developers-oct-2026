# Blue/Green Deployment

Blue/Green, yeni sürümü **canlı sürümün yanında, ayrı bir ortam olarak** kurar. Yeni ortam hazır ve test edilmiş olunca trafik tek hamlede ona çevrilir. Sorun çıkarsa trafik tek hamlede eskisine geri alınır.

- **Blue:** Şu an canlı olan sürüm.
- **Green:** Yeni sürüm. Kuruldu ama henüz trafik almıyor.

[Rolling update](../DeploymentStrategy/deployment-strategy.md)'ten farkı: iki sürüm kademeli karışmaz. Bir anda yalnızca biri canlıdır, geçiş ve geri alma anidir.

Bu doküman [Deployment](../Deployment/deployment.md) ve [Service](../Service/service.md) konularına dayanır. Tüm komutlar repo kök klasöründen çalıştırılır. Dosyalar:

| Dosya | Görevi |
|---|---|
| [blue-deployment.yaml](blue-deployment.yaml) | Canlı sürüm: 3 Pod, `microservice1-api:1.0`, `version: blue` |
| [green-deployment.yaml](green-deployment.yaml) | Yeni sürüm: 3 Pod, `microservice1-api:1.1`, `version: green` |
| [service-live.yaml](service-live.yaml) | **Canlı adres** (`bluegreen-svc`). `selector` hangi ortamın canlı olduğunu belirler |
| [service-preview.yaml](service-preview.yaml) | Green'i canlıya almadan test etmek için ayrı adres (`bluegreen-preview`) |
| [client-pod.yaml](client-pod.yaml) | Cluster içinden istek atmak için `busybox` Pod |

## Nasıl çalışır?

```
                  ┌─ selector: version=blue ──> app-blue  (3 Pod)   <- şu an canlı
bluegreen-svc ────┤
                  └─ selector: version=green ─> app-green (3 Pod)   <- geçişten sonra canlı

bluegreen-preview ── selector: version=green ──> app-green          <- her zaman green (test için)
```

- İki ortam **aynı `app` label'ına**, farklı `version` label'ına sahiptir.
- Canlı Service'in `selector`'ündeki `version` değeri trafiğin nereye gittiğini belirler. **Geçiş, bu tek satırın değişmesidir.** Pod'lar yeniden başlatılmaz, bu yüzden geçiş anidir.
- Service adı ve IP'si değişmez, istemciler hiçbir şey fark etmez.

## Hazırlık

Namespace, ResourceQuota ve LimitRange yoksa önce [namespace.md](../../week-3/namespace/namespace.md) adımlarını uygula. Image'ı build et (Docker Desktop kind modunda node'lar image'ı otomatik görür, kendi kind cluster'ında ayrıca `kind load docker-image` gerekir, bkz. [pod.md](../../week-3/1-pod/pod.md)). Yeni sürümü temsil etmek için aynı image'a ikinci bir tag ver:

```bash
docker build -t microservice1-api:1.0 Microservice1.API
docker tag microservice1-api:1.0 microservice1-api:1.1
```

> İki ortam birlikte çalıştığı için geçici olarak **2 kat kaynak** gerekir (6 Pod). `ecommerce-dev` quota'sına sığar.

Hangi ortamın cevap verdiğini görmek için Deployment'lar `App__Mode` ortam değişkenini ortam adına (`blue` / `green`) ayarlar. Uygulamanın `GET /config` endpoint'i bunu `mode` alanında döndürür, koda dokunmaya gerek yoktur.

Bu komut, aşağıdaki adımlarda kullanılacak ve istek sayılarını gösterecek kısa bir yardımcıdır (Service adını ve istek sayısını verirsin):

```bash
cnt() { kubectl exec client -n ecommerce-dev -- sh -c "for i in \$(seq $2); do wget -qO- -T 3 http://$1/config 2>/dev/null || echo '{\"mode\":\"HATA\"}'; echo; done" | sed 's/.*"mode":"\([^"]*\)".*/\1/' | sort | uniq -c; }
```

## Adım 1: Blue'yu canlıya al

```bash
kubectl apply -f kubernetes/week-4/BlueGreen/blue-deployment.yaml
kubectl apply -f kubernetes/week-4/BlueGreen/service-live.yaml
kubectl apply -f kubernetes/week-4/BlueGreen/client-pod.yaml
kubectl rollout status deploy/app-blue -n ecommerce-dev
kubectl wait --for=condition=Ready pod/client -n ecommerce-dev
```

20 istek at. Hepsi `blue` cevap vermeli:

```bash
cnt bluegreen-svc 20
```

## Adım 2: Green'i canlıya ALMADAN kur ve test et

Green Deployment'ı ve preview Service'ini oluştur. Canlı Service'in selector'ü hâlâ `blue` olduğu için canlı trafik etkilenmez:

```bash
kubectl apply -f kubernetes/week-4/BlueGreen/green-deployment.yaml
kubectl apply -f kubernetes/week-4/BlueGreen/service-preview.yaml
kubectl rollout status deploy/app-green -n ecommerce-dev
```

İki ortamın Pod'larını ve hangi Service'in hangi Pod'lara baktığını göster:

```bash
kubectl get pods -n ecommerce-dev -l app=bluegreen-api -L version -o wide
kubectl get svc -n ecommerce-dev -o wide
```

Canlı adres hâlâ blue'ya, preview adres ise green'e gider:

```bash
cnt bluegreen-svc 20
cnt bluegreen-preview 20
```

Testte canlı adres `20 blue`, preview adres `20 green` verdi. Gerçek bir sürümde burada yeni sürümün testlerini (duman testi, entegrasyon testi) **preview** adresine karşı çalıştırırsın. Kullanıcılar bu sırada hâlâ blue'yu kullanır.

## Adım 3: Geçiş: trafiği green'e çevir

Geçişten önce **green'in tamamen Ready olduğundan emin ol** (`kubectl get deploy` çıktısında `3/3`). Sonra canlı Service'in selector'ünü değiştir:

```bash
kubectl patch svc bluegreen-svc -n ecommerce-dev -p '{"spec":{"selector":{"version":"green"}}}'
```

Hemen kontrol et:

```bash
cnt bluegreen-svc 20
```

Hepsi `green` cevap verir.

### Kesintisiz mi? Geçiş sırasında ölçüm

Geçişin kesintisiz olduğunu görmek için ayrı bir terminalde sürekli istek at (32 sn boyunca saniyede 5 istek):

```bash
kubectl exec client -n ecommerce-dev -- sh -c 'for i in $(seq 160); do R=$(wget -qO- -T 2 http://bluegreen-svc/config 2>/dev/null); echo "${R:-HATA}"; sleep 0.2; done' | sed 's/.*"mode":"\([^"]*\)".*/\1/' | uniq -c
```

İstekler akarken (yaklaşık 10. saniyede) diğer terminalde yukarıdaki `kubectl patch` komutunu çalıştır. Testte 160 istekten **0 hata** çıktı: 59 istek `blue`, sonrasında 101 istek `green` cevap verdi. Aralarında karışma olmadı, çünkü iki sürüm bir arada trafik almıyor.

## Adım 4: Geri alma (rollback)

Yeni sürümde sorun çıkarsa selector'ü eski haline çevirmek yeter. Blue çalışmaya devam ettiği için geri dönüş anidir, Pod'ların yeniden açılmasını beklemek gerekmez:

```bash
kubectl patch svc bluegreen-svc -n ecommerce-dev -p '{"spec":{"selector":{"version":"blue"}}}'
cnt bluegreen-svc 20
```

Testte geri aldıktan sonra 20 isteğin hepsi `blue` cevap verdi.

> Rolling update'te hatalı sürümü geri almak için eski Pod'ların yeniden oluşturulması gerekir. Blue/Green'de eski ortam açık kaldığı sürece geri alma sadece bir selector değişikliğidir. Bedeli, eski ortamı açık tutmanın kaynak maliyetidir.

## Risk: Ready olmayan ortama geçiş

Service yalnızca Ready Pod'lara trafik gönderir. Green'de Ready Pod yokken selector'ü green'e çevirirsen canlı Service'in **endpoint listesi boşalır** ve tüm istekler hata verir. Testte green 0 replikaya indirilip selector green yapıldığında 5 istekten 5'i hata verdi, endpoint listesi boştu. Selector blue'ya döndürülünce düzeldi.

Bu yüzden her zaman geçişten önce:

```bash
kubectl get deploy app-green -n ecommerce-dev
kubectl get endpointslices -n ecommerce-dev -l kubernetes.io/service-name=bluegreen-preview
```

çıktılarında `READY 3/3` ve 3 `ready=true` endpoint olduğunu doğrula.

## YAML ile kalıcı geçiş

`kubectl patch` hızlı bir yoldur ama dosyadaki değeri değiştirmez. Aynı Service'i `kubectl apply` ile yeniden uygularsan selector dosyadaki değere (`blue`) **geri döner**. Testte patch ile `green` yapılan selector, `service-live.yaml` yeniden uygulanınca `blue`'ya döndü.

Kalıcı olması için `service-live.yaml` içindeki `version: blue` satırını `version: green` yap ve uygula:

```bash
kubectl apply -f kubernetes/week-4/BlueGreen/service-live.yaml
```

Gerçek projelerde geçiş bu dosyanın bir commit ile değişmesi olarak kayıt altına alınır.

## Adım 5: Eski ortamı kaldır

Yeni sürümün sağlıklı çalıştığından emin olduktan sonra eski ortamı kapat. Bir süre `0` replika ile bırakmak hem kaynak harcamaz hem de gerekirse hızlıca geri dönmeyi sağlar:

```bash
kubectl scale deploy/app-blue --replicas=0 -n ecommerce-dev
```

Geri dönmen gerekirse replikayı artırıp Ready olmasını bekle, sonra selector'ü çevir:

```bash
kubectl scale deploy/app-blue --replicas=3 -n ecommerce-dev
kubectl rollout status deploy/app-blue -n ecommerce-dev
```

Bir sonraki sürümde roller yer değiştirir: green canlı kalır, yeni sürüm boşalan blue ortamına kurulur ve selector tekrar blue'ya çevrilir. `blue` ve `green` adları sabit sürümleri değil, o an oynadıkları rolü gösterir.

## RollingUpdate ile karşılaştırma

| | Rolling update | Blue/Green |
|---|---|---|
| Geçiş | Kademeli, Pod Pod | Tek hamlede, anlık |
| Eski ve yeni sürüm aynı anda trafik alır mı | Evet, geçiş süresince | Hayır |
| Yeni sürümü canlıya almadan test | Zor | Kolay (preview Service) |
| Geri alma | Yeni bir rollout gerekir | Selector'ü geri çevirmek yeter (anında) |
| Ek kaynak | `maxSurge` kadar | 2 kat (iki ortam birlikte) |

## Dikkat edilecekler

- **Veritabanı:** İki ortam genelde aynı veritabanını kullanır. Şema değişiklikleri hem eski hem yeni sürümle uyumlu olmalıdır, yoksa geri alma imkânsızlaşır.
- **Maliyet:** Geçiş boyunca iki ortam birlikte çalışır.

## Temizle

```bash
kubectl delete -f kubernetes/week-4/BlueGreen/
docker rmi microservice1-api:1.1
```
