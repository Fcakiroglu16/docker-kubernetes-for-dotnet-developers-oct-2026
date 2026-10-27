# ReplicaSet

ReplicaSet, belirtilen sayıda Pod'un her zaman çalışır durumda kalmasını sağlar. Bir Pod silinir veya node çökerse eksik Pod'u şablondan yeniden oluşturur, fazla Pod varsa fazlasını siler. Pod'ları `selector` içindeki label'larla bulur ve sahiplenir.

Bu dokümanda `Microservice1.API` 4 replika ile çalıştırılır. Dosya: [microservice1-replicaset.yaml](microservice1-replicaset.yaml). Tüm komutlar repo kök klasöründen çalıştırılır.

> Pratikte ReplicaSet'i doğrudan oluşturmak yerine Deployment kullanılır. Deployment, ReplicaSet'i kendisi yönetir ve rolling update ekler. Burada amaç ReplicaSet'in temel davranışını görmektir.

## YAML'ın önemli alanları

| Alan | Anlamı |
|---|---|
| `spec.replicas` | İstenen Pod sayısı (burada `4`) |
| `spec.selector.matchLabels` | ReplicaSet'in hangi Pod'lara sahip çıkacağı |
| `spec.template` | Eksik Pod oluşturulurken kullanılan Pod şablonu |
| `template.metadata.labels` | `selector` ile eşleşmek **zorunda**. Eşleşmezse API nesneyi reddeder |

## Hazırlık

Namespace, ResourceQuota ve LimitRange yoksa önce [namespace.md](../../week-3/namespace/namespace.md) adımlarını uygula:

```bash
kubectl apply -f kubernetes/week-3/namespace/namespace.yaml
kubectl apply -f kubernetes/week-3/namespace/resourcequota.yaml
kubectl apply -f kubernetes/week-3/namespace/limitrange.yaml
```

Image'ı build et. Docker Desktop (kind modu) kullanıyorsan node'lar image'ı otomatik görür. Kendi kurduğun kind cluster'ında ayrıca `kind load docker-image microservice1-api:1.0 --name egitim` çalıştır (bkz. [pod.md](../../week-3/1-pod/pod.md)).

```bash
docker build -t microservice1-api:1.0 Microservice1.API
```

> Namespace'te ResourceQuota olduğu için template içindeki `resources` zorunludur. 4 replika toplam `400m` CPU ve `512Mi` memory ister, kota içinde kalır.

## ReplicaSet'i oluştur

```bash
kubectl apply -f kubernetes/week-4/ReplicaSet/microservice1-replicaset.yaml
```

Pod'ların hazır olmasını bekle (startup probe nedeniyle yaklaşık 15-20 sn sürer):

```bash
kubectl wait --for=condition=Ready pod -l app=microservice1-api -n ecommerce-dev --timeout=180s
```

## Durumu kontrol et

`DESIRED`, `CURRENT` ve `READY` sütunları `4` olmalı. Pod'lar node'lara dağılır ve adları `microservice1-rs-<rastgele>` biçiminde olur.

```bash
kubectl get rs -n ecommerce-dev
kubectl get pods -n ecommerce-dev -o wide
kubectl describe rs microservice1-rs -n ecommerce-dev
```

Pod'un sahibini göster (`ownerReferences` ReplicaSet'i işaret eder):

```bash
kubectl get pods -n ecommerce-dev -o jsonpath='{range .items[*]}{.metadata.name}{" -> "}{.metadata.ownerReferences[0].kind}{"/"}{.metadata.ownerReferences[0].name}{"\n"}{end}'
```

## API'ye eriş

Pod'a cluster dışından doğrudan erişilemez. `port-forward` komutu ReplicaSet'in Pod'larından birine bağlanır. Komut çalıştığı sürece açık kalır, bu yüzden ayrı bir terminalde çalıştır.

```bash
kubectl port-forward rs/microservice1-rs 8080:8080 -n ecommerce-dev
```

Başka bir terminalde:

```bash
curl localhost:8080/health/ready
curl localhost:8080/products
```

## Self-healing: Pod silinirse yenisi açılır

İzlemek için bir terminalde:

```bash
kubectl get pods -n ecommerce-dev -w
```

Başka bir terminalde bir Pod'u sil (`<pod-adı>` yerine listeden bir ad yaz):

```bash
kubectl delete pod <pod-adı> -n ecommerce-dev
```

Silinen Pod hemen `Terminating` olur, ReplicaSet aynı anda yeni bir Pod başlatır ve sayı `4`'e döner. Yeni Pod'un adı farklıdır.

## Ölçekle (scale)

Komutla:

```bash
kubectl scale rs microservice1-rs --replicas=6 -n ecommerce-dev
kubectl get rs -n ecommerce-dev
```

Azaltırken ReplicaSet fazla Pod'ları siler:

```bash
kubectl scale rs microservice1-rs --replicas=2 -n ecommerce-dev
```

Eski haline getir:

```bash
kubectl scale rs microservice1-rs --replicas=4 -n ecommerce-dev
```

> Kalıcı ayar için YAML'daki `replicas` değerini değiştirip `kubectl apply` kullan. `kubectl scale` ile yapılan değişiklik YAML'a yansımaz.

## Label ile Pod'u ReplicaSet'ten ayır

ReplicaSet Pod'ları adına değil label'ına göre sahiplenir. Bir Pod'un `app` label'ını değiştirirsen seçiciyle eşleşmez, ReplicaSet onu bırakır ve eksik kalan yerine yeni Pod açar. Ayrılan Pod çalışmaya devam eder. Hata ayıklama için bir Pod'u trafikten ve ReplicaSet'ten ayırmanın yaygın yoludur.

```bash
kubectl label pod <pod-adı> app=debug --overwrite -n ecommerce-dev
kubectl get pods -n ecommerce-dev -L app
```

`READY` sayısı kısa süre `3`'e düşer, yeni Pod hazır olunca tekrar `4` olur ve listede 5 Pod görünür. Label'ı geri verirsen Pod tekrar sahiplenilir. Bu durumda toplam 5 olduğu için ReplicaSet fazla olanı siler:

```bash
kubectl label pod <pod-adı> app=microservice1-api --overwrite -n ecommerce-dev
kubectl get pods -n ecommerce-dev
```

## Image güncelle: mevcut Pod'lar değişmez

ReplicaSet Deployment gibi rolling update yapmaz. Şablonu (örneğin image'ı) değiştirirsen yalnızca **bundan sonra oluşturulacak** Pod'lar yeni şablonu kullanır, çalışan Pod'lara dokunulmaz.

```bash
docker tag microservice1-api:1.0 microservice1-api:1.1
kubectl set image rs/microservice1-rs microservice1-api=microservice1-api:1.1 -n ecommerce-dev
kubectl get pods -n ecommerce-dev -o 'custom-columns=NAME:.metadata.name,IMAGE:.spec.containers[0].image'
```

Hepsi hâlâ `1.0` çalışır. Bir Pod'u silersen yerine gelen Pod `1.1` olur:

```bash
kubectl delete pod <pod-adı> -n ecommerce-dev
kubectl get pods -n ecommerce-dev -o 'custom-columns=NAME:.metadata.name,IMAGE:.spec.containers[0].image'
```

Bu yüzden sürüm geçişi (rolling update, rollback) için Deployment kullanılır.

## Temizle

ReplicaSet silinince sahip olduğu tüm Pod'lar da silinir:

```bash
kubectl delete rs microservice1-rs -n ecommerce-dev
```

Pod'lar silinmesin istiyorsan `--cascade=orphan` ekle. Pod'lar sahipsiz kalır ve çalışmaya devam eder.
