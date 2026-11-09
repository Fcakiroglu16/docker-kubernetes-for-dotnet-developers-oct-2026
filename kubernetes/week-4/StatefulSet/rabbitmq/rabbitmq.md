# StatefulSet ile RabbitMQ (3 node cluster)

RabbitMQ node'ları durumludur: her node'un **sabit bir adı** (Erlang node adı), **kendi diski** ve diğer node'ların bulabileceği **kalıcı bir DNS adı** olmalıdır. Bunlar StatefulSet + headless Service ile sağlanır. Kafka örneği için bkz. [kafka.md](../kafka/kafka.md), Redis için [statefulset.md](../statefulset.md).

Tüm komutlar repo kök klasöründen çalıştırılır. Namespace ve quota için önce [namespace.md](../../../week-3/namespace/namespace.md) adımlarını uygula. Dosyalar:

| Dosya | Görevi |
|---|---|
| [1-rabbitmq-secret.yaml](1-rabbitmq-secret.yaml) | Varsayılan kullanıcı/şifre ve Erlang cookie |
| [2-rabbitmq-configmap.yaml](2-rabbitmq-configmap.yaml) | Küme üyelerini listeleyen `10-cluster.conf` |
| [3-rabbitmq-headless-service.yaml](3-rabbitmq-headless-service.yaml) | `clusterIP: None` Service (5672, 15672, 4369, 25672) |
| [4-rabbitmq-statefulset.yaml](4-rabbitmq-statefulset.yaml) | 3 node, her biri 1Gi PVC alır |
| [5-rabbitmq-nodeport-service.yaml](5-rabbitmq-nodeport-service.yaml) | Dış erişim: AMQP 30672, yönetim arayüzü 31672 |

## Kur

```bash
kubectl apply -f kubernetes/week-4/StatefulSet/rabbitmq/
kubectl wait --for=condition=Ready pod/rabbitmq-0 pod/rabbitmq-1 pod/rabbitmq-2 -n ecommerce-dev --timeout=240s
kubectl get pods,pvc -n ecommerce-dev -o wide
```

## Tasarımın kritik noktaları

1. **Node adı kalıcı DNS adıdır:** `RABBITMQ_USE_LONGNAME=true` ve `RABBITMQ_NODENAME=rabbit@$(POD_NAME).rabbitmq-headless.ecommerce-dev.svc.cluster.local`. Pod IP'si değişse de node kimliği aynı kalır. Diskteki veri bu isme bağlıdır.
2. **Küme üyeleri sabit listeden bulunur:** `classic_config` peer discovery, 3 node adını ConfigMap'ten okur. Kubernetes API ve RBAC gerekmez.
3. **`publishNotReadyAddresses: true`:** Node'lar Ready olmadan birbirinin adını çözebilmelidir, yoksa küme kurulamaz.
4. **`podManagementPolicy: Parallel`:** Pod'lar aynı anda başlar, birbirini beklemez.
5. **Aynı Erlang cookie:** Node'lar birbirine bu cookie ile güvenir. Üçünde de aynı Secret'tan gelir.
6. **Tek dosya `subPath` ile mount edilir:** İmajın varsayılan `conf.d` dosyaları ezilmez.
7. **`pause_minority`:** Ağ bölünmesinde azınlıkta kalan node durur, veri tutarlılığı korunur.

## Kümeyi doğrula

```bash
kubectl exec rabbitmq-0 -n ecommerce-dev -- rabbitmqctl cluster_status
```

`Running Nodes` altında üç node listelenmelidir.

## Quorum queue, mesaj gönder ve oku

Quorum queue, mesajı 3 node'da kopyalar (Raft). Çoğunluk (2/3) ayaktaysa çalışır:

```bash
kubectl exec rabbitmq-0 -n ecommerce-dev -- rabbitmqadmin --username admin --password admin123 declare queue --name orders --type quorum
kubectl exec rabbitmq-0 -n ecommerce-dev -- rabbitmqadmin --username admin --password admin123 publish message --routing-key orders --payload "order-1"
kubectl exec rabbitmq-0 -n ecommerce-dev -- rabbitmqctl list_queues name type leader members online messages
```

**Başka bir node** üzerinden oku:

```bash
kubectl exec rabbitmq-2 -n ecommerce-dev -- rabbitmqadmin --username admin --password admin123 get messages --queue orders --count 5 --ack-mode ack_requeue_false
```

## Node düşünce ne olur

```bash
kubectl delete pod rabbitmq-0 -n ecommerce-dev
kubectl exec rabbitmq-1 -n ecommerce-dev -- rabbitmqctl list_queues name leader messages
```

Testte `rabbitmq-0` (lider) silinince lider `rabbitmq-2`'ye geçti, mesaj sayısı korundu. Pod aynı adla geri gelip kümeye yeniden katıldı. Veri `data-rabbitmq-0` PVC'sinde kaldığı için kaybolmadı.

## Dışarıya aç (NodePort)

`rabbitmq-external` Service'i AMQP için `30672`, yönetim arayüzü için `31672` portunu açar. Tüm node'lar eşit olduğu için selector `app: rabbitmq`'dur (Redis'teki gibi tek Pod seçmeye gerek yok).

- AMQP: `amqp://admin:admin123@<node-ip>:30672`
- Yönetim arayüzü: `http://<node-ip>:31672`

Docker Desktop/kind'da `localhost:<nodePort>` çalışmayabilir. O durumda:

```bash
kubectl port-forward svc/rabbitmq-external 15672:15672 5672:5672 -n ecommerce-dev
```

Sonra `http://localhost:15672` adresini aç. Kafka'nın aksine RabbitMQ client'ı bağlantı sonrası node adresi öğrenmez, bu yüzden tek NodePort yeterlidir.

## Temizle

```bash
kubectl delete -f kubernetes/week-4/StatefulSet/rabbitmq/
kubectl delete pvc -l app=rabbitmq -n ecommerce-dev
```

StatefulSet silinince PVC'ler **otomatik silinmez**. İkinci komut diskleri de temizler.
