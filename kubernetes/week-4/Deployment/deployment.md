# Deployment

Deployment, uygulamayı **güncellemek** için kullanılan nesnedir. ReplicaSet'in yaptığı her şeyi yapar (istenen sayıda Pod'u ayakta tutar, kaybolanı yeniden açar, ölçeklenir) ve üstüne şunları ekler:

- **Rolling update:** Yeni sürüme kademeli geçer, kesinti olmaz.
- **Rollback:** Hatalı sürümü tek komutla geri alır.
- **Sürüm geçmişi:** Her güncellemeyi bir *revision* olarak saklar.

[ReplicaSet](../ReplicaSet/replicaset.md) konusunda image'ı değiştirince çalışan Pod'ların değişmediğini görmüştük. Deployment bu sorunu çözer: Pod şablonu değişince yeni bir ReplicaSet oluşturur ve Pod'ları eskisinden yenisine kademeli taşır.

Tüm komutlar repo kök klasöründen çalıştırılır. Dosyalar:

| Dosya | Görevi |
|---|---|
| [microservice1-deployment.yaml](microservice1-deployment.yaml) | 4 replikalı Deployment, `RollingUpdate` stratejisi |
| [microservice1-deployment-recreate.yaml](microservice1-deployment-recreate.yaml) | 3 replikalı Deployment, `Recreate` stratejisi (karşılaştırma için) |

## Nasıl çalışır?

```
Deployment  ->  ReplicaSet (sürüm 1)  ->  Pod, Pod, Pod, Pod
            ->  ReplicaSet (sürüm 2)  ->  (yeni Pod'lar kademeli buraya geçer)
```

- Deployment doğrudan Pod oluşturmaz. Her Pod şablonu için bir ReplicaSet oluşturur ve onu yönetir.
- Pod şablonundaki (`spec.template`) **her değişiklik** (image, env, resources...) yeni bir ReplicaSet ve yeni bir rollout başlatır. `replicas` değişikliği rollout başlatmaz, yalnızca ölçekler.
- Eski ReplicaSet'ler silinmez, `0` replikaya indirilip rollback için saklanır. `revisionHistoryLimit` kaç tanesinin saklanacağını belirler.

## YAML'ın önemli alanları

| Alan | Anlamı |
|---|---|
| `spec.replicas` | İstenen Pod sayısı (burada `4`) |
| `spec.selector.matchLabels` | Deployment'ın hangi Pod'ları yönettiği. Oluşturulduktan sonra değiştirilemez |
| `spec.strategy.type` | `RollingUpdate` (varsayılan) veya `Recreate` |
| `rollingUpdate.maxSurge: 1` | Güncelleme sırasında istenen sayının üstüne en fazla 1 Pod açılabilir (4 -> 5) |
| `rollingUpdate.maxUnavailable: 0` | Hazır Pod sayısı hiçbir an 4'ün altına inmez, yani kesinti olmaz |
| `minReadySeconds: 5` | Yeni Pod Ready olduktan sonra 5 sn sorunsuz kalmadan "hazır" sayılmaz |
| `progressDeadlineSeconds: 60` | Rollout 60 sn ilerlemezse başarısız işaretlenir (varsayılan 600 sn) |
| `revisionHistoryLimit: 5` | Rollback için saklanan eski ReplicaSet sayısı (varsayılan 10) |

> `maxSurge` ve `maxUnavailable` sayı veya yüzde (`25%`) olabilir. Rolling update'in ilerlemesi **readiness probe**'a bağlıdır: yeni Pod Ready olmadan eski Pod silinmez. Bu yüzden probe'ların doğru ayarlanması önemlidir.

## Hazırlık

Namespace, ResourceQuota ve LimitRange yoksa önce [namespace.md](../../week-3/namespace/namespace.md) adımlarını uygula. Image'ı build et (Docker Desktop kind modunda node'lar image'ı otomatik görür, kendi kind cluster'ında ayrıca `kind load docker-image` gerekir, bkz. [pod.md](../../week-3/1-pod/pod.md)):

```bash
docker build -t microservice1-api:1.0 Microservice1.API
```

Güncelleme örneği için aynı image'a ikinci bir tag ver. İçerik aynı ama Kubernetes için farklı bir sürümdür:

```bash
docker tag microservice1-api:1.0 microservice1-api:1.1
```

> `ecommerce-dev` namespace'inde ResourceQuota var. `maxSurge` nedeniyle güncelleme sırasında 5 Pod çalışır, kota buna yeter (5 x `500m` CPU limiti).

## Deployment'ı oluştur

```bash
kubectl apply -f kubernetes/week-4/Deployment/microservice1-deployment.yaml
kubectl rollout status deploy/microservice1-deploy -n ecommerce-dev
```

Deployment, ReplicaSet ve Pod'ları göster. Pod adı `<deployment>-<şablon-hash>-<rastgele>` biçimindedir. Hash, Pod şablonunun özetidir:

```bash
kubectl get deploy,rs,pods -n ecommerce-dev
```

Revision geçmişi (ilk oluşturma revision 1'dir):

```bash
kubectl rollout history deploy/microservice1-deploy -n ecommerce-dev
```

## Rolling update: yeni sürüme geç

Bir terminalde Pod'ları izle:

```bash
kubectl get pods -n ecommerce-dev -w
```

Başka bir terminalde image'ı güncelle ve değişikliğin nedenini geçmişe yaz (`CHANGE-CAUSE` sütununda görünür):

```bash
kubectl set image deploy/microservice1-deploy microservice1-api=microservice1-api:1.1 -n ecommerce-dev
kubectl annotate deploy/microservice1-deploy kubernetes.io/change-cause="image 1.1" -n ecommerce-dev --overwrite
```

> `microservice1-api=` ifadesindeki ad, YAML'daki **container adıdır** (`containers[].name`). Kalıcı yol, YAML'daki `image` satırını değiştirip `kubectl apply` yapmaktır. `kubectl set image` hızlı deneme içindir.

Güncellemeyi izle. Rollout birkaç Pod için birkaç on saniye sürer:

```bash
kubectl rollout status deploy/microservice1-deploy -n ecommerce-dev
```

Testte görülen sıra: önce 1 yeni Pod açıldı (toplam 5), yeni Pod Ready olunca 1 eski Pod silindi, bu 4 kez tekrarlandı. Toplam Pod sayısı hiç 5'i aşmadı, hazır Pod sayısı hiç 4'ün altına inmedi.

Sonucu kontrol et. Yeni ReplicaSet 4 Pod'a sahiptir, eskisi `0` replika ile saklanır:

```bash
kubectl get rs -n ecommerce-dev
kubectl get pods -n ecommerce-dev -o 'custom-columns=NAME:.metadata.name,IMAGE:.spec.containers[0].image'
kubectl rollout history deploy/microservice1-deploy -n ecommerce-dev
```

## Hatalı güncelleme ve rollback

Var olmayan bir image tag'i ile hatalı bir güncelleme yap:

```bash
kubectl set image deploy/microservice1-deploy microservice1-api=microservice1-api:9.9 -n ecommerce-dev
kubectl annotate deploy/microservice1-deploy kubernetes.io/change-cause="image 9.9 (hatali)" -n ecommerce-dev --overwrite
```

Birkaç saniye sonra Pod'lara bak. Yeni Pod `ImagePullBackOff` durumundadır, **4 eski Pod ise çalışmaya devam eder**. `maxUnavailable: 0` olduğu için eski Pod'lar silinmez ve uygulama kesintisiz hizmet verir:

```bash
kubectl get pods -n ecommerce-dev
kubectl get deploy -n ecommerce-dev
```

`READY` sütunu `4/4`, `UP-TO-DATE` sütunu `1` görünür. `progressDeadlineSeconds` (60 sn) dolunca rollout başarısız işaretlenir:

```bash
kubectl rollout status deploy/microservice1-deploy -n ecommerce-dev --timeout=5s
kubectl get deploy microservice1-deploy -n ecommerce-dev -o jsonpath='{range .status.conditions[*]}{.type}={.status} ({.reason}){"\n"}{end}'
```

`Progressing=False (ProgressDeadlineExceeded)` görürsün. Deployment kendiliğinden geri **almaz**, durumu bildirir. Geri almak için:

```bash
kubectl rollout undo deploy/microservice1-deploy -n ecommerce-dev
kubectl rollout status deploy/microservice1-deploy -n ecommerce-dev
```

`undo` bir önceki revision'a döner ve hatalı Pod'u siler. `kubectl apply` ile oluşturulan bir Deployment'ta bir uyarı çıkar: `undo`, `last-applied-configuration` annotation'ını güncellemez. Kalıcı geri dönüş için YAML'daki `image` satırını da eski değere çevirmek en doğrusudur.

### Belirli bir revision'a dön

Geçmişi listele ve bir revision'ın Pod şablonunu incele:

```bash
kubectl rollout history deploy/microservice1-deploy -n ecommerce-dev
kubectl rollout history deploy/microservice1-deploy -n ecommerce-dev --revision=4
```

İstediğin revision'a dön:

```bash
kubectl rollout undo deploy/microservice1-deploy -n ecommerce-dev --to-revision=1
```

> Geri dönmek geçmişi geriye sarmaz, **yeni bir revision oluşturur**. Revision 1'e dönünce eski revision 1 silinir ve içeriği en yeni numarayla (örneğin 5) görünür. Bu yüzden `CHANGE-CAUSE` sütunu `<none>` olabilir (annotation o revision oluşturulduğunda yoktu).

## Ölçekle (scale)

Deployment'ın replika sayısı doğrudan değiştirilir. Bu yeni ReplicaSet açmaz, mevcut ReplicaSet'i ölçekler:

```bash
kubectl scale deploy/microservice1-deploy --replicas=6 -n ecommerce-dev
kubectl get deploy -n ecommerce-dev
kubectl scale deploy/microservice1-deploy --replicas=4 -n ecommerce-dev
```

> Kalıcı ayar için YAML'daki `replicas` değerini değiştirip `kubectl apply` yap. Otomatik ölçekleme için Deployment'ı [HPA](../hpa/hpa.md) ile birleştirebilirsin. HPA kullanırken YAML'a `replicas` yazılmaz, aksi halde her `apply` HPA'nın ayarını ezer.

## Pod'ları yeniden başlat

Şablon değişmese de tüm Pod'ları kademeli olarak yenilemek için (örneğin ConfigMap değişince veya sıkışan Pod'ları temizlemek için):

```bash
kubectl rollout restart deploy/microservice1-deploy -n ecommerce-dev
kubectl rollout status deploy/microservice1-deploy -n ecommerce-dev
```

Bu da yeni bir revision oluşturur.

## Recreate stratejisi

`Recreate`, önce **tüm** eski Pod'ları siler, sonra yenilerini açar. Güncelleme sırasında kesinti olur ama eski ve yeni sürüm hiçbir zaman aynı anda çalışmaz. Sürümlerin birlikte çalışamadığı durumlarda (örneğin veritabanı şeması uyumsuzsa) tercih edilir.

```bash
kubectl apply -f kubernetes/week-4/Deployment/microservice1-deployment-recreate.yaml
kubectl rollout status deploy/microservice1-recreate -n ecommerce-dev
```

Pod'ları izle (ayrı terminalde `-w`) ve image'ı güncelle:

```bash
kubectl get pods -n ecommerce-dev -l app=microservice1-recreate -w
```

```bash
docker tag microservice1-api:1.0 microservice1-api:1.2
kubectl set image deploy/microservice1-recreate microservice1-api=microservice1-api:1.2 -n ecommerce-dev
```

Üç eski Pod aynı anda `Terminating` olur, ardından üç yeni Pod `0/1` durumunda açılır. Testte yeni Pod'lar yaklaşık 14 sn boyunca Ready olmadı, bu sürede hizmet veren Pod yoktu. `RollingUpdate`'te bu süre boyunca eski Pod'lar çalışmaya devam eder.

## RollingUpdate ve Recreate karşılaştırması

| | RollingUpdate | Recreate |
|---|---|---|
| Kesinti | Yok (`maxUnavailable: 0` ile) | Var (yeni Pod'lar Ready olana kadar) |
| Eski ve yeni sürüm birlikte çalışır mı | Evet, geçiş süresince | Hayır |
| Geçici ek kaynak | `maxSurge` kadar fazladan Pod | Gerekmez |
| Ne zaman | Çoğu durumda (varsayılan) | Sürümler birlikte çalışamıyorsa |

## Temizle

Deployment silinince ReplicaSet'leri ve Pod'ları da silinir:

```bash
kubectl delete -f kubernetes/week-4/Deployment/microservice1-deployment.yaml
kubectl delete -f kubernetes/week-4/Deployment/microservice1-deployment-recreate.yaml
```

Oluşturduğun ek image tag'lerini silmek için:

```bash
docker rmi microservice1-api:1.1 microservice1-api:1.2
```
