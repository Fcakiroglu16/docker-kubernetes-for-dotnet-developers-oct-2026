# Headless Service ile Redis

Normal Service (`ClusterIP`) tek bir sanal IP verir ve trafiği Pod'lara rastgele dağıtır. Redis gibi **durumlu** sistemlerde bu yetmez: yazma yalnızca master'a gitmeli, replica'lar master'ı adıyla bulmalı, istemci istediği Pod'a tek tek ulaşabilmelidir. Headless Service (`clusterIP: None`) sanal IP vermez. DNS doğrudan Pod IP'lerini döndürür. StatefulSet ile birlikte her Pod'a **sabit bir DNS adı** verir.

Tüm komutlar repo kök klasöründen çalıştırılır. Namespace ve quota için önce [namespace.md](../../week-3/namespace/namespace.md) adımlarını uygula. Dosyalar:

| Dosya | Görevi |
|---|---|
| [redis-headless-service.yaml](redis-headless-service.yaml) | `clusterIP: None` Service |
| [redis-statefulset.yaml](redis-statefulset.yaml) | 3 Redis Pod'u: `redis-0` master, `redis-1` ve `redis-2` replica. Her biri 1Gi PVC alır |
| [redis-client.yaml](redis-client.yaml) | `redis-cli` ve `nslookup` içeren istemci Pod |

## Kur

```bash
kubectl apply -f kubernetes/week-4/StatefulSet/
kubectl rollout status sts/redis -n ecommerce-dev
kubectl wait --for=condition=Ready pod/redis-client -n ecommerce-dev
kubectl get pods,pvc -n ecommerce-dev -o wide
```

StatefulSet Pod'ları **sırayla** başlatır (`redis-0`, sonra `redis-1`, sonra `redis-2`). Her Pod için ayrı bir PVC oluşur: `data-redis-0`, `data-redis-1`, `data-redis-2`.

`CLUSTER-IP` sütunu `None` olmalıdır:

```bash
kubectl get svc redis-headless -n ecommerce-dev
```

## DNS iki şekilde cevap verir

Service adı **tüm Ready Pod'ların** IP'lerini döndürür:

```bash
kubectl exec redis-client -n ecommerce-dev -- nslookup redis-headless.ecommerce-dev.svc.cluster.local
```

Testte 3 `Address` satırı geldi (`10.244.1.6`, `10.244.2.5`, `10.244.3.6`). Bunlar Pod IP'leridir, sanal bir IP yoktur. Her Pod'un ayrıca kendi adı vardır: `<pod>.<service>.<namespace>.svc.cluster.local`:

```bash
kubectl exec redis-client -n ecommerce-dev -- nslookup redis-1.redis-headless.ecommerce-dev.svc.cluster.local
```

Bu sorgu yalnızca `redis-1`'in IP'sini döndürür. Pod IP'lerini karşılaştır:

```bash
kubectl get pods -n ecommerce-dev -l app=redis -o 'custom-columns=NAME:.metadata.name,IP:.status.podIP'
```

> `nslookup` çıktısındaki `NXDOMAIN` satırları busybox'ın `search` listesindeki diğer alan adlarını da denemesinden gelir, hata değildir.

## Master'a yaz, replica'dan oku

Replica'lar master'a IP ile değil **DNS adıyla** bağlanır (`--replicaof redis-0.redis-headless 6379`, bkz. StatefulSet'teki `args`). Master Pod yeniden oluşup IP'si değişse bile ad aynı kalır.

```bash
kubectl exec redis-client -n ecommerce-dev -- redis-cli -h redis-0.redis-headless.ecommerce-dev.svc.cluster.local SET greeting hello
kubectl exec redis-client -n ecommerce-dev -- redis-cli -h redis-1.redis-headless.ecommerce-dev.svc.cluster.local GET greeting
kubectl exec redis-client -n ecommerce-dev -- redis-cli -h redis-2.redis-headless.ecommerce-dev.svc.cluster.local GET greeting
```

İki replica da `hello` döndürür. Replica'ya yazma denersen reddedilir:

```bash
kubectl exec redis-client -n ecommerce-dev -- redis-cli -h redis-1.redis-headless.ecommerce-dev.svc.cluster.local SET x 1
```

Cevap: `READONLY You can't write against a read only replica.` Rolleri gör:

```bash
for i in 0 1 2; do echo -n "redis-$i: "; kubectl exec redis-client -n ecommerce-dev -- redis-cli -h redis-$i.redis-headless.ecommerce-dev.svc.cluster.local INFO replication | grep -E "^role|master_host" | tr '\r\n' ' '; echo; done
```

Testte `redis-0` `role:master`, diğerleri `role:slave` ve `master_host:redis-0.redis-headless` döndürdü.

## Pod silinince ad sabit kalır, IP değişir

```bash
kubectl get pod redis-1 -n ecommerce-dev -o jsonpath='{.status.podIP}{"\n"}'
kubectl delete pod redis-1 -n ecommerce-dev
kubectl wait --for=condition=Ready pod/redis-1 -n ecommerce-dev
kubectl get pod redis-1 -n ecommerce-dev -o jsonpath='{.status.podIP}{"\n"}'
kubectl exec redis-client -n ecommerce-dev -- nslookup redis-1.redis-headless.ecommerce-dev.svc.cluster.local
kubectl exec redis-client -n ecommerce-dev -- redis-cli -h redis-1.redis-headless.ecommerce-dev.svc.cluster.local GET greeting
```

Testte IP `10.244.2.5` -> `10.244.2.6` oldu. DNS adı yeni IP'yi gösterdi ve `greeting` hâlâ `hello` döndü (veri PVC'de durur, ayrıca replica master'dan yeniden senkronlanır).

## Önemli noktalar

- **Neden headless?** Redis master/replica ayrımı vardır. Normal Service istekleri rastgele dağıtır, yazma replica'ya düşüp hata verir. Headless ile hedef Pod'u adıyla seçersin.
- **`serviceName`:** StatefulSet'teki `serviceName: redis-headless` alanı Pod DNS adlarını üretir. Service adıyla aynı olmalıdır.
- **Bu örnek otomatik failover yapmaz.** `redis-0` ölürse yeni master seçilmez, StatefulSet aynı adla `redis-0`'ı yeniden başlatır. Otomatik failover için Redis Sentinel veya Redis Cluster gerekir.
- **ResourceQuota:** `ecommerce-dev` quota'sı en fazla 3 StatefulSet ve 5 PVC'ye izin verir. Bu örnek 1 StatefulSet ve 3 PVC kullanır.
- **Uygulamadan erişim:** .NET uygulamasında bağlantı dizesi olarak master adı (`redis-0.redis-headless:6379`) yazılır. Okuma için replica adları kullanılabilir.

## Temizle

StatefulSet silinince PVC'ler **silinmez**, ayrıca silinmelidir:

```bash
kubectl delete -f kubernetes/week-4/StatefulSet/
kubectl delete pvc -l app=redis -n ecommerce-dev
```

## Sonraki örnek

Kafka (KRaft, 3 broker): [kafka/kafka.md](kafka/kafka.md)
