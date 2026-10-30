# Service

Pod'lar geçicidir: silinir, yeniden oluşturulur, ölçeklenir ve her seferinde **farklı bir IP** alır. Bir uygulama Pod IP'sine bağlanırsa Pod yenilendiği anda bağlantı kopar. Service, bir grup Pod'un önüne **sabit bir adres (IP ve DNS adı)** koyar ve gelen trafiği o grubun Ready olan Pod'ları arasında dağıtır.

Tüm komutlar repo kök klasöründen çalıştırılır. Dosyalar:

| Dosya | Görevi |
|---|---|
| [backend-deployment.yaml](backend-deployment.yaml) | Service'in trafik göndereceği 3 Pod (Deployment) |
| [service-clusterip.yaml](service-clusterip.yaml) | `ClusterIP`: yalnızca cluster içinden erişim (varsayılan) |
| [service-nodeport.yaml](service-nodeport.yaml) | `NodePort`: her node'da bir port açar |
| [service-loadbalancer.yaml](service-loadbalancer.yaml) | `LoadBalancer`: dış IP ister |
| [service-headless.yaml](service-headless.yaml) | Headless: sanal IP yok, DNS Pod IP'lerini döndürür |
| [client-pod.yaml](client-pod.yaml) | Cluster içinden deneme yapmak için `busybox` Pod |
| [service-without-selector/](service-without-selector/service-without-selector.md) | Endpoint'i olmayan Service: `selector`'sız + EndpointSlice ve `ExternalName` |

## Nasıl çalışır?

```
istemci -> Service (sabit IP + DNS adı) -> Pod 1, Pod 2, Pod 3  (selector ile bulunan, Ready olanlar)
```

- Service, `selector` ile eşleşen Pod'ları bulur. Eşleşen **ve Ready olan** Pod'ların IP'leri bir **EndpointSlice** nesnesinde tutulur. Service'in kendisi trafik taşımaz, listeyi tutar.
- Her node'daki `kube-proxy` bu listeye bakarak Service IP'sine gelen bağlantıyı Pod'lardan birine yönlendirir.
- Pod eklenir, silinir veya Ready olmazsa liste otomatik güncellenir. İstemcinin hiçbir şey yapması gerekmez.

## YAML'ın önemli alanları

| Alan | Anlamı |
|---|---|
| `spec.selector` | Trafik hangi Pod'lara gitsin? Pod'un `labels` alanıyla eşleşmelidir |
| `spec.ports[].port` | Service'in dinlediği port (istemci bu porta bağlanır) |
| `spec.ports[].targetPort` | Pod'daki port. Port adı (`http`) veya numara (`8080`) olabilir |
| `spec.type` | `ClusterIP` (varsayılan), `NodePort`, `LoadBalancer` |
| `spec.clusterIP: None` | Headless Service |

## Hazırlık

Namespace, ResourceQuota ve LimitRange yoksa önce [namespace.md](../../week-3/namespace/namespace.md) adımlarını uygula. Image'ı build et (Docker Desktop kind modunda node'lar image'ı otomatik görür, kendi kind cluster'ında ayrıca `kind load docker-image` gerekir, bkz. [pod.md](../../week-3/1-pod/pod.md)):

```bash
docker build -t microservice1-api:1.0 Microservice1.API
```

## Backend Pod'larını ve istemciyi oluştur

Hangi Pod'un cevap verdiğini görebilmek için Pod'un adı `App__Mode` ortam değişkenine yazılır (downward API). Uygulamanın `GET /config` endpoint'i bunu `mode` alanında döndürür. Uygulama koduna dokunmaya gerek yoktur.

```bash
kubectl apply -f kubernetes/week-4/Service/backend-deployment.yaml
kubectl apply -f kubernetes/week-4/Service/client-pod.yaml
kubectl rollout status deploy/microservice1-backend -n ecommerce-dev
kubectl wait --for=condition=Ready pod/client -n ecommerce-dev
```

Pod'ların IP'lerine bak:

```bash
kubectl get pods -n ecommerce-dev -o wide
```

## 1. ClusterIP

Varsayılan tip. Service'e yalnızca cluster **içinden** erişilir. Servisler arası (örneğin sipariş servisi -> ürün servisi) iletişimin standart yoludur.

```bash
kubectl apply -f kubernetes/week-4/Service/service-clusterip.yaml
kubectl get svc -n ecommerce-dev
```

`CLUSTER-IP` sütununda sanal bir IP görürsün, `EXTERNAL-IP` boştur.

### Endpoint'leri göster

Service'in trafik gönderdiği Pod IP'leri (3 backend Pod'unun IP'si olmalı):

```bash
kubectl get endpointslices -n ecommerce-dev
kubectl get endpoints microservice1-svc -n ecommerce-dev
```

### Cluster içinden eriş

`client` Pod'undan Service'e DNS adıyla istek at. Aynı namespace'te kısa ad yeterlidir:

```bash
kubectl exec client -n ecommerce-dev -- wget -qO- http://microservice1-svc/config
```

Cevapta `"mode":"microservice1-backend-..."` yazan Pod adı görünür. Başka namespace'ten erişmek için tam ad kullanılır: `<service>.<namespace>.svc.cluster.local`.

```bash
kubectl exec client -n ecommerce-dev -- wget -qO- http://microservice1-svc.ecommerce-dev.svc.cluster.local/health/live
kubectl exec client -n ecommerce-dev -- nslookup microservice1-svc.ecommerce-dev.svc.cluster.local
```

`nslookup` Service'in `CLUSTER-IP` değerini döndürür. Service oluşturulduktan **sonra** başlayan Pod'lara Service bilgisi ortam değişkeni olarak da verilir (örneğin `MICROSERVICE1_SVC_SERVICE_PORT`), ama DNS adı kullanmak daha güvenilirdir.

### Yük dağılımını gör

30 isteği art arda at ve hangi Pod'un kaç tanesine cevap verdiğini say:

```bash
kubectl exec client -n ecommerce-dev -- sh -c 'for i in $(seq 30); do wget -qO- http://microservice1-svc/config; echo; done' | sed 's/.*"mode":"\([^"]*\)".*/\1/' | sort | uniq -c
```

Testte dağılım `8 / 14 / 8` çıktı. Dağılım sıralı (round-robin) değil **rastgeledir**, bu yüzden eşit çıkması beklenmez. İstek sayısı arttıkça dağılım dengelenir.

### Cluster dışından erişilemez

`CLUSTER-IP` sanal bir IP'dir ve yalnızca cluster ağında geçerlidir. Bilgisayarından erişemezsin:

```bash
curl -m 4 http://$(kubectl get svc microservice1-svc -n ecommerce-dev -o jsonpath='{.spec.clusterIP}')/health/live
```

Geliştirme sırasında hızlı erişim için `port-forward` kullanılır. Komut çalıştığı sürece açık kalır, ayrı bir terminalde çalıştır:

```bash
kubectl port-forward svc/microservice1-svc 8080:80 -n ecommerce-dev
```

Başka bir terminalde `curl localhost:8080/health/ready` çalışır. `port-forward` tek bir Pod'a bağlanır, yük dağıtımı yapmaz.

## Endpoint'ler Pod'larla birlikte değişir

Replika sayısını artır ve Pod'ları ile endpoint'leri izle:

```bash
kubectl scale deploy/microservice1-backend --replicas=5 -n ecommerce-dev
kubectl get pods -n ecommerce-dev -l app=microservice1-backend
kubectl get endpointslices -n ecommerce-dev -o jsonpath='{range .items[*].endpoints[*]}{.addresses[0]}{" ready="}{.conditions.ready}{"\n"}{end}'
```

Yeni Pod'lar oluşur oluşmaz listede görünür ama `ready=false` olur. Açılış sırasında (bu uygulamada ilk ~15 sn) readiness probe başarısız olduğu için **trafik almazlar**. Testte yeni 2 Pod Ready olana kadar `ready=true` sayısı 3'te kaldı, Ready olunca 5'e çıktı. `kubectl get endpoints` yalnızca Ready adresleri gösterir.

Bu yüzden Service'in sorunsuz çalışması için **readiness probe doğru ayarlanmalıdır**. Probe yoksa Pod henüz hazır değilken trafik almaya başlar.

Eski haline getir:

```bash
kubectl scale deploy/microservice1-backend --replicas=3 -n ecommerce-dev
```

## Sorun giderme: Service cevap vermiyorsa

Çoğu hata endpoint listesi boş olduğunda ortaya çıkar. Önce şuna bak:

```bash
kubectl get endpointslices -n ecommerce-dev
```

`ENDPOINTS` sütunu boşsa Service hiçbir Pod bulamamıştır (Ready Pod'lar listede görünmez). Üç olası neden:

1. **Selector yanlış:** Service'in `selector` değeri Pod label'larıyla eşleşmiyor. Testte selector `app: yanlis` yapılınca endpoint listesi boşaldı ve istek hata verdi. Düzeltilince hemen çalıştı.
   ```bash
   kubectl get svc microservice1-svc -n ecommerce-dev -o jsonpath='{.spec.selector}'
   kubectl get pods -n ecommerce-dev --show-labels
   ```
2. **Pod Ready değil:** Readiness probe başarısız. `kubectl get pods` çıktısında `READY` sütunu `0/1` olur.
3. **`targetPort` yanlış:** Endpoint listesi dolu görünür ama istek hata verir, çünkü Pod o portu dinlemiyor. Testte `targetPort: 9999` yapınca endpoint'ler `9999` portunu gösterdi ve bağlantı kurulamadı. Doğru port `8080`'dir. `targetPort` için port **adı** (`http`) veya **numara** (`8080`) yazılabilir, ikisi de çalıştı.

## Oturum yapışkanlığı (sessionAffinity)

Varsayılan olarak her bağlantı rastgele bir Pod'a gider. Aynı istemcinin hep aynı Pod'a gitmesi gerekiyorsa:

```bash
kubectl patch svc microservice1-svc -n ecommerce-dev -p '{"spec":{"sessionAffinity":"ClientIP"}}'
kubectl exec client -n ecommerce-dev -- sh -c 'for i in $(seq 20); do wget -qO- http://microservice1-svc/config; echo; done' | sed 's/.*"mode":"\([^"]*\)".*/\1/' | sort | uniq -c
```

`client` Pod'undan gelen 20 isteğin hepsi aynı Pod'a gitti. Geri almak için YAML'ı yeniden uygula:

```bash
kubectl apply -f kubernetes/week-4/Service/service-clusterip.yaml
```

> Mümkünse uygulamayı durumsuz (stateless) tasarlayıp yapışkanlığa ihtiyaç bırakma. Pod silinince yapışkanlık da biter.

## 2. NodePort

`ClusterIP`'ye ek olarak **her node'da** aynı portu (30000-32767 arası) açar. Node'un IP'sine ve bu porta gelen trafik Service'e, oradan Pod'lara iletilir.

```bash
kubectl apply -f kubernetes/week-4/Service/service-nodeport.yaml
kubectl get svc microservice1-nodeport -n ecommerce-dev
```

`PORT(S)` sütununda `80:30080/TCP` görünür (`80` ClusterIP portu, `30080` node portu).

Node IP'lerini al ve cluster içinden denet. Trafik **herhangi bir node** üzerinden Service'e ulaşır, Pod'un o node'da olması gerekmez:

```bash
kubectl get nodes -o wide
kubectl exec client -n ecommerce-dev -- wget -qO- http://172.18.0.7:30080/health/live
```

(`172.18.0.7` yerine kendi node IP'lerinden birini yaz.) Testte 4 node'un dördünde de `30080` portu cevap verdi.

> Docker Desktop (kind modu) üzerinde node'lar Docker'ın iç ağındadır (`172.18.0.x`). Bilgisayarından (`localhost:30080` veya node IP'si) NodePort'a **erişilemedi**. Node'un içinden (`docker exec <node> curl localhost:30080/health/live`) ve cluster içindeki Pod'lardan erişilebilir. Bilgisayardan erişmek için `LoadBalancer` veya `port-forward` kullan. Gerçek bir cluster'da node IP'lerine ağdan erişilebilir.

## 3. LoadBalancer

`NodePort`'a ek olarak bulut sağlayıcıdan (Azure, AWS, GCP) bir **dış IP / yük dengeleyici** ister. Gerçek ortamda uygulamayı internete açmanın temel yoludur.

```bash
kubectl apply -f kubernetes/week-4/Service/service-loadbalancer.yaml
kubectl get svc microservice1-lb -n ecommerce-dev
```

Docker Desktop (kind modu) bu Service'e bir `EXTERNAL-IP` verdi (`172.18.0.11`) ve trafiği bilgisayarın `localhost` adresine bağladı. `80` portundan test et:

```bash
curl http://localhost/health/live
```

30 isteğin Pod'lara dağılımı da `15 / 8 / 7` çıktı. `172.18.0.11` adresine bilgisayardan doğrudan erişilemedi, erişim `localhost` üzerindendir.

> Bu davranış Docker Desktop'a özeldir. Bulut sağlayıcısı olmayan bir cluster'da `EXTERNAL-IP` genellikle `<pending>` kalır. Kendi kind cluster'ında `localhost` üzerinden erişemezsen `port-forward` kullan.

### ResourceQuota notu

`ecommerce-dev` quota'sında `services.nodeports: 2` ve `services.loadbalancers: 1` sınırı vardır. `LoadBalancer` Service de bir `nodePort` tükettiği için `NodePort` + `LoadBalancer` birlikte kotayı doldurur (`2/2`). Yeni bir NodePort oluşturmak için önce birini silmen gerekir:

```bash
kubectl get quota -n ecommerce-dev -o jsonpath='{.items[0].status.used}'
```

## 4. Headless Service

`clusterIP: None` verilirse Service'e sanal IP atanmaz ve yük dağıtımı yapılmaz. DNS sorgusu Service'in IP'si yerine **doğrudan Pod IP'lerini** döndürür. Her Pod'a tek tek ulaşmak gereken durumlarda kullanılır (örneğin StatefulSet, veritabanı kümeleri).

```bash
kubectl apply -f kubernetes/week-4/Service/service-headless.yaml
kubectl get svc microservice1-headless -n ecommerce-dev
```

`CLUSTER-IP` sütunu `None` olur. DNS cevabını `ClusterIP` ile karşılaştır:

```bash
kubectl exec client -n ecommerce-dev -- nslookup microservice1-headless.ecommerce-dev.svc.cluster.local
kubectl get pods -n ecommerce-dev -l app=microservice1-backend -o 'custom-columns=NAME:.metadata.name,IP:.status.podIP'
```

`nslookup` 3 satır `Address` döndürür ve bunlar Pod IP'leridir. `microservice1-svc` için ise tek bir `ClusterIP` döner.

> `nslookup` çıktısındaki `server can't find ... NXDOMAIN` satırları ve `exit code 1`, busybox'ın DNS arama listesindeki (`search`) diğer alan adlarını da denemesinden kaynaklanır. Hata değildir, `Name:` ve `Address:` satırları doğru cevaptır.

## Tipleri karşılaştır

| Tip | Erişim | Kullanım |
|---|---|---|
| `ClusterIP` | Yalnızca cluster içi | Servisler arası iletişim (en yaygın) |
| `NodePort` | `<node-ip>:<nodePort>` | Basit dış erişim, test. Üretimde nadiren tek başına |
| `LoadBalancer` | Bulut sağlayıcının dış IP'si | Uygulamayı internete açmak |
| Headless | DNS Pod IP'lerini döndürür | Pod'lara tek tek erişim (StatefulSet) |

`NodePort`, `ClusterIP`'yi; `LoadBalancer` de `NodePort`'u içerir. Yani her `LoadBalancer`, ayrıca bir `ClusterIP` ve `nodePort`'a sahiptir.

## Temizle

```bash
kubectl delete -f kubernetes/week-4/Service/
```

Bu komut klasördeki tüm YAML dosyalarını siler (Service'ler, Deployment ve istemci Pod'u).
