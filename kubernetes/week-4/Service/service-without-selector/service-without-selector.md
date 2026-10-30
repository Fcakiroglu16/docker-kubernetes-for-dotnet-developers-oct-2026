# Endpoint'i olmayan Service (selector'sız ve ExternalName)

Normalde Service `selector` ile Pod bulur ve Kubernetes Pod IP'lerini **EndpointSlice** nesnesine otomatik yazar. Bazen endpoint'i sen yönetmek istersin: cluster dışındaki bir veritabanı, başka bir cluster'daki servis veya henüz taşınmamış bir VM. İki yol vardır:

| Yol | Endpoint | DNS cevabı |
|---|---|---|
| `selector`'sız Service + elle yazılan EndpointSlice | Sen yazarsın | Service'in `ClusterIP`'si |
| `type: ExternalName` | Yok | CNAME (başka bir DNS adı) |

Tüm komutlar repo kök klasöründen çalıştırılır. İstemci olarak `redis-client` Pod'u kullanılır.

| Dosya | Görevi |
|---|---|
| [external-redis.yaml](external-redis.yaml) | "Cluster dışı" Redis'i taklit eden tek Pod |
| [service-no-selector.yaml](service-no-selector.yaml) | `selector`'sız Service |
| [endpointslice.yaml](endpointslice.yaml) | Service'e elle bağlanan endpoint listesi |
| [service-externalname.yaml](service-externalname.yaml) | `ExternalName` Service |

## Hazırlık

```bash
kubectl apply -f kubernetes/week-4/Service/service-without-selector/redis-client.yaml
kubectl apply -f kubernetes/week-4/Service/service-without-selector/external-redis.yaml
kubectl wait --for=condition=Ready pod/redis-client pod/external-redis -n ecommerce-dev
```

## 1. Selector'sız Service

```bash
kubectl apply -f kubernetes/week-4/Service/service-without-selector/service-no-selector.yaml
kubectl get svc external-redis -n ecommerce-dev
kubectl get endpointslices -n ecommerce-dev -l kubernetes.io/service-name=external-redis
```

Service oluşur ve `CLUSTER-IP` alır, ama endpoint listesi **boştur** (`No resources found`). Bağlanmayı dene:

```bash
kubectl exec redis-client -n ecommerce-dev -- redis-cli -h external-redis -t 3 PING
```

Cevap: `Could not connect to Redis at external-redis:6379: Connection refused`. Service'in gideceği adres yoktur.

### Endpoint'i elle ekle

`endpointslice.yaml` içindeki `REPLACE_WITH_IP` yerine hedefin IP'si yazılır. Burada hedef `external-redis` Pod'unun IP'sidir. Gerçek ortamda dış sunucunun IP'si olur:

```bash
IP=$(kubectl get pod external-redis -n ecommerce-dev -o jsonpath='{.status.podIP}')
sed "s/REPLACE_WITH_IP/$IP/" kubernetes/week-4/Service/service-without-selector/endpointslice.yaml | kubectl apply -f -
kubectl get endpointslices -n ecommerce-dev -l kubernetes.io/service-name=external-redis
```

Bağlantı tamamlayan iki kural:

- `kubernetes.io/service-name: external-redis` label'ı EndpointSlice'ı Service'e bağlar.
- EndpointSlice'taki port **adı** (`redis`) Service'teki port adıyla aynı olmalıdır.

Birkaç saniye bekle (kube-proxy kuralı yazar). Testte ilk denemede hâlâ `Connection refused` geldi, 5 sn sonra çalıştı:

```bash
kubectl exec redis-client -n ecommerce-dev -- redis-cli -h external-redis PING
kubectl exec redis-client -n ecommerce-dev -- redis-cli -h external-redis SET who external
kubectl exec external-redis -n ecommerce-dev -- redis-cli GET who
```

Cevaplar: `PONG`, `OK`, `external`. İstek Service üzerinden hedef Pod'a ulaştı.

> Bu Pod'a `external-redis-demo` label'ıyla bakan bir selector olmadığı için Kubernetes endpoint'i kendisi yazmaz. IP'yi sen yönetirsin: hedefin IP'si değişirse EndpointSlice'ı güncellemek senin işindir.

> Eski `Endpoints` nesnesi (`kubectl get endpoints`) Kubernetes 1.33+ sürümünde kullanımdan kalkmıştır. Elle endpoint yazarken `EndpointSlice` kullan.

## 2. ExternalName

`ExternalName` Service'in selector'ı, ClusterIP'si ve endpoint'i yoktur. Yalnızca DNS'te **CNAME** kaydı üretir. İstemci `cache` adını sorar, DNS başka bir ada yönlendirir.

```bash
kubectl apply -f kubernetes/week-4/Service/service-without-selector/service-externalname.yaml
kubectl get svc cache -n ecommerce-dev
kubectl get endpointslices -n ecommerce-dev -l kubernetes.io/service-name=cache
kubectl exec redis-client -n ecommerce-dev -- nslookup cache.ecommerce-dev.svc.cluster.local
kubectl exec redis-client -n ecommerce-dev -- redis-cli -h cache GET who
```

Testte `CLUSTER-IP` `<none>`, endpoint listesi boş çıktı. `nslookup` şunu döndürdü: `canonical name = external-redis.ecommerce-dev.svc.cluster.local`, sonra o adın IP'si. `redis-cli -h cache GET who` `external` döndürdü.

> Gerçek kullanımda `externalName` cluster dışı bir ad olur (örneğin `mydb.example.com`). Uygulama `cache` adına bağlanır, dış sistem değişirse yalnızca Service güncellenir. `ExternalName` proxy yapmaz ve port çevirmez. Yalnızca DNS yönlendirir. HTTP/TLS'te `Host` başlığı uyuşmazlığı sorun çıkarabilir.

## Temizle

```bash
kubectl delete -f kubernetes/week-4/Service/service-without-selector/
kubectl delete -f kubernetes/week-4/Service/service-without-selector/redis-client.yaml
```

`endpointslice.yaml` içindeki `REPLACE_WITH_IP` yer tutucusu `kubectl delete -f` için sorun değildir, silme ad ve türe göre yapılır.
