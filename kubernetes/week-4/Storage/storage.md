# PersistentVolume, PersistentVolumeClaim ve StorageClass

Container dosya sistemi Pod ile birlikte silinir. Veriyi Pod'dan bağımsız tutmak için üç nesne kullanılır:

- **PersistentVolume (PV):** Cluster'daki gerçek disk. Namespace'i yoktur.
- **PersistentVolumeClaim (PVC):** Pod'un disk talebi. Namespace'e aittir. Kubernetes uygun PV'yi bulup PVC'ye bağlar (`Bound`).
- **StorageClass:** Diskin nasıl üretileceğini tanımlar. PVC isteyince PV'yi otomatik oluşturur (dinamik provisioning).

Pod diske değil **PVC'ye** referans verir. Disk türü (hostPath, NFS, bulut diski) Pod YAML'ını etkilemez.

Tüm komutlar repo kök klasöründen çalıştırılır. Namespace ve quota için önce [namespace.md](../../week-3/namespace/namespace.md) adımlarını uygula. Dosyalar:

| Dosya | Görevi |
|---|---|
| [pv.yaml](pv.yaml) | Elle yazılan PV (hostPath, 1Gi, `Retain`, sınıf `manual`) |
| [pvc.yaml](pvc.yaml) | `manual` sınıfındaki PV'ye bağlanan PVC |
| [pod-manual.yaml](pod-manual.yaml) | PVC'yi `/data` yoluna bağlayıp `log.txt`'ye satır ekleyen Pod |
| [pvc-dynamic.yaml](pvc-dynamic.yaml) | StorageClass yazılmayan PVC. PV otomatik üretilir |
| [pod-dynamic.yaml](pod-dynamic.yaml) | Dinamik PVC'yi kullanan Pod |

## 1. Statik provisioning: PV'yi elle yaz

`pv.yaml` içindeki node adını kendi cluster'ına göre düzenle:

```bash
kubectl get nodes
```

```bash
kubectl apply -f kubernetes/week-4/Storage/pv.yaml
kubectl get pv manual-pv
```

`STATUS` değeri `Available`: PV hazır ama kimse talep etmedi. PVC'yi ve Pod'u oluştur:

```bash
kubectl apply -f kubernetes/week-4/Storage/pvc.yaml
kubectl get pvc,pv -n ecommerce-dev | grep manual
```

PVC `Bound` olur, PV'nin `CLAIM` sütunu `ecommerce-dev/manual-pvc` gösterir. Eşleşme üç şeye bakılarak yapılır: `storageClassName`, `accessModes`, boyut (PV >= PVC isteği).

```bash
kubectl apply -f kubernetes/week-4/Storage/pod-manual.yaml
kubectl wait --for=condition=Ready pod/storage-writer -n ecommerce-dev
kubectl logs storage-writer -n ecommerce-dev
```

## 2. Pod silinince veri kalır

```bash
kubectl delete pod storage-writer -n ecommerce-dev
kubectl apply -f kubernetes/week-4/Storage/pod-manual.yaml
kubectl wait --for=condition=Ready pod/storage-writer -n ecommerce-dev
kubectl logs storage-writer -n ecommerce-dev
```

Testte log iki satır gösterdi (`14:04:03` ve `14:04:39`). İlk Pod'un yazdığı satır yeni Pod'da durdu.

> `hostPath` veriyi tek node'un diskinde tutar. Pod başka node'a düşerse klasör boş görünür. Bu yüzden PV'de `nodeAffinity` var ve Pod her zaman o node'da çalışır. Production'da bulut diski veya NFS gibi ağ diski kullanılır.

## 3. Dinamik provisioning: PV'yi StorageClass üretir

```bash
kubectl get storageclass
kubectl apply -f kubernetes/week-4/Storage/pvc-dynamic.yaml -f kubernetes/week-4/Storage/pod-dynamic.yaml
kubectl wait --for=condition=Ready pod/storage-dynamic -n ecommerce-dev
kubectl get pvc,pv -n ecommerce-dev | grep -E "dynamic|NAME"
```

PVC'de `storageClassName` yok, bu yüzden `(default)` işaretli sınıf (`standard`) kullanılır. `pvc-<uid>` adlı bir PV kendiliğinden oluşur. Sınıfın `VOLUMEBINDINGMODE` değeri `WaitForFirstConsumer`: PVC, bir Pod onu kullanana kadar `Pending` kalır. Disk, Pod'un düştüğü node'da üretilir.

StatefulSet'teki `volumeClaimTemplates` ([redis-statefulset.yaml](../StatefulSet/redis-statefulset.yaml)) bu mekanizmayı kullanır. Her Pod için bir PVC, her PVC için bir PV üretilir.

## 4. reclaimPolicy: PVC silinince PV'ye ne olur

| Politika | PVC silinince |
|---|---|
| `Retain` | PV ve veri kalır. `STATUS: Released`. Elle temizlenmeli |
| `Delete` | PV ve içindeki disk silinir. Dinamik PV'lerin varsayılanı |

```bash
kubectl delete pod storage-writer storage-dynamic -n ecommerce-dev
kubectl delete pvc manual-pvc dynamic-pvc -n ecommerce-dev
kubectl get pv | grep -E "NAME|manual|dynamic"
```

Testte `manual-pv` `Released` durumunda kaldı, dinamik PV listeden kayboldu. `Released` PV yeni bir PVC'ye bağlanmaz, çünkü eski `claimRef` içinde durur. Yeniden kullanmak için PV'yi silip yeniden oluştur (veya `claimRef` alanını temizle).

## Önemli noktalar

- **Pod'dan önce PVC gerekir.** PVC yoksa Pod `Pending` kalır (`persistentvolumeclaim "..." not found`).
- **accessModes:** `ReadWriteOnce` (tek node yazar), `ReadOnlyMany` (çok node okur), `ReadWriteMany` (çok node yazar, NFS gibi disk gerekir), `ReadWriteOncePod` (tek Pod).
- **ResourceQuota ve LimitRange:** `ecommerce-dev` en fazla 5 PVC ve 20Gi'a izin verir. LimitRange PVC başına en az 1Gi şart koşar, bu yüzden 500Mi'lik PVC reddedilir. Postgres'in 3 PVC'si ile birlikte bu örnek kotayı tam doldurur (5/5).
- **Boyut:** PVC 500Mi isteyip 1Gi'lik PV'ye bağlansa bile PVC'ye PV'nin tamamı (1Gi) görünür.

## Temizle

```bash
kubectl delete -f kubernetes/week-4/Storage/
```

`manual-pv` `Retain` olduğu için node üzerindeki `/mnt/data/manual-pv` klasörü silinmez. PV nesnesi ise yukarıdaki komutla silinir.
