# StatefulSet ile Kafka (KRaft, 3 broker)

Kafka broker'ları durumludur: her broker'ın **sabit bir kimliği** (`node.id`), **kendi diski** ve diğer broker'ların bulabileceği **sabit bir adı** olmalıdır. Bu üçü StatefulSet + headless Service ile sağlanır. Bu örnek ZooKeeper'sız **KRaft** modunu kullanır: 3 Pod'un her biri hem broker hem controller'dır. Redis örneği için bkz. [statefulset.md](../statefulset.md).

Tüm komutlar repo kök klasöründen çalıştırılır. Namespace ve quota için önce [namespace.md](../../../week-3/namespace/namespace.md) adımlarını uygula. Dosyalar:

| Dosya | Görevi |
|---|---|
| [kafka-headless-service.yaml](kafka-headless-service.yaml) | `clusterIP: None` Service (9092 broker, 9093 controller) |
| [kafka-statefulset.yaml](kafka-statefulset.yaml) | 3 broker, her biri 1Gi PVC alır |
| [kafka-client.yaml](kafka-client.yaml) | Kafka komut satırı araçlarını içeren istemci Pod |

## Kur

```bash
kubectl apply -f kubernetes/week-4/StatefulSet/kafka/
kubectl wait --for=condition=Ready pod/kafka-0 pod/kafka-1 pod/kafka-2 pod/kafka-client -n ecommerce-dev --timeout=240s
kubectl get pods,pvc -n ecommerce-dev -o wide
```

Image büyük olduğu için ilk çekim uzun sürebilir (`docker pull apache/kafka:3.9.0`).

## Tasarımın üç kritik noktası

1. **`podManagementPolicy: Parallel`:** Varsayılan `OrderedReady` Pod'ları sırayla başlatır, `kafka-0` Ready olmadan `kafka-1` başlamaz. Kafka controller quorum'u için 3 broker'ın **birlikte** ayakta olması gerekir. `kafka-0` tek başına quorum kuramaz, Ready olamaz ve diğerleri hiç başlamaz. `Parallel` ile üçü aynı anda başlar.
2. **`publishNotReadyAddresses: true`:** Headless Service normalde yalnızca Ready Pod'ları DNS'e koyar. Broker'lar açılışta birbirinin adını çözebilmelidir, ama Ready olmak için de birbirine ihtiyaç duyar. Bu alan Ready olmayan Pod'ları da DNS'e koyarak döngüyü kırar.
3. **Kendi kimliğini Pod adından alma:** `args` içinde `KAFKA_NODE_ID=${HOSTNAME##*-}` ile `kafka-1` -> `node.id=1` olur. `KAFKA_ADVERTISED_LISTENERS` broker'ı **IP ile değil kalıcı DNS adıyla** duyurur (`kafka-1.kafka-headless.ecommerce-dev.svc.cluster.local:9092`). İstemci metadata'dan bu adı öğrenir. IP duyurulsaydı Pod yenilenince istemciler bağlantıyı kaybederdi.

`KAFKA_CONTROLLER_QUORUM_VOTERS` controller'ları `<id>@<kalıcı-dns>:9093` olarak listeler. `CLUSTER_ID` tüm broker'larda aynıdır.

## Quorum'u doğrula

```bash
kubectl exec kafka-client -n ecommerce-dev -- /opt/kafka/bin/kafka-metadata-quorum.sh --bootstrap-server kafka-0.kafka-headless.ecommerce-dev.svc.cluster.local:9092 describe --status
```

Testte `LeaderId: 0` ve `CurrentVoters` içinde 0, 1, 2 numaralı üç controller listelendi.

## Topic oluştur, mesaj gönder ve oku

Her partition 3 broker'da kopyalanır:

```bash
kubectl exec kafka-client -n ecommerce-dev -- /opt/kafka/bin/kafka-topics.sh --bootstrap-server kafka-0.kafka-headless.ecommerce-dev.svc.cluster.local:9092 --create --topic orders --partitions 3 --replication-factor 3
kubectl exec kafka-client -n ecommerce-dev -- /opt/kafka/bin/kafka-topics.sh --bootstrap-server kafka-0.kafka-headless.ecommerce-dev.svc.cluster.local:9092 --describe --topic orders
```

Testte 3 partition'ın lider broker'ları farklıydı (1, 2, 0) ve hepsinde `Isr: 1,2,0` (üç kopya senkron) görüldü. `Isr` (in-sync replicas), lider ile senkron kopyaların listesidir.

3 mesaj yaz (`-i` stdin'i Pod'a iletir):

```bash
printf 'order-1\norder-2\norder-3\n' | kubectl exec -i kafka-client -n ecommerce-dev -- /opt/kafka/bin/kafka-console-producer.sh --bootstrap-server kafka-0.kafka-headless.ecommerce-dev.svc.cluster.local:9092 --topic orders
```

**Başka bir broker** üzerinden oku:

```bash
kubectl exec kafka-client -n ecommerce-dev -- /opt/kafka/bin/kafka-console-consumer.sh --bootstrap-server kafka-2.kafka-headless.ecommerce-dev.svc.cluster.local:9092 --topic orders --from-beginning --timeout-ms 8000
```

Üç mesaj da geldi. Komutun sonunda görünen `TimeoutException` hata değildir, `--timeout-ms` süresi dolunca consumer'ın kapanmasıdır. Mesajlar partition'lara dağıldığı için sırası karışık gelebilir (sıra yalnızca partition içinde garantilidir).

## Bir broker ölünce

```bash
kubectl get pod kafka-1 -n ecommerce-dev -o jsonpath='{.status.podIP}{"\n"}'
kubectl delete pod kafka-1 -n ecommerce-dev --wait=false
kubectl exec kafka-client -n ecommerce-dev -- /opt/kafka/bin/kafka-topics.sh --bootstrap-server kafka-0.kafka-headless.ecommerce-dev.svc.cluster.local:9092 --describe --topic orders
```

Testte `kafka-1` düşünce `Isr: 2,0` oldu (1 numara listeden çıktı) ve `kafka-1`'in lideri olduğu partition'ın lideri başka broker'a geçti. `min.insync.replicas=2` olduğu için kalan 2 senkron kopya yazmaya yetti, `order-4` mesajı **kesintisiz yazıldı**:

```bash
echo order-4 | kubectl exec -i kafka-client -n ecommerce-dev -- /opt/kafka/bin/kafka-console-producer.sh --bootstrap-server kafka-0.kafka-headless.ecommerce-dev.svc.cluster.local:9092 --topic orders
```

`kafka-1` yeniden açılınca (`10.244.3.9` -> `10.244.3.10`, IP değişti, DNS adı aynı) aynı diski (`data-kafka-1`) bağladı, kaçırdığı mesajları lidere yetişerek aldı ve `Isr: 2,0,1` ile tekrar senkron oldu. `order-1` ... `order-4` hepsi okundu.

> Aynı anda 2 broker kaybedilirse `Isr` 2'nin altına düşer ve `min.insync.replicas=2` yüzünden yazma reddedilir (okuma sürer). Veriyi kaybetmemek için bu bilinçli bir tercihtir. KRaft controller quorum'u da 3'te 2 çoğunluğa ihtiyaç duyar.

## Notlar

- **Kaynak:** Her broker `384Mi` request, `768Mi` limit ve `-Xmx256m` heap kullanır. `ecommerce-dev` LimitRange'i memory için limit/request oranını 4 ile sınırlar, bu değerler uyar. Üretimde çok daha büyük kaynak gerekir.
- **ResourceQuota:** 1 StatefulSet ve 3 PVC kullanır. Quota en fazla 3 StatefulSet ve 5 PVC'ye izin verir, yani Redis örneğiyle **aynı anda** çalıştırılırsa PVC sınırı (3 + 3 > 5) aşılır. Birini silip diğerini kur.
- **Dışarıdan erişim yok:** Broker'lar kendilerini cluster içi DNS adıyla duyurur. Cluster dışından bağlanmak için ek `advertised.listeners` ve dış erişim kurulumu gerekir.
- **Güvenlik yok:** Listener'lar `PLAINTEXT`'tir. Üretimde TLS ve kimlik doğrulama (SASL) eklenir.
- **Uygulamadan erişim (.NET):** `BootstrapServers = "kafka-0.kafka-headless:9092,kafka-1.kafka-headless:9092,kafka-2.kafka-headless:9092"` yazılır. Aynı namespace'ten kısa ad yeterlidir.

## Temizle

StatefulSet silinince PVC'ler silinmez, ayrıca silinmelidir:

```bash
kubectl delete -f kubernetes/week-4/StatefulSet/kafka/
kubectl delete pvc -l app=kafka -n ecommerce-dev
```
