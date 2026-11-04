# StatefulSet ile PostgreSQL (1 primary + 2 replica)

PostgreSQL streaming replication'da roller farklıdır: **primary** yazar, **replica'lar** primary'nin WAL kaydını akış (streaming) ile alıp salt okunur çalışır. Her Pod'un kendi diski ve sabit adı olmalıdır. Bu StatefulSet + headless Service ile sağlanır. Diğer örnekler: [Redis](../statefulset.md), [Kafka](../kafka/kafka.md), [RabbitMQ](../rabbitmq/rabbitmq.md).

Tüm komutlar repo kök klasöründen çalıştırılır. Namespace ve quota için önce [namespace.md](../../../week-3/namespace/namespace.md) adımlarını uygula. Dosyalar:

| Dosya | Görevi |
|---|---|
| [postgres-secret.yaml](postgres-secret.yaml) | Postgres ve replication kullanıcısının şifresi |
| [postgres-configmap.yaml](postgres-configmap.yaml) | Primary'de `replicator` rolünü ve `pg_hba.conf` kaydını oluşturan init script |
| [postgres-headless-service.yaml](postgres-headless-service.yaml) | `clusterIP: None` Service, Pod DNS adları için |
| [postgres-primary-service.yaml](postgres-primary-service.yaml) | Sadece `postgres-0`'a giden ClusterIP Service (yazma) |
| [postgres-nodeport-service.yaml](postgres-nodeport-service.yaml) | Primary'yi dışarı açar (30432) |
| [postgres-statefulset.yaml](postgres-statefulset.yaml) | 3 Pod, her biri 1Gi PVC alır |

## Kur

Quota'da `persistentvolumeclaims: 5` sınırı var. Redis, Kafka veya RabbitMQ ayaktaysa önce onları (ve PVC'lerini) sil.

```bash
kubectl apply -f kubernetes/week-4/StatefulSet/postgres/
kubectl wait --for=condition=Ready pod/postgres-0 pod/postgres-1 pod/postgres-2 -n ecommerce-dev --timeout=240s
kubectl get pods,pvc -n ecommerce-dev -o wide
```

## Nasıl çalışır

1. **Rol Pod sırasından gelir:** `postgres-0` primary, `postgres-1` ve `postgres-2` replica'dır. Pod adının sonundaki sıra numarası `${HOSTNAME##*-}` ile okunur.
2. **Primary:** İmajın entrypoint'i boş diskte `initdb` çalıştırır. `/docker-entrypoint-initdb.d` altındaki script `replicator` rolünü yaratır ve `pg_hba.conf`'a `replication` satırı ekler (varsayılan dosya replication bağlantısına izin vermez).
3. **Replica:** Diski boşsa `pg_basebackup -R` ile primary'den tam kopya alır. `-R`, `standby.signal` ve `primary_conninfo` dosyalarını yazar, böylece Pod otomatik replica olarak açılır. Disk doluysa kopyalama atlanır ve kaldığı yerden devam eder.
4. **Varsayılan `OrderedReady`:** Pod'lar sırayla başlar. Replica'lar primary hazır olduktan sonra gelir. Bu yüzden `podManagementPolicy: Parallel` kullanılmadı (Kafka'nın tersine burada sıra gerekli). Replica ayrıca `pg_isready` ile primary'yi bekler.
5. **`PGDATA` alt klasördedir:** PVC kökünde `lost+found` bulunabilir, `initdb` boş olmayan dizine yazmaz.
6. **`runAsUser: 70`:** Postgres root ile çalışmaz. Alpine imajında `postgres` kullanıcısı 70'tir.

## Doğrula

Rolleri ve replication durumunu kontrol et:

```bash
for i in 0 1 2; do echo -n "postgres-$i in_recovery="; kubectl exec postgres-$i -n ecommerce-dev -- psql -U postgres -Atc "select pg_is_in_recovery()"; done
kubectl exec postgres-0 -n ecommerce-dev -- psql -U postgres -c "select client_addr, state, sync_state from pg_stat_replication"
```

Testte `postgres-0` için `f` (primary), diğer ikisi için `t` (replica) döndü. `pg_stat_replication` iki satırda `streaming | async` gösterdi.

Primary'ye yaz, replica'lardan oku:

```bash
kubectl exec postgres-0 -n ecommerce-dev -- psql -U postgres -c "create table orders(id serial primary key, name text); insert into orders(name) values ('order-1'),('order-2');"
kubectl exec postgres-1 -n ecommerce-dev -- psql -U postgres -c "select * from orders"
kubectl exec postgres-2 -n ecommerce-dev -- psql -U postgres -c "select * from orders"
```

Replica'ya yazmayı dene, reddedilmeli:

```bash
kubectl exec postgres-1 -n ecommerce-dev -- psql -U postgres -c "insert into orders(name) values ('x')"
```

Testte `ERROR:  cannot execute INSERT in a read-only transaction` döndü.

## Pod silinince ne olur

```bash
kubectl delete pod postgres-0 postgres-1 -n ecommerce-dev
```

Testte iki Pod aynı adla geri geldi, roller değişmedi, `pg_stat_replication` yine 2 replica gösterdi. Silmeden sonra yazılan satır iki replica'ya da ulaştı. Veri PVC'de kaldığı için replica yeniden kopyalanmadı.

## Bağlantı adresleri

| Amaç | Adres |
|---|---|
| Yazma (cluster içi) | `postgres-primary.ecommerce-dev.svc.cluster.local:5432` |
| Okuma (cluster içi) | `postgres-1.postgres-headless.ecommerce-dev.svc.cluster.local:5432`, `postgres-2...` |
| Dışarıdan (NodePort) | `<node-ip>:30432` (primary) |

Replica'ları tek Service ile seçmek mümkün değil: StatefulSet'in tüm Pod'ları aynı label'ı taşır ve Service selector'ı "primary hariç" diyemez. Okuma için uygulama replica DNS adlarını kullanır.

Docker Desktop/kind'da `localhost:30432` çalışmayabilir:

```bash
kubectl port-forward svc/postgres-external 5432:5432 -n ecommerce-dev
```

## Sınırlar

- **Otomatik failover yok.** Primary kalıcı giderse bir replica'yı elle `pg_ctl promote` ile yükseltip Service'i yönlendirmek gerekir. Gerçek ortamda Patroni veya CloudNativePG gibi bir operator kullanılır.
- Replication **asenkrondur** (`async`). Primary çökerse son yazmalar replica'ya ulaşmamış olabilir.
- Secret'taki şifreler sadece öğrenme amaçlıdır.

## Temizle

```bash
kubectl delete -f kubernetes/week-4/StatefulSet/postgres/
kubectl delete pvc -l app=postgres -n ecommerce-dev
```

StatefulSet silinince PVC'ler **otomatik silinmez**. İkinci komut diskleri de temizler.
